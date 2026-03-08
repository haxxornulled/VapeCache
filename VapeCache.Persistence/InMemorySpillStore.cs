using System.Diagnostics;
using System.Globalization;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Persistence;

/// <summary>
/// ENTERPRISE ONLY: File-based spill store with scatter/gather distribution.
/// Distributes spill files across 65,536 directories to avoid filesystem bottlenecks.
/// </summary>
public sealed class FileSpillStore : IInMemorySpillStore, ISpillStoreDiagnostics
{
    private readonly string _rootPath;
    private readonly ISpillEncryptionProvider _encryption;
    private readonly InMemorySpillOptions _options;
    private readonly long _cleanupIntervalTicks;
    private long _lastCleanupTicks;
    private int _cleanupRunning;
    private readonly ConcurrentDictionary<int, int> _shardFileCounts = new();
    private long _totalSpillFiles;
    private int _inventoryInitialized;

    /// <summary>
    /// Executes file spill store.
    /// </summary>
    public FileSpillStore(IOptionsMonitor<InMemorySpillOptions> options, ISpillEncryptionProvider encryption)
    {
        _options = options.CurrentValue;
        _rootPath = ResolveRoot(_options.SpillDirectory);
        _encryption = encryption;
        _cleanupIntervalTicks = ToStopwatchTicks(_options.OrphanCleanupInterval);
        _lastCleanupTicks = 0;
        Directory.CreateDirectory(_rootPath);
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public async ValueTask WriteAsync(Guid spillRef, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        EnsureInventoryInitialized();
        var path = GetPath(spillRef);
        var shardKey = GetShardKey(spillRef);
        var existedBefore = File.Exists(path);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var encrypted = await _encryption.EncryptAsync(data, ct).ConfigureAwait(false);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(encrypted, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
            if (!existedBefore)
                IncrementShard(shardKey);

            CacheTelemetry.SpillWriteCount.Add(1);
            CacheTelemetry.SpillWriteBytes.Add(data.Length);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
        finally
        {
            TryScheduleOrphanCleanup();
        }
    }

    /// <summary>
    /// Attempts to value.
    /// </summary>
    public async ValueTask<byte[]?> TryReadAsync(Guid spillRef, CancellationToken ct)
    {
        var path = GetPath(spillRef);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (stream.Length > int.MaxValue)
                throw new InvalidOperationException("Spill payload exceeds supported size.");

            var buffer = new byte[stream.Length];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct).ConfigureAwait(false);
                if (n == 0)
                    throw new EndOfStreamException();
                read += n;
            }

            var decrypted = await _encryption.DecryptAsync(buffer, ct).ConfigureAwait(false);
            var result = EnsureArray(decrypted);
            TryRefreshActivityTimestamp(path);
            CacheTelemetry.SpillReadCount.Add(1);
            CacheTelemetry.SpillReadBytes.Add(result.Length);
            TryScheduleOrphanCleanup();
            return result;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask DeleteAsync(Guid spillRef, CancellationToken ct)
    {
        EnsureInventoryInitialized();
        var shardKey = GetShardKey(spillRef);
        var deleted = false;
        try
        {
            var path = GetPath(spillRef);
            if (File.Exists(path))
            {
                File.Delete(path);
                deleted = true;
            }
        }
        catch
        {
            // Best-effort cleanup.
        }

        if (deleted)
            DecrementShard(shardKey);

        TryScheduleOrphanCleanup();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Gets value.
    /// </summary>
    public SpillStoreDiagnosticsSnapshot GetSnapshot()
    {
        EnsureInventoryInitialized();

        var shards = _shardFileCounts.ToArray();
        var total = Volatile.Read(ref _totalSpillFiles);
        var activeShards = shards.Length;
        var maxFilesInShard = activeShards == 0 ? 0 : shards.Max(static pair => pair.Value);
        var avgFilesPerActive = activeShards == 0 ? 0d : total / (double)activeShards;
        var imbalance = avgFilesPerActive <= 0d ? 0d : maxFilesInShard / avgFilesPerActive;

        var top = shards
            .OrderByDescending(static pair => pair.Value)
            .Take(8)
            .Select(static pair => new SpillShardLoad(FormatShard(pair.Key), pair.Value))
            .ToArray();

        return new SpillStoreDiagnosticsSnapshot(
            SupportsDiskSpill: true,
            SpillToDiskConfigured: _options.EnableSpillToDisk,
            Mode: "file",
            TotalSpillFiles: total,
            ActiveShards: activeShards,
            MaxFilesInShard: maxFilesInShard,
            AvgFilesPerActiveShard: avgFilesPerActive,
            ImbalanceRatio: imbalance,
            TopShards: top,
            SampledAtUtc: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Scatter/gather pattern: Distributes files across 256x256 = 65,536 directories.
    /// Example: abcdef123456... => /spill/ab/cd/abcdef123456....bin
    /// </summary>
    private string GetPath(Guid spillRef)
    {
        var name = spillRef.ToString("N");
        var dir = Path.Combine(_rootPath, name.Substring(0, 2), name.Substring(2, 2));
        return Path.Combine(dir, $"{name}.bin");
    }

    private static string ResolveRoot(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "VapeCache", "spill");
    }

    private static byte[] EnsureArray(ReadOnlyMemory<byte> data)
    {
        if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment) &&
            segment.Array is not null &&
            segment.Offset == 0 &&
            segment.Count == segment.Array.Length)
        {
            return segment.Array;
        }

        return data.ToArray();
    }

    private static void TryRefreshActivityTimestamp(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
            // Best-effort touch to keep active spill files from looking orphaned.
        }
    }

    private void EnsureInventoryInitialized()
    {
        if (Volatile.Read(ref _inventoryInitialized) != 0)
            return;
        if (Interlocked.CompareExchange(ref _inventoryInitialized, 1, 0) != 0)
            return;

        if (!Directory.Exists(_rootPath))
            return;

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(_rootPath, "*.bin", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name is null || name.Length < 4)
                continue;

            var shardKey = ParseShard(name);
            if (shardKey < 0)
                continue;

            _shardFileCounts.AddOrUpdate(shardKey, 1, static (_, current) => current + 1);
            total++;
        }

        Interlocked.Exchange(ref _totalSpillFiles, total);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private void IncrementShard(int shardKey)
    {
        _shardFileCounts.AddOrUpdate(shardKey, 1, static (_, current) => current + 1);
        Interlocked.Increment(ref _totalSpillFiles);
    }

    private void DecrementShard(int shardKey)
    {
        while (true)
        {
            if (!_shardFileCounts.TryGetValue(shardKey, out var current))
                break;

            if (current <= 1)
            {
                if (_shardFileCounts.TryRemove(new KeyValuePair<int, int>(shardKey, current)))
                    break;

                continue;
            }

            if (_shardFileCounts.TryUpdate(shardKey, current - 1, current))
                break;
        }

        InterlockedExtensions.DecrementIfPositive(ref _totalSpillFiles);
    }

    private static int GetShardKey(Guid spillRef)
    {
        var name = spillRef.ToString("N");
        return ParseShard(name);
    }

    private static int ParseShard(string spillName)
    {
        return int.TryParse(spillName.AsSpan(0, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var shard)
            ? shard
            : -1;
    }

    private static string FormatShard(int shard) => shard.ToString("x4", CultureInfo.InvariantCulture);

    private void TryScheduleOrphanCleanup()
    {
        if (!_options.EnableOrphanCleanup)
            return;
        if (_cleanupIntervalTicks <= 0 || _options.OrphanMaxAge <= TimeSpan.Zero)
            return;

        var now = Stopwatch.GetTimestamp();
        var last = Volatile.Read(ref _lastCleanupTicks);
        if (last != 0 && now - last < _cleanupIntervalTicks)
            return;

        if (Interlocked.Exchange(ref _cleanupRunning, 1) == 1)
            return;

        _ = Task.Run(() => RunOrphanCleanupAsync(now));
    }

    private async Task RunOrphanCleanupAsync(long scheduledAt)
    {
        try
        {
            Volatile.Write(ref _lastCleanupTicks, scheduledAt);
            await CleanupOrphansAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort cleanup.
        }
        finally
        {
            Interlocked.Exchange(ref _cleanupRunning, 0);
        }
    }

    private Task CleanupOrphansAsync()
    {
        if (!Directory.Exists(_rootPath))
            return Task.CompletedTask;

        var cutoff = DateTimeOffset.UtcNow - _options.OrphanMaxAge;
        var cutoffUtc = cutoff.UtcDateTime;

        long scanned = 0;
        long deleted = 0;
        long deletedBytes = 0;

        foreach (var file in Directory.EnumerateFiles(_rootPath, "*.bin", SearchOption.AllDirectories))
        {
            scanned++;
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc <= cutoffUtc)
                {
                    var spillName = Path.GetFileNameWithoutExtension(file);
                    deletedBytes += info.Length;
                    File.Delete(file);
                    if (!string.IsNullOrWhiteSpace(spillName))
                    {
                        var shardKey = ParseShard(spillName);
                        if (shardKey >= 0)
                            DecrementShard(shardKey);
                    }
                    deleted++;
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        foreach (var tempFile in Directory.EnumerateFiles(_rootPath, "*.tmp", SearchOption.AllDirectories))
        {
            scanned++;
            try
            {
                var info = new FileInfo(tempFile);
                if (info.LastWriteTimeUtc <= cutoffUtc)
                {
                    deletedBytes += info.Length;
                    File.Delete(tempFile);
                    deleted++;
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        if (scanned > 0)
            CacheTelemetry.SpillOrphanScanned.Add(scanned);
        if (deleted > 0)
        {
            CacheTelemetry.SpillOrphanCleanupCount.Add(deleted);
            CacheTelemetry.SpillOrphanCleanupBytes.Add(deletedBytes);
        }

        return Task.CompletedTask;
    }

    private static long ToStopwatchTicks(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            return 0;

        var seconds = interval.TotalSeconds;
        if (seconds <= 0)
            return 0;

        var ticks = seconds * Stopwatch.Frequency;
        if (ticks > long.MaxValue)
            return long.MaxValue;

        return (long)ticks;
    }
}

/// <summary>
/// No-op encryption provider (plaintext storage).
/// For production, use AES-256 or custom HSM integration.
/// </summary>
public sealed class NoopSpillEncryptionProvider : ISpillEncryptionProvider
{
    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<byte[]> EncryptAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct)
        => ValueTask.FromResult(ToArray(plaintext));

    /// <summary>
    /// Executes value.
    /// </summary>
    public ValueTask<byte[]> DecryptAsync(ReadOnlyMemory<byte> ciphertext, CancellationToken ct)
        => ValueTask.FromResult(ToArray(ciphertext));

    private static byte[] ToArray(ReadOnlyMemory<byte> data)
    {
        if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment) &&
            segment.Array is not null &&
            segment.Offset == 0 &&
            segment.Count == segment.Array.Length)
        {
            return segment.Array;
        }

        return data.ToArray();
    }
}

internal static class InterlockedExtensions
{
    public static void DecrementIfPositive(ref long value)
    {
        while (true)
        {
            var current = Volatile.Read(ref value);
            if (current <= 0)
                return;

            if (Interlocked.CompareExchange(ref value, current - 1, current) == current)
                return;
        }
    }
}

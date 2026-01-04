using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Persistence;

/// <summary>
/// ENTERPRISE ONLY: File-based spill store with scatter/gather distribution.
/// Distributes spill files across 65,536 directories to avoid filesystem bottlenecks.
/// </summary>
public sealed class FileSpillStore : IInMemorySpillStore
{
    private readonly string _rootPath;
    private readonly ISpillEncryptionProvider _encryption;
    private readonly InMemorySpillOptions _options;
    private readonly long _cleanupIntervalTicks;
    private long _lastCleanupTicks;
    private int _cleanupRunning;

    public FileSpillStore(IOptionsMonitor<InMemorySpillOptions> options, ISpillEncryptionProvider encryption)
    {
        _options = options.CurrentValue;
        _rootPath = ResolveRoot(_options.SpillDirectory);
        _encryption = encryption;
        _cleanupIntervalTicks = ToStopwatchTicks(_options.OrphanCleanupInterval);
        _lastCleanupTicks = 0;
    }

    public async ValueTask WriteAsync(Guid spillRef, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var path = GetPath(spillRef);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var encrypted = await _encryption.EncryptAsync(data, ct).ConfigureAwait(false);
        var tempPath = path + ".tmp";

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

        CacheTelemetry.SpillWriteCount.Add(1);
        CacheTelemetry.SpillWriteBytes.Add(data.Length);
        TryScheduleOrphanCleanup();
    }

    public async ValueTask<byte[]?> TryReadAsync(Guid spillRef, CancellationToken ct)
    {
        var path = GetPath(spillRef);
        if (!File.Exists(path))
            return null;

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
        CacheTelemetry.SpillReadCount.Add(1);
        CacheTelemetry.SpillReadBytes.Add(result.Length);
        TryScheduleOrphanCleanup();
        return result;
    }

    public ValueTask DeleteAsync(Guid spillRef, CancellationToken ct)
    {
        try
        {
            var path = GetPath(spillRef);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }

        TryScheduleOrphanCleanup();
        return ValueTask.CompletedTask;
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
                    deletedBytes += info.Length;
                    File.Delete(file);
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
    public ValueTask<byte[]> EncryptAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct)
        => ValueTask.FromResult(ToArray(plaintext));

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

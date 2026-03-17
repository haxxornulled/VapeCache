using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching;

/// <summary>
/// Log-structured spill store that uses preallocated append-only segments and positional async I/O.
/// </summary>
internal sealed class SegmentedLogSpillStore : IInMemorySpillStore, ISpillStoreDiagnostics, IAsyncDisposable
{
    private const string SegmentFilePrefix = "segment-";
    private const string SegmentFileExtension = ".vsl";
    private const string SegmentFilePattern = "segment-*.vsl";
    private const int HeaderSize = 32;
    private const byte RecordVersion = 1;
    private const byte FlagEncrypted = 0x01;
    private const uint RecordMagic = 0x5653504Cu; // "VSPL"
    private static readonly uint[] Crc32Table = CreateCrc32Table();
    private readonly IOptionsMonitor<InMemorySpillOptions> _spillOptionsMonitor;
    private readonly ISpillEncryptionProvider? _encryptionProvider;
    private readonly ILogger<SegmentedLogSpillStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Settings _settings;
    private readonly ConcurrentDictionary<Guid, SpillLocation> _index = new();
    private readonly ConcurrentDictionary<int, SegmentState> _segments = new();
    private readonly Lock _appendGate = new();
    private readonly CancellationTokenSource _maintenanceCts = new();
    private readonly Task _maintenanceTask;
    private readonly string _segmentDirectory;
    private volatile SegmentState _activeSegment;
    private bool _disposed;

    public SegmentedLogSpillStore(
        IOptionsMonitor<InMemorySpillOptions> spillOptionsMonitor,
        ILogger<SegmentedLogSpillStore>? logger = null,
        ISpillEncryptionProvider? encryptionProvider = null)
        : this(
            spillOptionsMonitor,
            logger ?? NullLogger<SegmentedLogSpillStore>.Instance,
            encryptionProvider,
            TimeProvider.System,
            Settings.Default)
    {
    }

    internal SegmentedLogSpillStore(
        IOptionsMonitor<InMemorySpillOptions> spillOptionsMonitor,
        ILogger<SegmentedLogSpillStore> logger,
        ISpillEncryptionProvider? encryptionProvider,
        TimeProvider timeProvider,
        Settings settings)
    {
        _spillOptionsMonitor = spillOptionsMonitor;
        _logger = logger;
        _encryptionProvider = encryptionProvider;
        _timeProvider = timeProvider;
        _settings = settings;
        _segmentDirectory = ResolveSpillDirectory(spillOptionsMonitor.CurrentValue);

        Directory.CreateDirectory(_segmentDirectory);
        var startSegmentId = DiscoverNextSegmentId(_segmentDirectory);
        _activeSegment = CreateSegment(startSegmentId, _settings.DefaultSegmentSizeBytes);
        _segments[_activeSegment.Id] = _activeSegment;
        _maintenanceTask = Task.Run(MaintenanceLoopAsync);
    }

    public async ValueTask WriteAsync(Guid spillRef, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        byte[]? encryptedBuffer = null;
        var flags = (byte)0;
        ReadOnlyMemory<byte> payload = data;
        if (_encryptionProvider is not null)
        {
            encryptedBuffer = await _encryptionProvider.EncryptAsync(data, ct).ConfigureAwait(false);
            payload = encryptedBuffer;
            flags |= FlagEncrypted;
        }

        var crc = ComputeCrc32(payload.Span);
        var write = await AppendRecordAsync(spillRef, payload, flags, crc, ct).ConfigureAwait(false);
        var location = new SpillLocation(
            SegmentId: write.SegmentId,
            Offset: write.Offset,
            PayloadLength: payload.Length,
            Flags: flags,
            Crc32: crc,
            RecordLength: write.RecordLength);
        _index.AddOrUpdate(spillRef, location, (_, _) => location);
        CacheTelemetry.SpillWriteCount.Add(1);
        CacheTelemetry.SpillWriteBytes.Add(payload.Length);
    }

    public async ValueTask<byte[]?> TryReadAsync(Guid spillRef, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();

        if (!_index.TryGetValue(spillRef, out var location))
            return null;
        if (!_segments.TryGetValue(location.SegmentId, out var segment))
            return null;

        segment.AcquireReader();
        try
        {
            var payload = GC.AllocateUninitializedArray<byte>(location.PayloadLength);
            await ReadExactlyAsync(segment.Handle, payload, location.Offset + HeaderSize, ct).ConfigureAwait(false);
            if (ComputeCrc32(payload) != location.Crc32)
                return null;

            CacheTelemetry.SpillReadCount.Add(1);
            CacheTelemetry.SpillReadBytes.Add(payload.Length);

            if ((location.Flags & FlagEncrypted) == 0)
                return payload;
            if (_encryptionProvider is null)
                return null;

            return await _encryptionProvider.DecryptAsync(payload, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        finally
        {
            segment.ReleaseReader();
        }
    }

    public ValueTask DeleteAsync(Guid spillRef, CancellationToken ct)
    {
        ThrowIfDisposed();
        ct.ThrowIfCancellationRequested();
        _index.TryRemove(spillRef, out _);
        return ValueTask.CompletedTask;
    }

    public SpillStoreDiagnosticsSnapshot GetSnapshot()
    {
        var spillConfigured = _spillOptionsMonitor.CurrentValue.EnableSpillToDisk;
        var totalFiles = SafeCountSegmentFiles(_segmentDirectory);
        var activeShards = totalFiles > 0 ? 1 : 0;
        var maxFilesInShard = totalFiles > int.MaxValue ? int.MaxValue : (int)totalFiles;
        var average = activeShards == 0 ? 0d : maxFilesInShard;
        var imbalance = activeShards == 0 ? 0d : 1d;
        var topShards = activeShards == 0
            ? Array.Empty<SpillShardLoad>()
            : [new SpillShardLoad("log", maxFilesInShard)];

        return new SpillStoreDiagnosticsSnapshot(
            SupportsDiskSpill: true,
            SpillToDiskConfigured: spillConfigured,
            Mode: "segmented-log",
            TotalSpillFiles: totalFiles,
            ActiveShards: activeShards,
            MaxFilesInShard: maxFilesInShard,
            AvgFilesPerActiveShard: average,
            ImbalanceRatio: imbalance,
            TopShards: topShards,
            SampledAtUtc: _timeProvider.GetUtcNow());
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _maintenanceCts.Cancel();
        try
        {
            await _maintenanceTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _maintenanceCts.Dispose();
        }

        foreach (var segment in _segments.Values)
            segment.Dispose();
        _segments.Clear();
        _index.Clear();
    }

    internal async ValueTask RunMaintenanceCycleForTestsAsync(CancellationToken ct = default)
        => await RunMaintenanceCycleCoreAsync(ct).ConfigureAwait(false);

    private async Task MaintenanceLoopAsync()
    {
        var token = _maintenanceCts.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RunMaintenanceCycleCoreAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Spill maintenance cycle failed.");
            }

            var delay = _settings.MaintenanceInterval;
            try
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async ValueTask RunMaintenanceCycleCoreAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        await CompactClosedSegmentsAsync(ct).ConfigureAwait(false);
        await CleanupRetiredSegmentsAsync(ct).ConfigureAwait(false);
        await CleanupOrphanSegmentFilesAsync(ct).ConfigureAwait(false);
    }

    private async ValueTask<AppendResult> AppendRecordAsync(
        Guid spillRef,
        ReadOnlyMemory<byte> payload,
        byte flags,
        uint crc32,
        CancellationToken ct)
    {
        var requiredBytes = checked(HeaderSize + payload.Length);
        var reservation = ReserveRecord(requiredBytes);

        Span<byte> headerBuffer = stackalloc byte[HeaderSize];
        WriteHeader(headerBuffer, spillRef, payload.Length, flags, crc32);
        RandomAccess.Write(reservation.Segment.Handle, headerBuffer, reservation.Offset);
        if (!payload.IsEmpty)
            await RandomAccess.WriteAsync(reservation.Segment.Handle, payload, reservation.Offset + HeaderSize, ct).ConfigureAwait(false);

        return new AppendResult(
            SegmentId: reservation.Segment.Id,
            Offset: reservation.Offset,
            RecordLength: requiredBytes);
    }

    private SegmentReservation ReserveRecord(int requiredBytes)
    {
        lock (_appendGate)
        {
            var active = _activeSegment;
            if (!active.TryReserve(requiredBytes, out var offset))
            {
                active.MarkRetired(_timeProvider.GetUtcNow());
                var nextSegmentSize = Math.Max(_settings.DefaultSegmentSizeBytes, AlignToPage(requiredBytes + HeaderSize));
                var nextId = active.Id + 1;
                var rotated = CreateSegment(nextId, nextSegmentSize);
                _segments[rotated.Id] = rotated;
                _activeSegment = rotated;
                active = rotated;
                if (!active.TryReserve(requiredBytes, out offset))
                    throw new InvalidOperationException("Unable to reserve space for spill record.");
            }

            return new SegmentReservation(active, offset);
        }
    }

    private async ValueTask CompactClosedSegmentsAsync(CancellationToken ct)
    {
        var activeId = _activeSegment.Id;
        var indexSnapshot = _index.ToArray();
        if (indexSnapshot.Length == 0)
            return;

        var bySegment = indexSnapshot
            .GroupBy(static x => x.Value.SegmentId)
            .ToDictionary(static g => g.Key, static g => g.ToArray());
        var closedSegments = _segments.Values
            .Where(s => s.Id != activeId)
            .OrderBy(s => s.Id)
            .ToArray();

        foreach (var segment in closedSegments)
        {
            ct.ThrowIfCancellationRequested();
            if (!bySegment.TryGetValue(segment.Id, out var liveEntries) || liveEntries.Length == 0)
            {
                segment.MarkRetired(_timeProvider.GetUtcNow());
                continue;
            }

            var liveBytes = liveEntries.Sum(static e => (long)e.Value.RecordLength);
            var deadBytes = Math.Max(0L, segment.BytesWritten - liveBytes);
            if (segment.BytesWritten <= 0)
            {
                segment.MarkRetired(_timeProvider.GetUtcNow());
                continue;
            }

            var deadRatio = deadBytes / (double)segment.BytesWritten;
            if (deadRatio < _settings.CompactWhenDeadRatioAtLeast)
                continue;

            var moved = 0;
            foreach (var entry in liveEntries)
            {
                if (moved >= _settings.MaxCompactionMovesPerCycle)
                    break;
                if (!_index.TryGetValue(entry.Key, out var current) || current != entry.Value)
                    continue;

                var payload = await TryReadPayloadRawAsync(entry.Value, ct).ConfigureAwait(false);
                if (payload is null)
                    continue;

                var append = await AppendRecordAsync(entry.Key, payload, entry.Value.Flags, entry.Value.Crc32, ct).ConfigureAwait(false);
                var relocated = entry.Value with
                {
                    SegmentId = append.SegmentId,
                    Offset = append.Offset,
                    RecordLength = append.RecordLength,
                    PayloadLength = payload.Length
                };
                _index.TryUpdate(entry.Key, relocated, entry.Value);
                moved++;
            }

            if (!_index.Values.Any(v => v.SegmentId == segment.Id))
                segment.MarkRetired(_timeProvider.GetUtcNow());
        }
    }

    private async ValueTask CleanupRetiredSegmentsAsync(CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var activeId = _activeSegment.Id;
        var segments = _segments.Values.Where(s => s.Id != activeId).ToArray();
        foreach (var segment in segments)
        {
            ct.ThrowIfCancellationRequested();
            if (!segment.TryGetRetiredAtUtc(out var retiredAtUtc))
                continue;
            if (now - retiredAtUtc < _settings.DeleteRetiredSegmentAfter)
                continue;
            if (_index.Values.Any(v => v.SegmentId == segment.Id))
                continue;
            if (segment.ActiveReaders > 0)
                continue;

            if (_segments.TryRemove(segment.Id, out var removed))
            {
                try
                {
                    removed.Dispose();
                    File.Delete(removed.Path);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed deleting retired spill segment {SegmentPath}", removed.Path);
                }
            }
        }
    }

    private async ValueTask CleanupOrphanSegmentFilesAsync(CancellationToken ct)
    {
        var options = _spillOptionsMonitor.CurrentValue;
        if (!options.EnableOrphanCleanup || options.OrphanMaxAge <= TimeSpan.Zero)
            return;

        var cutoff = _timeProvider.GetUtcNow().Subtract(options.OrphanMaxAge);
        var livePaths = _segments.Values
            .Select(static s => s.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_segmentDirectory))
            return;

        foreach (var path in Directory.EnumerateFiles(_segmentDirectory, SegmentFilePattern, SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            if (livePaths.Contains(path))
                continue;

            var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            if (lastWrite > cutoff)
                continue;

            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed deleting orphan spill file {Path}", path);
            }
        }

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private async ValueTask<byte[]?> TryReadPayloadRawAsync(SpillLocation location, CancellationToken ct)
    {
        if (!_segments.TryGetValue(location.SegmentId, out var segment))
            return null;

        segment.AcquireReader();
        try
        {
            var payload = GC.AllocateUninitializedArray<byte>(location.PayloadLength);
            await ReadExactlyAsync(segment.Handle, payload, location.Offset + HeaderSize, ct).ConfigureAwait(false);
            if (ComputeCrc32(payload) != location.Crc32)
                return null;
            return payload;
        }
        catch
        {
            return null;
        }
        finally
        {
            segment.ReleaseReader();
        }
    }

    private static async ValueTask ReadExactlyAsync(
        SafeFileHandle handle,
        Memory<byte> destination,
        long fileOffset,
        CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var read = await RandomAccess.ReadAsync(
                handle,
                destination.Slice(totalRead),
                fileOffset + totalRead,
                ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of segment while reading spill payload.");
            totalRead += read;
        }
    }

    private SegmentState CreateSegment(int id, long capacityBytes)
    {
        var path = Path.Combine(_segmentDirectory, $"{SegmentFilePrefix}{id:D8}{SegmentFileExtension}");
        var handle = File.OpenHandle(
            path,
            mode: FileMode.CreateNew,
            access: FileAccess.ReadWrite,
            share: FileShare.Read,
            options: FileOptions.Asynchronous | FileOptions.RandomAccess,
            preallocationSize: capacityBytes);
        return new SegmentState(id, path, capacityBytes, handle);
    }

    private static int DiscoverNextSegmentId(string segmentDirectory)
    {
        if (!Directory.Exists(segmentDirectory))
            return 1;

        var max = 0;
        foreach (var path in Directory.EnumerateFiles(segmentDirectory, SegmentFilePattern, SearchOption.TopDirectoryOnly))
        {
            var file = Path.GetFileNameWithoutExtension(path);
            if (!file.StartsWith(SegmentFilePrefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var token = file.Substring(SegmentFilePrefix.Length);
            if (int.TryParse(token, out var parsed))
                max = Math.Max(max, parsed);
        }

        return max + 1;
    }

    private static long SafeCountSegmentFiles(string segmentDirectory)
    {
        if (!Directory.Exists(segmentDirectory))
            return 0;

        try
        {
            return Directory.EnumerateFiles(segmentDirectory, SegmentFilePattern, SearchOption.TopDirectoryOnly).LongCount();
        }
        catch
        {
            return 0;
        }
    }

    private static string ResolveSpillDirectory(InMemorySpillOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.SpillDirectory))
            return Environment.ExpandEnvironmentVariables(options.SpillDirectory);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "VapeCache", "spill", "segments");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long AlignToPage(long bytes)
    {
        const int page = 4096;
        return ((bytes + page - 1) / page) * page;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHeader(Span<byte> destination, Guid spillRef, int payloadLength, byte flags, uint crc32)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(0, 4), RecordMagic);
        destination[4] = RecordVersion;
        destination[5] = flags;
        destination[6] = 0;
        destination[7] = 0;
        spillRef.TryWriteBytes(destination.Slice(8, 16));
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(24, 4), payloadLength);
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(28, 4), crc32);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = 0; i < data.Length; i++)
        {
            var idx = (crc ^ data[i]) & 0xFF;
            crc = (crc >> 8) ^ Crc32Table[idx];
        }

        return ~crc;
    }

    private static uint[] CreateCrc32Table()
    {
        const uint polynomial = 0xEDB88320u;
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var j = 0; j < 8; j++)
            {
                if ((value & 1) != 0)
                    value = (value >> 1) ^ polynomial;
                else
                    value >>= 1;
            }

            table[i] = value;
        }

        return table;
    }

    private readonly record struct AppendResult(
        int SegmentId,
        long Offset,
        int RecordLength);

    private readonly record struct SegmentReservation(
        SegmentState Segment,
        long Offset);

    private readonly record struct SpillLocation(
        int SegmentId,
        long Offset,
        int PayloadLength,
        byte Flags,
        uint Crc32,
        int RecordLength);

    internal sealed record Settings(
        long DefaultSegmentSizeBytes,
        TimeSpan MaintenanceInterval,
        double CompactWhenDeadRatioAtLeast,
        TimeSpan DeleteRetiredSegmentAfter,
        int MaxCompactionMovesPerCycle)
    {
        public static readonly Settings Default = new(
            DefaultSegmentSizeBytes: 128L * 1024L * 1024L,
            MaintenanceInterval: TimeSpan.FromMinutes(1),
            CompactWhenDeadRatioAtLeast: 0.35d,
            DeleteRetiredSegmentAfter: TimeSpan.FromSeconds(30),
            MaxCompactionMovesPerCycle: 4096);
    }

    private sealed class SegmentState : IDisposable
    {
        private long _nextOffset;
        private long _bytesWritten;
        private long _retiredAtUnixTimeMilliseconds;
        private int _activeReaders;

        public SegmentState(int id, string path, long capacityBytes, SafeFileHandle handle)
        {
            Id = id;
            Path = path;
            CapacityBytes = capacityBytes;
            Handle = handle;
        }

        public int Id { get; }
        public string Path { get; }
        public long CapacityBytes { get; }
        public SafeFileHandle Handle { get; }
        public int ActiveReaders => Volatile.Read(ref _activeReaders);
        public long BytesWritten => Volatile.Read(ref _bytesWritten);

        public bool TryReserve(int bytes, out long offset)
        {
            var proposed = _nextOffset + bytes;
            if (proposed > CapacityBytes)
            {
                offset = 0;
                return false;
            }

            offset = _nextOffset;
            _nextOffset = proposed;
            _bytesWritten = Math.Max(_bytesWritten, proposed);
            return true;
        }

        public void AcquireReader() => Interlocked.Increment(ref _activeReaders);
        public void ReleaseReader() => Interlocked.Decrement(ref _activeReaders);

        public void MarkRetired(DateTimeOffset utc)
        {
            var marker = utc.ToUnixTimeMilliseconds();
            Interlocked.CompareExchange(ref _retiredAtUnixTimeMilliseconds, marker, 0);
        }

        public bool TryGetRetiredAtUtc(out DateTimeOffset retiredAtUtc)
        {
            var marker = Interlocked.Read(ref _retiredAtUnixTimeMilliseconds);
            if (marker <= 0)
            {
                retiredAtUtc = default;
                return false;
            }

            retiredAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(marker);
            return true;
        }

        public void Dispose()
        {
            if (!Handle.IsClosed)
                Handle.Dispose();
        }
    }
}

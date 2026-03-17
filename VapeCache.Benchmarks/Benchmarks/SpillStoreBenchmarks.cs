using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Extensions.Logging.Abstractions;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Benchmarks.Benchmarks;

[Config(typeof(EnterpriseBenchmarkConfig))]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class SpillStoreBenchmarks
{
    private static readonly int[] FullPayloadBytes = [4 * 1024, 64 * 1024, 256 * 1024];
    private static readonly int[] QuickPayloadBytes = [4 * 1024, 64 * 1024];
    private static readonly int[] FullWorkingSetSizes = [512, 2048];
    private static readonly int[] QuickWorkingSetSizes = [256];
    private static readonly int[] FullSegmentMegabytes = [64, 128];
    private static readonly int[] QuickSegmentMegabytes = [64];

    public IEnumerable<int> PayloadBytesValues =>
        BenchmarkRedisConfig.ResolveIntParams("VAPECACHE_BENCH_SPILL_PAYLOADS", FullPayloadBytes, QuickPayloadBytes);

    public IEnumerable<int> WorkingSetValues =>
        BenchmarkRedisConfig.ResolveIntParams("VAPECACHE_BENCH_SPILL_WORKING_SET", FullWorkingSetSizes, QuickWorkingSetSizes);

    public IEnumerable<int> SegmentSizeMegabytesValues =>
        BenchmarkRedisConfig.ResolveIntParams("VAPECACHE_BENCH_SPILL_SEGMENT_MB", FullSegmentMegabytes, QuickSegmentMegabytes);

    [ParamsSource(nameof(PayloadBytesValues))]
    public int PayloadBytes { get; set; }

    [ParamsSource(nameof(WorkingSetValues))]
    public int WorkingSet { get; set; }

    [ParamsSource(nameof(SegmentSizeMegabytesValues))]
    public int SegmentSizeMegabytes { get; set; }

    private SegmentedLogSpillStore _segmented = null!;
    private ScatterFileSpillStore _scatter = null!;
    private byte[] _payload = null!;
    private Guid[] _writeRefs = null!;
    private Guid[] _readRefs = null!;
    private Guid[] _cycleRefs = null!;
    private int _writeCursor;
    private int _readCursor;
    private int _cycleCursor;
    private string _rootDirectory = string.Empty;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "vapecache-benchmarks",
            "spill",
            $"{PayloadBytes}-{WorkingSet}-{SegmentSizeMegabytes}-{Guid.NewGuid():N}");
        var segmentedDirectory = Path.Combine(_rootDirectory, "segmented");
        var scatterDirectory = Path.Combine(_rootDirectory, "scatter");
        Directory.CreateDirectory(segmentedDirectory);
        Directory.CreateDirectory(scatterDirectory);

        _payload = GC.AllocateUninitializedArray<byte>(PayloadBytes);
        BenchmarkRedisConfig.FillPayload(_payload, seed: 11_000 + PayloadBytes + WorkingSet + SegmentSizeMegabytes);

        _writeRefs = CreateGuidRing(WorkingSet);
        _readRefs = CreateGuidRing(WorkingSet);
        _cycleRefs = CreateGuidRing(WorkingSet);

        _segmented = new SegmentedLogSpillStore(
            new BenchmarkOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
            {
                EnableSpillToDisk = true,
                SpillDirectory = segmentedDirectory,
                EnableOrphanCleanup = false
            }),
            NullLogger<SegmentedLogSpillStore>.Instance,
            encryptionProvider: null,
            TimeProvider.System,
            new SegmentedLogSpillStore.Settings(
                DefaultSegmentSizeBytes: SegmentSizeMegabytes * 1024L * 1024L,
                MaintenanceInterval: TimeSpan.FromMinutes(5),
                CompactWhenDeadRatioAtLeast: 0.30d,
                DeleteRetiredSegmentAfter: TimeSpan.FromSeconds(10),
                MaxCompactionMovesPerCycle: 8192));

        _scatter = new ScatterFileSpillStore(scatterDirectory);

        await SeedStoreAsync(_segmented, _writeRefs).ConfigureAwait(false);
        await SeedStoreAsync(_segmented, _readRefs).ConfigureAwait(false);
        await SeedStoreAsync(_scatter, _writeRefs).ConfigureAwait(false);
        await SeedStoreAsync(_scatter, _readRefs).ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_segmented is not null)
            await _segmented.DisposeAsync().ConfigureAwait(false);

        if (_scatter is not null)
            await _scatter.DisposeAsync().ConfigureAwait(false);

        try
        {
            if (Directory.Exists(_rootDirectory))
                Directory.Delete(_rootDirectory, recursive: true);
        }
        catch
        {
            // Best-effort benchmark cleanup.
        }
    }

    [Benchmark(Baseline = true, Description = "Scatter spill write (ring update)")]
    [BenchmarkCategory("Spill", "Write")]
    public ValueTask Scatter_WriteRingAsync()
        => _scatter.WriteAsync(_writeRefs[NextIndex(ref _writeCursor, _writeRefs.Length)], _payload, CancellationToken.None);

    [Benchmark(Description = "Segmented spill write (ring update)")]
    [BenchmarkCategory("Spill", "Write")]
    public ValueTask Segmented_WriteRingAsync()
        => _segmented.WriteAsync(_writeRefs[NextIndex(ref _writeCursor, _writeRefs.Length)], _payload, CancellationToken.None);

    [Benchmark(Baseline = true, Description = "Scatter spill read hit")]
    [BenchmarkCategory("Spill", "Read")]
    public async Task Scatter_ReadHitAsync()
    {
        var payload = await _scatter.TryReadAsync(_readRefs[NextIndex(ref _readCursor, _readRefs.Length)], CancellationToken.None).ConfigureAwait(false);
        ValidatePayload(payload);
    }

    [Benchmark(Description = "Segmented spill read hit")]
    [BenchmarkCategory("Spill", "Read")]
    public async Task Segmented_ReadHitAsync()
    {
        var payload = await _segmented.TryReadAsync(_readRefs[NextIndex(ref _readCursor, _readRefs.Length)], CancellationToken.None).ConfigureAwait(false);
        ValidatePayload(payload);
    }

    [Benchmark(Baseline = true, Description = "Scatter spill write->read->delete")]
    [BenchmarkCategory("Spill", "Cycle")]
    public async Task Scatter_WriteReadDeleteAsync()
    {
        var key = _cycleRefs[NextIndex(ref _cycleCursor, _cycleRefs.Length)];
        await _scatter.WriteAsync(key, _payload, CancellationToken.None).ConfigureAwait(false);
        var payload = await _scatter.TryReadAsync(key, CancellationToken.None).ConfigureAwait(false);
        ValidatePayload(payload);
        await _scatter.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
    }

    [Benchmark(Description = "Segmented spill write->read->delete")]
    [BenchmarkCategory("Spill", "Cycle")]
    public async Task Segmented_WriteReadDeleteAsync()
    {
        var key = _cycleRefs[NextIndex(ref _cycleCursor, _cycleRefs.Length)];
        await _segmented.WriteAsync(key, _payload, CancellationToken.None).ConfigureAwait(false);
        var payload = await _segmented.TryReadAsync(key, CancellationToken.None).ConfigureAwait(false);
        ValidatePayload(payload);
        await _segmented.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
    }

    [Benchmark(Description = "Segmented spill maintenance cycle")]
    [BenchmarkCategory("Spill", "Maintenance")]
    public async Task Segmented_MaintenanceCycleAsync()
    {
        // Add churn so maintenance has realistic dead-byte pressure.
        for (var i = 0; i < 64; i++)
        {
            var key = _writeRefs[NextIndex(ref _writeCursor, _writeRefs.Length)];
            await _segmented.WriteAsync(key, _payload, CancellationToken.None).ConfigureAwait(false);
        }

        await _segmented.RunMaintenanceCycleForTestsAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task SeedStoreAsync(IInMemorySpillStore store, IReadOnlyList<Guid> refs)
    {
        for (var i = 0; i < refs.Count; i++)
            await store.WriteAsync(refs[i], _payload, CancellationToken.None).ConfigureAwait(false);
    }

    private void ValidatePayload(byte[]? payload)
    {
        if (payload is null || payload.Length != _payload.Length)
            throw new InvalidOperationException("Spill payload validation failed.");
    }

    private static Guid[] CreateGuidRing(int count)
    {
        var arr = new Guid[count];
        for (var i = 0; i < arr.Length; i++)
            arr[i] = Guid.NewGuid();
        return arr;
    }

    private static int NextIndex(ref int cursor, int length)
        => (Interlocked.Increment(ref cursor) & int.MaxValue) % length;

    private sealed class ScatterFileSpillStore(string rootDirectory) : IInMemorySpillStore, IAsyncDisposable
    {
        private readonly string _rootDirectory = rootDirectory;

        public ValueTask WriteAsync(Guid spillRef, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            var path = ResolvePath(spillRef);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new ValueTask(File.WriteAllBytesAsync(path, data.ToArray(), ct));
        }

        public async ValueTask<byte[]?> TryReadAsync(Guid spillRef, CancellationToken ct)
        {
            var path = ResolvePath(spillRef);
            if (!File.Exists(path))
                return null;
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }

        public ValueTask DeleteAsync(Guid spillRef, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = ResolvePath(spillRef);
            if (File.Exists(path))
                File.Delete(path);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private string ResolvePath(Guid spillRef)
        {
            var hex = spillRef.ToString("N");
            var shardA = hex.Substring(0, 2);
            var shardB = hex.Substring(2, 2);
            return Path.Combine(_rootDirectory, shardA, shardB, $"{hex}.bin");
        }
    }
}

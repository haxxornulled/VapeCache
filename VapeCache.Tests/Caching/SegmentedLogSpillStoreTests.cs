using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;
using VapeCache.Tests.Infrastructure;

namespace VapeCache.Tests.Caching;

public sealed class SegmentedLogSpillStoreTests : IAsyncDisposable
{
    private readonly string _spillDir = Path.Combine(Path.GetTempPath(), "vapecache-spill-tests", Guid.NewGuid().ToString("N"));

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_spillDir))
                Directory.Delete(_spillDir, recursive: true);
        }
        catch
        {
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task WriteReadDelete_RoundTripsPayload()
    {
        await using var sut = CreateStore();
        var spillRef = Guid.NewGuid();
        var payload = CreatePayload(8 * 1024);

        await sut.WriteAsync(spillRef, payload, CancellationToken.None);
        var fetched = await sut.TryReadAsync(spillRef, CancellationToken.None);
        await sut.DeleteAsync(spillRef, CancellationToken.None);
        var missing = await sut.TryReadAsync(spillRef, CancellationToken.None);

        Assert.NotNull(fetched);
        Assert.Equal(payload, fetched);
        Assert.Null(missing);
    }

    [Fact]
    public async Task MaintenanceCycle_DeletesDeadClosedSegments()
    {
        await using var sut = CreateStore(new SegmentedLogSpillStore.Settings(
            DefaultSegmentSizeBytes: 512,
            MaintenanceInterval: TimeSpan.FromHours(1),
            CompactWhenDeadRatioAtLeast: 0.30,
            DeleteRetiredSegmentAfter: TimeSpan.Zero,
            MaxCompactionMovesPerCycle: 64));

        var p = CreatePayload(128);
        var refs = new[]
        {
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid()
        };

        for (var i = 0; i < refs.Length; i++)
            await sut.WriteAsync(refs[i], p, CancellationToken.None);

        await sut.DeleteAsync(refs[0], CancellationToken.None);
        await sut.DeleteAsync(refs[1], CancellationToken.None);
        await sut.DeleteAsync(refs[2], CancellationToken.None);
        await sut.RunMaintenanceCycleForTestsAsync(CancellationToken.None);
        await sut.RunMaintenanceCycleForTestsAsync(CancellationToken.None);

        var files = Directory.EnumerateFiles(_spillDir, "segment-*.vsl", SearchOption.TopDirectoryOnly).ToArray();
        Assert.Single(files);
    }

    [Fact]
    public async Task AddVapeCachePersistence_ReplacesNoopSpillStoreRegistration()
    {
        var services = new ServiceCollection();
        services.AddVapecacheCaching();
        services.AddOptions<InMemorySpillOptions>().Configure(o => o.SpillDirectory = _spillDir);
        services.AddVapeCachePersistence();

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IInMemorySpillStore>();
        var diagnostics = provider.GetRequiredService<ISpillStoreDiagnostics>();

        Assert.IsType<SegmentedLogSpillStore>(store);
        Assert.Same(store, diagnostics);
    }

    private SegmentedLogSpillStore CreateStore(SegmentedLogSpillStore.Settings? settings = null)
    {
        Directory.CreateDirectory(_spillDir);
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            EnableSpillToDisk = true,
            SpillDirectory = _spillDir,
            EnableOrphanCleanup = false
        });
        return new SegmentedLogSpillStore(
            options,
            NullLogger<SegmentedLogSpillStore>.Instance,
            encryptionProvider: null,
            TimeProvider.System,
            settings ?? SegmentedLogSpillStore.Settings.Default);
    }

    private static byte[] CreatePayload(int bytes)
    {
        var payload = new byte[bytes];
        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i & 0xFF);
        return payload;
    }
}

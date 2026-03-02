using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VapeCache.Abstractions.Caching;
using VapeCache.Persistence;
using VapeCache.Tests.Infrastructure;
using Xunit;

namespace VapeCache.Tests.Persistence;

public sealed class OrphanCleanupTests : IDisposable
{
    private readonly string _testRoot;

    public OrphanCleanupTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "vapecache-test-cleanup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [Fact]
    public async Task OrphanCleanup_RemovesOldFiles_BasedOnMaxAge()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = true,
            OrphanMaxAge = TimeSpan.FromSeconds(1),
            OrphanCleanupInterval = TimeSpan.FromMilliseconds(100)
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var spillRef1 = Guid.NewGuid();
        var spillRef2 = Guid.NewGuid();
        var data = new byte[] { 1, 2, 3 };

        // Act - Write first file
        await store.WriteAsync(spillRef1, data, CancellationToken.None);

        // Wait for file to become "old"
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        // Write second file (should be "new")
        await store.WriteAsync(spillRef2, data, CancellationToken.None);

        // Trigger cleanup by doing another operation
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await store.WriteAsync(Guid.NewGuid(), data, CancellationToken.None);

        // Wait for cleanup to complete
        await Task.Delay(TimeSpan.FromSeconds(1));

        // Assert
        var result1 = await store.TryReadAsync(spillRef1, CancellationToken.None);
        var result2 = await store.TryReadAsync(spillRef2, CancellationToken.None);

        Assert.Null(result1); // Old file should be cleaned up
        Assert.NotNull(result2); // New file should still exist
    }

    [Fact]
    public async Task OrphanCleanup_DoesNotRun_WhenDisabled()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false, // Disabled
            OrphanMaxAge = TimeSpan.FromMilliseconds(1),
            OrphanCleanupInterval = TimeSpan.FromMilliseconds(1)
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var spillRef = Guid.NewGuid();
        var data = new byte[] { 1, 2, 3 };

        // Act
        await store.WriteAsync(spillRef, data, CancellationToken.None);

        // Wait well past the "max age"
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // Trigger potential cleanup
        await store.WriteAsync(Guid.NewGuid(), data, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // Assert - File should still exist because cleanup is disabled
        var result = await store.TryReadAsync(spillRef, CancellationToken.None);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task OrphanCleanup_HandlesEmptyDirectory()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = true,
            OrphanMaxAge = TimeSpan.FromSeconds(1),
            OrphanCleanupInterval = TimeSpan.FromMilliseconds(100)
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        // Act - Trigger cleanup on empty directory (should not throw)
        var data = new byte[] { 1 };
        var exception = await Record.ExceptionAsync(async () =>
        {
            await store.WriteAsync(Guid.NewGuid(), data, CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        });

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task OrphanCleanup_PreservesRecentFiles()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = true,
            OrphanMaxAge = TimeSpan.FromHours(1), // Long max age
            OrphanCleanupInterval = TimeSpan.FromMilliseconds(100)
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var spillRef = Guid.NewGuid();
        var data = new byte[] { 1, 2, 3 };

        // Act
        await store.WriteAsync(spillRef, data, CancellationToken.None);

        // Trigger cleanup
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await store.WriteAsync(Guid.NewGuid(), data, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        // Assert - File should still exist because it's within max age
        var result = await store.TryReadAsync(spillRef, CancellationToken.None);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task TryReadAsync_RefreshesTimestamp_AndPreventsLiveFileCleanup()
    {
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = true,
            OrphanMaxAge = TimeSpan.FromSeconds(1),
            OrphanCleanupInterval = TimeSpan.FromMilliseconds(100)
        });
        var store = new FileSpillStore(options, new NoopSpillEncryptionProvider());

        var spillRef = Guid.NewGuid();
        await store.WriteAsync(spillRef, new byte[] { 1, 2, 3 }, CancellationToken.None);

        await Task.Delay(TimeSpan.FromSeconds(1.2));

        var refreshed = await store.TryReadAsync(spillRef, CancellationToken.None);
        Assert.NotNull(refreshed);

        await store.WriteAsync(Guid.NewGuid(), new byte[] { 9 }, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        var result = await store.TryReadAsync(spillRef, CancellationToken.None);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task CleanupInterval_PreventsTooFrequentCleanup()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = true,
            OrphanMaxAge = TimeSpan.FromSeconds(1),
            OrphanCleanupInterval = TimeSpan.FromHours(1) // Long interval
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var spillRef = Guid.NewGuid();
        var data = new byte[] { 1, 2, 3 };

        // Act
        await store.WriteAsync(spillRef, data, CancellationToken.None);

        // Wait past max age
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        // Trigger potential cleanup multiple times
        for (int i = 0; i < 5; i++)
        {
            await store.WriteAsync(Guid.NewGuid(), data, CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        // Assert - File should still exist because cleanup interval prevents frequent runs
        var result = await store.TryReadAsync(spillRef, CancellationToken.None);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ManualCleanup_CanBeTriggered_ByCreatingOldFile()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = true,
            OrphanMaxAge = TimeSpan.FromSeconds(1),
            OrphanCleanupInterval = TimeSpan.FromMilliseconds(100)
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        // Create a file manually with old timestamp
        var spillRef = Guid.NewGuid();
        var name = spillRef.ToString("N");
        var dir = Path.Combine(_testRoot, name.Substring(0, 2), name.Substring(2, 2));
        Directory.CreateDirectory(dir);
        var filePath = Path.Combine(dir, $"{name}.bin");

        await File.WriteAllBytesAsync(filePath, new byte[] { 1, 2, 3 });
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddHours(-2));

        // Act - Trigger cleanup
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await store.WriteAsync(Guid.NewGuid(), new byte[] { 1 }, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(1));

        // Assert - Old file should be cleaned up
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task NoopSpillStore_DoesNothing()
    {
        // Arrange
        var store = new VapeCache.Infrastructure.Caching.NoopSpillStore();
        var spillRef = Guid.NewGuid();
        var data = new byte[] { 1, 2, 3 };

        // Act & Assert - All operations complete without errors but do nothing
        await store.WriteAsync(spillRef, data, CancellationToken.None);

        var result = await store.TryReadAsync(spillRef, CancellationToken.None);
        Assert.Null(result);

        await store.DeleteAsync(spillRef, CancellationToken.None);
    }
}

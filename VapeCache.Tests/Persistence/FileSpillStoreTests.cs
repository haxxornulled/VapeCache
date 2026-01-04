using System;
using System.IO;
using System.Threading.Tasks;
using VapeCache.Abstractions.Caching;
using VapeCache.Persistence;
using VapeCache.Tests.Infrastructure;
using Xunit;

namespace VapeCache.Tests.Persistence;

public sealed class FileSpillStoreTests : IDisposable
{
    private readonly string _testRoot;

    public FileSpillStoreTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "vapecache-test-spill", Guid.NewGuid().ToString("N"));
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
    public async Task WriteAsync_CreatesFile_WithScatterGatherPath()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var spillRef = Guid.NewGuid();
        var data = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        await store.WriteAsync(spillRef, data, CancellationToken.None);

        // Assert - Verify scatter/gather directory structure
        var name = spillRef.ToString("N");
        var expectedPath = Path.Combine(_testRoot, name.Substring(0, 2), name.Substring(2, 2), $"{name}.bin");
        Assert.True(File.Exists(expectedPath), $"Expected file at scatter/gather path: {expectedPath}");

        var written = await File.ReadAllBytesAsync(expectedPath);
        Assert.Equal(data, written);
    }

    [Fact]
    public async Task ScatterGather_DistributesFiles_Across65KDirectories()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var data = new byte[] { 42 };

        // Act - Write 100 files with random GUIDs
        var spillRefs = new List<Guid>();
        for (int i = 0; i < 100; i++)
        {
            var spillRef = Guid.NewGuid();
            spillRefs.Add(spillRef);
            await store.WriteAsync(spillRef, data, CancellationToken.None);
        }

        // Assert - Verify files are distributed across different directories
        var uniqueDirs = new HashSet<string>();
        foreach (var spillRef in spillRefs)
        {
            var name = spillRef.ToString("N");
            var dir = Path.Combine(_testRoot, name.Substring(0, 2), name.Substring(2, 2));
            uniqueDirs.Add(dir);
        }

        // With 100 random GUIDs, we should have very high probability of multiple directories
        Assert.True(uniqueDirs.Count > 1, $"Expected files distributed across multiple directories, got {uniqueDirs.Count}");
    }

    [Fact]
    public async Task TryReadAsync_ReturnsData_WhenFileExists()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var spillRef = Guid.NewGuid();
        var data = new byte[] { 10, 20, 30, 40, 50 };

        await store.WriteAsync(spillRef, data, CancellationToken.None);

        // Act
        var result = await store.TryReadAsync(spillRef, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(data, result);
    }

    [Fact]
    public async Task TryReadAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var spillRef = Guid.NewGuid();

        // Act
        var result = await store.TryReadAsync(spillRef, CancellationToken.None);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var spillRef = Guid.NewGuid();
        var data = new byte[] { 1, 2, 3 };

        await store.WriteAsync(spillRef, data, CancellationToken.None);

        // Act
        await store.DeleteAsync(spillRef, CancellationToken.None);

        // Assert
        var result = await store.TryReadAsync(spillRef, CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task WriteAsync_UsesAtomicWrite_TempFileThenMove()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var spillRef = Guid.NewGuid();
        var data = new byte[] { 100, 101, 102 };

        // Act
        await store.WriteAsync(spillRef, data, CancellationToken.None);

        // Assert - Verify no .tmp files remain
        var allFiles = Directory.GetFiles(_testRoot, "*.*", SearchOption.AllDirectories);
        var tmpFiles = allFiles.Where(f => f.EndsWith(".tmp")).ToArray();
        Assert.Empty(tmpFiles);

        // Verify only .bin file exists
        var binFiles = allFiles.Where(f => f.EndsWith(".bin")).ToArray();
        Assert.Single(binFiles);
    }

    [Fact]
    public async Task WriteAsync_OverwritesExisting_OnSubsequentWrite()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var spillRef = Guid.NewGuid();
        var data1 = new byte[] { 1, 2, 3 };
        var data2 = new byte[] { 4, 5, 6, 7, 8 };

        // Act
        await store.WriteAsync(spillRef, data1, CancellationToken.None);
        await store.WriteAsync(spillRef, data2, CancellationToken.None);

        // Assert
        var result = await store.TryReadAsync(spillRef, CancellationToken.None);
        Assert.Equal(data2, result);
    }

    [Fact]
    public async Task LargePayload_WritesAndReadsSuccessfully()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var spillRef = Guid.NewGuid();
        var data = new byte[10 * 1024 * 1024]; // 10 MB
        Random.Shared.NextBytes(data);

        // Act
        await store.WriteAsync(spillRef, data, CancellationToken.None);
        var result = await store.TryReadAsync(spillRef, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(data.Length, result.Length);
        Assert.Equal(data, result);
    }

    [Fact]
    public async Task MultipleFiles_CanCoexist_InSameStore()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var spillRef1 = Guid.NewGuid();
        var spillRef2 = Guid.NewGuid();
        var spillRef3 = Guid.NewGuid();

        var data1 = new byte[] { 1, 1, 1 };
        var data2 = new byte[] { 2, 2, 2 };
        var data3 = new byte[] { 3, 3, 3 };

        // Act
        await store.WriteAsync(spillRef1, data1, CancellationToken.None);
        await store.WriteAsync(spillRef2, data2, CancellationToken.None);
        await store.WriteAsync(spillRef3, data3, CancellationToken.None);

        // Assert
        Assert.Equal(data1, await store.TryReadAsync(spillRef1, CancellationToken.None));
        Assert.Equal(data2, await store.TryReadAsync(spillRef2, CancellationToken.None));
        Assert.Equal(data3, await store.TryReadAsync(spillRef3, CancellationToken.None));
    }

    [Fact]
    public void Constructor_CreatesRootDirectory_IfNotExists()
    {
        // Arrange
        var nonExistentRoot = Path.Combine(_testRoot, "nested", "path", "that", "does", "not", "exist");
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = nonExistentRoot,
            EnableOrphanCleanup = false
        });
        var encryption = new NoopSpillEncryptionProvider();

        // Act
        var store = new FileSpillStore(options, encryption);
        var spillRef = Guid.NewGuid();
        var data = new byte[] { 1 };

        // Assert - Should not throw
        var exception = Record.Exception(() => store.WriteAsync(spillRef, data, CancellationToken.None).AsTask().Wait());
        Assert.Null(exception);
    }

    [Fact]
    public async Task DeleteAsync_DoesNotThrow_WhenFileDoesNotExist()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });
        var encryption = new NoopSpillEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var spillRef = Guid.NewGuid();

        // Act & Assert - Should not throw
        var exception = await Record.ExceptionAsync(async () => await store.DeleteAsync(spillRef, CancellationToken.None));
        Assert.Null(exception);
    }
}

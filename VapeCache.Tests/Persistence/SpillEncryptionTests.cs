using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using VapeCache.Abstractions.Caching;
using VapeCache.Persistence;
using VapeCache.Tests.Infrastructure;
using Xunit;

namespace VapeCache.Tests.Persistence;

public sealed class SpillEncryptionTests : IDisposable
{
    private readonly string _testRoot;

    public SpillEncryptionTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "vapecache-test-encryption", Guid.NewGuid().ToString("N"));
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
    public async Task NoopEncryption_StoresPlaintext()
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

        // Assert - File should contain plaintext (no encryption)
        var name = spillRef.ToString("N");
        var path = Path.Combine(_testRoot, name.Substring(0, 2), name.Substring(2, 2), $"{name}.bin");
        var fileData = await File.ReadAllBytesAsync(path);
        Assert.Equal(data, fileData);
    }

    [Fact]
    public async Task CustomEncryption_EncryptsAndDecrypts()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });
        var encryption = new TestAesEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var spillRef = Guid.NewGuid();
        var data = new byte[] { 10, 20, 30, 40, 50 };

        // Act
        await store.WriteAsync(spillRef, data, CancellationToken.None);
        var result = await store.TryReadAsync(spillRef, CancellationToken.None);

        // Assert
        Assert.Equal(data, result);

        // Verify file is encrypted (not plaintext)
        var name = spillRef.ToString("N");
        var path = Path.Combine(_testRoot, name.Substring(0, 2), name.Substring(2, 2), $"{name}.bin");
        var fileData = await File.ReadAllBytesAsync(path);
        Assert.NotEqual(data, fileData); // Should be encrypted
    }

    [Fact]
    public async Task EncryptionRoundTrip_PreservesData()
    {
        // Arrange
        var options = new TestOptionsMonitor<InMemorySpillOptions>(new InMemorySpillOptions
        {
            SpillDirectory = _testRoot,
            EnableOrphanCleanup = false
        });
        var encryption = new TestAesEncryptionProvider();
        var store = new FileSpillStore(options, encryption);

        var spillRef = Guid.NewGuid();
        var data = new byte[1024];
        Random.Shared.NextBytes(data);

        // Act
        await store.WriteAsync(spillRef, data, CancellationToken.None);
        var result = await store.TryReadAsync(spillRef, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(data.Length, result.Length);
        Assert.Equal(data, result);
    }

    /// <summary>
    /// Simple AES-256 encryption provider for testing.
    /// Production should use proper key management (Azure Key Vault, HSM, etc.)
    /// </summary>
    private sealed class TestAesEncryptionProvider : ISpillEncryptionProvider
    {
        private readonly byte[] _key;
        private readonly byte[] _iv;

        public TestAesEncryptionProvider()
        {
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.GenerateKey();
            aes.GenerateIV();
            _key = aes.Key;
            _iv = aes.IV;
        }

        public async ValueTask<byte[]> EncryptAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct)
        {
            using var aes = Aes.Create();
            aes.Key = _key;
            aes.IV = _iv;

            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream();
            await using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            {
                await cs.WriteAsync(plaintext, ct);
            }

            return ms.ToArray();
        }

        public async ValueTask<byte[]> DecryptAsync(ReadOnlyMemory<byte> ciphertext, CancellationToken ct)
        {
            using var aes = Aes.Create();
            aes.Key = _key;
            aes.IV = _iv;

            using var decryptor = aes.CreateDecryptor();
            using var ms = new MemoryStream(ciphertext.ToArray());
            await using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var output = new MemoryStream();
            await cs.CopyToAsync(output, ct);

            return output.ToArray();
        }
    }
}

namespace VapeCache.Abstractions.Caching;

public interface ISpillEncryptionProvider
{
    ValueTask<byte[]> EncryptAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct);
    ValueTask<byte[]> DecryptAsync(ReadOnlyMemory<byte> ciphertext, CancellationToken ct);
}

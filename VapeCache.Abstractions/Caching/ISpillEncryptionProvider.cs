namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Defines the spill encryption provider contract.
/// </summary>
public interface ISpillEncryptionProvider
{
    /// <summary>
    /// Executes encrypt async.
    /// </summary>
    ValueTask<byte[]> EncryptAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct);
    /// <summary>
    /// Executes decrypt async.
    /// </summary>
    ValueTask<byte[]> DecryptAsync(ReadOnlyMemory<byte> ciphertext, CancellationToken ct);
}

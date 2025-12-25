using System.Buffers;

namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Defines serialization and deserialization for a specific type.
/// Implementations can be zero-allocation friendly by using IBufferWriter
/// for serialization and ReadOnlySpan for deserialization.
/// </summary>
/// <typeparam name="T">The type this codec handles.</typeparam>
public interface ICacheCodec<T>
{
    /// <summary>
    /// Serializes a value into the provided buffer writer.
    /// Zero-allocation implementations should write directly to the buffer
    /// without intermediate allocations.
    /// </summary>
    void Serialize(IBufferWriter<byte> buffer, T value);

    /// <summary>
    /// Deserializes a value from the provided byte span.
    /// Zero-allocation implementations should parse the span directly
    /// without creating intermediate byte arrays.
    /// </summary>
    T Deserialize(ReadOnlySpan<byte> data);
}

/// <summary>
/// Provides codec instances for specific types.
/// Implementations can use strategy pattern to support multiple serialization
/// formats (JSON, MessagePack, Protobuf, custom binary) with per-type overrides.
/// </summary>
public interface ICacheCodecProvider
{
    /// <summary>
    /// Gets a codec for the specified type.
    /// Providers should cache codec instances internally to avoid repeated allocations.
    /// </summary>
    ICacheCodec<T> Get<T>();

    /// <summary>
    /// Registers a custom codec for a specific type, overriding the default strategy.
    /// Useful for performance-critical types that need custom binary serialization.
    /// </summary>
    void Register<T>(ICacheCodec<T> codec);
}

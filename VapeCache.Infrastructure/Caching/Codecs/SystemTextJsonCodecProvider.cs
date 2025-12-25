using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using VapeCache.Abstractions.Caching;

namespace VapeCache.Infrastructure.Caching.Codecs;

/// <summary>
/// Default codec provider using System.Text.Json for serialization.
/// Provides good out-of-the-box performance with zero configuration required.
///
/// For performance-critical types, register custom codecs using Register&lt;T&gt;()
/// to use MessagePack, Protobuf, or hand-crafted binary serialization.
/// </summary>
public sealed class SystemTextJsonCodecProvider : ICacheCodecProvider
{
    private readonly JsonSerializerOptions _options;
    private readonly ConcurrentDictionary<Type, object> _customCodecs = new();
    private readonly ConcurrentDictionary<Type, object> _codecCache = new();

    public SystemTextJsonCodecProvider(JsonSerializerOptions? options = null)
    {
        _options = options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
    }

    public ICacheCodec<T> Get<T>()
    {
        // Check for custom codec first
        if (_customCodecs.TryGetValue(typeof(T), out var customCodec))
            return (ICacheCodec<T>)customCodec;

        // Cache codec instances to avoid repeated allocations
        if (_codecCache.TryGetValue(typeof(T), out var cachedCodec))
            return (ICacheCodec<T>)cachedCodec;

        var codec = new SystemTextJsonCodec<T>(_options);
        _codecCache.TryAdd(typeof(T), codec);
        return codec;
    }

    public void Register<T>(ICacheCodec<T> codec)
    {
        if (codec == null)
            throw new ArgumentNullException(nameof(codec));

        _customCodecs[typeof(T)] = codec;
    }

    /// <summary>
    /// System.Text.Json codec implementation with zero-allocation serialization
    /// via IBufferWriter and span-based deserialization.
    /// </summary>
    private sealed class SystemTextJsonCodec<T> : ICacheCodec<T>
    {
        private readonly JsonSerializerOptions _options;

        public SystemTextJsonCodec(JsonSerializerOptions options)
        {
            _options = options;
        }

        public void Serialize(IBufferWriter<byte> buffer, T value)
        {
            // JsonSerializer.Serialize with IBufferWriter is zero-allocation
            // when the buffer is large enough (which it usually is from ArrayPool)
            using var writer = new Utf8JsonWriter(buffer);
            JsonSerializer.Serialize(writer, value, _options);
        }

        public T Deserialize(ReadOnlySpan<byte> data)
        {
            // Span-based deserialization avoids byte[] allocation
            var result = JsonSerializer.Deserialize<T>(data, _options);

            if (result == null && default(T) != null)
                throw new InvalidOperationException($"Deserialization returned null for non-nullable type {typeof(T).Name}");

            return result!;
        }
    }
}

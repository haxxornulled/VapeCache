namespace VapeCache.Abstractions.Caching;

/// <summary>
/// Deserializes a value from a span without allocating.
///
/// <para>
/// <see cref="ReadOnlySpan{T}"/> is a ref struct and cannot be used as a generic type argument
/// (for example, <c>Func&lt;ReadOnlySpan&lt;byte&gt;, T&gt;</c> is illegal). A custom delegate avoids that limitation.
/// </para>
/// </summary>
public delegate T SpanDeserializer<out T>(ReadOnlySpan<byte> data);

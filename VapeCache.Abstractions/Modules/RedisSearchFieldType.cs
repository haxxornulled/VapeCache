namespace VapeCache.Abstractions.Modules;

/// <summary>
/// Supported RediSearch field kinds for HASH-backed indexes.
/// </summary>
public enum RedisSearchFieldType
{
    /// <summary>
    /// Full-text field.
    /// </summary>
    Text,

    /// <summary>
    /// Exact-match tag field.
    /// </summary>
    Tag,

    /// <summary>
    /// Numeric range field.
    /// </summary>
    Numeric
}

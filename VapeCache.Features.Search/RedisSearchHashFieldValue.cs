namespace VapeCache.Features.Search;

/// <summary>
/// Single HASH field value emitted by a search document mapper.
/// </summary>
public readonly record struct RedisSearchHashFieldValue(string Field, string? Value);

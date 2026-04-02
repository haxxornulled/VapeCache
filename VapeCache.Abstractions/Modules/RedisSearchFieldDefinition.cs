namespace VapeCache.Abstractions.Modules;

/// <summary>
/// Field definition for a HASH-backed RediSearch index.
/// </summary>
public sealed class RedisSearchFieldDefinition
{
    /// <summary>
    /// Creates a RediSearch field definition.
    /// </summary>
    public RedisSearchFieldDefinition(
        string name,
        RedisSearchFieldType type,
        bool sortable = false,
        string? alias = null,
        double? weight = null,
        string? tagSeparator = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (weight.HasValue && type != RedisSearchFieldType.Text)
            throw new ArgumentException("WEIGHT is only supported for TEXT fields.", nameof(weight));

        if (!string.IsNullOrEmpty(tagSeparator))
        {
            if (type != RedisSearchFieldType.Tag)
                throw new ArgumentException("SEPARATOR is only supported for TAG fields.", nameof(tagSeparator));

            if (tagSeparator.Length != 1)
                throw new ArgumentException("TAG field separators must be a single character.", nameof(tagSeparator));
        }

        if (weight.HasValue && weight.Value <= 0d)
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "WEIGHT must be greater than zero.");

        Name = name;
        Type = type;
        Sortable = sortable;
        Alias = alias;
        Weight = weight;
        TagSeparator = tagSeparator;
    }

    /// <summary>
    /// Field name in the HASH document.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Optional alias exposed through the index schema.
    /// </summary>
    public string? Alias { get; }

    /// <summary>
    /// RediSearch field type.
    /// </summary>
    public RedisSearchFieldType Type { get; }

    /// <summary>
    /// Whether the field is sortable.
    /// </summary>
    public bool Sortable { get; }

    /// <summary>
    /// Optional text weight for TEXT fields.
    /// </summary>
    public double? Weight { get; }

    /// <summary>
    /// Optional tag separator for TAG fields.
    /// </summary>
    public string? TagSeparator { get; }

    /// <summary>
    /// Creates a TEXT field definition.
    /// </summary>
    public static RedisSearchFieldDefinition Text(
        string name,
        bool sortable = false,
        string? alias = null,
        double? weight = null)
        => new(name, RedisSearchFieldType.Text, sortable, alias, weight, tagSeparator: null);

    /// <summary>
    /// Creates a TAG field definition.
    /// </summary>
    public static RedisSearchFieldDefinition Tag(
        string name,
        bool sortable = false,
        string? alias = null,
        string? separator = null)
        => new(name, RedisSearchFieldType.Tag, sortable, alias, weight: null, tagSeparator: separator);

    /// <summary>
    /// Creates a NUMERIC field definition.
    /// </summary>
    public static RedisSearchFieldDefinition Numeric(
        string name,
        bool sortable = false,
        string? alias = null)
        => new(name, RedisSearchFieldType.Numeric, sortable, alias, weight: null, tagSeparator: null);
}

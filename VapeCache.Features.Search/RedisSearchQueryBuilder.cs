using System.Globalization;
using System.Text;

namespace VapeCache.Features.Search;

/// <summary>
/// Builds RediSearch query strings for common enterprise filters.
/// </summary>
public sealed class RedisSearchQueryBuilder
{
    private readonly List<string> _clauses = [];

    /// <summary>
    /// Adds a free-text search clause.
    /// </summary>
    public RedisSearchQueryBuilder MatchText(string value, bool usePrefixMatching = true)
    {
        if (string.IsNullOrWhiteSpace(value))
            return this;

        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return this;

        for (var i = 0; i < tokens.Length; i++)
        {
            var escaped = EscapeToken(tokens[i]);
            _clauses.Add(usePrefixMatching ? $"{escaped}*" : escaped);
        }

        return this;
    }

    /// <summary>
    /// Adds a TAG clause for one or more exact-match values.
    /// </summary>
    public RedisSearchQueryBuilder Tag(string field, params string[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        var normalized = NormalizeValues(values);
        if (normalized.Length == 0)
            return this;

        var joined = string.Join('|', normalized.Select(EscapeTagValue));
        _clauses.Add($"@{field}:{{{joined}}}");
        return this;
    }

    /// <summary>
    /// Adds a numeric range clause.
    /// </summary>
    public RedisSearchQueryBuilder NumericRange(string field, double? min = null, double? max = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        var minToken = min.HasValue
            ? min.Value.ToString("0.################", CultureInfo.InvariantCulture)
            : "-inf";
        var maxToken = max.HasValue
            ? max.Value.ToString("0.################", CultureInfo.InvariantCulture)
            : "+inf";

        _clauses.Add($"@{field}:[{minToken} {maxToken}]");
        return this;
    }

    /// <summary>
    /// Adds a raw clause for advanced queries.
    /// </summary>
    public RedisSearchQueryBuilder Raw(string clause)
    {
        if (string.IsNullOrWhiteSpace(clause))
            return this;

        _clauses.Add(clause.Trim());
        return this;
    }

    /// <summary>
    /// Builds the final query.
    /// </summary>
    public RedisSearchQuery Build(int? offset = null, int? count = null)
    {
        var rawQuery = _clauses.Count == 0
            ? "*"
            : string.Join(' ', _clauses);

        return new RedisSearchQuery(rawQuery, offset, count);
    }

    private static string[] NormalizeValues(string[] values)
    {
        if (values.Length == 0)
            return [];

        var normalized = new List<string>(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i]?.Trim();
            if (!string.IsNullOrEmpty(value))
                normalized.Add(value);
        }

        return normalized.Count == 0 ? [] : [.. normalized];
    }

    private static string EscapeToken(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is '@' or ':' or '-' or '|' or '(' or ')' or '{' or '}' or '[' or ']'
                or '"' or '\'' or '~' or '*' or '?' or '\\' or '!' or ';')
            {
                builder.Append('\\');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static string EscapeTagValue(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is '{' or '}' or '|' or '\\')
                builder.Append('\\');

            builder.Append(c);
        }

        return builder.ToString();
    }
}

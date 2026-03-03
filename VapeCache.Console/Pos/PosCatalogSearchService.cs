using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Abstractions.Modules;

namespace VapeCache.Console.Pos;

internal sealed class PosCatalogSearchService(
    SqlitePosCatalogStore catalogStore,
    IRedisSearchService redisSearch,
    IRedisCommandExecutor redis,
    IOptionsMonitor<PosSearchDemoOptions> optionsMonitor,
    ILogger<PosCatalogSearchService> logger)
{
    private static readonly string[] HashFields = ["sku", "code", "upc", "name", "category", "price", "stock"];
    private static readonly string[] IndexFields = ["sku", "code", "upc", "name", "category"];

    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private int _indexInitialized;

    public async ValueTask InitializeAsync(CancellationToken ct)
    {
        await catalogStore.EnsureInitializedAsync(ct).ConfigureAwait(false);
        await catalogStore.SeedIfEmptyAsync(ct).ConfigureAwait(false);

        if (!await redisSearch.IsAvailableAsync(ct).ConfigureAwait(false))
            return;

        if (Volatile.Read(ref _indexInitialized) == 1)
            return;

        await _indexGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _indexInitialized) == 1)
                return;

            var options = optionsMonitor.CurrentValue;
            var created = await redisSearch.CreateIndexAsync(
                options.RedisIndexName,
                options.RedisKeyPrefix,
                IndexFields,
                ct).ConfigureAwait(false);

            Volatile.Write(ref _indexInitialized, 1);
            logger.LogInformation(
                "POS RediSearch index ready: {Index} (created={Created}) prefix={Prefix}",
                options.RedisIndexName,
                created,
                options.RedisKeyPrefix);
        }
        finally
        {
            _indexGate.Release();
        }
    }

    public async ValueTask<PosSearchResult> SearchAsync(string query, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        await InitializeAsync(ct).ConfigureAwait(false);

        var options = optionsMonitor.CurrentValue;
        var normalized = (query ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return new PosSearchResult(
                Query: normalized,
                Source: PosSearchSource.None,
                SearchModuleAvailable: false,
                SearchDocumentIds: 0,
                Elapsed: started.Elapsed,
                Products: Array.Empty<PosCatalogProduct>());
        }

        var searchAvailable = await redisSearch.IsAvailableAsync(ct).ConfigureAwait(false);
        if (searchAvailable)
        {
            var redisQuery = BuildRedisQuery(normalized);
            var ids = await redisSearch.SearchAsync(
                options.RedisIndexName,
                redisQuery,
                offset: 0,
                count: Math.Clamp(options.TopResults, 1, 100),
                ct).ConfigureAwait(false);

            if (ids.Length > 0)
            {
                var cachedProducts = await LoadCachedProductsAsync(ids, ct).ConfigureAwait(false);
                if (cachedProducts.Count > 0)
                {
                    return new PosSearchResult(
                        Query: normalized,
                        Source: PosSearchSource.Cache,
                        SearchModuleAvailable: true,
                        SearchDocumentIds: ids.Length,
                        Elapsed: started.Elapsed,
                        Products: cachedProducts);
                }
            }
        }

        var dbProducts = await catalogStore.SearchAsync(normalized, options.TopResults, ct).ConfigureAwait(false);
        if (dbProducts.Count > 0 && searchAvailable)
            await BackfillCacheAsync(dbProducts, options.RedisKeyPrefix, ct).ConfigureAwait(false);

        return new PosSearchResult(
            Query: normalized,
            Source: dbProducts.Count > 0 ? PosSearchSource.Database : PosSearchSource.None,
            SearchModuleAvailable: searchAvailable,
            SearchDocumentIds: 0,
            Elapsed: started.Elapsed,
            Products: dbProducts);
    }

    private async ValueTask<List<PosCatalogProduct>> LoadCachedProductsAsync(string[] documentIds, CancellationToken ct)
    {
        var products = new List<PosCatalogProduct>(Math.Min(documentIds.Length, 100));
        var dedupe = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < documentIds.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var key = documentIds[i];
            if (key.Length == 0 || !dedupe.Add(key))
                continue;

            var values = await redis.HMGetAsync(key, HashFields, ct).ConfigureAwait(false);
            if (!TryMapHashToProduct(values, out var product))
                continue;

            products.Add(product);
        }

        return products;
    }

    private async ValueTask BackfillCacheAsync(IReadOnlyList<PosCatalogProduct> products, string keyPrefix, CancellationToken ct)
    {
        for (var i = 0; i < products.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var product = products[i];
            var key = string.Concat(keyPrefix, product.Sku);

            await redis.HSetAsync(key, "sku", Encode(product.Sku), ct).ConfigureAwait(false);
            await redis.HSetAsync(key, "code", Encode(product.Code), ct).ConfigureAwait(false);
            await redis.HSetAsync(key, "upc", Encode(product.Upc), ct).ConfigureAwait(false);
            await redis.HSetAsync(key, "name", Encode(product.Name), ct).ConfigureAwait(false);
            await redis.HSetAsync(key, "category", Encode(product.Category), ct).ConfigureAwait(false);
            await redis.HSetAsync(key, "price", Encode(product.Price.ToString("0.00", CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);
            await redis.HSetAsync(key, "stock", Encode(product.StockQuantity.ToString(CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);
        }
    }

    private static string BuildRedisQuery(string query)
    {
        if (query.StartsWith("code:", StringComparison.OrdinalIgnoreCase))
        {
            var code = query["code:".Length..].Trim();
            if (code.Length == 0)
                return "*";

            var exact = BuildExactFieldQuery("code", code);
            var tokenized = BuildTokenizedFieldQuery("code", code, wildcard: true);
            return tokenized.Length == 0
                ? exact
                : $"({exact})|({tokenized})";
        }

        if (query.StartsWith("upc:", StringComparison.OrdinalIgnoreCase))
        {
            var upc = query["upc:".Length..].Trim();
            if (upc.Length == 0)
                return "*";

            var exact = BuildExactFieldQuery("upc", upc);
            var tokenized = BuildTokenizedFieldQuery("upc", upc, wildcard: false);
            return tokenized.Length == 0
                ? exact
                : $"({exact})|({tokenized})";
        }

        if (query.AsSpan().IndexOfAny(" \t".AsSpan()) >= 0)
        {
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return words.Length == 0
                ? "*"
                : string.Join(' ', words.Select(static word => $"{EscapeToken(word)}*"));
        }

        return $"{EscapeToken(query)}*";
    }

    private static string BuildExactFieldQuery(string field, string value)
        => $"@{field}:\"{EscapeQuoted(value)}\"";

    private static string BuildTokenizedFieldQuery(string field, string value, bool wildcard)
    {
        var tokens = ExtractSearchTokens(value);
        if (tokens.Length == 0)
            return string.Empty;

        static string WithWildcard(string token, bool includeWildcard)
            => includeWildcard ? $"{token}*" : token;

        if (tokens.Length == 1)
            return $"@{field}:{WithWildcard(EscapeToken(tokens[0]), wildcard)}";

        var joined = string.Join(' ', tokens.Select(t => WithWildcard(EscapeToken(t), wildcard)));
        return $"@{field}:({joined})";
    }

    private static string[] ExtractSearchTokens(string value)
    {
        if (value.Length == 0)
            return Array.Empty<string>();

        var tokens = new List<string>(4);
        var current = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens.Count == 0 ? Array.Empty<string>() : [.. tokens];
    }

    private static string EscapeQuoted(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string EscapeToken(string token)
    {
        if (token.Length == 0)
            return token;

        var builder = new StringBuilder(token.Length + 8);
        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];
            if (IsSpecial(c))
                builder.Append('\\');
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static bool IsSpecial(char c)
        => c is '@' or ':' or '-' or '|' or '(' or ')' or '{' or '}' or '[' or ']'
            or '"' or '\'' or '~' or '*' or '?' or '\\' or '!' or ';';

    private static byte[] Encode(string value) => Encoding.UTF8.GetBytes(value);

    private static bool TryMapHashToProduct(byte[]?[] values, out PosCatalogProduct product)
    {
        product = default!;
        if (values.Length < HashFields.Length)
            return false;

        var sku = Decode(values[0]);
        var code = Decode(values[1]);
        var upc = Decode(values[2]);
        var name = Decode(values[3]);
        var category = Decode(values[4]);
        var priceText = Decode(values[5]);
        var stockText = Decode(values[6]);
        if (sku.Length == 0 || code.Length == 0 || upc.Length == 0 || name.Length == 0)
            return false;

        if (!decimal.TryParse(priceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
            price = 0m;
        if (!int.TryParse(stockText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stock))
            stock = 0;

        product = new PosCatalogProduct(sku, code, upc, name, category, price, stock);
        return true;
    }

    private static string Decode(byte[]? bytes)
        => bytes is null ? string.Empty : Encoding.UTF8.GetString(bytes);
}

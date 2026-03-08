using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VapeCache.Console.Pos;

internal sealed class SqlitePosCatalogStore(
    IOptionsMonitor<PosSearchDemoOptions> optionsMonitor,
    ILogger<SqlitePosCatalogStore> logger)
{
    private static readonly PosCatalogProduct[] RequiredProducts =
    [
        new("SKU-000001", "PCL-0001", "012345678901", "No.2 Pencil HB", "Stationery", 0.39m, 250),
        new("SKU-000002", "ERS-0001", "012345678902", "Pink Eraser", "Stationery", 0.49m, 200),
        new("SKU-000003", "NBK-0001", "012345678903", "College Ruled Notebook", "Stationery", 2.99m, 180),
        new("SKU-000004", "TV-0099", "012345678904", "65in Smart TV 4K", "Electronics", 799.99m, 12),
        new("SKU-000005", "MIL-0001", "012345678905", "Whole Milk 1 Gallon", "Grocery", 4.29m, 90)
    ];

    private static readonly string[] CategorySeed = ["Stationery", "Electronics", "Grocery", "Office", "Home", "Accessories"];
    private static readonly string[] AdjectiveSeed = ["Classic", "Premium", "Ultra", "Compact", "Everyday", "Deluxe", "Value"];
    private static readonly string[] NounSeed = ["Pencil", "Pen", "Notebook", "Marker", "Headphones", "Mouse", "Lamp", "Cable", "Mug", "Folder"];

    private readonly SemaphoreSlim _initGate = new(1, 1);
    private int _initialized;
    private string? _connectionString;
    private string? _resolvedPath;

    public async ValueTask EnsureInitializedAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _initialized) == 1)
            return;

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _initialized) == 1)
                return;

            var options = optionsMonitor.CurrentValue;
            _resolvedPath = ResolvePath(options.SqlitePath);
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _resolvedPath,
                Cache = SqliteCacheMode.Shared,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            var directory = Path.GetDirectoryName(_resolvedPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await using var connection = OpenConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;";
                await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS pos_products (
                  sku TEXT PRIMARY KEY,
                  code TEXT NOT NULL UNIQUE,
                  upc TEXT NOT NULL UNIQUE,
                  name TEXT NOT NULL,
                  category TEXT NOT NULL,
                  price REAL NOT NULL,
                  stock_quantity INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_pos_products_name ON pos_products(name);
                CREATE INDEX IF NOT EXISTS idx_pos_products_category ON pos_products(category);
                CREATE INDEX IF NOT EXISTS idx_pos_products_code ON pos_products(code);
                CREATE INDEX IF NOT EXISTS idx_pos_products_upc ON pos_products(upc);
                """;
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            Volatile.Write(ref _initialized, 1);
            logger.LogInformation("POS SQLite catalog initialized at {Path}", _resolvedPath);
        }
        finally
        {
            _initGate.Release();
        }
    }

    public async ValueTask SeedIfEmptyAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var options = optionsMonitor.CurrentValue;
        if (!options.SeedIfEmpty)
            return;

        await using var connection = OpenConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(1) FROM pos_products";
            var count = Convert.ToInt32(await countCommand.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (count > 0)
                return;
        }

        var desiredCount = Math.Max(RequiredProducts.Length, options.SeedProductCount);
        var seeded = 0;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO pos_products (sku, code, upc, name, category, price, stock_quantity)
            VALUES ($sku, $code, $upc, $name, $category, $price, $stock);
            """;
        var skuParam = insert.Parameters.Add("$sku", SqliteType.Text);
        var codeParam = insert.Parameters.Add("$code", SqliteType.Text);
        var upcParam = insert.Parameters.Add("$upc", SqliteType.Text);
        var nameParam = insert.Parameters.Add("$name", SqliteType.Text);
        var categoryParam = insert.Parameters.Add("$category", SqliteType.Text);
        var priceParam = insert.Parameters.Add("$price", SqliteType.Real);
        var stockParam = insert.Parameters.Add("$stock", SqliteType.Integer);

        foreach (var product in BuildSeedProducts(desiredCount))
        {
            skuParam.Value = product.Sku;
            codeParam.Value = product.Code;
            upcParam.Value = product.Upc;
            nameParam.Value = product.Name;
            categoryParam.Value = product.Category;
            priceParam.Value = product.Price;
            stockParam.Value = product.StockQuantity;
            seeded += await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        logger.LogInformation("POS SQLite catalog seeded with {Count} products.", seeded);
    }

    public async ValueTask<IReadOnlyList<PosCatalogProduct>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return Array.Empty<PosCatalogProduct>();

        var cappedLimit = Math.Clamp(limit, 1, 100);
        var likeTerm = $"%{EscapeLike(trimmed)}%";
        var prefixTerm = $"{EscapeLike(trimmed)}%";

        await using var connection = OpenConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sku, code, upc, name, category, price, stock_quantity
            FROM pos_products
            WHERE code = $exact
               OR upc = $exact
               OR name LIKE $like ESCAPE '\'
               OR category LIKE $like ESCAPE '\'
            ORDER BY
              CASE
                WHEN code = $exact OR upc = $exact THEN 0
                WHEN name LIKE $prefix ESCAPE '\' THEN 1
                ELSE 2
              END,
              name
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$exact", NormalizeQueryValue(trimmed));
        command.Parameters.AddWithValue("$like", likeTerm);
        command.Parameters.AddWithValue("$prefix", prefixTerm);
        command.Parameters.AddWithValue("$limit", cappedLimit);

        var results = new List<PosCatalogProduct>(cappedLimit);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var product = new PosCatalogProduct(
                Sku: reader.GetString(0),
                Code: reader.GetString(1),
                Upc: reader.GetString(2),
                Name: reader.GetString(3),
                Category: reader.GetString(4),
                Price: reader.GetDecimal(5),
                StockQuantity: reader.GetInt32(6));
            results.Add(product);
        }

        return results;
    }

    private SqliteConnection OpenConnection()
    {
        var connectionString = _connectionString ?? throw new InvalidOperationException("POS SQLite store is not initialized.");
        var connection = new SqliteConnection(connectionString)
        {
            DefaultTimeout = 5
        };
        return connection;
    }

    private static IEnumerable<PosCatalogProduct> BuildSeedProducts(int count)
    {
        for (var i = 0; i < RequiredProducts.Length; i++)
            yield return RequiredProducts[i];

        for (var i = RequiredProducts.Length + 1; i <= count; i++)
        {
            var category = CategorySeed[i % CategorySeed.Length];
            var adjective = AdjectiveSeed[i % AdjectiveSeed.Length];
            var noun = NounSeed[i % NounSeed.Length];
            var sku = $"SKU-{i:D6}";
            var code = $"PRD-{i:D6}";
            var upc = $"{400000000000L + i:D12}";
            var price = decimal.Round(0.49m + ((i % 250) * 0.37m), 2);
            var stock = 5 + (i % 400);
            var name = $"{adjective} {noun} {i:D4}";
            yield return new PosCatalogProduct(sku, code, upc, name, category, price, stock);
        }
    }

    private static string NormalizeQueryValue(string value)
    {
        if (value.StartsWith("code:", StringComparison.OrdinalIgnoreCase))
            return value["code:".Length..].Trim();
        if (value.StartsWith("upc:", StringComparison.OrdinalIgnoreCase))
            return value["upc:".Length..].Trim();
        return value;
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string ResolvePath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath));

        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "VapeCache", "pos", "catalog.db");
    }
}

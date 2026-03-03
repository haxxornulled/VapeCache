namespace VapeCache.Console.Pos;

public sealed record PosCatalogProduct(
    string Sku,
    string Code,
    string Upc,
    string Name,
    string Category,
    decimal Price,
    int StockQuantity);

public enum PosSearchSource
{
    None = 0,
    Cache = 1,
    Database = 2
}

public sealed record PosSearchResult(
    string Query,
    PosSearchSource Source,
    bool SearchModuleAvailable,
    int SearchDocumentIds,
    TimeSpan Elapsed,
    IReadOnlyList<PosCatalogProduct> Products);

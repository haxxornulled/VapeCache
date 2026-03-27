namespace VapeCache.Console.Pos;

public sealed record PosCatalogProduct
{
    public PosCatalogProduct(
        string Sku,
        string Code,
        string Upc,
        string Name,
        string Category,
        decimal Price,
        int StockQuantity)
    {
        this.Sku = Sku;
        this.Code = Code;
        this.Upc = Upc;
        this.Name = Name;
        this.Category = Category;
        this.Price = Price;
        this.StockQuantity = StockQuantity;
    }

    public string Sku { get; init; }
    public string Code { get; init; }
    public string Upc { get; init; }
    public string Name { get; init; }
    public string Category { get; init; }
    public decimal Price { get; init; }
    public int StockQuantity { get; init; }
}

public enum PosSearchSource
{
    None = 0,
    Cache = 1,
    Database = 2
}

public sealed record PosSearchResult
{
    public PosSearchResult(
        string Query,
        PosSearchSource Source,
        bool SearchModuleAvailable,
        int SearchDocumentIds,
        TimeSpan Elapsed,
        IReadOnlyList<PosCatalogProduct> Products)
    {
        this.Query = Query;
        this.Source = Source;
        this.SearchModuleAvailable = SearchModuleAvailable;
        this.SearchDocumentIds = SearchDocumentIds;
        this.Elapsed = Elapsed;
        this.Products = Products;
    }

    public string Query { get; init; }
    public PosSearchSource Source { get; init; }
    public bool SearchModuleAvailable { get; init; }
    public int SearchDocumentIds { get; init; }
    public TimeSpan Elapsed { get; init; }
    public IReadOnlyList<PosCatalogProduct> Products { get; init; }
}

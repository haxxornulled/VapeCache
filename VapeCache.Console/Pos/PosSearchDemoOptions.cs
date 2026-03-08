namespace VapeCache.Console.Pos;

public sealed class PosSearchDemoOptions
{
    public bool Enabled { get; init; } = false;
    public bool StopHostOnCompletion { get; init; } = true;
    public string SqlitePath { get; init; } = "%LOCALAPPDATA%\\VapeCache\\pos\\catalog.db";
    public bool SeedIfEmpty { get; init; } = true;
    public int SeedProductCount { get; init; } = 2_000;
    public string RedisIndexName { get; init; } = "idx:pos:catalog";
    public string RedisKeyPrefix { get; init; } = "pos:sku:";
    public int TopResults { get; init; } = 10;
    public string CashierQuery { get; init; } = "pencil";
    public string LookupCode { get; init; } = "PCL-0001";
    public string LookupUpc { get; init; } = "012345678901";
}

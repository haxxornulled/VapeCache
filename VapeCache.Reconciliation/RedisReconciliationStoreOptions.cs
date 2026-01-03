namespace VapeCache.Reconciliation;

public sealed class RedisReconciliationStoreOptions
{
    public bool UseSqlite { get; set; } = true;
    public string? StorePath { get; set; }
    public int BusyTimeoutMs { get; set; } = 1000;
    public bool EnablePragmaOptimizations { get; set; } = true;
    public bool VacuumOnClear { get; set; } = false;
}

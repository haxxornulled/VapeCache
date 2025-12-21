namespace VapeCache.Console.Stress;

public sealed record RedisStressOptions
{
    public bool Enabled { get; init; } = true;
    public string Mode { get; init; } = "pool"; // "pool", "factory", "mux", or "burn"
    public int Workers { get; init; } = 32;
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);

    public string Workload { get; init; } = "ping"; // "ping" or "payload"
    public int PayloadBytes { get; init; } = 1024;
    public int KeySpace { get; init; } = 10_000; // pool/factory modes
    public int VirtualUsers { get; init; } = 25_000; // mux payload mode
    public string KeyPrefix { get; init; } = "vapecache:stress:";
    public int SetPercent { get; init; } = 50; // payload workload: % SET vs GET
    public TimeSpan PayloadTtl { get; init; } = TimeSpan.FromSeconds(30);
    public bool PreloadKeys { get; init; } = true;

    // Leaky bucket pacing (global rate). If TargetRps <= 0, pacing is disabled.
    public double TargetRps { get; init; } = 0;
    public int BurstRequests { get; init; } = 1000;

    public int OperationsPerLease { get; init; } = 1; // pool-mode: number of ops per rent
    public TimeSpan LogEvery { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public int BurnConnectionsTarget { get; init; } = 0; // burn-mode: stop after creating/disconnecting this many connections
    public int BurnLogEvery { get; init; } = 100; // burn-mode progress logging
}

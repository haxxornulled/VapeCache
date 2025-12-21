namespace VapeCache.Console.Hosting;

public sealed record LiveDemoOptions
{
    public bool Enabled { get; init; } = true;
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);
    public string Key { get; init; } = "demo:time";
    public TimeSpan Ttl { get; init; } = TimeSpan.FromSeconds(10);
}


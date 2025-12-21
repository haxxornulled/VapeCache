namespace VapeCache.Abstractions.Connections;

public sealed record RedisMultiplexerOptions
{
    public int Connections { get; init; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int MaxInFlightPerConnection { get; init; } = 4096;
}

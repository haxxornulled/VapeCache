namespace VapeCache.Console.Hosting;

public sealed record WebHostOptions
{
    public bool Enabled { get; init; } = true;
    public string Urls { get; init; } = "http://localhost:5080";
}


namespace VapeCache.Console.Hosting;

public sealed record StartupPreflightOptions
{
    public bool Enabled { get; init; }
    public bool FailFast { get; init; } = true;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    // Number of concurrent connection attempts to validate Redis (useful to catch ACL/maxclients issues early).
    public int Connections { get; init; } = 1;

    // Sends a RESP PING after connect/auth/select to validate round-trip.
    public bool ValidatePing { get; init; } = true;
}


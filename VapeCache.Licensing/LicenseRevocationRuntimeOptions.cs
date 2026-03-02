namespace VapeCache.Licensing;

/// <summary>
/// Runtime options for online license revocation checks.
/// Controlled through environment variables to keep host integration simple.
/// </summary>
public static class LicenseRevocationRuntimeOptions
{
    public const string RevocationEnabledEnvironmentVariable = "VAPECACHE_LICENSE_REVOCATION_ENABLED";
    public const string RevocationEndpointEnvironmentVariable = "VAPECACHE_LICENSE_REVOCATION_ENDPOINT";
    public const string RevocationApiKeyEnvironmentVariable = "VAPECACHE_LICENSE_REVOCATION_API_KEY";
    public const string RevocationFailOpenEnvironmentVariable = "VAPECACHE_LICENSE_REVOCATION_FAIL_OPEN";
    public const string RevocationTimeoutMsEnvironmentVariable = "VAPECACHE_LICENSE_REVOCATION_TIMEOUT_MS";
    public const string RevocationCacheSecondsEnvironmentVariable = "VAPECACHE_LICENSE_REVOCATION_CACHE_SECONDS";

    public static bool ResolveEnabled()
        => ParseBool(Environment.GetEnvironmentVariable(RevocationEnabledEnvironmentVariable), fallback: false);

    public static string? ResolveEndpoint()
    {
        var value = Environment.GetEnvironmentVariable(RevocationEndpointEnvironmentVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static string? ResolveApiKey()
    {
        var value = Environment.GetEnvironmentVariable(RevocationApiKeyEnvironmentVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public static bool ResolveFailOpen()
        => ParseBool(Environment.GetEnvironmentVariable(RevocationFailOpenEnvironmentVariable), fallback: false);

    public static TimeSpan ResolveTimeout()
    {
        var value = Environment.GetEnvironmentVariable(RevocationTimeoutMsEnvironmentVariable);
        if (!int.TryParse(value, out var timeoutMs) || timeoutMs <= 0)
            timeoutMs = 2000;
        return TimeSpan.FromMilliseconds(timeoutMs);
    }

    public static TimeSpan ResolveCacheTtl()
    {
        var value = Environment.GetEnvironmentVariable(RevocationCacheSecondsEnvironmentVariable);
        if (!int.TryParse(value, out var seconds) || seconds < 0)
            seconds = 60;
        return TimeSpan.FromSeconds(seconds);
    }

    private static bool ParseBool(string? value, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            "true" => true,
            "false" => false,
            "TRUE" => true,
            "FALSE" => false,
            "True" => true,
            "False" => false,
            "yes" => true,
            "no" => false,
            "on" => true,
            "off" => false,
            "ON" => true,
            "OFF" => false,
            _ => fallback
        };
    }
}

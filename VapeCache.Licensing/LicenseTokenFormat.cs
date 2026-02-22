using System.Text.Json.Serialization;

namespace VapeCache.Licensing;

internal static class LicenseTokenFormat
{
    internal const string TokenPrefix = "VC2";
    internal const string TokenType = "VC-LIC";
    internal const string Algorithm = "ES256";
}

internal sealed class LicenseTokenHeader
{
    [JsonPropertyName("alg")]
    public string Algorithm { get; init; } = string.Empty;

    [JsonPropertyName("typ")]
    public string TokenType { get; init; } = string.Empty;

    [JsonPropertyName("kid")]
    public string KeyId { get; init; } = string.Empty;
}

internal sealed class LicenseTokenPayload
{
    [JsonPropertyName("org")]
    public string OrganizationId { get; init; } = string.Empty;

    [JsonPropertyName("tier")]
    public string Tier { get; init; } = string.Empty;

    [JsonPropertyName("features")]
    public string[] Features { get; init; } = Array.Empty<string>();

    [JsonPropertyName("iat")]
    public long IssuedAtUnixSeconds { get; init; }

    [JsonPropertyName("nbf")]
    public long NotBeforeUnixSeconds { get; init; }

    [JsonPropertyName("exp")]
    public long ExpiresAtUnixSeconds { get; init; }

    [JsonPropertyName("jti")]
    public string LicenseId { get; init; } = string.Empty;
}

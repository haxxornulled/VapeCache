using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VapeCache.Licensing;

/// <summary>
/// Performs optional online revocation checks against the licensing control-plane.
/// </summary>
public sealed class LicenseRevocationClient
{
    private const int MaxCachedDecisions = 10_000;
    private static readonly HttpClient SharedHttpClient = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, CachedDecision> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Checks whether a validated enterprise license is revoked or kill-switched.
    /// </summary>
    public async ValueTask<LicenseRevocationCheckResult> CheckAsync(LicenseValidationResult validationResult, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(validationResult);

        if (!validationResult.IsValid || validationResult.Tier != LicenseTier.Enterprise)
            return LicenseRevocationCheckResult.Allowed("non-enterprise-or-invalid");

        if (!LicenseRevocationRuntimeOptions.ResolveEnabled())
            return LicenseRevocationCheckResult.Allowed("revocation-disabled");

        var endpoint = LicenseRevocationRuntimeOptions.ResolveEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint))
            return LicenseRevocationCheckResult.Allowed("revocation-endpoint-not-configured");

        if (string.IsNullOrWhiteSpace(validationResult.LicenseId))
            return LicenseRevocationCheckResult.Revoked("license-id-missing");

        var cacheTtl = LicenseRevocationRuntimeOptions.ResolveCacheTtl();
        var cacheKey = BuildCacheKey(validationResult);
        var now = DateTimeOffset.UtcNow;
        if (cacheTtl > TimeSpan.Zero &&
            _cache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresAtUtc > now)
        {
            return cached.Decision;
        }

        var timeout = LicenseRevocationRuntimeOptions.ResolveTimeout();
        var failOpen = LicenseRevocationRuntimeOptions.ResolveFailOpen();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            var uri = BuildStatusUri(endpoint, validationResult);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            var apiKey = LicenseRevocationRuntimeOptions.ResolveApiKey();
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.TryAddWithoutValidation("X-VapeCache-ApiKey", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<RevocationStatusPayload>(stream, JsonOptions, timeoutCts.Token).ConfigureAwait(false);
            if (payload is null)
            {
                return CacheAndReturn(
                    cacheKey,
                    cacheTtl,
                    now,
                    failOpen
                        ? LicenseRevocationCheckResult.Allowed("revocation-payload-empty-fail-open")
                        : LicenseRevocationCheckResult.Revoked("revocation-payload-empty"));
            }

            var decision = payload.Revoked
                ? LicenseRevocationCheckResult.Revoked(string.IsNullOrWhiteSpace(payload.Reason) ? "revoked" : payload.Reason)
                : LicenseRevocationCheckResult.Allowed("active");

            return CacheAndReturn(cacheKey, cacheTtl, now, decision);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && failOpen)
        {
            return CacheAndReturn(cacheKey, cacheTtl, now, LicenseRevocationCheckResult.Allowed("revocation-timeout-fail-open"));
        }
        catch (Exception ex) when (failOpen)
        {
            return CacheAndReturn(cacheKey, cacheTtl, now, LicenseRevocationCheckResult.Allowed($"revocation-error-fail-open:{ex.GetType().Name}"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return CacheAndReturn(cacheKey, cacheTtl, now, LicenseRevocationCheckResult.Revoked("revocation-timeout-fail-closed"));
        }
        catch (Exception ex)
        {
            return CacheAndReturn(cacheKey, cacheTtl, now, LicenseRevocationCheckResult.Revoked($"revocation-error-fail-closed:{ex.GetType().Name}"));
        }
    }

    private static string BuildStatusUri(string endpoint, LicenseValidationResult validationResult)
    {
        var baseUri = endpoint.TrimEnd('/');
        var licenseId = Uri.EscapeDataString(validationResult.LicenseId!);
        var orgId = Uri.EscapeDataString(validationResult.CustomerId ?? string.Empty);
        var keyId = Uri.EscapeDataString(validationResult.KeyId ?? string.Empty);
        return $"{baseUri}/api/v1/revocations/status/{licenseId}?organizationId={orgId}&keyId={keyId}";
    }

    private static string BuildCacheKey(LicenseValidationResult validationResult)
        => $"{validationResult.LicenseId}|{validationResult.CustomerId}|{validationResult.KeyId}";

    private LicenseRevocationCheckResult CacheAndReturn(
        string cacheKey,
        TimeSpan cacheTtl,
        DateTimeOffset now,
        LicenseRevocationCheckResult decision)
    {
        if (cacheTtl > TimeSpan.Zero)
        {
            if (_cache.Count >= MaxCachedDecisions)
                _cache.Clear();
            _cache[cacheKey] = new CachedDecision(decision, now.Add(cacheTtl));
        }

        return decision;
    }

    private sealed record CachedDecision(LicenseRevocationCheckResult Decision, DateTimeOffset ExpiresAtUtc);

    private sealed class RevocationStatusPayload
    {
        public bool Revoked { get; set; }
        public string? Reason { get; set; }
    }
}

using VapeCache.Features.Invalidation;

namespace VapeCache.Tests.Features.Invalidation;

public sealed class CacheInvalidationOptionsTests
{
    [Fact]
    public void ResolveRuntimeSettings_UsesSmallWebsiteDefaults()
    {
        var options = new CacheInvalidationOptions
        {
            Profile = CacheInvalidationProfile.SmallWebsite
        };

        var settings = options.ResolveRuntimeSettings();

        Assert.False(settings.ThrowOnFailure);
        Assert.False(settings.ExecuteTargetsInParallel);
        Assert.False(settings.EvaluatePoliciesInParallel);
        Assert.Equal(1, settings.MaxConcurrency);
    }

    [Fact]
    public void ResolveRuntimeSettings_UsesHighTrafficDefaults()
    {
        var options = new CacheInvalidationOptions
        {
            Profile = CacheInvalidationProfile.HighTrafficSite
        };

        var settings = options.ResolveRuntimeSettings();

        Assert.True(settings.ThrowOnFailure);
        Assert.True(settings.ExecuteTargetsInParallel);
        Assert.True(settings.EvaluatePoliciesInParallel);
        Assert.True(settings.MaxConcurrency >= 4);
    }

    [Fact]
    public void ResolveRuntimeSettings_RespectsExplicitOverrides()
    {
        var options = new CacheInvalidationOptions
        {
            Profile = CacheInvalidationProfile.HighTrafficSite,
            ThrowOnFailure = false,
            ExecuteTargetsInParallel = false,
            EvaluatePoliciesInParallel = false,
            MaxConcurrency = 2
        };

        var settings = options.ResolveRuntimeSettings();

        Assert.False(settings.ThrowOnFailure);
        Assert.False(settings.ExecuteTargetsInParallel);
        Assert.False(settings.EvaluatePoliciesInParallel);
        Assert.Equal(2, settings.MaxConcurrency);
    }

    [Fact]
    public void ResolveRuntimeSettings_ClampsMaxConcurrencyToSafeUpperBound()
    {
        var options = new CacheInvalidationOptions
        {
            Profile = CacheInvalidationProfile.HighTrafficSite,
            MaxConcurrency = int.MaxValue
        };

        var settings = options.ResolveRuntimeSettings();
        var expectedCap = Math.Min(256, Math.Max(4, Environment.ProcessorCount * 8));
        Assert.Equal(expectedCap, settings.MaxConcurrency);
    }
}

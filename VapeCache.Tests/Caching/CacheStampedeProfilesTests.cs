using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Text;
using VapeCache.Abstractions.Caching;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Tests.Caching;

public sealed class CacheStampedeProfilesTests
{
    [Fact]
    public void Create_Balanced_ReturnsExpectedDefaults()
    {
        var options = CacheStampedeProfiles.Create(CacheStampedeProfile.Balanced);

        Assert.True(options.Enabled);
        Assert.Equal(50_000, options.MaxKeys);
        Assert.True(options.RejectSuspiciousKeys);
        Assert.Equal(512, options.MaxKeyLength);
        Assert.Equal(TimeSpan.FromMilliseconds(750), options.LockWaitTimeout);
        Assert.True(options.EnableFailureBackoff);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.FailureBackoff);
    }

    [Fact]
    public void UseCacheStampedeProfile_AppliesStrictProfile()
    {
        var services = new ServiceCollection();
        services.AddOptions<CacheStampedeOptions>()
            .UseCacheStampedeProfile(CacheStampedeProfile.Strict);

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<CacheStampedeOptions>>().Value;

        Assert.Equal(25_000, options.MaxKeys);
        Assert.Equal(256, options.MaxKeyLength);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.LockWaitTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), options.FailureBackoff);
    }

    [Fact]
    public void UseProfileThenBind_AllowsBoundValuesToOverrideProfile()
    {
        var json = """
        {
          "CacheStampede": {
            "MaxKeys": 1234,
            "LockWaitTimeout": "00:00:02",
            "FailureBackoff": "00:00:00.125"
          }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<CacheStampedeOptions>()
            .UseCacheStampedeProfile(CacheStampedeProfile.Strict)
            .Bind(config.GetSection("CacheStampede"));

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<CacheStampedeOptions>>().Value;

        Assert.Equal(1234, options.MaxKeys);
        Assert.Equal(TimeSpan.FromSeconds(2), options.LockWaitTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(125), options.FailureBackoff);
    }

    [Fact]
    public void ConfigureCacheStampede_AllowsFluentOverride()
    {
        var services = new ServiceCollection();
        services.AddOptions<CacheStampedeOptions>()
            .UseCacheStampedeProfile(CacheStampedeProfile.Balanced)
            .ConfigureCacheStampede(static o =>
            {
                o.WithMaxKeys(77_777)
                 .WithLockWaitTimeout(TimeSpan.FromMilliseconds(600))
                 .WithFailureBackoff(TimeSpan.FromMilliseconds(300));
            });

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<CacheStampedeOptions>>().Value;

        Assert.Equal(77_777, options.MaxKeys);
        Assert.Equal(TimeSpan.FromMilliseconds(600), options.LockWaitTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(300), options.FailureBackoff);
    }
}

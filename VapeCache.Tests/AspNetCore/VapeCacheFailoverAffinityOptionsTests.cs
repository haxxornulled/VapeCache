using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VapeCache.Extensions.AspNetCore;

namespace VapeCache.Tests.AspNetCore;

public sealed class VapeCacheFailoverAffinityOptionsTests
{
    [Fact]
    public void AddVapeCacheFailoverAffinityHints_UsesExpectedDefaults()
    {
        var services = new ServiceCollection();
        services.AddVapeCacheFailoverAffinityHints();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<VapeCacheFailoverAffinityOptions>>().CurrentValue;

        Assert.True(options.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(options.NodeId));
        Assert.Equal("X-VapeCache-Node", options.NodeHeaderName);
        Assert.Equal("X-VapeCache-Failover-State", options.StateHeaderName);
        Assert.Equal("VapeCacheAffinity", options.CookieName);
        Assert.Equal(TimeSpan.FromMinutes(20), options.CookieTtl);
        Assert.True(options.SetCookieOnlyWhenFailingOver);
        Assert.True(options.EmitMismatchHeader);
    }

    [Theory]
    [MemberData(nameof(InvalidOptionCases))]
    public void AddVapeCacheFailoverAffinityHints_RejectsInvalidConfiguration(
        Action<VapeCacheFailoverAffinityOptions> configure,
        string expectedMessageToken)
    {
        var services = new ServiceCollection();
        services.AddVapeCacheFailoverAffinityHints(configure);

        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptionsMonitor<VapeCacheFailoverAffinityOptions>>().CurrentValue);

        Assert.Contains(expectedMessageToken, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> InvalidOptionCases()
    {
        yield return [new Action<VapeCacheFailoverAffinityOptions>(o => o.NodeId = " "), "NodeId"];
        yield return [new Action<VapeCacheFailoverAffinityOptions>(o => o.NodeHeaderName = ""), "NodeHeaderName"];
        yield return [new Action<VapeCacheFailoverAffinityOptions>(o => o.StateHeaderName = " "), "StateHeaderName"];
        yield return [new Action<VapeCacheFailoverAffinityOptions>(o => o.CookieName = ""), "CookieName"];
        yield return [new Action<VapeCacheFailoverAffinityOptions>(o => o.CookieTtl = TimeSpan.Zero), "CookieTtl"];
    }
}

using VapeCache.Console.Pos;

namespace VapeCache.Tests.Console;

public sealed class PosSearchLoadHostedServiceTests
{
    [Fact]
    public void ParseRampSteps_uses_fallback_when_input_is_blank()
    {
        var steps = PosSearchLoadHostedService.ParseRampSteps("   ", fallbackTargetSps: 1800);

        Assert.Equal([1800], steps);
    }

    [Fact]
    public void ParseRampSteps_parses_unique_positive_steps_in_order()
    {
        var steps = PosSearchLoadHostedService.ParseRampSteps("1600, 2000;2400 2000 -1 nope 0", fallbackTargetSps: 1800);

        Assert.Equal([1600, 2000, 2400], steps);
    }

    [Fact]
    public void ParseRampSteps_uses_default_fallback_when_target_is_not_set()
    {
        var steps = PosSearchLoadHostedService.ParseRampSteps("bad, values", fallbackTargetSps: 0);

        Assert.Equal([2200], steps);
    }
}

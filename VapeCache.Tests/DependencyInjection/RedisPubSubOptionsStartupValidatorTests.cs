using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;
using VapeCache.Infrastructure.DependencyInjection;

namespace VapeCache.Tests.DependencyInjection;

public sealed class RedisPubSubOptionsStartupValidatorTests
{
    [Fact]
    public void Constructor_ThrowsWhenOptionsMonitorIsNull()
    {
        var validator = new RedisPubSubOptionsValidator();
        Assert.Throws<ArgumentNullException>(() =>
            new RedisPubSubOptionsStartupValidator(
                options: null!,
                validator));
    }

    [Fact]
    public void Constructor_ThrowsWhenValidatorIsNull()
    {
        var options = new StaticOptionsMonitor<RedisPubSubOptions>(new RedisPubSubOptions());
        Assert.Throws<ArgumentNullException>(() =>
            new RedisPubSubOptionsStartupValidator(
                options,
                validator: null!));
    }

    [Fact]
    public void Start_DoesNotThrow_WhenOptionsAreValid()
    {
        var options = new StaticOptionsMonitor<RedisPubSubOptions>(new RedisPubSubOptions());
        var validator = new RedisPubSubOptionsValidator();
        var startupValidator = new RedisPubSubOptionsStartupValidator(options, validator);

        startupValidator.Start();
    }

    [Fact]
    public void Start_ThrowsOptionsValidationException_WhenOptionsAreInvalid()
    {
        var invalidOptions = new RedisPubSubOptions
        {
            DeliveryQueueCapacity = 0,
            ReconnectDelayMin = TimeSpan.FromSeconds(2),
            ReconnectDelayMax = TimeSpan.FromSeconds(1)
        };

        var options = new StaticOptionsMonitor<RedisPubSubOptions>(invalidOptions);
        var validator = new RedisPubSubOptionsValidator();
        var startupValidator = new RedisPubSubOptionsStartupValidator(options, validator);

        var ex = Assert.Throws<OptionsValidationException>(() => startupValidator.Start());
        Assert.Contains(ex.Failures, f => f.Contains("DeliveryQueueCapacity", StringComparison.Ordinal));
        Assert.Contains(ex.Failures, f => f.Contains("ReconnectDelayMax must be >= ReconnectDelayMin", StringComparison.Ordinal));
    }
}

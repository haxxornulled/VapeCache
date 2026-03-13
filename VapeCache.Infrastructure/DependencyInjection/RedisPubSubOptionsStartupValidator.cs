using Autofac;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Infrastructure.DependencyInjection;

internal sealed class RedisPubSubOptionsStartupValidator(
    IOptionsMonitor<RedisPubSubOptions> options,
    RedisPubSubOptionsValidator validator) : IStartable
{
    public void Start()
    {
        var result = validator.Validate(Options.DefaultName, options.CurrentValue);
        if (!result.Failed)
            return;

        throw new OptionsValidationException(Options.DefaultName, typeof(RedisPubSubOptions), result.Failures);
    }
}

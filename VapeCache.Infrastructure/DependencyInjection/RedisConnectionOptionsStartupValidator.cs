using Autofac;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Infrastructure.DependencyInjection;

internal sealed class RedisConnectionOptionsStartupValidator(
    IOptionsMonitor<RedisConnectionOptions> options,
    RedisConnectionOptionsValidator validator) : IStartable
{
    public void Start()
    {
        var result = validator.Validate(Options.DefaultName, options.CurrentValue);
        if (!result.Failed)
            return;

        throw new OptionsValidationException(Options.DefaultName, typeof(RedisConnectionOptions), result.Failures);
    }
}

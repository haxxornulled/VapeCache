using Autofac;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Infrastructure.DependencyInjection;

internal sealed class RedisConnectionOptionsStartupValidator : IStartable
{
    private readonly IOptionsMonitor<RedisConnectionOptions> _options;
    private readonly RedisConnectionOptionsValidator _validator;

    public RedisConnectionOptionsStartupValidator(
        IOptionsMonitor<RedisConnectionOptions> options,
        RedisConnectionOptionsValidator validator)
    {
        _options = options;
        _validator = validator;
    }

    public void Start()
    {
        var result = _validator.Validate(Options.DefaultName, _options.CurrentValue);
        if (!result.Failed)
            return;

        throw new OptionsValidationException(Options.DefaultName, typeof(RedisConnectionOptions), result.Failures);
    }
}

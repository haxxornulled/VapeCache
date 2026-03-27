using Autofac;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Infrastructure.DependencyInjection;

internal sealed class RedisMultiplexerOptionsStartupValidator : IStartable
{
    private readonly IOptionsMonitor<RedisMultiplexerOptions> _options;
    private readonly RedisMultiplexerOptionsValidator _validator;

    public RedisMultiplexerOptionsStartupValidator(
        IOptionsMonitor<RedisMultiplexerOptions> options,
        RedisMultiplexerOptionsValidator validator)
    {
        _options = options;
        _validator = validator;
    }

    public void Start()
    {
        var result = _validator.Validate(Options.DefaultName, _options.CurrentValue);
        if (!result.Failed)
            return;

        throw new OptionsValidationException(Options.DefaultName, typeof(RedisMultiplexerOptions), result.Failures);
    }
}

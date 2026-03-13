using Autofac;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;
using VapeCache.Guards;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Infrastructure.DependencyInjection;

internal sealed class RedisPubSubOptionsStartupValidator : IStartable
{
    private readonly IOptionsMonitor<RedisPubSubOptions> options;
    private readonly RedisPubSubOptionsValidator validator;

    public RedisPubSubOptionsStartupValidator(
        IOptionsMonitor<RedisPubSubOptions> options,
        RedisPubSubOptionsValidator validator)
    {
        this.options = ParanoiaThrowGuard.Against.NotNull(options);
        this.validator = ParanoiaThrowGuard.Against.NotNull(validator);
    }

    public void Start()
    {
        var result = validator.Validate(Options.DefaultName, options.CurrentValue);
        if (!result.Failed)
            return;

        throw new OptionsValidationException(Options.DefaultName, typeof(RedisPubSubOptions), result.Failures);
    }
}

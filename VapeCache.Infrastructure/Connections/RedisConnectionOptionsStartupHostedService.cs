using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisConnectionOptionsStartupHostedService : IHostedService
{
    private readonly IOptionsMonitor<RedisConnectionOptions> _options;
    private readonly RedisConnectionOptionsValidator _validator;

    public RedisConnectionOptionsStartupHostedService(
        IOptionsMonitor<RedisConnectionOptions> options,
        RedisConnectionOptionsValidator validator)
    {
        _options = options;
        _validator = validator;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var result = _validator.Validate(Options.DefaultName, _options.CurrentValue);
        if (result.Failed)
            throw new OptionsValidationException(Options.DefaultName, typeof(RedisConnectionOptions), result.Failures);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

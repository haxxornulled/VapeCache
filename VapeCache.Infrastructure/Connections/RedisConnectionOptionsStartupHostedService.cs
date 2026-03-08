using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisConnectionOptionsStartupHostedService(
    IOptionsMonitor<RedisConnectionOptions> options,
    RedisConnectionOptionsValidator validator) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var result = validator.Validate(Options.DefaultName, options.CurrentValue);
        if (result.Failed)
            throw new OptionsValidationException(Options.DefaultName, typeof(RedisConnectionOptions), result.Failures);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisMultiplexerOptionsStartupHostedService(
    IOptionsMonitor<RedisMultiplexerOptions> options,
    RedisMultiplexerOptionsValidator validator) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var result = validator.Validate(Options.DefaultName, options.CurrentValue);
        if (result.Failed)
            throw new OptionsValidationException(Options.DefaultName, typeof(RedisMultiplexerOptions), result.Failures);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

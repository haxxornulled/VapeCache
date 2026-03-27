using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisMultiplexerOptionsStartupHostedService : IHostedService
{
    private readonly IOptionsMonitor<RedisMultiplexerOptions> _options;
    private readonly RedisMultiplexerOptionsValidator _validator;

    public RedisMultiplexerOptionsStartupHostedService(
        IOptionsMonitor<RedisMultiplexerOptions> options,
        RedisMultiplexerOptionsValidator validator)
    {
        _options = options;
        _validator = validator;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var result = _validator.Validate(Options.DefaultName, _options.CurrentValue);
        if (result.Failed)
            throw new OptionsValidationException(Options.DefaultName, typeof(RedisMultiplexerOptions), result.Failures);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

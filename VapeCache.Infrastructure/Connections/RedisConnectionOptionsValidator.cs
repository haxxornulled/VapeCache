using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisConnectionOptionsValidator : IValidateOptions<RedisConnectionOptions>
{
    public ValidateOptionsResult Validate(string? name, RedisConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string>? failures = null;

        static void AddFailure(ref List<string>? failures, string message)
        {
            failures ??= new List<string>();
            failures.Add(message);
        }

        if (string.IsNullOrWhiteSpace(options.Host) && string.IsNullOrWhiteSpace(options.ConnectionString))
            AddFailure(ref failures, "RedisConnection:Host or RedisConnection:ConnectionString is required.");

        if (options.MaxConnections <= 0)
            AddFailure(ref failures, "RedisConnection:MaxConnections must be > 0.");

        if (options.MaxIdle <= 0)
            AddFailure(ref failures, "RedisConnection:MaxIdle must be > 0.");

        if (options.MaxIdle > options.MaxConnections)
            AddFailure(ref failures, "RedisConnection:MaxIdle must be <= MaxConnections.");

        if (options.Warm < 0)
            AddFailure(ref failures, "RedisConnection:Warm must be >= 0.");

        if (options.Warm > options.MaxIdle)
            AddFailure(ref failures, "RedisConnection:Warm must be <= MaxIdle.");

        if (options.ValidateAfterIdle < TimeSpan.Zero)
            AddFailure(ref failures, "RedisConnection:ValidateAfterIdle must be >= 0.");

        if (options.ValidateTimeout < TimeSpan.Zero)
            AddFailure(ref failures, "RedisConnection:ValidateTimeout must be >= 0.");

        if (options.IdleTimeout < TimeSpan.Zero)
            AddFailure(ref failures, "RedisConnection:IdleTimeout must be >= 0.");

        if (options.MaxConnectionLifetime < TimeSpan.Zero)
            AddFailure(ref failures, "RedisConnection:MaxConnectionLifetime must be >= 0.");

        if (options.ReaperPeriod < TimeSpan.Zero)
            AddFailure(ref failures, "RedisConnection:ReaperPeriod must be >= 0.");

        if (options.TcpSendBufferBytes < 0)
            AddFailure(ref failures, "RedisConnection:TcpSendBufferBytes must be >= 0.");

        if (options.TcpReceiveBufferBytes < 0)
            AddFailure(ref failures, "RedisConnection:TcpReceiveBufferBytes must be >= 0.");

        if (options.RespProtocolVersion is not (2 or 3))
            AddFailure(ref failures, "RedisConnection:RespProtocolVersion must be 2 or 3.");

        if (options.MaxClusterRedirects < 0 || options.MaxClusterRedirects > 16)
            AddFailure(ref failures, "RedisConnection:MaxClusterRedirects must be in range 0..16.");

        return failures is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

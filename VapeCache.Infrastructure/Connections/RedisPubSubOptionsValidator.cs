using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisPubSubOptionsValidator : IValidateOptions<RedisPubSubOptions>
{
    public ValidateOptionsResult Validate(string? name, RedisPubSubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string>? failures = null;

        static void AddFailure(ref List<string>? failures, string message)
        {
            failures ??= new List<string>();
            failures.Add(message);
        }

        if (options.DeliveryQueueCapacity <= 0)
            AddFailure(ref failures, "RedisPubSub:DeliveryQueueCapacity must be > 0.");

        if (options.ReconnectDelayMin <= TimeSpan.Zero)
            AddFailure(ref failures, "RedisPubSub:ReconnectDelayMin must be > 0.");

        if (options.ReconnectDelayMax <= TimeSpan.Zero)
            AddFailure(ref failures, "RedisPubSub:ReconnectDelayMax must be > 0.");

        if (options.ReconnectDelayMax < options.ReconnectDelayMin)
            AddFailure(ref failures, "RedisPubSub:ReconnectDelayMax must be >= ReconnectDelayMin.");

        return failures is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

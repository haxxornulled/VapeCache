using System;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace ResultDemo.Examples;

public sealed class UserProvisioningHandler
{
    private readonly ILogger<UserProvisioningHandler> _logger;

    public UserProvisioningHandler(ILogger<UserProvisioningHandler> logger) => _logger = logger;

    public void Handle(ProvisionUserMessage msg)
    {
        try
        {
            var hasCorrelationId = msg.CorrelationId.Match(
                some =>
                {
                    _logger.LogInformation("Handling provisioning. CorrelationId={CorrelationId}", some);
                    return true;
                },
                () =>
                {
                    _logger.LogWarning("CorrelationId missing. MessageId={MessageId}", msg.MessageId);
                    return false;
                });

            if (!hasCorrelationId)
                return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error processing message {MessageId}", msg.MessageId);
        }
    }
}

public sealed record ProvisionUserMessage(string MessageId, Option<Guid> CorrelationId);
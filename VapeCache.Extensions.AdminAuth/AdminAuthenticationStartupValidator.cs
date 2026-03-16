using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace VapeCache.Extensions.AdminAuth;

internal sealed class AdminAuthenticationStartupValidator : IHostedService
{
    private readonly IAuthenticationSchemeReadinessProbe _readinessProbe;
    private readonly IOptions<AdminAuthenticationValidationOptions> _options;

    public AdminAuthenticationStartupValidator(
        IAuthenticationSchemeReadinessProbe readinessProbe,
        IOptions<AdminAuthenticationValidationOptions> options)
    {
        _readinessProbe = readinessProbe ?? throw new ArgumentNullException(nameof(readinessProbe));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Value.RequireAdminAuthorization)
            return;

        if (await _readinessProbe.HasAuthenticationSchemeAsync(cancellationToken).ConfigureAwait(false))
            return;

        throw new InvalidOperationException(
            "VapeCache admin authorization is required, but no authentication schemes are registered. " +
            "Configure authentication (for example Authentication:JwtBearer) or allow insecure development " +
            "with VapeCache:Endpoints:RequireAdminAuthorizationInDevelopment=false.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}

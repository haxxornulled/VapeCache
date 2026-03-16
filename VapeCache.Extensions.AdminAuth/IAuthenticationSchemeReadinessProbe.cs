using Microsoft.AspNetCore.Authentication;

namespace VapeCache.Extensions.AdminAuth;

internal interface IAuthenticationSchemeReadinessProbe
{
    ValueTask<bool> HasAuthenticationSchemeAsync(CancellationToken ct = default);
}

internal sealed class AuthenticationSchemeReadinessProbe : IAuthenticationSchemeReadinessProbe
{
    private readonly IAuthenticationSchemeProvider _schemeProvider;

    public AuthenticationSchemeReadinessProbe(IAuthenticationSchemeProvider schemeProvider)
    {
        _schemeProvider = schemeProvider ?? throw new ArgumentNullException(nameof(schemeProvider));
    }

    public async ValueTask<bool> HasAuthenticationSchemeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var schemes = await _schemeProvider.GetAllSchemesAsync().ConfigureAwait(false);
        return schemes.Any();
    }
}

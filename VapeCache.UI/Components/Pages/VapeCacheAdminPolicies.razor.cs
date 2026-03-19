using VapeCache.Abstractions.Caching;
using VapeCache.UI.Features.Admin;

namespace VapeCache.UI.Components.Pages;

/// <summary>
/// Admin cache policy inspection page code-behind.
/// </summary>
public partial class VapeCacheAdminPolicies
{
    private readonly VapeCacheAdminOrchestrator _admin;
    private IReadOnlyList<CacheIntentEntry> _policies = Array.Empty<CacheIntentEntry>();
    private string? _error;

    /// <summary>
    /// Initializes a new instance of the <see cref="VapeCacheAdminPolicies"/> class.
    /// </summary>
    /// <param name="admin">Admin orchestrator.</param>
    public VapeCacheAdminPolicies(VapeCacheAdminOrchestrator admin)
    {
        _admin = admin ?? throw new ArgumentNullException(nameof(admin));
    }

    /// <summary>
    /// Executes component initialization.
    /// </summary>
    protected override Task OnInitializedAsync()
        => RefreshAsync();

    private Task RefreshAsync()
    {
        try
        {
            _policies = _admin.GetRecentPolicies(200)
                .OrderByDescending(static x => x.RecordedAtUtc)
                .ToArray();
            _error = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }

        return Task.CompletedTask;
    }
}


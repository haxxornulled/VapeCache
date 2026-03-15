using VapeCache.UI.Features.Admin;

namespace VapeCache.UI.Components.Pages;

/// <summary>
/// Admin health page code-behind.
/// </summary>
public partial class VapeCacheAdminHealth
{
    private readonly VapeCacheAdminOrchestrator _admin;
    private VapeCacheAdminSnapshot _snapshot = VapeCacheAdminPageDefaults.EmptySnapshot;
    private string? _error;

    /// <summary>
    /// Initializes a new instance of the <see cref="VapeCacheAdminHealth"/> class.
    /// </summary>
    /// <param name="admin">Admin orchestrator.</param>
    public VapeCacheAdminHealth(VapeCacheAdminOrchestrator admin)
    {
        _admin = admin ?? throw new ArgumentNullException(nameof(admin));
    }

    /// <summary>
    /// Executes component initialization.
    /// </summary>
    protected override Task OnInitializedAsync()
        => RefreshAsync();

    private async Task RefreshAsync()
    {
        try
        {
            _snapshot = await _admin.GetSnapshotAsync().ConfigureAwait(false);
            _error = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }
}

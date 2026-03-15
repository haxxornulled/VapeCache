using VapeCache.UI.Features.Admin;

namespace VapeCache.UI.Components.Pages;

/// <summary>
/// Admin reconciliation page code-behind.
/// </summary>
public partial class VapeCacheAdminReconciliation
{
    private readonly VapeCacheAdminOrchestrator _admin;
    private VapeCacheAdminSnapshot _snapshot = VapeCacheAdminPageDefaults.EmptySnapshot;
    private string? _error;
    private string? _message;

    /// <summary>
    /// Initializes a new instance of the <see cref="VapeCacheAdminReconciliation"/> class.
    /// </summary>
    /// <param name="admin">Admin orchestrator.</param>
    public VapeCacheAdminReconciliation(VapeCacheAdminOrchestrator admin)
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

    private async Task ReconcileAsync()
    {
        try
        {
            var invoked = await _admin.ReconcileAsync().ConfigureAwait(false);
            _message = invoked
                ? "Reconciliation run completed."
                : "Reconciliation service is not enabled in this runtime.";
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private async Task FlushAsync()
    {
        try
        {
            var invoked = await _admin.FlushReconciliationAsync().ConfigureAwait(false);
            _message = invoked
                ? "Reconciliation persisted state flushed."
                : "Reconciliation service is not enabled in this runtime.";
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }
}

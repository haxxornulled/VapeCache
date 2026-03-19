using VapeCache.UI.Features.Admin;

namespace VapeCache.UI.Components.Pages;

/// <summary>
/// Admin streams/event feed page code-behind.
/// </summary>
public partial class VapeCacheAdminStreams
{
    private readonly VapeCacheAdminOrchestrator _admin;
    private VapeCacheAdminEventStreamStatus _status = VapeCacheAdminEventStreamStatus.Disabled;
    private string? _error;

    /// <summary>
    /// Initializes a new instance of the <see cref="VapeCacheAdminStreams"/> class.
    /// </summary>
    /// <param name="admin">Admin orchestrator.</param>
    public VapeCacheAdminStreams(VapeCacheAdminOrchestrator admin)
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
            _status = _admin.GetEventStreamStatus();
            _error = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }

        return Task.CompletedTask;
    }
}


using VapeCache.UI.Features.Admin;
using System.Globalization;

namespace VapeCache.UI.Components.Pages;

/// <summary>
/// Admin home page code-behind.
/// </summary>
public partial class VapeCacheAdminHome
{
    private readonly VapeCacheAdminOrchestrator _admin;
    private VapeCacheAdminSnapshot _snapshot = VapeCacheAdminPageDefaults.EmptySnapshot;
    private string? _error;

    /// <summary>
    /// Initializes a new instance of the <see cref="VapeCacheAdminHome"/> class.
    /// </summary>
    /// <param name="admin">Admin orchestrator.</param>
    public VapeCacheAdminHome(VapeCacheAdminOrchestrator admin)
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

    private static string FormatSampleTimestamp(DateTimeOffset timestampUtc)
    {
        if (timestampUtc == DateTimeOffset.MinValue)
            return "n/a";

        return timestampUtc.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }
}

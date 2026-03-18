using VapeCache.UI.Features.Admin;
using VapeCache.Abstractions.Connections;

namespace VapeCache.UI.Components.Pages;

/// <summary>
/// Admin autoscaler page code-behind.
/// </summary>
public partial class VapeCacheAdminAutoscaler
{
    private readonly VapeCacheAdminOrchestrator _admin;
    private VapeCacheAdminSnapshot _snapshot = VapeCacheAdminPageDefaults.EmptySnapshot;
    private string? _error;

    /// <summary>
    /// Initializes a new instance of the <see cref="VapeCacheAdminAutoscaler"/> class.
    /// </summary>
    /// <param name="admin">Admin orchestrator.</param>
    public VapeCacheAdminAutoscaler(VapeCacheAdminOrchestrator admin)
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

    private static int GetOptionalInt(RedisAutoscalerSnapshot snapshot, string propertyName, int fallback = 0)
    {
        var value = ReadOptionalProperty(snapshot, propertyName);
        if (value is int typed)
            return typed;
        if (value is long longTyped)
            return checked((int)longTyped);
        return fallback;
    }

    private static long GetOptionalLong(RedisAutoscalerSnapshot snapshot, string propertyName, long fallback = 0)
    {
        var value = ReadOptionalProperty(snapshot, propertyName);
        if (value is long typed)
            return typed;
        if (value is int intTyped)
            return intTyped;
        return fallback;
    }

    private static double GetOptionalDouble(RedisAutoscalerSnapshot snapshot, string propertyName, double fallback = 0d)
    {
        var value = ReadOptionalProperty(snapshot, propertyName);
        if (value is double typed)
            return typed;
        if (value is float floatTyped)
            return floatTyped;
        if (value is decimal decimalTyped)
            return (double)decimalTyped;
        return fallback;
    }

    private static string GetOptionalString(RedisAutoscalerSnapshot snapshot, string propertyName, string fallback = "n/a")
    {
        var value = ReadOptionalProperty(snapshot, propertyName);
        return value as string ?? fallback;
    }

    private static object? ReadOptionalProperty(RedisAutoscalerSnapshot snapshot, string propertyName)
        => snapshot.GetType().GetProperty(propertyName)?.GetValue(snapshot);
}

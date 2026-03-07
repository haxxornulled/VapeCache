using System.Diagnostics;

namespace VapeCache.UI.Components.Pages;

#pragma warning disable CA1716 // Component class name is generated from Error.razor.
public partial class Error
{
    private string? RequestId { get; set; }
    private bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    protected override void OnInitialized()
        => RequestId = Activity.Current?.Id;
}
#pragma warning restore CA1716

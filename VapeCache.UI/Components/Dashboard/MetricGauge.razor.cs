using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace VapeCache.UI.Components.Dashboard;

/// <summary>
/// Represents the metric gauge.
/// </summary>
public partial class MetricGauge
{
    /// <summary>
    /// Gets or sets the gauge label.
    /// </summary>
    [Parameter, EditorRequired] public string Label { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the current value.
    /// </summary>
    [Parameter] public double Value { get; set; }
    /// <summary>
    /// Gets or sets the minimum value.
    /// </summary>
    [Parameter] public double Min { get; set; }
    /// <summary>
    /// Gets or sets the maximum value.
    /// </summary>
    [Parameter] public double Max { get; set; } = 100d;
    /// <summary>
    /// Gets or sets the display unit suffix.
    /// </summary>
    [Parameter] public string Unit { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the optional caption.
    /// </summary>
    [Parameter] public string? Caption { get; set; }
    /// <summary>
    /// Gets or sets the decimal precision for value display.
    /// </summary>
    [Parameter] public int Decimals { get; set; } = 1;

    private string FillPercentCss { get; set; } = "0.0%";
    private string GaugeStyle { get; set; } = "--gauge-fill:0.0%; --gauge-accent:hsl(192 88% 58%);";
    private string RangeDisplay { get; set; } = "0..100";
    private string DisplayValue { get; set; } = "0.0";

    /// <summary>
    /// Executes on parameters set.
    /// </summary>
    protected override void OnParametersSet()
    {
        var fillPercent = Max <= Min
            ? 0d
            : Math.Clamp((Value - Min) / (Max - Min), 0d, 1d) * 100d;
        FillPercentCss = string.Concat(fillPercent.ToString("F1", CultureInfo.InvariantCulture), "%");

        var hue = 192d - (fillPercent * 0.72d);
        var clampedHue = Math.Clamp(hue, 22d, 192d);
        var hueText = Math.Round(clampedHue, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);
        GaugeStyle = string.Concat("--gauge-fill:", FillPercentCss, "; --gauge-accent:hsl(", hueText, " 88% 58%);");

        RangeDisplay = string.Concat(
            Min.ToString("F0", CultureInfo.InvariantCulture),
            "..",
            Max.ToString("F0", CultureInfo.InvariantCulture));

        var decimals = Math.Max(0, Decimals);
        var format = $"F{decimals}";
        var valueText = Value.ToString(format, CultureInfo.InvariantCulture);
        DisplayValue = string.IsNullOrWhiteSpace(Unit)
            ? valueText
            : string.Concat(valueText, Unit);
    }
}

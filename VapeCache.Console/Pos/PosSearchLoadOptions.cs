namespace VapeCache.Console.Pos;

public sealed class PosSearchLoadOptions
{
    public bool Enabled { get; init; } = false;
    public bool StopHostOnCompletion { get; init; } = true;
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(2);
    public int Concurrency { get; init; } = 256;
    public TimeSpan LogEvery { get; init; } = TimeSpan.FromSeconds(5);
    public int TargetShoppersPerSecond { get; init; } = 0;
    public bool EnableAutoRamp { get; init; } = false;
    public string RampSteps { get; init; } = "1600,2000,2400,2800";
    public TimeSpan RampStepDuration { get; init; } = TimeSpan.FromSeconds(20);
    public bool StopOnFirstUnstable { get; init; } = true;
    public bool TreatOpenCircuitAsUnstable { get; init; } = true;
    public double MaxFailurePercent { get; init; } = 0.5d;
    public double MaxP95Ms { get; init; } = 30d;
    public string HotQuery { get; init; } = "code:TV-0099";
    public int HotQueryPercent { get; init; } = 90;
    public int CashierQueryPercent { get; init; } = 7;
    public int LookupUpcPercent { get; init; } = 3;
    public int LatencySampleSize { get; init; } = 8192;
}

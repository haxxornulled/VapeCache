namespace VapeCache.StressHost;

public sealed class RuntimeStressHostOptions
{
    public const string SectionName = "RuntimeStressHost";

    public bool AutoStart { get; set; }
    public string Scenario { get; set; } = "soak";
    public int Workers { get; set; } = 32;
    public int Keyspace { get; set; } = 256;
    public int DurationSeconds { get; set; } = 120;
    public int ForceOpenAfterMs { get; set; } = 1500;
    public int ForceOpenHoldMs { get; set; } = 2500;
    public int ForceOpenCycles { get; set; } = 3;
    public int SampleIntervalMs { get; set; } = 100;
    public bool StampedeEnabled { get; set; } = true;
    public int StampedeWorkers { get; set; } = 24;
    public int StampedeFactoryDelayMs { get; set; } = 1500;
    public int StampedeWaveIntervalMs { get; set; } = 3000;
}

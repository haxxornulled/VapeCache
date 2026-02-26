namespace VapeCache.Console.Plugins;

public sealed class PluginDemoOptions
{
    public bool Enabled { get; init; } = false;
    public string KeyPrefix { get; init; } = "plugin:sample";
    public TimeSpan Ttl { get; init; } = TimeSpan.FromMinutes(5);
}

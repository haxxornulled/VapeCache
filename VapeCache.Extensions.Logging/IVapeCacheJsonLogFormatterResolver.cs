using Microsoft.Extensions.Configuration;
using Serilog.Formatting;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Json;
using VapeCache.Guards;

namespace VapeCache.Extensions.Logging;

/// <summary>
/// Resolves optional JSON log formatters for Serilog sinks.
/// </summary>
public interface IVapeCacheJsonLogFormatterResolver
{
    /// <summary>
    /// Returns a JSON formatter for the sink target when enabled; otherwise returns null.
    /// </summary>
    ITextFormatter? ResolveFormatter(IConfiguration configuration, VapeCacheJsonSinkTarget target);
}

/// <summary>
/// Sink targets that can use JSON formatting.
/// </summary>
public enum VapeCacheJsonSinkTarget
{
    Console,
    File
}

/// <summary>
/// Default JSON formatter policy driven by Serilog:Json:* configuration keys.
/// </summary>
public sealed class DefaultVapeCacheJsonLogFormatterResolver : IVapeCacheJsonLogFormatterResolver
{
    public static DefaultVapeCacheJsonLogFormatterResolver Instance { get; } = new();

    private DefaultVapeCacheJsonLogFormatterResolver()
    {
    }

    public ITextFormatter? ResolveFormatter(IConfiguration configuration, VapeCacheJsonSinkTarget target)
    {
        ParanoiaThrowGuard.Against.NotNull(configuration);

        var enabled = configuration.GetValue<bool?>("Serilog:Json:Enabled") ?? false;
        if (!enabled || !IsTargetEnabled(configuration, target))
            return null;

        var formatterName = configuration["Serilog:Json:Formatter"];
        var renderMessage = configuration.GetValue<bool?>("Serilog:Json:RenderMessage") ?? true;

        if (string.Equals(formatterName, "Json", StringComparison.OrdinalIgnoreCase))
            return new JsonFormatter(renderMessage: renderMessage);

        if (string.Equals(formatterName, "RenderedCompact", StringComparison.OrdinalIgnoreCase))
            return new RenderedCompactJsonFormatter();

        return new CompactJsonFormatter();
    }

    private static bool IsTargetEnabled(IConfiguration configuration, VapeCacheJsonSinkTarget target)
    {
        return target switch
        {
            VapeCacheJsonSinkTarget.Console => configuration.GetValue<bool?>("Serilog:Json:ConsoleEnabled") ?? false,
            VapeCacheJsonSinkTarget.File => configuration.GetValue<bool?>("Serilog:Json:FileEnabled") ?? true,
            _ => false
        };
    }
}

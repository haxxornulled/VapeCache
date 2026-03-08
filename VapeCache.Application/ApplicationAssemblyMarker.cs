using VapeCache.Core.Domain.Primitives;

namespace VapeCache.Application;

/// <summary>
/// Marker type used for assembly discovery and architecture tests.
/// </summary>
public sealed class ApplicationAssemblyMarker
{
    /// <summary>
    /// Executes typeof.
    /// </summary>
    public static Type CoreAnchorType => typeof(ValueObject);
}

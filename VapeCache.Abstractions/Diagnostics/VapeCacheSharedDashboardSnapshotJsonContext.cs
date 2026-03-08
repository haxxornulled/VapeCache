using System.Text.Json;
using System.Text.Json.Serialization;

namespace VapeCache.Abstractions.Diagnostics;

/// <summary>
/// Represents the vape cache shared dashboard snapshot json context.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(VapeCacheSharedDashboardSnapshot))]
public partial class VapeCacheSharedDashboardSnapshotJsonContext : JsonSerializerContext
{
}

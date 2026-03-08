using System.Text.Json;
using System.Text.Json.Serialization;

namespace VapeCache.Abstractions.Diagnostics;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(VapeCacheSharedDashboardSnapshot))]
public partial class VapeCacheSharedDashboardSnapshotJsonContext : JsonSerializerContext
{
}

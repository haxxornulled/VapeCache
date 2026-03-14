using System.Data.Common;

namespace VapeCache.Extensions.EntityFrameworkCore;

/// <summary>
/// Builds deterministic cache keys for EF Core query commands.
/// </summary>
public interface IEfCoreQueryCacheKeyBuilder
{
    /// <summary>
    /// Builds a deterministic cache key for the command and provider identity.
    /// </summary>
    string BuildQueryCacheKey(string providerName, DbCommand command, string? modelIdentity = null);
}

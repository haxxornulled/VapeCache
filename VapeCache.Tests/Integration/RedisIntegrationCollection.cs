using Xunit;

namespace VapeCache.Tests.Integration;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RedisIntegrationCollection
{
    public const string Name = "RedisIntegration";
}


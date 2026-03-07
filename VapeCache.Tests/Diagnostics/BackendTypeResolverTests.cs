using VapeCache.Abstractions.Diagnostics;
using Xunit;

namespace VapeCache.Tests.Diagnostics;

public sealed class BackendTypeResolverTests
{
    [Theory]
    [InlineData("redis")]
    [InlineData("Redis")]
    [InlineData(" REDIS ")]
    public void TryParseName_ParsesRedisVariants(string value)
    {
        var ok = BackendTypeResolver.TryParseName(value, out var backend);

        Assert.True(ok);
        Assert.Equal(BackendType.Redis, backend);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("in-memory")]
    [InlineData("in_memory")]
    [InlineData("InMemory")]
    [InlineData(" inmemory ")]
    public void TryParseName_ParsesInMemoryVariants(string value)
    {
        var ok = BackendTypeResolver.TryParseName(value, out var backend);

        Assert.True(ok);
        Assert.Equal(BackendType.InMemory, backend);
    }

    [Fact]
    public void Resolve_PrefersFailoverStateOverInputName()
    {
        var backend = BackendTypeResolver.Resolve("redis", breakerOpen: false, forcedOpen: true);

        Assert.Equal(BackendType.InMemory, backend);
    }
}

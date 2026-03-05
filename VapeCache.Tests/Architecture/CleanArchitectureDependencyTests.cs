using VapeCache.Abstractions.Caching;
using VapeCache.Application;
using VapeCache.Core.Guards;
using VapeCache.Infrastructure.Caching;
using System.Reflection;

namespace VapeCache.Tests.Architecture;

public sealed class CleanArchitectureDependencyTests
{
    [Fact]
    public void Core_DoesNotReference_OtherVapeCacheAssemblies()
    {
        var references = GetAssemblyReferenceNames(typeof(Guard).Assembly);
        Assert.DoesNotContain(references, static name => name.StartsWith("VapeCache.", StringComparison.Ordinal));
    }

    [Fact]
    public void Application_References_Core_ButNotInfrastructure()
    {
        var references = GetAssemblyReferenceNames(typeof(ApplicationAssemblyMarker).Assembly);
        Assert.Contains("VapeCache.Core", references);
        Assert.DoesNotContain("VapeCache.Infrastructure", references);
    }

    [Fact]
    public void Abstractions_DoesNotReference_ApplicationOrInfrastructureOrCore()
    {
        var references = GetAssemblyReferenceNames(typeof(ICacheService).Assembly);
        Assert.DoesNotContain("VapeCache.Application", references);
        Assert.DoesNotContain("VapeCache.Infrastructure", references);
        Assert.DoesNotContain("VapeCache.Core", references);
    }

    [Fact]
    public void Infrastructure_DoesNotReference_Application()
    {
        var references = GetAssemblyReferenceNames(typeof(VapeCacheClient).Assembly);
        Assert.DoesNotContain("VapeCache.Application", references);
    }

    private static HashSet<string> GetAssemblyReferenceNames(Assembly assembly)
        => assembly
            .GetReferencedAssemblies()
            .Select(static a => a.Name)
            .Where(static a => !string.IsNullOrWhiteSpace(a))
            .Select(static a => a!)
            .ToHashSet(StringComparer.Ordinal);
}

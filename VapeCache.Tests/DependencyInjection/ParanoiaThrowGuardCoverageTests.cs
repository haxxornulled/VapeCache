using System.Reflection;
using VapeCache.Extensions.Logging;
using VapeCache.Extensions.PubSub;
using VapeCache.Infrastructure.Caching;

namespace VapeCache.Tests.DependencyInjection;

public sealed class ParanoiaThrowGuardCoverageTests
{
    [Fact]
    public void GuardMethods_LoggingAssembly_CoverSuccessAndFailureBranches()
        => AssertGuardBehaviorInAssembly(typeof(VapeCacheSerilogExtensions).Assembly);

    [Fact]
    public void GuardMethods_PubSubAssembly_CoverSuccessAndFailureBranches()
        => AssertGuardBehaviorInAssembly(typeof(VapeCachePubSubServiceCollectionExtensions).Assembly);

    [Fact]
    public void GuardMethods_InfrastructureAssembly_CoverSuccessAndFailureBranches()
        => AssertGuardBehaviorInAssembly(typeof(PubSubRegistration).Assembly);

    private static void AssertGuardBehaviorInAssembly(Assembly assembly)
    {
        var guardType = assembly.GetType("VapeCache.Guards.ParanoiaThrowGuard", throwOnError: true)!;
        var againstType = guardType.GetNestedType("Against", BindingFlags.NonPublic)
                          ?? throw new InvalidOperationException("ParanoiaThrowGuard.Against not found.");

        var notNull = againstType.GetMethod("NotNull", BindingFlags.Public | BindingFlags.Static);
        var notNullOrEmpty = againstType.GetMethod("NotNullOrEmpty", BindingFlags.Public | BindingFlags.Static);
        var notNullOrWhitespace = againstType.GetMethod("NotNullOrWhiteSpace", BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(notNull);
        Assert.NotNull(notNullOrEmpty);
        Assert.NotNull(notNullOrWhitespace);

        var notNullGeneric = notNull!.MakeGenericMethod(typeof(object));

        // NotNull success/failure
        var okRef = new object();
        var okResult = notNullGeneric.Invoke(null, new object?[] { okRef, "value" });
        Assert.Same(okRef, okResult);
        Assert.Throws<TargetInvocationException>(() => notNullGeneric.Invoke(null, new object?[] { null, "value" }));

        // NotNullOrEmpty success/failure
        var okText = (string)notNullOrEmpty!.Invoke(null, new object?[] { "ok", "text" })!;
        Assert.Equal("ok", okText);
        Assert.Throws<TargetInvocationException>(() => notNullOrEmpty.Invoke(null, new object?[] { "", "text" }));

        // NotNullOrWhiteSpace success/failure
        var okWs = (string)notNullOrWhitespace!.Invoke(null, new object?[] { "x", "text" })!;
        Assert.Equal("x", okWs);
        Assert.Throws<TargetInvocationException>(() => notNullOrWhitespace.Invoke(null, new object?[] { "   ", "text" }));
    }
}

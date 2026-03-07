using VapeCache.Features.Invalidation;

namespace VapeCache.Tests.Features.Invalidation;

public sealed class CacheInvalidationPlanTests
{
    [Fact]
    public void PlanNormalization_RemovesDuplicatesAndWhitespace()
    {
        var plan = new CacheInvalidationPlan(
            tags: [" customers ", "customers", "orders", ""],
            zones: ["sales", " sales ", " "],
            keys: ["order:1", "order:1", " order:2 "]);

        Assert.Equal(2, plan.Tags.Count);
        Assert.Contains("customers", plan.Tags);
        Assert.Contains("orders", plan.Tags);

        Assert.Single(plan.Zones);
        Assert.Contains("sales", plan.Zones);

        Assert.Equal(2, plan.Keys.Count);
        Assert.Contains("order:1", plan.Keys);
        Assert.Contains("order:2", plan.Keys);
    }

    [Fact]
    public void PlanBuilder_MergesPlans()
    {
        var planA = new CacheInvalidationPlan(tags: ["a", "b"], zones: ["z1"]);
        var planB = new CacheInvalidationPlan(tags: ["b", "c"], keys: ["k1"]);

        var merged = new CacheInvalidationPlanBuilder()
            .AddPlan(planA)
            .AddPlan(planB)
            .Build();

        Assert.Equal(3, merged.Tags.Count);
        Assert.Contains("a", merged.Tags);
        Assert.Contains("b", merged.Tags);
        Assert.Contains("c", merged.Tags);
        Assert.Single(merged.Zones);
        Assert.Single(merged.Keys);
        Assert.Equal(5, merged.TotalTargets);
    }
}

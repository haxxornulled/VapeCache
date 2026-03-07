using VapeCache.Features.Invalidation;

namespace VapeCache.Tests.Features.Invalidation;

public sealed class InvalidationPolicyTests
{
    [Fact]
    public async Task DelegatePolicy_BuildsPlanFromEvent()
    {
        var policy = new DelegateInvalidationPolicy<OrderUpdatedEvent>(
            tagSelector: static e => [$"order:{e.OrderId}"],
            zoneSelector: static e => [e.Region],
            keySelector: static e => [$"order:summary:{e.OrderId}"]);

        var plan = await policy.BuildPlanAsync(new OrderUpdatedEvent("42", "orders"));

        Assert.Single(plan.Tags);
        Assert.Single(plan.Zones);
        Assert.Single(plan.Keys);
        Assert.Contains("order:42", plan.Tags);
        Assert.Contains("orders", plan.Zones);
        Assert.Contains("order:summary:42", plan.Keys);
    }

    [Fact]
    public async Task BuiltInTagZoneKeyPolicies_ProjectTargets()
    {
        var eventData = new OrderUpdatedEvent("42", "orders");
        var tagPolicy = new TagInvalidationPolicy<OrderUpdatedEvent>(static e => [$"order:{e.OrderId}"]);
        var zonePolicy = new ZoneInvalidationPolicy<OrderUpdatedEvent>(static e => [e.Region]);
        var keyPolicy = new KeyInvalidationPolicy<OrderUpdatedEvent>(static e => [$"order:summary:{e.OrderId}"]);

        var tagPlan = await tagPolicy.BuildPlanAsync(eventData);
        var zonePlan = await zonePolicy.BuildPlanAsync(eventData);
        var keyPlan = await keyPolicy.BuildPlanAsync(eventData);

        Assert.Single(tagPlan.Tags);
        Assert.Single(zonePlan.Zones);
        Assert.Single(keyPlan.Keys);
    }

    [Fact]
    public async Task EntityPolicy_BuildsEntityTagsAndKeys()
    {
        var policy = new EntityInvalidationPolicy<OrderUpdatedEvent>(
            entityName: "order",
            idsSelector: static e => [e.OrderId],
            zonesSelector: static e => [e.Region],
            keyPrefixes: ["order", "order:summary"]);

        var plan = await policy.BuildPlanAsync(new OrderUpdatedEvent("42", "orders"));

        Assert.Contains("order:42", plan.Tags);
        Assert.Contains("orders", plan.Zones);
        Assert.Contains("order:42", plan.Keys);
        Assert.Contains("order:summary:42", plan.Keys);
    }

    [Fact]
    public async Task CompositePolicy_MergesChildren()
    {
        var staticPolicy = new StaticInvalidationPolicy<OrderUpdatedEvent>(
            new CacheInvalidationPlan(tags: ["a"], zones: ["z1"]));
        var delegatePolicy = new DelegateInvalidationPolicy<OrderUpdatedEvent>(
            tagSelector: static _ => ["b"],
            keySelector: static _ => ["k1"]);
        var composite = new CompositeInvalidationPolicy<OrderUpdatedEvent>([staticPolicy, delegatePolicy]);

        var plan = await composite.BuildPlanAsync(new OrderUpdatedEvent("99", "ignored"));

        Assert.Equal(2, plan.Tags.Count);
        Assert.Single(plan.Zones);
        Assert.Single(plan.Keys);
    }

    public sealed record OrderUpdatedEvent(string OrderId, string Region);
}

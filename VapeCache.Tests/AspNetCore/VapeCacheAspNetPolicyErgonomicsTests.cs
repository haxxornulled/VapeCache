using System.Buffers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using VapeCache.Abstractions.Caching;
using VapeCache.Extensions.AspNetCore;

namespace VapeCache.Tests.AspNetCore;

public sealed class VapeCacheAspNetPolicyErgonomicsTests
{
    [Fact]
    public async Task AddVapeCacheAspNetPolicies_VaryByQuery_CachesPerQueryValue()
    {
        var counter = 0;
        await using var app = await CreateAppAsync(
            configureApp: builder =>
            {
                builder.MapGet("/query", (string q) => $"{q}:{Interlocked.Increment(ref counter)}")
                    .CacheWithVapeCache("query-policy");
            },
            configureServices: services =>
            {
                services.AddVapeCacheAspNetPolicies(policies =>
                {
                    policies.AddPolicy("query-policy", policy => policy
                        .Ttl(TimeSpan.FromSeconds(20))
                        .VaryByQuery());
                });
            });

        using var client = app.GetTestClient();

        var alphaFirst = await client.GetStringAsync("/query?q=alpha");
        var alphaSecond = await client.GetStringAsync("/query?q=alpha");
        var betaFirst = await client.GetStringAsync("/query?q=beta");
        var betaSecond = await client.GetStringAsync("/query?q=beta");

        Assert.Equal("alpha:1", alphaFirst);
        Assert.Equal("alpha:1", alphaSecond);
        Assert.Equal("beta:2", betaFirst);
        Assert.Equal("beta:2", betaSecond);
        Assert.Equal(2, counter);
    }

    [Fact]
    public async Task CacheWithVapeCache_InlinePolicy_CachesEndpointResponses()
    {
        var counter = 0;
        await using var app = await CreateAppAsync(builder =>
        {
            builder.MapGet("/inline", () => Interlocked.Increment(ref counter).ToString())
                .CacheWithVapeCache(policy => policy
                    .Ttl(TimeSpan.FromSeconds(20))
                    .Tags("inline"));
        });

        using var client = app.GetTestClient();
        var first = await client.GetStringAsync("/inline");
        var second = await client.GetStringAsync("/inline");

        Assert.Equal("1", first);
        Assert.Equal("1", second);
        Assert.Equal(1, counter);
    }

    [Fact]
    public async Task AddVapeCacheAspNetPolicies_NamedNoStorePolicy_DisablesCaching()
    {
        var counter = 0;
        await using var app = await CreateAppAsync(
            configureApp: builder =>
            {
                builder.MapGet("/volatile", () => Interlocked.Increment(ref counter).ToString())
                    .CacheWithVapeCache("volatile");
            },
            configureServices: services =>
            {
                services.AddVapeCacheAspNetPolicies(policies =>
                {
                    policies.AddPolicy("volatile", policy => policy.NoStore());
                });
            });

        using var client = app.GetTestClient();
        var first = await client.GetStringAsync("/volatile");
        var second = await client.GetStringAsync("/volatile");

        Assert.Equal("1", first);
        Assert.Equal("2", second);
        Assert.Equal(2, counter);
    }

    [Fact]
    public void VapeCachePolicyAttribute_MapsFriendlyProperties_ToOutputCacheMetadata()
    {
        var attribute = new VapeCachePolicyAttribute("products")
        {
            TtlSeconds = 300,
            VaryByQuery = true,
            VaryByHeaders = ["x-tenant", "accept-language"],
            VaryByRouteValues = ["id"],
            CacheTags = ["products", "catalog"],
            IntentKind = "QueryResult",
            IntentReason = "API endpoint policy"
        };

        var outputAttribute = attribute.ToOutputCacheAttribute();

        Assert.Equal("products", outputAttribute.PolicyName);
        Assert.Equal(300, outputAttribute.Duration);
        Assert.Contains("*", outputAttribute.VaryByQueryKeys ?? Array.Empty<string>());
        Assert.Equal(["x-tenant", "accept-language"], outputAttribute.VaryByHeaderNames ?? Array.Empty<string>());
        Assert.Equal(["id"], outputAttribute.VaryByRouteValueNames ?? Array.Empty<string>());
        Assert.Equal(["products", "catalog"], outputAttribute.Tags ?? Array.Empty<string>());
        Assert.Equal("QueryResult", attribute.IntentKind);
        Assert.Equal("API endpoint policy", attribute.IntentReason);
    }

    [Fact]
    public async Task VapeCachePolicyAttribute_OnMvcAction_CachesResponses()
    {
        MvcCounterApiController.Reset();
        await using var app = await CreateAppAsync(
            configureApp: builder =>
            {
                builder.MapControllers();
            },
            configureServices: services =>
            {
                services.AddControllers().AddApplicationPart(typeof(MvcCounterApiController).Assembly);
            });

        using var client = app.GetTestClient();
        var first = await client.GetStringAsync("/api/mvccounterapi/7");
        var second = await client.GetStringAsync("/api/mvccounterapi/7");

        Assert.Equal("1", first);
        Assert.Equal("1", second);
    }

    [Fact]
    public void VapeCachePolicyAttribute_ToOutputCacheAttribute_Throws_WhenTtlIsNegative()
    {
        var attribute = new VapeCachePolicyAttribute
        {
            TtlSeconds = -1
        };

        var exception = Assert.Throws<InvalidOperationException>(() => attribute.ToOutputCacheAttribute());
        Assert.Contains("negative", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VapeCachePolicyAttribute_ToOutputCacheAttribute_UsesExplicitQueryKeys_WhenProvided()
    {
        var attribute = new VapeCachePolicyAttribute
        {
            TtlSeconds = 60,
            VaryByQuery = true,
            VaryByQueryKeys = ["tenant", "culture", "tenant"]
        };

        var output = attribute.ToOutputCacheAttribute();
        Assert.Equal(["tenant", "culture"], output.VaryByQueryKeys ?? Array.Empty<string>());
    }

    [Fact]
    public void AddVapeCacheAspNetPolicies_ThrowsOnDuplicatePolicyNames()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddVapeCacheAspNetPolicies(policies =>
            {
                policies.AddPolicy("products", policy => policy.Ttl(TimeSpan.FromMinutes(1)));
                policies.AddPolicy("products", policy => policy.Ttl(TimeSpan.FromMinutes(5)));
            }));

        Assert.Contains("already registered", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VapeCacheHttpPolicyBuilder_ValidatesInputs()
    {
        var registry = new VapeCacheAspNetPolicyRegistry();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            registry.AddPolicy("ttl-invalid", policy => policy.Ttl(TimeSpan.Zero)));
        Assert.Throws<ArgumentException>(() =>
            registry.AddPolicy("prefix-invalid", policy => policy.CacheKeyPrefix("  ")));
        Assert.Throws<ArgumentException>(() =>
            registry.AddPolicy("intent-invalid", policy => policy.WithIntent(" ")));
    }

    [Fact]
    public async Task VapeCachePolicyAttribute_ActionPolicy_WinsOverControllerPolicy()
    {
        await using var app = await CreateAppAsync(
            configureApp: builder => builder.MapControllers(),
            configureServices: services =>
            {
                services.AddControllers().AddApplicationPart(typeof(MvcPolicyOverrideApiController).Assembly);
            });

        var endpoint = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText?.Contains("api/policy-override", StringComparison.OrdinalIgnoreCase) == true);

        var outputCacheMetadata = endpoint.Metadata.OfType<OutputCacheAttribute>().ToArray();
        Assert.Single(outputCacheMetadata);
        Assert.Equal("action-policy", outputCacheMetadata[0].PolicyName);
        Assert.Equal(["action"], outputCacheMetadata[0].Tags ?? Array.Empty<string>());
    }

    [Fact]
    public async Task VapeCachePolicyAttribute_DoesNotDuplicate_WhenNativeOutputCacheIsPresent()
    {
        await using var app = await CreateAppAsync(
            configureApp: builder => builder.MapControllers(),
            configureServices: services =>
            {
                services.AddControllers().AddApplicationPart(typeof(MvcNativeOutputCacheApiController).Assembly);
            });

        var endpoint = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText?.Contains("api/native-output-cache", StringComparison.OrdinalIgnoreCase) == true);

        var outputCacheMetadata = endpoint.Metadata.OfType<OutputCacheAttribute>().ToArray();
        Assert.Single(outputCacheMetadata);
        Assert.True(outputCacheMetadata[0].NoStore);
    }

    private static async Task<WebApplication> CreateAppAsync(
        Action<WebApplication> configureApp,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ICacheService, InMemoryRawCacheService>();
        builder.Services.AddVapeCacheOutputCaching();
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseRouting();
        app.UseVapeCacheOutputCaching();
        configureApp(app);
        await app.StartAsync();
        return app;
    }

    private sealed class InMemoryRawCacheService : ICacheService
    {
        private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public string Name => "in-memory-raw";

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct)
        {
            lock (_gate)
            {
                return ValueTask.FromResult(_store.TryGetValue(key, out var payload) ? payload : null);
            }
        }

        public ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CacheEntryOptions options, CancellationToken ct)
        {
            lock (_gate)
            {
                _store[key] = value.ToArray();
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken ct)
        {
            lock (_gate)
            {
                return ValueTask.FromResult(_store.Remove(key));
            }
        }

        public ValueTask<T?> GetAsync<T>(string key, SpanDeserializer<T> deserialize, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask SetAsync<T>(string key, T value, Action<IBufferWriter<byte>, T> serialize, CacheEntryOptions options, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<T> GetOrSetAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, Action<IBufferWriter<byte>, T> serialize, SpanDeserializer<T> deserialize, CacheEntryOptions options, CancellationToken ct)
            => throw new NotSupportedException();
    }

}

[ApiController]
[Route("api/[controller]")]
public sealed class MvcCounterApiController : ControllerBase
{
    private static int _counter;

    [HttpGet("{id:int}")]
    [VapeCachePolicy(TtlSeconds = 30, VaryByRouteValues = ["id"], CacheTags = ["mvc-counter"])]
    public IActionResult Get(int id)
    {
        _ = id;
        return Content(Interlocked.Increment(ref _counter).ToString());
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _counter, 0);
    }
}

[ApiController]
[Route("api/policy-override")]
[VapeCachePolicy("controller-policy", TtlSeconds = 10, CacheTags = ["controller"])]
public sealed class MvcPolicyOverrideApiController : ControllerBase
{
    [HttpGet("{id:int}")]
    [VapeCachePolicy("action-policy", TtlSeconds = 30, CacheTags = ["action"])]
    public IActionResult Get(int id)
    {
        _ = id;
        return Content("ok");
    }
}

[ApiController]
[Route("api/native-output-cache")]
public sealed class MvcNativeOutputCacheApiController : ControllerBase
{
    [HttpGet("{id:int}")]
    [OutputCache(NoStore = true)]
    [VapeCachePolicy("action-policy", TtlSeconds = 30, CacheTags = ["action"])]
    public IActionResult Get(int id)
    {
        _ = id;
        return Content("ok");
    }
}

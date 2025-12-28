using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VapeCache.Abstractions.Collections;
using VapeCache.Abstractions.Connections;
using VapeCache.Infrastructure.Caching;
using VapeCache.Infrastructure.Connections;

namespace VapeCache.Benchmarks.Benchmarks;

/// <summary>
/// Benchmarks for VapeCache typed collections API (LIST, SET, HASH).
/// Tests real-world scenarios like shopping carts, user sets, and profile storage.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(EnterpriseBenchmarkConfig))]
public class TypedCollectionsBenchmarks
{
    private ICacheCollectionFactory _collections = null!;

    private CartItem _cartItem = null!;
    private CartItem[] _cartItems = null!;
    private string _userId = null!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();

        // Add logging (required)
        services.AddLogging();

        // Configure Redis connection options (circuit will open immediately - no real Redis)
        services.Configure<RedisConnectionOptions>(options =>
        {
            // Use reflection to set init-only properties for benchmarking
            typeof(RedisConnectionOptions).GetProperty(nameof(RedisConnectionOptions.Host))!
                .SetValue(options, "invalid-host-benchmark");
            typeof(RedisConnectionOptions).GetProperty(nameof(RedisConnectionOptions.Port))!
                .SetValue(options, 9999);
            typeof(RedisConnectionOptions).GetProperty(nameof(RedisConnectionOptions.ConnectTimeout))!
                .SetValue(options, TimeSpan.FromMilliseconds(100));
        });

        // Register VapeCache
        services.AddVapecacheRedisConnections();
        services.AddVapecacheCaching();

        var provider = services.BuildServiceProvider();
        _collections = provider.GetRequiredService<ICacheCollectionFactory>();

        // Prepare test data
        _userId = "user-benchmark-001";
        _cartItem = new CartItem(
            Guid.NewGuid(),
            "Premium Coffee Beans",
            19.99m,
            2,
            DateTime.UtcNow);

        _cartItems = Enumerable.Range(0, 10)
            .Select(i => new CartItem(
                Guid.NewGuid(),
                $"Product {i}",
                (decimal)(9.99 + i),
                i + 1,
                DateTime.UtcNow))
            .ToArray();

        // Warm up collections with data
        var warmupList = _collections.List<CartItem>($"cart:{_userId}");
        foreach (var item in _cartItems)
        {
            warmupList.PushBackAsync(item).AsTask().Wait();
        }

        var warmupSet = _collections.Set<string>("users:active");
        warmupSet.AddAsync(_userId).AsTask().Wait();

        var warmupHash = _collections.Hash<string>($"profile:{_userId}");
        warmupHash.SetAsync("name", "John Doe").AsTask().Wait();
        warmupHash.SetAsync("email", "john@example.com").AsTask().Wait();
    }

    // ===========================
    // LIST Operations (Shopping Cart)
    // ===========================

    [Benchmark(Description = "List: Add item to cart (PushBack)")]
    public async Task<long> List_AddToCart()
    {
        var cart = _collections.List<CartItem>($"cart:{_userId}");
        return await cart.PushBackAsync(_cartItem);
    }

    [Benchmark(Description = "List: Add urgent item to cart (PushFront)")]
    public async Task<long> List_AddUrgentToCart()
    {
        var cart = _collections.List<CartItem>($"cart:{_userId}");
        return await cart.PushFrontAsync(_cartItem);
    }

    [Benchmark(Description = "List: Get cart items (Range 0-9)")]
    public async Task<CartItem[]> List_GetCartItems()
    {
        var cart = _collections.List<CartItem>($"cart:{_userId}");
        return await cart.RangeAsync(0, 9);
    }

    [Benchmark(Description = "List: Get all cart items (Range 0--1)")]
    public async Task<CartItem[]> List_GetAllCartItems()
    {
        var cart = _collections.List<CartItem>($"cart:{_userId}");
        return await cart.RangeAsync(0, -1);
    }

    [Benchmark(Description = "List: Get cart count")]
    public async Task<long> List_GetCartCount()
    {
        var cart = _collections.List<CartItem>($"cart:{_userId}");
        return await cart.LengthAsync();
    }

    [Benchmark(Description = "List: Remove from cart (PopFront)")]
    public async Task<CartItem?> List_RemoveFromCart()
    {
        var cart = _collections.List<CartItem>($"cart:{_userId}");
        return await cart.PopFrontAsync();
    }

    [Benchmark(Description = "List: Checkout (PopBack)")]
    public async Task<CartItem?> List_Checkout()
    {
        var cart = _collections.List<CartItem>($"cart:{_userId}");
        return await cart.PopBackAsync();
    }

    // ===========================
    // LIST Bulk Operations
    // ===========================

    [Benchmark(Description = "List: Add 10 items to cart (bulk)")]
    public async Task List_BulkAddToCart()
    {
        var cart = _collections.List<CartItem>($"cart:bulk:{Guid.NewGuid()}");
        foreach (var item in _cartItems)
        {
            await cart.PushBackAsync(item);
        }
    }

    [Benchmark(Description = "List: Process entire cart (10 PopFront)")]
    public async Task List_ProcessCart()
    {
        var cart = _collections.List<CartItem>($"cart:process:{Guid.NewGuid()}");

        // Pre-populate
        foreach (var item in _cartItems)
        {
            await cart.PushBackAsync(item);
        }

        // Process all items
        for (int i = 0; i < _cartItems.Length; i++)
        {
            await cart.PopFrontAsync();
        }
    }

    // ===========================
    // SET Operations (Active Users)
    // ===========================

    [Benchmark(Description = "Set: Mark user online (Add)")]
    public async Task<long> Set_MarkUserOnline()
    {
        var activeUsers = _collections.Set<string>("users:active");
        return await activeUsers.AddAsync(_userId);
    }

    [Benchmark(Description = "Set: Check if user online (Contains)")]
    public async Task<bool> Set_IsUserOnline()
    {
        var activeUsers = _collections.Set<string>("users:active");
        return await activeUsers.ContainsAsync(_userId);
    }

    [Benchmark(Description = "Set: Get online user count")]
    public async Task<long> Set_GetOnlineCount()
    {
        var activeUsers = _collections.Set<string>("users:active");
        return await activeUsers.CountAsync();
    }

    [Benchmark(Description = "Set: Get all online users")]
    public async Task<string[]> Set_GetAllOnlineUsers()
    {
        var activeUsers = _collections.Set<string>("users:active");
        return await activeUsers.MembersAsync();
    }

    [Benchmark(Description = "Set: Mark user offline (Remove)")]
    public async Task<long> Set_MarkUserOffline()
    {
        var activeUsers = _collections.Set<string>("users:active");
        return await activeUsers.RemoveAsync(_userId);
    }

    // ===========================
    // SET Bulk Operations
    // ===========================

    [Benchmark(Description = "Set: Add 100 users online (bulk)")]
    public async Task Set_BulkAddUsers()
    {
        var activeUsers = _collections.Set<string>($"users:bulk:{Guid.NewGuid()}");
        for (int i = 0; i < 100; i++)
        {
            await activeUsers.AddAsync($"user-{i:D6}");
        }
    }

    [Benchmark(Description = "Set: Check 100 users online (bulk)")]
    public async Task Set_BulkCheckUsers()
    {
        var activeUsers = _collections.Set<string>("users:active");
        for (int i = 0; i < 100; i++)
        {
            await activeUsers.ContainsAsync($"user-{i:D6}");
        }
    }

    // ===========================
    // HASH Operations (User Profile)
    // ===========================

    [Benchmark(Description = "Hash: Update profile field (Set)")]
    public async Task<long> Hash_UpdateProfile()
    {
        var profile = _collections.Hash<string>($"profile:{_userId}");
        return await profile.SetAsync("name", "John Doe");
    }

    [Benchmark(Description = "Hash: Get profile field (Get)")]
    public async Task<string?> Hash_GetProfileField()
    {
        var profile = _collections.Hash<string>($"profile:{_userId}");
        return await profile.GetAsync("name");
    }

    [Benchmark(Description = "Hash: Get 3 profile fields (GetMany)")]
    public async Task<string?[]> Hash_GetProfileFields()
    {
        var profile = _collections.Hash<string>($"profile:{_userId}");
        return await profile.GetManyAsync(new[] { "name", "email", "phone" });
    }

    [Benchmark(Description = "Hash: Get 10 profile fields (GetMany)")]
    public async Task<string?[]> Hash_GetManyProfileFields()
    {
        var profile = _collections.Hash<string>($"profile:{_userId}");
        return await profile.GetManyAsync(new[]
        {
            "name", "email", "phone", "address", "city",
            "state", "zip", "country", "bio", "avatar"
        });
    }

    // ===========================
    // HASH Bulk Operations
    // ===========================

    [Benchmark(Description = "Hash: Build complete profile (10 Sets)")]
    public async Task Hash_BuildProfile()
    {
        var profile = _collections.Hash<string>($"profile:new:{Guid.NewGuid()}");

        await profile.SetAsync("name", "John Doe");
        await profile.SetAsync("email", "john@example.com");
        await profile.SetAsync("phone", "+1-555-1234");
        await profile.SetAsync("address", "123 Main St");
        await profile.SetAsync("city", "San Francisco");
        await profile.SetAsync("state", "CA");
        await profile.SetAsync("zip", "94102");
        await profile.SetAsync("country", "USA");
        await profile.SetAsync("bio", "Software developer");
        await profile.SetAsync("avatar", "https://example.com/avatar.jpg");
    }

    // ===========================
    // Mixed Workload (Real-World Scenario)
    // ===========================

    [Benchmark(Description = "Mixed: Complete shopping flow")]
    public async Task Mixed_ShoppingFlow()
    {
        var userId = $"user-{Guid.NewGuid()}";

        // 1. Mark user online
        var activeUsers = _collections.Set<string>("users:active");
        await activeUsers.AddAsync(userId);

        // 2. Add items to cart
        var cart = _collections.List<CartItem>($"cart:{userId}");
        await cart.PushBackAsync(_cartItems[0]);
        await cart.PushBackAsync(_cartItems[1]);
        await cart.PushBackAsync(_cartItems[2]);

        // 3. Get cart count
        await cart.LengthAsync();

        // 4. View cart
        await cart.RangeAsync(0, -1);

        // 5. Update profile
        var profile = _collections.Hash<string>($"profile:{userId}");
        await profile.SetAsync("last_activity", DateTime.UtcNow.ToString("O"));

        // 6. Checkout (remove items)
        await cart.PopFrontAsync();
        await cart.PopFrontAsync();
        await cart.PopFrontAsync();

        // 7. Mark user offline
        await activeUsers.RemoveAsync(userId);
    }

    [Benchmark(Description = "Mixed: User session lifecycle")]
    public async Task Mixed_UserSessionLifecycle()
    {
        var userId = $"user-{Guid.NewGuid()}";

        // Login: Create session hash
        var session = _collections.Hash<string>($"session:{userId}");
        await session.SetAsync("user_id", userId);
        await session.SetAsync("login_time", DateTime.UtcNow.ToString("O"));
        await session.SetAsync("ip", "192.168.1.100");

        // Activity: Update session
        await session.SetAsync("last_seen", DateTime.UtcNow.ToString("O"));
        await session.SetAsync("page_views", "42");

        // Check active: Add to set
        var activeSessions = _collections.Set<string>("sessions:active");
        await activeSessions.AddAsync(userId);

        // Get session data
        await session.GetManyAsync(new[] { "user_id", "login_time", "last_seen" });

        // Logout: Remove from active set
        await activeSessions.RemoveAsync(userId);
    }
}

public record CartItem(Guid ProductId, string ProductName, decimal Price, int Quantity, DateTime AddedAt);

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using VapeCache.Guards;

namespace VapeCache.Extensions.EntityFrameworkCore;

/// <summary>
/// DbContext options extensions for wiring VapeCache EF interceptors.
/// </summary>
public static class VapeCacheEfCoreDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Resolves and adds VapeCache EF interceptors from the provided service provider.
    /// </summary>
    public static DbContextOptionsBuilder UseVapeCacheEntityFrameworkCore(
        this DbContextOptionsBuilder optionsBuilder,
        IServiceProvider serviceProvider)
    {
        ParanoiaThrowGuard.Against.NotNull(optionsBuilder);
        ParanoiaThrowGuard.Against.NotNull(serviceProvider);

        var interceptors = serviceProvider.GetService(typeof(IEnumerable<IInterceptor>)) as IEnumerable<IInterceptor>;
        if (interceptors is null)
            return optionsBuilder;

        foreach (var interceptor in interceptors)
        {
            if (interceptor is not VapeCacheEfCoreCommandInterceptor and not VapeCacheEfCoreSaveChangesInterceptor)
                continue;

            optionsBuilder.AddInterceptors(interceptor);
        }

        return optionsBuilder;
    }

    /// <summary>
    /// Generic overload for fluent DbContext options usage.
    /// </summary>
    public static DbContextOptionsBuilder<TContext> UseVapeCacheEntityFrameworkCore<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        IServiceProvider serviceProvider)
        where TContext : DbContext
    {
        ParanoiaThrowGuard.Against.NotNull(optionsBuilder);
        ParanoiaThrowGuard.Against.NotNull(serviceProvider);

        UseVapeCacheEntityFrameworkCore((DbContextOptionsBuilder)optionsBuilder, serviceProvider);
        return optionsBuilder;
    }
}

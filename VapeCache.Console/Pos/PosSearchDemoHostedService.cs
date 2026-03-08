using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VapeCache.Console.Pos;

internal sealed class PosSearchDemoHostedService(
    IHostApplicationLifetime hostLifetime,
    IOptionsMonitor<PosSearchDemoOptions> optionsMonitor,
    PosCatalogSearchService searchService,
    ILogger<PosSearchDemoHostedService> logger) : BackgroundService, IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = optionsMonitor.CurrentValue;
        if (!options.Enabled)
            return;

        logger.LogInformation("==================================================");
        logger.LogInformation("  POS SEARCH DEMO - CACHE FIRST, DB SECOND");
        logger.LogInformation("==================================================");
        logger.LogInformation("SQLite Catalog Path: {Path}", options.SqlitePath);
        logger.LogInformation("Redis Search Index: {Index}", options.RedisIndexName);
        logger.LogInformation("Redis Key Prefix:   {Prefix}", options.RedisKeyPrefix);

        await searchService.InitializeAsync(stoppingToken).ConfigureAwait(false);

        await RunQueryAsync("Cashier pass #1", options.CashierQuery, stoppingToken).ConfigureAwait(false);
        await RunQueryAsync("Cashier pass #2", options.CashierQuery, stoppingToken).ConfigureAwait(false);
        await RunQueryAsync("Code lookup #1", $"code:{options.LookupCode}", stoppingToken).ConfigureAwait(false);
        await RunQueryAsync("Code lookup #2", $"code:{options.LookupCode}", stoppingToken).ConfigureAwait(false);
        await RunQueryAsync("UPC lookup #1", $"upc:{options.LookupUpc}", stoppingToken).ConfigureAwait(false);
        await RunQueryAsync("UPC lookup #2", $"upc:{options.LookupUpc}", stoppingToken).ConfigureAwait(false);

        logger.LogInformation("POS search demo complete.");
        if (options.StopHostOnCompletion)
        {
            logger.LogInformation("Stopping host after POS search demo completion.");
            hostLifetime.StopApplication();
        }
    }

    private async ValueTask RunQueryAsync(string phase, string query, CancellationToken ct)
    {
        var result = await searchService.SearchAsync(query, ct).ConfigureAwait(false);
        logger.LogInformation(
            "[{Phase}] Query='{Query}' Source={Source} SearchAvailable={SearchAvailable} ResultCount={Count} SearchIds={Ids} ElapsedMs={Elapsed:F2}",
            phase,
            result.Query,
            result.Source,
            result.SearchModuleAvailable,
            result.Products.Count,
            result.SearchDocumentIds,
            result.Elapsed.TotalMilliseconds);

        var top = Math.Min(3, result.Products.Count);
        for (var i = 0; i < top; i++)
        {
            var item = result.Products[i];
            logger.LogInformation(
                "  #{Rank}: {Code} | {Name} | ${Price} | Stock={Stock}",
                i + 1,
                item.Code,
                item.Name,
                item.Price,
                item.StockQuantity);
        }
    }
}

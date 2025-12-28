# VapeCache vs StackExchange.Redis Performance Comparison Runner
# Run this script to compare VapeCache and StackExchange.Redis side-by-side

param(
    [int]$ShopperCount = 10000
)

Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  VapeCache vs StackExchange.Redis Performance Showdown" -ForegroundColor Cyan
Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# Set environment variable to trigger comparison mode
$env:VAPECACHE_RUN_COMPARISON = "true"

# Run the console app with --compare flag
& dotnet run --project "VapeCache.Console/VapeCache.Console.csproj" -c Release -- --compare

# Clear the environment variable
$env:VAPECACHE_RUN_COMPARISON = $null

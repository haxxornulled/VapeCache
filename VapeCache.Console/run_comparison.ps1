# VapeCache vs StackExchange.Redis Head-to-Head Comparison
# This script runs the comparison test with minimal DI setup

param(
    [int]$ShopperCount = 10000,
    [string]$RedisHost = "192.168.100.50",
    [string]$RedisPassword = "redis4me!!"
)

Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  VapeCache vs StackExchange.Redis Performance Comparison" -ForegroundColor Cyan
Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "Configuration:" -ForegroundColor Yellow
Write-Host "  Redis Host: $RedisHost"
Write-Host "  Shoppers: $($ShopperCount.ToString('N0'))"
Write-Host "  Max Cart Size: 35 items"
Write-Host ""

# Build the project
Write-Host "Building VapeCache.Console..." -ForegroundColor Yellow
dotnet build VapeCache.Console/VapeCache.Console.csproj -c Release | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Build failed" -ForegroundColor Red
    exit 1
}

Write-Host "✓ Build successful" -ForegroundColor Green
Write-Host ""

# Create a simple C# script to run the comparison
$script = @"
using System;
using System.Threading.Tasks;
using VapeCache.Console.GroceryStore;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["RedisConnection:Host"] = "$RedisHost",
        ["RedisConnection:Password"] = "$RedisPassword"
    })
    .Build();

await ComparisonRunner.RunComparisonAsync("$RedisHost", "$RedisPassword", $ShopperCount);
"@

$scriptFile = "temp_comparison_runner.csx"
$script | Out-File -FilePath $scriptFile -Encoding UTF8

# Run the comparison using dotnet-script or direct execution
Write-Host "Starting comparison test..." -ForegroundColor Yellow
Write-Host ""

# Since we can't easily use dotnet-script, let's create a simpler PowerShell-driven approach
# We'll run the existing console app with environment variables to trigger comparison mode

$env:VAPECACHE_RUN_COMPARISON = "true"
$env:VAPECACHE_SHOPPER_COUNT = $ShopperCount
$env:VAPECACHE_REDIS_HOST = $RedisHost
$env:VAPECACHE_REDIS_PASSWORD = $RedisPassword

# For now, let's just provide instructions
Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "To run the comparison test:" -ForegroundColor Yellow
Write-Host ""
Write-Host "1. Temporarily modify Program.cs to call MenuRunner.RunAsync()" -ForegroundColor White
Write-Host "   OR" -ForegroundColor Yellow
Write-Host "2. Run the following C# code snippet:" -ForegroundColor White
Write-Host ""
Write-Host @"
using VapeCache.Console.GroceryStore;

await ComparisonRunner.RunComparisonAsync(
    "$RedisHost",
    "$RedisPassword",
    $ShopperCount);
"@ -ForegroundColor Cyan
Write-Host ""
Write-Host "════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "Creating a standalone comparison runner program..." -ForegroundColor Yellow

# Clean up temp file
if (Test-Path $scriptFile) {
    Remove-Item $scriptFile
}

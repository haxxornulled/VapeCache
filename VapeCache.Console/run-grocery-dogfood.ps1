param(
    [string]$ConnectionString = "",
    [int]$ConcurrentShoppers = 200,
    [int]$TotalShoppers = 5000,
    [int]$TargetDurationSeconds = 30,
    [ValidateSet("FullTilt", "Balanced", "LowLatency")]
    [string]$Profile = "FullTilt",
    [ValidateSet("Trace", "Debug", "Information", "Warning", "Error", "Critical", "None")]
    [string]$BenchLogLevel = "Debug",
    [ValidateSet("true", "false")]
    [string]$GroceryVerbose = "true",
    [switch]$EnablePluginDemo
)

$ErrorActionPreference = "Stop"

if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
    $env:VAPECACHE_REDIS_CONNECTIONSTRING = $ConnectionString
    $env:RedisSecret__EnvVar = "VAPECACHE_REDIS_CONNECTIONSTRING"
}

$env:GroceryStoreStress__Enabled = "true"
$env:GroceryStoreStress__StopHostOnCompletion = "true"
$env:GroceryStoreStress__ConcurrentShoppers = "$ConcurrentShoppers"
$env:GroceryStoreStress__TotalShoppers = "$TotalShoppers"
$env:GroceryStoreStress__TargetDurationSeconds = "$TargetDurationSeconds"
$env:GroceryStoreStress__StartupDelaySeconds = "1"
$env:GroceryStoreStress__CountdownSeconds = "1"
$env:VAPECACHE_BENCH_LOG_LEVEL = $BenchLogLevel
$env:VAPECACHE_GROCERYSTORE_VERBOSE = $GroceryVerbose.ToLowerInvariant()

# Tuned multiplexer defaults for high-throughput console demos.
$env:RedisMultiplexer__Connections = "12"
$env:RedisMultiplexer__MaxInFlightPerConnection = "8192"
$env:RedisMultiplexer__EnableCoalescedSocketWrites = "true"
$env:RedisMultiplexer__EnableAutoscaling = "true"
$env:RedisMultiplexer__MinConnections = "12"
$env:RedisMultiplexer__MaxConnections = "24"

switch ($Profile) {
    "FullTilt" {
        $env:GroceryStoreStress__BrowseChancePercent = "70"
        $env:GroceryStoreStress__BrowseMinProducts = "10"
        $env:GroceryStoreStress__BrowseMaxProducts = "25"
        $env:GroceryStoreStress__FlashSaleJoinChancePercent = "30"
        $env:GroceryStoreStress__AddToCartChancePercent = "50"
        $env:GroceryStoreStress__CartItemsMin = "15"
        $env:GroceryStoreStress__CartItemsMax = "35"
        $env:GroceryStoreStress__CartItemQuantityMin = "1"
        $env:GroceryStoreStress__CartItemQuantityMax = "10"
        $env:GroceryStoreStress__ViewCartChancePercent = "30"
        $env:GroceryStoreStress__CheckoutChancePercent = "20"
        $env:GroceryStoreStress__RemoveFromCartChancePercent = "10"
        $env:GroceryStoreStress__StatsIntervalSeconds = "10"
    }
    "Balanced" {
        $env:GroceryStoreStress__BrowseChancePercent = "65"
        $env:GroceryStoreStress__BrowseMinProducts = "8"
        $env:GroceryStoreStress__BrowseMaxProducts = "16"
        $env:GroceryStoreStress__FlashSaleJoinChancePercent = "25"
        $env:GroceryStoreStress__AddToCartChancePercent = "45"
        $env:GroceryStoreStress__CartItemsMin = "8"
        $env:GroceryStoreStress__CartItemsMax = "16"
        $env:GroceryStoreStress__CartItemQuantityMin = "1"
        $env:GroceryStoreStress__CartItemQuantityMax = "6"
        $env:GroceryStoreStress__ViewCartChancePercent = "25"
        $env:GroceryStoreStress__CheckoutChancePercent = "15"
        $env:GroceryStoreStress__RemoveFromCartChancePercent = "8"
        $env:GroceryStoreStress__StatsIntervalSeconds = "10"
    }
    "LowLatency" {
        $env:GroceryStoreStress__BrowseChancePercent = "55"
        $env:GroceryStoreStress__BrowseMinProducts = "4"
        $env:GroceryStoreStress__BrowseMaxProducts = "10"
        $env:GroceryStoreStress__FlashSaleJoinChancePercent = "20"
        $env:GroceryStoreStress__AddToCartChancePercent = "35"
        $env:GroceryStoreStress__CartItemsMin = "4"
        $env:GroceryStoreStress__CartItemsMax = "10"
        $env:GroceryStoreStress__CartItemQuantityMin = "1"
        $env:GroceryStoreStress__CartItemQuantityMax = "4"
        $env:GroceryStoreStress__ViewCartChancePercent = "20"
        $env:GroceryStoreStress__CheckoutChancePercent = "10"
        $env:GroceryStoreStress__RemoveFromCartChancePercent = "5"
        $env:GroceryStoreStress__StatsIntervalSeconds = "5"
    }
}

$env:PluginDemo__Enabled = if ($EnablePluginDemo) { "true" } else { "false" }

Write-Host "Running GroceryStore dogfood workload..."
Write-Host "Concurrent shoppers: $ConcurrentShoppers"
Write-Host "Total shoppers: $TotalShoppers"
Write-Host "Target duration: $TargetDurationSeconds seconds"
Write-Host "Workload profile: $Profile"
Write-Host "Logging: BenchLogLevel=$($env:VAPECACHE_BENCH_LOG_LEVEL) GroceryVerbose=$($env:VAPECACHE_GROCERYSTORE_VERBOSE)"
Write-Host "Plugin demo enabled: $($EnablePluginDemo.IsPresent)"

dotnet run --project "$PSScriptRoot\VapeCache.Console.csproj" -c Release

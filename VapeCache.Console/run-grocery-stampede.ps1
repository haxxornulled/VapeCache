param(
    [string]$ConnectionString = "",
    [string]$RedisHost = "127.0.0.1",
    [int]$RedisPort = 6379,
    [string]$RedisUsername = "",
    [string]$RedisPassword = "",
    [int]$ConcurrentShoppers = 4000,
    [int]$TotalShoppers = 300000,
    [int]$TargetDurationSeconds = 240,
    [int]$CartItems = 70,
    [string]$HotProductId = "prod-025",
    [ValidateRange(0, 100)]
    [int]$HotProductBiasPercent = 100,
    [bool]$ForceHotProductFlashSale = $true,
    [ValidateSet("Trace", "Debug", "Information", "Warning", "Error", "Critical", "None")]
    [string]$BenchLogLevel = "Debug",
    [ValidateSet("true", "false")]
    [string]$GroceryVerbose = "true"
)

$ErrorActionPreference = "Stop"

if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
    $env:VAPECACHE_REDIS_CONNECTIONSTRING = $ConnectionString
    $env:RedisSecret__EnvVar = "VAPECACHE_REDIS_CONNECTIONSTRING"
    $env:RedisConnection__ConnectionString = ""
}
else {
    $env:RedisConnection__ConnectionString = ""
    $env:RedisConnection__Host = $RedisHost
    $env:RedisConnection__Port = "$RedisPort"
    $env:RedisConnection__Username = $RedisUsername
    $env:RedisConnection__Password = $RedisPassword
}

# High-throughput multiplexer defaults.
$env:RedisMultiplexer__Connections = "12"
$env:RedisMultiplexer__MaxInFlightPerConnection = "8192"
$env:RedisMultiplexer__EnableCoalescedSocketWrites = "true"
$env:RedisMultiplexer__EnableAutoscaling = "true"
$env:RedisMultiplexer__MinConnections = "12"
$env:RedisMultiplexer__MaxConnections = "24"

# Hot-key stampede workload.
$env:GroceryStoreStress__Enabled = "true"
$env:GroceryStoreStress__StopHostOnCompletion = "true"
$env:GroceryStoreStress__ConcurrentShoppers = "$ConcurrentShoppers"
$env:GroceryStoreStress__TotalShoppers = "$TotalShoppers"
$env:GroceryStoreStress__TargetDurationSeconds = "$TargetDurationSeconds"
$env:GroceryStoreStress__StartupDelaySeconds = "1"
$env:GroceryStoreStress__CountdownSeconds = "1"
$env:GroceryStoreStress__BrowseChancePercent = "100"
$env:GroceryStoreStress__BrowseMinProducts = "1"
$env:GroceryStoreStress__BrowseMaxProducts = "1"
$env:GroceryStoreStress__FlashSaleJoinChancePercent = "0"
$env:GroceryStoreStress__AddToCartChancePercent = "100"
$env:GroceryStoreStress__CartItemsMin = "$CartItems"
$env:GroceryStoreStress__CartItemsMax = "$CartItems"
$env:GroceryStoreStress__CartItemQuantityMin = "1"
$env:GroceryStoreStress__CartItemQuantityMax = "1"
$env:GroceryStoreStress__ViewCartChancePercent = "0"
$env:GroceryStoreStress__CheckoutChancePercent = "0"
$env:GroceryStoreStress__RemoveFromCartChancePercent = "0"
$env:GroceryStoreStress__StatsIntervalSeconds = "10"
$env:GroceryStoreStress__HotProductId = $HotProductId
$env:GroceryStoreStress__HotProductBiasPercent = "$HotProductBiasPercent"
$env:GroceryStoreStress__ForceHotProductFlashSale = $ForceHotProductFlashSale.ToString().ToLowerInvariant()
$env:VAPECACHE_BENCH_LOG_LEVEL = $BenchLogLevel
$env:VAPECACHE_GROCERYSTORE_VERBOSE = $GroceryVerbose.ToLowerInvariant()

$env:PluginDemo__Enabled = "false"
$env:LiveDemo__Enabled = "false"

Write-Host "Running GroceryStore hot-key stampede..."
Write-Host "Concurrent shoppers: $ConcurrentShoppers"
Write-Host "Total shoppers: $TotalShoppers"
Write-Host "Cart items per shopper: $CartItems"
Write-Host "Hot product: $HotProductId (bias $HotProductBiasPercent%)"
Write-Host "Mux: Connections=12 InFlight=8192 Autoscaling=true Coalesce=true"
Write-Host "Logging: BenchLogLevel=$($env:VAPECACHE_BENCH_LOG_LEVEL) GroceryVerbose=$($env:VAPECACHE_GROCERYSTORE_VERBOSE)"

dotnet run --project "$PSScriptRoot\VapeCache.Console.csproj" -c Release --no-build

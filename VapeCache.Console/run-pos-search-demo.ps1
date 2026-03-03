param(
    [string]$ConnectionString = "",
    [string]$RedisHost = "127.0.0.1",
    [int]$RedisPort = 6379,
    [string]$RedisUsername = "",
    [string]$RedisPassword = "",
    [string]$SqlitePath = "$env:TEMP\\vapecache\\pos\\catalog.db",
    [string]$RedisIndexName = "idx:pos:catalog",
    [string]$RedisKeyPrefix = "pos:sku:",
    [string]$CashierQuery = "pencil",
    [string]$LookupCode = "PCL-0001",
    [string]$LookupUpc = "012345678901",
    [int]$TopResults = 10,
    [int]$SeedProductCount = 2000
)

$ErrorActionPreference = "Stop"

if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
    $env:VAPECACHE_REDIS_CONNECTIONSTRING = $ConnectionString
    $env:RedisSecret__EnvVar = "VAPECACHE_REDIS_CONNECTIONSTRING"
}
else {
    $env:RedisConnection__ConnectionString = ""
    $env:RedisConnection__Host = $RedisHost
    $env:RedisConnection__Port = "$RedisPort"
    $env:RedisConnection__Username = $RedisUsername
    $env:RedisConnection__Password = $RedisPassword
}

# Keep the demo isolated from other hosted workloads.
$env:GroceryStoreStress__Enabled = "false"
$env:PluginDemo__Enabled = "false"
$env:LiveDemo__Enabled = "false"

$env:PosSearchDemo__Enabled = "true"
$env:PosSearchDemo__StopHostOnCompletion = "true"
$env:PosSearchDemo__SqlitePath = $SqlitePath
$env:PosSearchDemo__SeedIfEmpty = "true"
$env:PosSearchDemo__SeedProductCount = "$SeedProductCount"
$env:PosSearchDemo__RedisIndexName = $RedisIndexName
$env:PosSearchDemo__RedisKeyPrefix = $RedisKeyPrefix
$env:PosSearchDemo__TopResults = "$TopResults"
$env:PosSearchDemo__CashierQuery = $CashierQuery
$env:PosSearchDemo__LookupCode = $LookupCode
$env:PosSearchDemo__LookupUpc = $LookupUpc

Write-Host "Running POS Search demo (cache-first via RediSearch, SQLite fallback)..."
Write-Host "SQLite Path: $SqlitePath"
Write-Host "Redis Index: $RedisIndexName"
Write-Host "Redis Prefix: $RedisKeyPrefix"
Write-Host "Cashier Query: $CashierQuery"
Write-Host "Lookup Code: $LookupCode"
Write-Host "Lookup UPC: $LookupUpc"

dotnet run --project "$PSScriptRoot\VapeCache.Console.csproj" -c Release --no-build

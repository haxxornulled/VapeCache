param(
    [string]$ConnectionString = "",
    [string]$RedisHost = "127.0.0.1",
    [int]$RedisPort = 6379,
    [string]$RedisUsername = "",
    [string]$RedisPassword = "",
    [string]$SqlitePath = "$env:TEMP\\vapecache\\pos\\catalog.db",
    [string]$RedisIndexName = "idx:pos:catalog:load",
    [string]$RedisKeyPrefix = "pos:load:sku:",
    [string]$CashierQuery = "pencil",
    [string]$LookupCode = "PCL-0001",
    [string]$LookupUpc = "012345678901",
    [int]$TopResults = 10,
    [int]$SeedProductCount = 2000,
    [int]$Concurrency = 256,
    [string]$Duration = "00:02:00",
    [int]$TargetShoppersPerSecond = 2200,
    [switch]$EnableAutoRamp,
    [string]$RampSteps = "1600,2000,2400,2800",
    [string]$RampStepDuration = "00:00:20",
    [bool]$StopOnFirstUnstable = $true,
    [bool]$TreatOpenCircuitAsUnstable = $true,
    [double]$MaxFailurePercent = 0.5,
    [double]$MaxP95Ms = 30.0,
    [string]$HotQuery = "code:TV-0099",
    [int]$HotQueryPercent = 90,
    [int]$CashierQueryPercent = 7,
    [int]$LookupUpcPercent = 3,
    [string]$LogEvery = "00:00:05"
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

# Keep the load run isolated from other hosted workloads.
$env:GroceryStoreStress__Enabled = "false"
$env:PluginDemo__Enabled = "false"
$env:LiveDemo__Enabled = "false"
$env:PosSearchDemo__Enabled = "false"

# Search/index/catalog settings used by PosCatalogSearchService.
$env:PosSearchDemo__SqlitePath = $SqlitePath
$env:PosSearchDemo__SeedIfEmpty = "true"
$env:PosSearchDemo__SeedProductCount = "$SeedProductCount"
$env:PosSearchDemo__RedisIndexName = $RedisIndexName
$env:PosSearchDemo__RedisKeyPrefix = $RedisKeyPrefix
$env:PosSearchDemo__TopResults = "$TopResults"
$env:PosSearchDemo__CashierQuery = $CashierQuery
$env:PosSearchDemo__LookupCode = $LookupCode
$env:PosSearchDemo__LookupUpc = $LookupUpc

$env:PosSearchLoad__Enabled = "true"
$env:PosSearchLoad__StopHostOnCompletion = "true"
$env:PosSearchLoad__Duration = $Duration
$env:PosSearchLoad__Concurrency = "$Concurrency"
$env:PosSearchLoad__TargetShoppersPerSecond = "$TargetShoppersPerSecond"
$env:PosSearchLoad__EnableAutoRamp = $(if ($EnableAutoRamp.IsPresent) { "true" } else { "false" })
$env:PosSearchLoad__RampSteps = $RampSteps
$env:PosSearchLoad__RampStepDuration = $RampStepDuration
$env:PosSearchLoad__StopOnFirstUnstable = $(if ($StopOnFirstUnstable) { "true" } else { "false" })
$env:PosSearchLoad__TreatOpenCircuitAsUnstable = $(if ($TreatOpenCircuitAsUnstable) { "true" } else { "false" })
$env:PosSearchLoad__MaxFailurePercent = $MaxFailurePercent.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$env:PosSearchLoad__MaxP95Ms = $MaxP95Ms.ToString([System.Globalization.CultureInfo]::InvariantCulture)
$env:PosSearchLoad__HotQuery = $HotQuery
$env:PosSearchLoad__HotQueryPercent = "$HotQueryPercent"
$env:PosSearchLoad__CashierQueryPercent = "$CashierQueryPercent"
$env:PosSearchLoad__LookupUpcPercent = "$LookupUpcPercent"
$env:PosSearchLoad__LogEvery = $LogEvery

Write-Host "Running POS search load test (cache-stampede simulation)..."
Write-Host "Duration: $Duration"
Write-Host "Concurrency: $Concurrency"
Write-Host "Target Shoppers/s: $TargetShoppersPerSecond (0 = unlimited)"
Write-Host "Auto Ramp: $($EnableAutoRamp.IsPresent)"
Write-Host "Ramp Steps: $RampSteps"
Write-Host "Ramp Step Duration: $RampStepDuration"
Write-Host "Stability: MaxFailure=$MaxFailurePercent% MaxP95=${MaxP95Ms}ms BreakerUnstable=$TreatOpenCircuitAsUnstable StopOnFirstUnstable=$StopOnFirstUnstable"
Write-Host "Hot Query: $HotQuery"
Write-Host "Hot/Cashier/UPC Mix: $HotQueryPercent% / $CashierQueryPercent% / $LookupUpcPercent%"
Write-Host "SQLite Path: $SqlitePath"
Write-Host "Redis Index: $RedisIndexName"
Write-Host "Redis Prefix: $RedisKeyPrefix"

dotnet run --project "$PSScriptRoot\VapeCache.Console.csproj" -c Release --no-build

param(
    [string]$RedisConnectionString = "",
    [string]$RedisHost = "127.0.0.1",
    [int]$RedisPort = 6379,
    [string]$RedisUsername = "",
    [string]$RedisPassword = "",
    [ValidateSet("apples", "optimized", "both")]
    [string]$Track = "both",
    [int]$ShopperCount = 20000,
    [int]$MinCartSize = 15,
    [int]$MaxCartSize = 40,
    [int]$Runs = 1,
    [int]$WarmupRuns = 0,
    [int]$MaxDegree = 0,
    [int]$CheckoutLanes = 128,
    [ValidateSet("gold-standard", "moduleless-safe")]
    [string]$WorkloadProfile = "gold-standard",
    [ValidateSet("Trace", "Debug", "Information", "Warning", "Error", "Critical", "None")]
    [string]$BenchLogLevel = "Information",
    [int]$LiveProgressIntervalSeconds = 5,
    [switch]$CleanupKeys,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

if ($ShopperCount -le 0) {
    throw "ShopperCount must be greater than zero."
}

if ($Runs -le 0) {
    throw "Runs must be greater than zero."
}

if ($WarmupRuns -lt 0) {
    throw "WarmupRuns cannot be negative."
}

if ($MinCartSize -le 0) {
    throw "MinCartSize must be greater than zero."
}

if ($MaxCartSize -lt $MinCartSize) {
    throw "MaxCartSize must be greater than or equal to MinCartSize."
}

if ($LiveProgressIntervalSeconds -le 0) {
    throw "LiveProgressIntervalSeconds must be greater than zero."
}

if ($CheckoutLanes -le 0) {
    throw "CheckoutLanes must be greater than zero."
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "VapeCache.Console\VapeCache.Console.csproj"

if (-not $SkipBuild) {
    dotnet build $projectPath -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

if (-not [string]::IsNullOrWhiteSpace($RedisConnectionString)) {
    $env:VAPECACHE_REDIS_CONNECTIONSTRING = $RedisConnectionString
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

$env:VAPECACHE_RUN_COMPARISON = "true"
$env:VAPECACHE_BENCH_TRACK = $Track
$env:VAPECACHE_BENCH_SHOPPERS = "$ShopperCount"
$env:VAPECACHE_MIN_CART_SIZE = "$MinCartSize"
$env:VAPECACHE_MAX_CART_SIZE = "$MaxCartSize"
$env:VAPECACHE_BENCH_RUNS = "$Runs"
$env:VAPECACHE_BENCH_WARMUPS = "$WarmupRuns"
$env:VAPECACHE_BENCH_LOG_LEVEL = $BenchLogLevel
$env:VAPECACHE_COMPARE_LIVE_PROGRESS = "true"
$env:VAPECACHE_COMPARE_LIVE_INTERVAL_SECONDS = "$LiveProgressIntervalSeconds"
$env:VAPECACHE_BENCH_CHECKOUT_LANES = "$CheckoutLanes"
$env:VAPECACHE_BENCH_CLEANUP_RUN_KEYS = if ($CleanupKeys) { "true" } else { "false" }

switch ($WorkloadProfile) {
    "gold-standard" {
        $env:VAPECACHE_BENCH_ENABLE_COMMAND_COVERAGE = "true"
        $env:VAPECACHE_BENCH_ENABLE_TAG_INVALIDATION = "true"
        $env:VAPECACHE_BENCH_ENABLE_CHECKOUT_RECEIPT_FLOW = "true"
    }
    "moduleless-safe" {
        $env:VAPECACHE_BENCH_ENABLE_COMMAND_COVERAGE = "false"
        $env:VAPECACHE_BENCH_ENABLE_TAG_INVALIDATION = "true"
        $env:VAPECACHE_BENCH_ENABLE_CHECKOUT_RECEIPT_FLOW = "false"
    }
}

if ($MaxDegree -gt 0) {
    $env:VAPECACHE_BENCH_MAX_DEGREE = "$MaxDegree"
}
else {
    Remove-Item Env:VAPECACHE_BENCH_MAX_DEGREE -ErrorAction SilentlyContinue
}

Write-Host "Grocery live comparison runner"
Write-Host "Project: $projectPath"
Write-Host "Mode: --compare-live (non-isolated, live progress)"
Write-Host "Track: $Track"
Write-Host "Shoppers: $ShopperCount"
Write-Host "Cart size: $MinCartSize..$MaxCartSize"
Write-Host "Runs: $Runs (warmups: $WarmupRuns)"
Write-Host "Live progress interval: ${LiveProgressIntervalSeconds}s"
Write-Host "Checkout lanes: $CheckoutLanes"
Write-Host "Workload profile: $WorkloadProfile"
Write-Host "Log level: $BenchLogLevel"

if (-not [string]::IsNullOrWhiteSpace($RedisConnectionString)) {
    Write-Host "Redis source: connection string"
}
else {
    Write-Host ("Redis endpoint: {0}:{1}" -f $RedisHost, $RedisPort)
}

dotnet run --project $projectPath -c Release --no-build -- --compare-live
exit $LASTEXITCODE

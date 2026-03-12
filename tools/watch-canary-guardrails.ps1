param(
    [Alias("BaseUrl")]
    [string]$AdminBaseUrl = "http://localhost:5000",
    [string]$Prefix = "/vapecache",
    [int]$DurationMinutes = 10,
    [int]$SampleIntervalSeconds = 15,
    [int]$WarmupSamples = 3,
    [double]$MinHitRate = 0.70,
    [double]$MaxFallbackEventsPerMinute = 10,
    [switch]$AllowBreakerOpen
)

$ErrorActionPreference = "Stop"

if ($DurationMinutes -le 0) { throw "DurationMinutes must be greater than zero." }
if ($SampleIntervalSeconds -le 0) { throw "SampleIntervalSeconds must be greater than zero." }
if ($WarmupSamples -lt 0) { throw "WarmupSamples cannot be negative." }
if ($MinHitRate -lt 0 -or $MinHitRate -gt 1) { throw "MinHitRate must be in range 0..1." }
if ($MaxFallbackEventsPerMinute -lt 0) { throw "MaxFallbackEventsPerMinute must be greater than or equal to zero." }

function Join-Url([string]$baseUrl, [string]$path)
{
    $left = $baseUrl.TrimEnd('/')
    $right = $path
    if (-not $right.StartsWith("/")) { $right = "/" + $right }
    return "$left$right"
}

function Get-Stats([string]$baseUrl, [string]$prefix)
{
    $uri = Join-Url $baseUrl "$prefix/stats"
    return Invoke-RestMethod -Method Get -Uri $uri -TimeoutSec 10
}

function Get-Status([string]$baseUrl, [string]$prefix)
{
    $uri = Join-Url $baseUrl "$prefix/status"
    return Invoke-RestMethod -Method Get -Uri $uri -TimeoutSec 10
}

$samples = [Math]::Floor(($DurationMinutes * 60) / $SampleIntervalSeconds)
if ($samples -lt 1) { $samples = 1 }

Write-Host "Canary guardrail watch started."
Write-Host " AdminBaseUrl=$AdminBaseUrl Prefix=$Prefix DurationMinutes=$DurationMinutes SampleIntervalSeconds=$SampleIntervalSeconds"
Write-Host " MinHitRate=$MinHitRate MaxFallbackEventsPerMinute=$MaxFallbackEventsPerMinute WarmupSamples=$WarmupSamples AllowBreakerOpen=$($AllowBreakerOpen.IsPresent)"

$previousStats = Get-Stats -baseUrl $AdminBaseUrl -prefix $Prefix
$violations = New-Object System.Collections.Generic.List[string]
$worstFallbackPerMinute = 0d
$lowestHitRate = 1d

for ($i = 1; $i -le $samples; $i++)
{
    Start-Sleep -Seconds $SampleIntervalSeconds

    $stats = Get-Stats -baseUrl $AdminBaseUrl -prefix $Prefix
    $status = Get-Status -baseUrl $AdminBaseUrl -prefix $Prefix

    $hitRate = [double]$stats.HitRate
    $fallbackNow = [long]$stats.FallbackToMemory
    $fallbackPrev = [long]$previousStats.FallbackToMemory
    $fallbackDelta = [Math]::Max(0L, $fallbackNow - $fallbackPrev)
    $fallbackPerMinute = [double]$fallbackDelta * (60.0 / [double]$SampleIntervalSeconds)
    $breakerOpen = [bool]$status.CircuitBreaker.IsOpen

    if ($fallbackPerMinute -gt $worstFallbackPerMinute) { $worstFallbackPerMinute = $fallbackPerMinute }
    if ($hitRate -lt $lowestHitRate) { $lowestHitRate = $hitRate }

    $ts = (Get-Date).ToString("HH:mm:ss")
    Write-Host ("[{0}] sample={1}/{2} hitRate={3:P2} fallbackDelta={4} fallbackPerMin={5:N2} breakerOpen={6}" -f
        $ts,
        $i,
        $samples,
        $hitRate,
        $fallbackDelta,
        $fallbackPerMinute,
        $breakerOpen)

    if (-not $AllowBreakerOpen.IsPresent -and $breakerOpen)
    {
        $violations.Add("Circuit breaker is OPEN at sample $i.")
    }

    if ($i -gt $WarmupSamples)
    {
        if ($hitRate -lt $MinHitRate)
        {
            $violations.Add(("Hit rate {0:P2} dropped below minimum {1:P2} at sample {2}." -f $hitRate, $MinHitRate, $i))
        }

        if ($fallbackPerMinute -gt $MaxFallbackEventsPerMinute)
        {
            $violations.Add(("Fallback rate {0:N2}/min exceeded max {1:N2}/min at sample {2}." -f $fallbackPerMinute, $MaxFallbackEventsPerMinute, $i))
        }
    }

    $previousStats = $stats
}

Write-Host ""
Write-Host ("Canary summary: lowestHitRate={0:P2}, worstFallbackPerMinute={1:N2}" -f $lowestHitRate, $worstFallbackPerMinute)

if ($violations.Count -gt 0)
{
    Write-Host "Guardrail violations detected:"
    foreach ($v in $violations)
    {
        Write-Host " - $v"
    }
    exit 1
}

Write-Host "Canary guardrails passed."
exit 0

# Test script to verify circuit breaker fallback is working
# Runs the grocery store demo and captures the output

Write-Host "Starting VapeCache Grocery Store Demo (without Redis)..." -ForegroundColor Cyan
Write-Host "This will take ~2 minutes. Watching for circuit breaker activity..." -ForegroundColor Cyan
Write-Host ""

$output = & dotnet run --project "VapeCache.Console/VapeCache.Console.csproj" 2>&1 | Out-String

# Extract key metrics from output
$fallbackMatch = $output -match 'Fallback Events: (\d+)'
$breakerMatch = $output -match 'Circuit Breaker Opens: (\d+)'
$completedMatch = $output -match 'STRESS TEST COMPLETE'

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  TEST RESULTS" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green

if ($completedMatch) {
    Write-Host "[PASS] Demo completed successfully" -ForegroundColor Green
} else {
    Write-Host "[FAIL] Demo did not complete" -ForegroundColor Red
}

if ($fallbackMatch) {
    $fallbackCount = [int]($Matches[1])
    if ($fallbackCount -gt 0) {
        Write-Host "[PASS] Fallback Events: $fallbackCount (circuit breaker is working)" -ForegroundColor Green
    } else {
        Write-Host "[WARN] Fallback Events: 0 (expected >0 when Redis unavailable)" -ForegroundColor Yellow
    }
} else {
    Write-Host "[INFO] Could not parse Fallback Events from output" -ForegroundColor Yellow
}

if ($breakerMatch) {
    $breakerCount = [int]($Matches[1])
    Write-Host "[INFO] Circuit Breaker Opens: $breakerCount" -ForegroundColor Cyan
} else {
    Write-Host "[INFO] Could not parse Circuit Breaker Opens from output" -ForegroundColor Yellow
}

# Show last 30 lines of output (final stats)
Write-Host ""
Write-Host "Final Output (last 30 lines):" -ForegroundColor Cyan
$output -split "`n" | Select-Object -Last 30 | ForEach-Object { Write-Host $_ }

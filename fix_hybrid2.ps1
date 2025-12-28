$content = Get-Content 'VapeCache.Infrastructure/Connections/HybridCommandExecutor.cs' -Raw

# Fix the broken catch blocks - remove extra opening brace after MarkRedisFailure
$content = $content -replace '(_breakerController\.MarkRedisFailure\(\);)\s*\{\s*\n', '$1' + "`n"

$content | Set-Content 'VapeCache.Infrastructure/Connections/HybridCommandExecutor.cs' -NoNewline
Write-Host "Fixed catch blocks"

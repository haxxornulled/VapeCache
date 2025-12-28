$content = Get-Content 'VapeCache.Infrastructure/Connections/HybridCommandExecutor.cs' -Raw

# Remove duplicate MarkRedisSuccess and SetCurrent lines
$content = $content -replace '(_breakerController\.MarkRedisSuccess\(\);[\r\n\s]+_current\.SetCurrent\("redis"\);[\r\n\s]+)_breakerController\.MarkRedisSuccess\(\);[\r\n\s]+_current\.SetCurrent\("redis"\);', '$1'

# Fix duplicate catch opening braces and MarkRedisFailure
$content = $content -replace '(catch \(Exception ex\)[\r\n\s]+\{[\r\n\s]+_breakerController\.MarkRedisFailure\(\);[\r\n\s]+)\{[\r\n\s]+_breakerController\.MarkRedisFailure\(\);', '$1'

$content | Set-Content 'VapeCache.Infrastructure/Connections/HybridCommandExecutor.cs' -NoNewline
Write-Host "Fixed HybridCommandExecutor.cs"

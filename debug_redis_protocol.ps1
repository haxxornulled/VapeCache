# Debug script to capture what VapeCache sends to Redis
# We'll use tcpdump/wireshark or just add debug logging

Write-Host "To debug the Redis protocol issue with Redis 8.4.0:" -ForegroundColor Yellow
Write-Host ""
Write-Host "Option 1: Enable Redis MONITOR command" -ForegroundColor Cyan
Write-Host "  redis-cli -h 192.168.100.50 -a redis4me!! --user admin MONITOR" -ForegroundColor White
Write-Host ""
Write-Host "Option 2: Check Redis logs on the server" -ForegroundColor Cyan
Write-Host "  Look for protocol errors in /var/log/redis/" -ForegroundColor White
Write-Host ""
Write-Host "Option 3: Add debug logging to VapeCache" -ForegroundColor Cyan
Write-Host "  Modify RedisRespProtocol to log raw bytes being sent" -ForegroundColor White
Write-Host ""

Write-Host "The error 'expected $, got /' suggests:" -ForegroundColor Yellow
Write-Host "  - VapeCache might be sending SET command with malformed bulk string length" -ForegroundColor White
Write-Host "  - OR Redis 8.4.0 changed how it expects certain commands formatted" -ForegroundColor White
Write-Host ""
Write-Host "Since StackExchange.Redis works fine, let's check if it's a RESP2 vs RESP3 issue..." -ForegroundColor Yellow

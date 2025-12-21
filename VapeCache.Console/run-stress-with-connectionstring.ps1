param(
  [string]$RedisHost = 'localhost',
  [int]$Port = 6379,
  [string]$Username = 'default',
  [int]$Database = 0,
  [switch]$UseTls,
  [TimeSpan]$Duration = ([TimeSpan]::FromSeconds(15)),
  [string]$Mode = 'pool',
  [int]$Workers = 64
)

$ErrorActionPreference = 'Stop'

$sec = Read-Host 'Redis password' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
try {
  $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
} finally {
  [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

$scheme = if ($UseTls) { 'rediss' } else { 'redis' }
$escapedPass = [Uri]::EscapeDataString($plain)
$connStr = "${scheme}://${Username}:${escapedPass}@${RedisHost}:${Port}/${Database}"

$env:VAPECACHE_REDIS_CONNECTIONSTRING = $connStr
$env:RedisSecret__EnvVar = 'VAPECACHE_REDIS_CONNECTIONSTRING'

$env:RedisStress__Mode = $Mode
$env:RedisStress__Workers = "$Workers"
$env:RedisStress__Duration = $Duration.ToString()

Write-Host 'Starting console stress run using RedisConnection:ConnectionString from env var (password not printed).'
dotnet run --project "$PSScriptRoot\VapeCache.Console.csproj" -c Release

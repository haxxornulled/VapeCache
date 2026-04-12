param(
    [string]$ContainerName = "vapecache-keydb",
    [int]$HostPort = 6391,
    [string]$Image = "eqalpha/keydb:latest",
    [string]$Configuration = "Debug",
    [string]$TestFilter = "FullyQualifiedName~VapeCache.Tests.Integration",
    [switch]$RunReconnectDrill,
    [switch]$StopContainerOnExit
)

$ErrorActionPreference = "Stop"

function Ensure-Docker {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw "Docker CLI is not available. Install Docker Desktop first."
    }

    $null = docker version
}

function Start-KeyDbContainer {
    $exists = docker ps -a --format "{{.Names}}" | Select-String -SimpleMatch $ContainerName
    if ($exists) {
        docker start $ContainerName | Out-Null
    }
    else {
        docker run --name $ContainerName -d -p "$HostPort`:6379" $Image | Out-Null
    }

    $attempts = 0
    while ($attempts -lt 30) {
        try {
            $pong = docker exec $ContainerName sh -lc "(keydb-cli ping || redis-cli ping)"
            if ($pong -match "PONG") {
                return
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 500
        $attempts++
    }

    throw "KeyDB container did not become ready in time."
}

function Run-CompatibilityTests {
    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
    Set-Location $repoRoot

    $env:VAPECACHE_REDIS_HOST = "localhost"
    $env:VAPECACHE_REDIS_PORT = "$HostPort"
    Remove-Item Env:VAPECACHE_REDIS_CONNECTIONSTRING -ErrorAction SilentlyContinue
    Remove-Item Env:VAPECACHE_REDIS_USE_TLS -ErrorAction SilentlyContinue
    Remove-Item Env:VAPECACHE_REDIS_USERNAME -ErrorAction SilentlyContinue
    Remove-Item Env:VAPECACHE_REDIS_PASSWORD -ErrorAction SilentlyContinue

    dotnet test "VapeCache.Tests/VapeCache.Tests.csproj" --configuration $Configuration --nologo --filter $TestFilter
}

function Run-ReconnectDrill {
    $repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
    Set-Location $repoRoot

    $env:VAPECACHE_REDIS_HOST = "localhost"
    $env:VAPECACHE_REDIS_PORT = "$HostPort"
    $env:VAPECACHE_RECONNECT_DRILL_ENABLED = "true"

    dotnet test "VapeCache.Tests/VapeCache.Tests.csproj" --configuration $Configuration --nologo --filter "FullyQualifiedName~RedisReconnectDrillIntegrationTests"
}

function Stop-KeyDbContainer {
    $exists = docker ps -a --format "{{.Names}}" | Select-String -SimpleMatch $ContainerName
    if ($exists) {
        docker rm -f $ContainerName | Out-Null
    }
}

try {
    Ensure-Docker
    Start-KeyDbContainer

    Write-Host "Running integration compatibility tests against KeyDB on localhost:$HostPort"
    Run-CompatibilityTests

    if ($RunReconnectDrill) {
        Write-Host "Running reconnect drill against KeyDB on localhost:$HostPort"
        Run-ReconnectDrill
    }
}
finally {
    if ($StopContainerOnExit) {
        Stop-KeyDbContainer
    }
}

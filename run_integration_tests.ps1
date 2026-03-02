#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs VapeCache integration tests against a local or remote Redis endpoint.

.DESCRIPTION
    Sets integration environment variables and executes the Redis integration
    xUnit tests. Optionally starts/stops a local Docker Redis container.

.PARAMETER StartRedis
    Starts a local Docker Redis container named vapecache-redis.

.PARAMETER StopRedis
    Stops/removes the local Docker Redis container after the run.

.PARAMETER Host
    Redis host for integration tests (default: localhost).

.PARAMETER Port
    Redis port for integration tests (default: 6379).

.PARAMETER Password
    Optional Redis password.

.PARAMETER UseTls
    Enables TLS by setting VAPECACHE_REDIS_USE_TLS=true.

.PARAMETER TestFilter
    dotnet test filter expression.

.PARAMETER Configuration
    Build configuration passed to dotnet test.

.PARAMETER Project
    Test project path passed to dotnet test.
#>

param(
    [switch]$StartRedis,
    [switch]$StopRedis,
    [Alias("Host")]
    [string]$RedisHost = "localhost",
    [int]$Port = 6379,
    [string]$Password = "",
    [switch]$UseTls,
    [string]$TestFilter = "FullyQualifiedName~VapeCache.Tests.Integration",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Project = "VapeCache.Tests/VapeCache.Tests.csproj"
)

$ErrorActionPreference = "Stop"

function Write-Header {
    param([string]$Message)
    Write-Host ""
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host " $Message" -ForegroundColor Cyan
    Write-Host "============================================================" -ForegroundColor Cyan
    Write-Host ""
}

function Test-DockerInstalled {
    try {
        $null = Get-Command docker -ErrorAction Stop
        return $true
    }
    catch {
        return $false
    }
}

function Get-ContainerName {
    return "vapecache-redis"
}

function Start-RedisContainer {
    Write-Header "Starting Redis Docker Container"

    if (-not (Test-DockerInstalled)) {
        throw "Docker is not installed. Install Docker Desktop or run against an existing Redis host."
    }

    $containerName = Get-ContainerName
    $existing = docker ps -a --filter "name=$containerName" --format "{{.Names}}"
    if ($existing -eq $containerName) {
        Write-Host "Container exists, starting: $containerName" -ForegroundColor Yellow
        docker start $containerName | Out-Null
    }
    else {
        Write-Host "Creating container: $containerName" -ForegroundColor Yellow
        docker run --name $containerName -d -p "$Port`:6379" redis:7-alpine | Out-Null
    }

    Write-Host "Waiting for Redis readiness..." -ForegroundColor Yellow
    $retries = 0
    $maxRetries = 30
    while ($retries -lt $maxRetries) {
        try {
            $pong = docker exec $containerName redis-cli ping 2>$null
            if ($pong -eq "PONG") {
                Write-Host "Redis is ready." -ForegroundColor Green
                return
            }
        }
        catch {
            # Keep retrying until timeout.
        }

        Start-Sleep -Seconds 1
        $retries++
    }

    throw "Redis did not become ready within $maxRetries seconds."
}

function Stop-RedisContainer {
    Write-Header "Stopping Redis Docker Container"

    if (-not (Test-DockerInstalled)) {
        Write-Host "Docker not found; skipping container stop." -ForegroundColor Yellow
        return
    }

    $containerName = Get-ContainerName
    $existing = docker ps -a --filter "name=$containerName" --format "{{.Names}}"
    if ($existing -eq $containerName) {
        Write-Host "Stopping container: $containerName" -ForegroundColor Yellow
        docker stop $containerName | Out-Null
        Write-Host "Removing container: $containerName" -ForegroundColor Yellow
        docker rm $containerName | Out-Null
        Write-Host "Container removed." -ForegroundColor Green
    }
    else {
        Write-Host "Container not found; nothing to stop." -ForegroundColor Yellow
    }
}

function Set-IntegrationEnvironment {
    Write-Header "Setting Integration Environment"

    $env:VAPECACHE_REDIS_HOST = $RedisHost
    $env:VAPECACHE_REDIS_PORT = $Port.ToString()

    Write-Host "VAPECACHE_REDIS_HOST=$env:VAPECACHE_REDIS_HOST"
    Write-Host "VAPECACHE_REDIS_PORT=$env:VAPECACHE_REDIS_PORT"

    if (-not [string]::IsNullOrWhiteSpace($Password)) {
        $env:VAPECACHE_REDIS_PASSWORD = $Password
        Write-Host "VAPECACHE_REDIS_PASSWORD=********" -ForegroundColor Yellow
    }
    else {
        Remove-Item Env:VAPECACHE_REDIS_PASSWORD -ErrorAction SilentlyContinue
    }

    if ($UseTls) {
        $env:VAPECACHE_REDIS_USE_TLS = "true"
    }
    else {
        Remove-Item Env:VAPECACHE_REDIS_USE_TLS -ErrorAction SilentlyContinue
    }
    Write-Host "VAPECACHE_REDIS_USE_TLS=$($env:VAPECACHE_REDIS_USE_TLS)"
}

function Invoke-IntegrationTests {
    Write-Header "Running Integration Tests"
    Write-Host "Project: $Project"
    Write-Host "Configuration: $Configuration"
    Write-Host "Filter: $TestFilter"
    Write-Host ""

    $testArgs = @(
        "test", $Project,
        "-c", $Configuration,
        "--filter", $TestFilter,
        "--nologo",
        "--verbosity", "normal"
    )

    & dotnet @testArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Integration tests failed with exit code $LASTEXITCODE."
    }
}

try {
    Write-Header "VapeCache Integration Test Runner"

    if ($StartRedis) {
        Start-RedisContainer
    }

    Set-IntegrationEnvironment
    Invoke-IntegrationTests

    Write-Header "Integration Tests Completed Successfully"
}
catch {
    Write-Host ""
    Write-Host "Integration runner error: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    exit 1
}
finally {
    if ($StopRedis) {
        Stop-RedisContainer
    }
}

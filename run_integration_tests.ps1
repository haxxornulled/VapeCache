#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs VapeCache integration tests against a local or remote Redis instance.

.DESCRIPTION
    This script sets up environment variables and runs integration tests.
    It can optionally start a Docker Redis container for local testing.

.PARAMETER StartRedis
    Start a local Redis container using Docker before running tests.

.PARAMETER StopRedis
    Stop and remove the Redis container after tests complete.

.PARAMETER Host
    Redis server hostname (default: localhost)

.PARAMETER Port
    Redis server port (default: 6379)

.PARAMETER Password
    Redis password (optional)

.PARAMETER UseTls
    Enable TLS/SSL connection (default: false)

.PARAMETER TestFilter
    Specific test filter (default: "FullyQualifiedName~Integration")

.EXAMPLE
    .\run_integration_tests.ps1 -StartRedis -StopRedis
    Starts Redis in Docker, runs all integration tests, then stops Redis

.EXAMPLE
    .\run_integration_tests.ps1 -Host "redis.example.com" -Port 6380 -Password "secret" -UseTls
    Runs tests against a remote Redis server with authentication and TLS

.EXAMPLE
    .\run_integration_tests.ps1 -TestFilter "FullyQualifiedName~CoalescedWrites"
    Runs only coalesced writes integration tests
#>

param(
    [switch]$StartRedis,
    [switch]$StopRedis,
    [string]$Host = "localhost",
    [int]$Port = 6379,
    [string]$Password = "",
    [switch]$UseTls,
    [string]$TestFilter = "FullyQualifiedName~Integration"
)

$ErrorActionPreference = "Stop"

function Write-Header {
    param([string]$Message)
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host " $Message" -ForegroundColor Cyan
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
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

function Start-RedisContainer {
    Write-Header "Starting Redis Container"

    if (-not (Test-DockerInstalled)) {
        Write-Error "Docker is not installed. Please install Docker Desktop or use -Host to connect to an existing Redis instance."
    }

    # Check if container already exists
    $existing = docker ps -a --filter "name=vapecache-redis" --format "{{.Names}}"
    if ($existing -eq "vapecache-redis") {
        Write-Host "ℹ Redis container already exists. Starting it..." -ForegroundColor Yellow
        docker start vapecache-redis | Out-Null
    }
    else {
        Write-Host "� Creating new Redis 7 container..." -ForegroundColor Green
        docker run --name vapecache-redis -d -p 6379:6379 redis:7-alpine | Out-Null
    }

    # Wait for Redis to be ready
    Write-Host "⏳ Waiting for Redis to be ready..." -ForegroundColor Yellow
    $retries = 0
    $maxRetries = 30
    while ($retries -lt $maxRetries) {
        try {
            $result = docker exec vapecache-redis redis-cli ping 2>$null
            if ($result -eq "PONG") {
                Write-Host "✓ Redis is ready!" -ForegroundColor Green
                return
            }
        }
        catch {
            # Ignore errors while waiting
        }
        Start-Sleep -Seconds 1
        $retries++
    }

    Write-Error "Redis failed to start within $maxRetries seconds"
}

function Stop-RedisContainer {
    Write-Header "Stopping Redis Container"

    $existing = docker ps -a --filter "name=vapecache-redis" --format "{{.Names}}"
    if ($existing -eq "vapecache-redis") {
        Write-Host "� Stopping Redis container..." -ForegroundColor Yellow
        docker stop vapecache-redis | Out-Null
        Write-Host "🗑 Removing Redis container..." -ForegroundColor Yellow
        docker rm vapecache-redis | Out-Null
        Write-Host "✓ Redis container removed" -ForegroundColor Green
    }
    else {
        Write-Host "ℹ No Redis container found to stop" -ForegroundColor Yellow
    }
}

function Set-TestEnvironment {
    Write-Header "Setting Environment Variables"

    $env:VAPECACHE_REDIS_HOST = $Host
    $env:VAPECACHE_REDIS_PORT = $Port.ToString()

    Write-Host "VAPECACHE_REDIS_HOST      = $Host"
    Write-Host "VAPECACHE_REDIS_PORT      = $Port"

    if ($Password) {
        $env:VAPECACHE_REDIS_PASSWORD = $Password
        Write-Host "VAPECACHE_REDIS_PASSWORD  = ********" -ForegroundColor Yellow
    }

    if ($UseTls) {
        $env:VAPECACHE_REDIS_USE_TLS = "true"
        Write-Host "VAPECACHE_REDIS_USE_TLS   = true" -ForegroundColor Yellow
    }

    Write-Host ""
}

function Invoke-IntegrationTests {
    Write-Header "Running Integration Tests"

    Write-Host "Test Filter: $TestFilter" -ForegroundColor Cyan
    Write-Host ""

    $testArgs = @(
        "test"
        "--filter", $TestFilter
        "--nologo"
        "--verbosity", "normal"
    )

    & dotnet $testArgs

    if ($LASTEXITCODE -ne 0) {
        throw "Integration tests failed with exit code $LASTEXITCODE"
    }
}

# Main execution
try {
    Write-Header "VapeCache Integration Test Runner"

    if ($StartRedis) {
        Start-RedisContainer
    }

    Set-TestEnvironment
    Invoke-IntegrationTests

    Write-Header "✓ Integration Tests Completed Successfully"
}
catch {
    Write-Host ""
    Write-Host "❌ ERROR: $_" -ForegroundColor Red
    Write-Host ""
    exit 1
}
finally {
    if ($StopRedis) {
        Stop-RedisContainer
    }
}

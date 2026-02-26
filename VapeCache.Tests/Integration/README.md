# VapeCache Integration Tests

Integration tests that require a live Redis instance.

## Quick Start

### 1. Start Redis Locally

**Using Docker (Recommended):**
```bash
# Start Redis 7.x
docker run --name vapecache-redis -d -p 6379:6379 redis:7-alpine

# Verify it's running
docker ps | grep vapecache-redis
```

**Using Windows Subsystem for Linux (WSL):**
```bash
# Install Redis
sudo apt-get update
sudo apt-get install redis-server

# Start Redis
redis-server --daemonize yes

# Verify
redis-cli ping
```

### 2. Set Environment Variables

**PowerShell:**
```powershell
$env:VAPECACHE_REDIS_HOST = "localhost"
$env:VAPECACHE_REDIS_PORT = "6379"
```

**Bash/WSL:**
```bash
export VAPECACHE_REDIS_HOST=localhost
export VAPECACHE_REDIS_PORT=6379
```

### 3. Run Integration Tests

```bash
# Run all integration tests
dotnet test --filter "FullyQualifiedName~Integration"

# Run specific test
dotnet test --filter "FullyQualifiedName~CoalescedWritesIntegrationTests"
```

## Environment Variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `VAPECACHE_REDIS_HOST` | ✅ Yes | - | Redis server hostname or IP |
| `VAPECACHE_REDIS_PORT` | ❌ No | `6379` | Redis server port |
| `VAPECACHE_REDIS_USERNAME` | ❌ No | - | Redis ACL username (Redis 6+) |
| `VAPECACHE_REDIS_PASSWORD` | ❌ No | - | Redis password (AUTH) |
| `VAPECACHE_REDIS_DATABASE` | ❌ No | `0` | Redis database number (0-15) |
| `VAPECACHE_REDIS_USE_TLS` | ❌ No | `false` | Enable TLS/SSL |
| `VAPECACHE_REDIS_TLS_HOST` | ❌ No | - | SNI hostname for TLS |
| `VAPECACHE_REDIS_ALLOW_INVALID_CERT` | ❌ No | `false` | Skip cert validation (dev only) |

## Test Categories

### CoalescedWritesIntegrationTests
Tests coalesced write functionality (batching multiple commands into single socket send).

- `CoalescedWrites_SingleCommand`: Verifies basic SET/GET/DELETE with coalescing enabled
- `CoalescedWrites_Concurrent`: Stress test with 100 concurrent operations
- `CoalescedWrites_Hash_RoundTrip`: Validates coalesced write path for HASH commands
- `CoalescedWrites_List_RoundTrip`: Validates coalesced write path for LIST commands
- `CoalescedWrites_Set_RoundTrip`: Validates coalesced write path for SET commands
- `CoalescedWrites_SortedSet_RoundTrip`: Validates coalesced write path for SORTED SET commands
- `CoalescedWrites_Json_RoundTrip_WhenRedisJsonAvailable`: Validates coalesced JSON commands when RedisJSON is installed

### RedisCommandExecutorIntegrationTests
Tests core Redis commands against live server.

- `Executor_can_set_get_del`: Basic key-value operations
- `Executor_supports_getex_ttl_pttl_mget_mset_unlink`: Expiration and bulk commands
- Hash, List, and Set operations

### RedisConnectionFactoryIntegrationTests
Tests connection establishment, pooling, and TLS.

### RedisConnectionPoolIntegrationTests
Tests connection pool behavior, leasing, and cleanup.

## Running Tests in CI/CD

### GitHub Actions Example

```yaml
jobs:
  integration-tests:
    runs-on: ubuntu-latest

    services:
      redis:
        image: redis:7-alpine
        ports:
          - 6379:6379
        options: >-
          --health-cmd "redis-cli ping"
          --health-interval 10s
          --health-timeout 5s
          --health-retries 5

    steps:
      - uses: actions/checkout@v3
      - uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '10.0.x'

      - name: Run integration tests
        env:
          VAPECACHE_REDIS_HOST: localhost
          VAPECACHE_REDIS_PORT: 6379
        run: dotnet test --filter "FullyQualifiedName~Integration"
```

### Azure Pipelines Example

```yaml
steps:
- task: Docker@2
  displayName: 'Start Redis Container'
  inputs:
    command: 'run'
    arguments: '--name vapecache-redis -d -p 6379:6379 redis:7-alpine'

- task: DotNetCoreCLI@2
  displayName: 'Run Integration Tests'
  inputs:
    command: 'test'
    arguments: '--filter "FullyQualifiedName~Integration"'
  env:
    VAPECACHE_REDIS_HOST: localhost
    VAPECACHE_REDIS_PORT: 6379
```

## Testing Against Azure Cache for Redis

```powershell
$env:VAPECACHE_REDIS_HOST = "your-cache.redis.cache.windows.net"
$env:VAPECACHE_REDIS_PORT = "6380"
$env:VAPECACHE_REDIS_PASSWORD = "your-access-key"
$env:VAPECACHE_REDIS_USE_TLS = "true"

dotnet test --filter "FullyQualifiedName~Integration"
```

## Testing Against AWS ElastiCache

```bash
export VAPECACHE_REDIS_HOST=your-cluster.cache.amazonaws.com
export VAPECACHE_REDIS_PORT=6379
export VAPECACHE_REDIS_USE_TLS=true

dotnet test --filter "FullyQualifiedName~Integration"
```

## Troubleshooting

### Tests Are Skipped

**Symptom**: All integration tests show as "Skipped"
**Cause**: `VAPECACHE_REDIS_HOST` environment variable not set
**Fix**: Set the environment variable and restart your terminal/IDE

### Connection Timeout

**Symptom**: Tests fail with "Connection timeout" errors
**Cause**: Redis not running or firewall blocking connection
**Fix**:
```bash
# Check if Redis is running
docker ps | grep redis
# Or
redis-cli ping

# Check if port 6379 is open
telnet localhost 6379
```

### Authentication Errors

**Symptom**: "NOAUTH Authentication required" or "ERR invalid password"
**Cause**: Redis requires password but none provided
**Fix**: Set `VAPECACHE_REDIS_PASSWORD` environment variable

### TLS/SSL Errors

**Symptom**: "SSL connection error" or "The remote certificate is invalid"
**Cause**: TLS certificate validation failing
**Fix**: For development only, set `VAPECACHE_REDIS_ALLOW_INVALID_CERT=true`

## Cleanup

```bash
# Stop and remove Redis container
docker stop vapecache-redis
docker rm vapecache-redis

# Or if using local Redis
redis-cli FLUSHALL  # Clear all data
redis-cli SHUTDOWN  # Stop server
```

## Best Practices

1. **Always use isolated database**: Integration tests run against `Database=0` by default
2. **Clean up test keys**: All test keys use prefix `vapecache:test:` or `vapecache:coalesce:` with GUIDs
3. **Run tests locally before CI**: Verify tests pass on your machine first
4. **Use dedicated Redis instance**: Don't run integration tests against production Redis

## Test Data

Integration tests create temporary keys with predictable patterns:
- `vapecache:test:{guid}` - General test keys
- `vapecache:coalesce:{guid}` - Coalesced write test keys
- `vapecache:ex:{guid}` - Expiration test keys

All keys are cleaned up during test execution.

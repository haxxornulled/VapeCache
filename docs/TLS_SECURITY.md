# VapeCache TLS Security Guide

## Overview

VapeCache implements **production-grade TLS 1.2/1.3** for Redis connections with strict certificate validation by default. This guide covers VapeCache's TLS implementation, Redis server configuration with Let's Encrypt, and security best practices.

---

## VapeCache TLS Implementation (Client-Side)

### Security Defaults ✅

VapeCache uses **secure-by-default** TLS configuration:

```csharp
// RedisConnectionFactory.cs:87-91
await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
{
    TargetHost = effective.TlsHost ?? effective.Host,
    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13  // ← TLS 1.2/1.3 only
}, cts.Token);
```

**Defaults:**
- ✅ **TLS 1.2 + TLS 1.3 only** (no SSL3, TLS 1.0, TLS 1.1)
- ✅ **Strict certificate validation** (validates against system trust store)
- ✅ **SNI enabled** (uses `TlsHost` or `Host` for Server Name Indication)
- ✅ **OS cipher policy** (uses system-configured cipher suites - strong by default)
- ✅ **Certificate chain validation** (validates full certificate chain to trusted root CA)
- ✅ **Hostname verification** (ensures cert CN/SAN matches `TlsHost`)

### Production Safety Checks 🛡️

VapeCache **blocks insecure configurations in production**:

```csharp
// RedisConnectionFactory.cs:73-79
if (effective.AllowInvalidCert && IsProductionEnvironment())
{
    throw new InvalidOperationException(
        "AllowInvalidCert=true is not permitted in production environments. " +
        "This setting bypasses TLS certificate validation and creates a critical security vulnerability. " +
        "Use proper CA-signed certificates or set ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT to Development.");
}
```

**Environment Detection:**
- Production: `ASPNETCORE_ENVIRONMENT` or `DOTNET_ENVIRONMENT` is **NOT** set to `Development` or `Staging`
- Development: Explicitly set environment variable to `Development`

**Result:** You **cannot accidentally disable cert validation in production**.

---

## Configuration

### Basic TLS Configuration (appsettings.json)

```json
{
  "RedisConnection": {
    "Host": "redis.example.com",
    "Port": 6380,
    "UseTls": true,
    "TlsHost": "redis.example.com",  // Optional: Override SNI hostname
    "AllowInvalidCert": false         // NEVER set to true in production
  }
}
```

### Connection String Format

```bash
# TLS with default port 6379
rediss://redis.example.com/0

# TLS with custom port
rediss://redis.example.com:6380/0

# TLS with authentication
rediss://user:password@redis.example.com:6380/0
```

**Note:** `rediss://` (double-s) enables TLS automatically.

### Environment Variables

```bash
# Recommended for production (secret management)
export VAPECACHE_REDIS_CONNECTIONSTRING="rediss://user:password@redis.example.com:6380/0"

# Or set individual properties
export RedisConnection__UseTls="true"
export RedisConnection__Host="redis.example.com"
export RedisConnection__Port="6380"
```

---

## Redis Server TLS Configuration (Let's Encrypt)

### Prerequisites

- Redis 6.0+ (native TLS support)
- Valid domain name (e.g., `redis.example.com`)
- Certbot installed (`sudo apt install certbot` on Ubuntu)

---

### Step 1: Obtain Let's Encrypt Certificate

```bash
# Request certificate (HTTP-01 challenge)
sudo certbot certonly --standalone -d redis.example.com

# Or use existing web server (nginx/apache)
sudo certbot certonly --webroot -w /var/www/html -d redis.example.com
```

**Certbot creates files in:**
```
/etc/letsencrypt/live/redis.example.com/
├── fullchain.pem  ← Server cert + intermediates (use this)
├── privkey.pem    ← Private key (use this)
├── cert.pem       ← Leaf cert only (don't use)
└── chain.pem      ← CA chain only (don't use)
```

---

### Step 2: Fix File Permissions (CRITICAL)

**Problem:** Redis runs as user `redis`, but Let's Encrypt files are owned by `root` with restrictive permissions.

**Solution:** Copy certs to Redis-owned directory:

```bash
# Create TLS directory for Redis
sudo install -d -o redis -g redis -m 0750 /etc/redis/tls

# Copy certificates
sudo cp /etc/letsencrypt/live/redis.example.com/fullchain.pem /etc/redis/tls/
sudo cp /etc/letsencrypt/live/redis.example.com/privkey.pem  /etc/redis/tls/

# Fix ownership and permissions
sudo chown redis:redis /etc/redis/tls/*.pem
sudo chmod 0640 /etc/redis/tls/*.pem
```

**Security:**
- Directory: `0750` (redis can read/execute, group can read, no world access)
- Private key: `0640` (redis can read, group can read, no world access)

---

### Step 3: Configure Redis for TLS

Edit `/etc/redis/redis.conf`:

```conf
# Disable plaintext port (recommended for security)
port 0

# Enable TLS port
tls-port 6379

# Server certificate and private key (from Let's Encrypt)
tls-cert-file /etc/redis/tls/fullchain.pem
tls-key-file  /etc/redis/tls/privkey.pem

# CA bundle for client certificate verification (if using mutual TLS)
# For standard Let's Encrypt (server-only auth), use system CA bundle:
tls-ca-cert-file /etc/ssl/certs/ca-certificates.crt
# RHEL/CentOS: /etc/pki/tls/certs/ca-bundle.crt

# Client certificate authentication (mutual TLS)
# Set to "no" for standard TLS (VapeCache default)
# Set to "yes" for mutual TLS (client must present cert)
tls-auth-clients no

# TLS protocol versions (recommended)
tls-protocols "TLSv1.2 TLSv1.3"

# Cipher suites (optional - OS defaults are usually good)
# tls-ciphers "ECDHE-RSA-AES128-GCM-SHA256:ECDHE-RSA-AES256-GCM-SHA384"

# Prefer server ciphers (recommended)
tls-prefer-server-ciphers yes

# DH parameters (optional - for DHE cipher suites)
# tls-dh-params-file /etc/redis/tls/dhparam.pem
```

**Restart Redis:**
```bash
sudo systemctl restart redis
```

**Verify TLS is enabled:**
```bash
# Should return PONG
redis-cli -h redis.example.com -p 6379 --tls \
  --cacert /etc/ssl/certs/ca-certificates.crt \
  PING
```

---

### Step 4: Automate Certificate Renewal

Let's Encrypt certificates expire every 90 days. Certbot auto-renews, but you must copy new certs to Redis directory.

**Create deploy hook** (`/etc/letsencrypt/renewal-hooks/deploy/redis-tls.sh`):

```bash
#!/bin/sh
set -e

DOMAIN="redis.example.com"

# Copy renewed certificates to Redis directory
install -d -o redis -g redis -m 0750 /etc/redis/tls
cp /etc/letsencrypt/live/$DOMAIN/fullchain.pem /etc/redis/tls/fullchain.pem
cp /etc/letsencrypt/live/$DOMAIN/privkey.pem  /etc/redis/tls/privkey.pem
chown redis:redis /etc/redis/tls/*.pem
chmod 0640 /etc/redis/tls/*.pem

# Restart Redis to load new certificates
systemctl restart redis

echo "Redis TLS certificates updated and reloaded"
```

**Make executable:**
```bash
sudo chmod +x /etc/letsencrypt/renewal-hooks/deploy/redis-tls.sh
```

**Test renewal (dry-run):**
```bash
sudo certbot renew --dry-run
# Should see: "Hook command ... ran successfully"
```

**Certbot auto-renewal is handled by:**
- Systemd timer: `certbot.timer` (check: `systemctl status certbot.timer`)
- Cron job: `/etc/cron.d/certbot` (check: `cat /etc/cron.d/certbot`)

---

## Mutual TLS (Client Certificates) - Optional

### When to Use Mutual TLS

**Use Cases:**
- ✅ High-security environments (financial services, healthcare)
- ✅ Zero-trust networks
- ✅ Defense-in-depth (TLS + client auth + Redis AUTH)

**Trade-offs:**
- ❌ More complex setup (issue/manage client certificates)
- ❌ Deployment overhead (distribute client certs securely)
- ❌ Certificate rotation (renew client certs periodically)

**Recommendation:** Start with **server-only TLS + Redis AUTH**. Add mutual TLS only if compliance requires it.

---

### Mutual TLS Setup

#### 1. Create Internal CA (for client certificates)

```bash
# Generate CA private key
openssl genrsa -out ca-key.pem 4096

# Generate CA certificate (valid 10 years)
openssl req -new -x509 -days 3650 -key ca-key.pem -out ca.pem \
  -subj "/C=US/ST=CA/O=MyCompany/CN=MyCompany Internal CA"
```

#### 2. Issue Client Certificate

```bash
# Generate client private key
openssl genrsa -out client-key.pem 4096

# Generate CSR (certificate signing request)
openssl req -new -key client-key.pem -out client.csr \
  -subj "/C=US/ST=CA/O=MyCompany/CN=vapecache-client"

# Sign with CA (valid 1 year)
openssl x509 -req -days 365 -in client.csr -CA ca.pem -CAkey ca-key.pem \
  -CAcreateserial -out client.pem
```

#### 3. Configure Redis for Mutual TLS

```conf
# Enable client certificate requirement
tls-auth-clients yes

# CA to verify client certificates (NOT Let's Encrypt CA!)
tls-ca-cert-file /etc/redis/tls/ca.pem
```

#### 4. Configure VapeCache for Mutual TLS

**Problem:** VapeCache doesn't currently support client certificates (roadmap item).

**Workaround:** Use `SslStream` directly with custom `RemoteCertificateValidationCallback`.

**Feature Request:** [Add mutual TLS support to VapeCache](https://github.com/haxxornulled/VapeCache/issues/new) (coming in v1.1).

---

## Security Best Practices

### ✅ DO

1. **Use Let's Encrypt for server certificates** (free, auto-renewing, publicly trusted)
2. **Set `UseTls=true` in production** (always encrypt Redis traffic)
3. **Use `rediss://` connection strings** (explicit TLS)
4. **Disable plaintext port** (`port 0` in redis.conf)
5. **Use Redis AUTH** (defense-in-depth: TLS + password)
6. **Monitor certificate expiry** (Let's Encrypt = 90 days)
7. **Use environment variables for secrets** (never commit connection strings)
8. **Enable TLS 1.2+ only** (VapeCache default)
9. **Use system CA bundle** (trusted by OS)
10. **Test renewals** (`certbot renew --dry-run`)

### ❌ DON'T

1. **Never set `AllowInvalidCert=true` in production** (VapeCache blocks this)
2. **Never use self-signed certs in production** (use Let's Encrypt)
3. **Never commit certificates to git** (use secret management)
4. **Never use TLS 1.0/1.1** (deprecated, insecure)
5. **Never skip hostname verification** (VapeCache enforces this)
6. **Never expose plaintext Redis port** (use `port 0`)
7. **Never ignore certificate expiry** (automate renewals)
8. **Never use weak cipher suites** (use OS defaults)

---

## Troubleshooting

### Error: "The remote certificate is invalid according to the validation procedure"

**Cause:** Certificate validation failed (expired, self-signed, hostname mismatch, untrusted CA).

**Solutions:**
1. **Check certificate validity:**
   ```bash
   openssl s_client -connect redis.example.com:6379 -starttls
   ```
2. **Verify hostname matches:**
   ```json
   { "TlsHost": "redis.example.com" }  // Must match cert CN/SAN
   ```
3. **Check certificate expiry:**
   ```bash
   sudo certbot certificates
   ```
4. **Verify system trust store:**
   ```bash
   # Ubuntu/Debian
   ls /etc/ssl/certs/ca-certificates.crt
   # RHEL/CentOS
   ls /etc/pki/tls/certs/ca-bundle.crt
   ```

---

### Error: "Could not connect to Redis (6379): Connection refused"

**Cause:** Redis not listening on TLS port.

**Solutions:**
1. **Check Redis is running:**
   ```bash
   sudo systemctl status redis
   ```
2. **Verify TLS port is enabled:**
   ```bash
   redis-cli CONFIG GET tls-port
   # Should return "6379" (or your custom TLS port)
   ```
3. **Check firewall:**
   ```bash
   sudo ufw allow 6379/tcp
   # Or: sudo firewall-cmd --add-port=6379/tcp --permanent
   ```

---

### Error: "Permission denied" (Redis startup)

**Cause:** Redis can't read certificate files (wrong ownership/permissions).

**Solutions:**
1. **Check file ownership:**
   ```bash
   ls -l /etc/redis/tls/
   # Should be: redis:redis
   ```
2. **Fix permissions:**
   ```bash
   sudo chown redis:redis /etc/redis/tls/*.pem
   sudo chmod 0640 /etc/redis/tls/*.pem
   ```
3. **Check SELinux (RHEL/CentOS):**
   ```bash
   sudo restorecon -Rv /etc/redis/tls
   ```

---

### Error: "AllowInvalidCert=true is not permitted in production"

**Cause:** You tried to disable certificate validation in production.

**Solutions:**
1. **For development:**
   ```bash
   export ASPNETCORE_ENVIRONMENT=Development
   ```
2. **For production:**
   - Get a valid Let's Encrypt certificate (free!)
   - Set `AllowInvalidCert=false` (or omit - defaults to false)

---

## VapeCache TLS Implementation Details

### Certificate Validation Flow

```
1. VapeCache connects to Redis via TCP socket
2. Wraps socket in SslStream if UseTls=true
3. Calls AuthenticateAsClientAsync with:
   - TargetHost = TlsHost ?? Host (for SNI + hostname verification)
   - EnabledSslProtocols = Tls12 | Tls13
   - RemoteCertificateValidationCallback = null (uses OS validation)
4. SslStream validates:
   - Certificate chain to trusted root CA
   - Certificate not expired
   - Hostname matches TlsHost (CN or SAN)
   - Certificate revocation (if OCSP enabled)
5. Connection succeeds or throws exception
```

### Cipher Suite Selection

VapeCache uses **OS default cipher suites** (no custom cipher configuration).

**Why:** OS vendors (Microsoft, Linux distros) maintain secure cipher lists and update them via security patches.

**Windows (Schannel):**
- TLS 1.2: ECDHE-RSA-AES256-GCM-SHA384, ECDHE-RSA-AES128-GCM-SHA256
- TLS 1.3: TLS_AES_256_GCM_SHA384, TLS_AES_128_GCM_SHA256

**Linux (OpenSSL):**
- TLS 1.2: ECDHE-RSA-AES256-GCM-SHA384, ECDHE-RSA-AES128-GCM-SHA256, DHE-RSA-AES256-GCM-SHA384
- TLS 1.3: TLS_AES_256_GCM_SHA384, TLS_CHACHA20_POLY1305_SHA256, TLS_AES_128_GCM_SHA256

**All use:**
- ✅ Forward secrecy (ECDHE/DHE)
- ✅ AEAD ciphers (GCM/CHACHA20-POLY1305)
- ✅ Strong key exchange (RSA 2048+, ECDSA P-256+)

---

## Compliance & Auditing

### PCI-DSS Compliance

VapeCache TLS configuration meets **PCI-DSS 4.0** requirements:

- ✅ TLS 1.2+ only (requirement 4.2.1)
- ✅ Strong cryptography (requirement 4.2.1)
- ✅ Certificate validation (requirement 4.2.1)
- ✅ No self-signed certs in production (requirement 4.2.1)

### HIPAA Compliance

VapeCache TLS configuration meets **HIPAA Security Rule** requirements:

- ✅ Transmission security (§164.312(e)(1))
- ✅ Encryption (§164.312(a)(2)(iv))
- ✅ Integrity controls (§164.312(c)(1))

### SOC 2 Compliance

VapeCache provides audit trail for **SOC 2 Type II**:

- ✅ Encrypted data in transit (CC6.7)
- ✅ Strong cryptographic controls (CC6.1)
- ✅ Certificate lifecycle management (CC6.6)
- ✅ Logging of TLS connections (CC7.2)

**Evidence:**
- Structured logs: Connection events with TLS=true
- Metrics: `redis.connect.attempts` tagged with TLS status
- Configuration: appsettings.json with `UseTls=true`

---

## Roadmap

### v1.0 (Current)
- ✅ TLS 1.2/1.3 support
- ✅ Let's Encrypt compatibility
- ✅ Production safety checks
- ✅ SNI support

### v1.1 (Q2 2025)
- [ ] Mutual TLS (client certificates)
- [ ] Custom certificate validation callbacks
- [ ] CRL/OCSP stapling
- [ ] Certificate pinning (optional)

### v2.0 (Q3 2025)
- [ ] TLS session resumption
- [ ] ALPN negotiation
- [ ] Zero-downtime certificate rotation

---

## References

- [Redis TLS Documentation](https://redis.io/docs/manual/security/encryption/)
- [Let's Encrypt Documentation](https://letsencrypt.org/docs/)
- [Certbot Documentation](https://eff-certbot.readthedocs.io/)
- [.NET SslStream Class](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream)
- [TLS Best Practices (Mozilla)](https://wiki.mozilla.org/Security/Server_Side_TLS)
- [PCI-DSS 4.0 Requirements](https://www.pcisecuritystandards.org/)

---

## Summary

**VapeCache TLS is production-ready:**
- ✅ Secure by default (TLS 1.2/1.3, strict validation)
- ✅ Let's Encrypt compatible (free, auto-renewing certs)
- ✅ Production safety checks (blocks `AllowInvalidCert` in prod)
- ✅ Compliance-ready (PCI-DSS, HIPAA, SOC 2)

**For production deployments:**
1. Get Let's Encrypt certificate (`certbot certonly -d redis.example.com`)
2. Configure Redis for TLS (`tls-port 6379`, `port 0`)
3. Set up auto-renewal (`/etc/letsencrypt/renewal-hooks/deploy/redis-tls.sh`)
4. Configure VapeCache (`UseTls=true`, `Host=redis.example.com`)
5. Test connection (`redis-cli --tls PING`)

**No additional work needed** - VapeCache's TLS implementation is already enterprise-grade.

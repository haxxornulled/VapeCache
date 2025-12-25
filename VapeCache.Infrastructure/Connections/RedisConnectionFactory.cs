// ========================= File: Vapecache.Infrastructure/Connections/RedisConnectionFactory.cs =========================
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Diagnostics;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using LanguageExt.Common;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using VapeCache.Abstractions.Connections;

namespace VapeCache.Infrastructure.Connections;

internal sealed class RedisConnectionFactory(
    IOptionsMonitor<RedisConnectionOptions> options,
    ILogger<RedisConnectionFactory> logger,
    IEnumerable<IRedisConnectionObserver> observers) : IRedisConnectionFactory
{
    private int _disposed;
    private static long _ids;
    private int _loggedConnectionStringResolution;
    private readonly IRedisConnectionObserver[] _observers = observers as IRedisConnectionObserver[] ?? observers.ToArray();

    public async ValueTask<Result<IRedisConnection>> CreateAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) == 1)
            return new Result<IRedisConnection>(new ObjectDisposedException(nameof(RedisConnectionFactory)));

        var o = options.CurrentValue;
        var effective = ResolveOptions(o);

        NotifyConnectAttempt(effective);

        if (!string.IsNullOrWhiteSpace(o.ConnectionString) &&
            Interlocked.Exchange(ref _loggedConnectionStringResolution, 1) == 0)
        {
            try
            {
                logger.LogInformation(
                    "Redis options resolved from ConnectionString: Host={Host} Port={Port} Db={Db} Tls={Tls} Username={Username} PasswordSet={PasswordSet}",
                    effective.Host,
                    effective.Port,
                    effective.Database,
                    effective.UseTls,
                    string.IsNullOrWhiteSpace(effective.Username) ? "<none>" : effective.Username,
                    !string.IsNullOrWhiteSpace(effective.Password));
            }
            catch { }
        }

        RedisTelemetry.ConnectAttempts.Add(1);
        using var activity = RedisTelemetry.ActivitySource.StartActivity("redis.connect");
        var sw = Stopwatch.StartNew();

        Socket? socket = null;
        Stream? stream = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(effective.ConnectTimeout);

            socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            TryConfigureKeepAlive(socket, effective);
            await socket.ConnectAsync(effective.Host, effective.Port, cts.Token).ConfigureAwait(false);

            stream = new NetworkStream(socket, ownsSocket: false);

            if (effective.UseTls)
            {
                // SECURITY: Block AllowInvalidCert in production to prevent MITM attacks
                if (effective.AllowInvalidCert && IsProductionEnvironment())
                {
                    throw new InvalidOperationException(
                        "AllowInvalidCert=true is not permitted in production environments. " +
                        "This setting bypasses TLS certificate validation and creates a critical security vulnerability. " +
                        "Use proper CA-signed certificates or set ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT to Development.");
                }

                var ssl = new SslStream(
                    stream,
                    leaveInnerStreamOpen: false,
                    effective.AllowInvalidCert ? static (_, _, _, _) => true : null
                );

                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = effective.TlsHost ?? effective.Host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, cts.Token).ConfigureAwait(false);

                stream = ssl;
            }

            var id = Interlocked.Increment(ref _ids);
            await AuthenticateAndSelectAsync(id, stream, effective, cts.Token).ConfigureAwait(false);

            sw.Stop();
            RedisTelemetry.ConnectMs.Record(sw.Elapsed.TotalMilliseconds);

            var conn = (IRedisConnection)new RedisConnection(id, socket, stream);

            try
            {
                logger.LogInformation(
                    "Redis connected (Id={Id}) {LocalEndPoint} -> {RemoteEndPoint} Tls={Tls} Time={Ms}ms",
                    id,
                    socket.LocalEndPoint?.ToString() ?? "?",
                    socket.RemoteEndPoint?.ToString() ?? "?",
                    effective.UseTls,
                    sw.Elapsed.TotalMilliseconds);
            }
            catch { }

            NotifyConnected(id, effective, sw.Elapsed);

            socket = null;
            stream = null;
            return new Result<IRedisConnection>(conn);
        }
        catch (OperationCanceledException oce) when (!ct.IsCancellationRequested)
        {
            // Likely ConnectTimeout/handshake timeout via the linked CTS. Don't spam stack traces for expected timeouts.
            sw.Stop();
            RedisTelemetry.ConnectFailures.Add(1);
            RedisTelemetry.ConnectMs.Record(sw.Elapsed.TotalMilliseconds);

            var timeout = new TimeoutException(
                $"Redis connect timed out to {effective.Host}:{effective.Port} (Tls={effective.UseTls}).",
                oce);

            try
            {
                logger.LogWarning(
                    "Redis connect timed out to {Host}:{Port} Tls={Tls} after {Ms}ms",
                    effective.Host,
                    effective.Port,
                    effective.UseTls,
                    sw.Elapsed.TotalMilliseconds);
            }
            catch { }

            NotifyConnectFailed(effective, timeout);

            try { stream?.Dispose(); } catch { }
            try { socket?.Dispose(); } catch { }
            return new Result<IRedisConnection>(timeout);
        }
        catch (OperationCanceledException oce) when (ct.IsCancellationRequested)
        {
            // Host/app shutdown: treat as a canceled attempt without noisy logging.
            sw.Stop();
            RedisTelemetry.ConnectFailures.Add(1);
            RedisTelemetry.ConnectMs.Record(sw.Elapsed.TotalMilliseconds);
            try { stream?.Dispose(); } catch { }
            try { socket?.Dispose(); } catch { }
            return new Result<IRedisConnection>(oce);
        }
        catch (Exception ex)
        {
            sw.Stop();
            RedisTelemetry.ConnectFailures.Add(1);
            RedisTelemetry.ConnectMs.Record(sw.Elapsed.TotalMilliseconds);
            try
            {
                logger.LogWarning(ex, "Redis connect failed to {Host}:{Port} Tls={Tls} after {Ms}ms", effective.Host, effective.Port, effective.UseTls, sw.Elapsed.TotalMilliseconds);
            }
            catch { }

            NotifyConnectFailed(effective, ex);

            try { stream?.Dispose(); } catch { }
            try { socket?.Dispose(); } catch { }
            return new Result<IRedisConnection>(ex);
        }
    }

    private static void TryConfigureKeepAlive(Socket socket, RedisConnectionOptions o)
    {
        byte[]? rented = null;
        try
        {
            if (!o.EnableTcpKeepAlive) return;
            if (!OperatingSystem.IsWindows()) return;

            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            var timeMs = (uint)Math.Clamp((long)o.TcpKeepAliveTime.TotalMilliseconds, 1, int.MaxValue);
            var intervalMs = (uint)Math.Clamp((long)o.TcpKeepAliveInterval.TotalMilliseconds, 1, int.MaxValue);

            // Windows SIO_KEEPALIVE_VALS: [onoff, time, interval] as 3 x u32.
            rented = ArrayPool<byte>.Shared.Rent(12);
            var values = rented.AsSpan(0, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(values.Slice(0, 4), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(values.Slice(4, 4), timeMs);
            BinaryPrimitives.WriteUInt32LittleEndian(values.Slice(8, 4), intervalMs);
            socket.IOControl(IOControlCode.KeepAliveValues, rented, null);
        }
        catch
        {
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async Task AuthenticateAndSelectAsync(long id, Stream stream, RedisConnectionOptions o, CancellationToken ct)
    {
        var usedFallback = false;
        if (!string.IsNullOrEmpty(o.Password))
        {
            if (string.IsNullOrEmpty(o.Username))
            {
                await WriteHandshakeAsync(stream, ct, write =>
                {
                    var idx = 0;
                    idx += RedisRespProtocol.WriteAuthCommand(write.Slice(idx), null, o.Password);
                    if (o.Database != 0)
                        idx += RedisRespProtocol.WriteSelectCommand(write.Slice(idx), o.Database);
                    if (o.LogWhoAmIOnConnect)
                        idx += RedisRespProtocol.WriteAclWhoAmICommand(write.Slice(idx));
                    return idx;
                }, () =>
                {
                    var len = RedisRespProtocol.GetAuthCommandLength(null, o.Password);
                    if (o.Database != 0) len += RedisRespProtocol.GetSelectCommandLength(o.Database);
                    if (o.LogWhoAmIOnConnect) len += RedisRespProtocol.GetAclWhoAmICommandLength();
                    return len;
                }).ConfigureAwait(false);

                await RedisRespProtocol.ExpectOkAsync(stream, ct).ConfigureAwait(false);
                if (o.Database != 0)
                {
                    await RedisRespProtocol.ExpectOkAsync(stream, ct).ConfigureAwait(false);
                    NotifyDatabaseSelected(id, o.Database);
                }
                if (o.LogWhoAmIOnConnect)
                {
                    var user = await RedisRespProtocol.ReadBulkStringAsync(stream, ct).ConfigureAwait(false);
                    logger.LogInformation("Redis ACL WHOAMI={User}", user);
                }

                NotifyAuthenticated(id, o.Username, usedFallback);
            }
            else
            {
                if (!o.AllowAuthFallbackToPasswordOnly)
                {
                    await WriteHandshakeAsync(stream, ct, write =>
                    {
                        var idx = 0;
                        idx += RedisRespProtocol.WriteAuthCommand(write.Slice(idx), o.Username, o.Password);
                        if (o.Database != 0)
                            idx += RedisRespProtocol.WriteSelectCommand(write.Slice(idx), o.Database);
                        if (o.LogWhoAmIOnConnect)
                            idx += RedisRespProtocol.WriteAclWhoAmICommand(write.Slice(idx));
                        return idx;
                    }, () =>
                    {
                        var len = RedisRespProtocol.GetAuthCommandLength(o.Username, o.Password);
                        if (o.Database != 0) len += RedisRespProtocol.GetSelectCommandLength(o.Database);
                        if (o.LogWhoAmIOnConnect) len += RedisRespProtocol.GetAclWhoAmICommandLength();
                        return len;
                    }).ConfigureAwait(false);

                    var ok = await RedisRespProtocol.TryExpectOkAsync(stream, ct).ConfigureAwait(false);
                    if (!ok) throw new InvalidOperationException("AUTH with username failed.");

                    if (o.Database != 0)
                    {
                        await RedisRespProtocol.ExpectOkAsync(stream, ct).ConfigureAwait(false);
                        NotifyDatabaseSelected(id, o.Database);
                    }
                    if (o.LogWhoAmIOnConnect)
                    {
                        var user = await RedisRespProtocol.ReadBulkStringAsync(stream, ct).ConfigureAwait(false);
                        logger.LogInformation("Redis ACL WHOAMI={User}", user);
                    }

                    NotifyAuthenticated(id, o.Username, usedFallback);
                    return;
                }

                // Fallback path: auth must be verified before sending SELECT/WHOAMI to avoid NOAUTH pipelining.
                var okFallback = await AuthUsernamePasswordAsync(stream, o.Username, o.Password, ct).ConfigureAwait(false);
                if (!okFallback)
                {
                    usedFallback = true;
                    try
                    {
                        logger.LogWarning("AUTH with username failed; attempting password-only AUTH as fallback (default user).");
                    }
                    catch { }

                    await WriteHandshakeAsync(stream, ct, write => RedisRespProtocol.WriteAuthCommand(write, null, o.Password), () => RedisRespProtocol.GetAuthCommandLength(null, o.Password)).ConfigureAwait(false);
                    await RedisRespProtocol.ExpectOkAsync(stream, ct).ConfigureAwait(false);
                }

                // Auth succeeded (or fallback succeeded); now pipeline remaining steps.
                if (o.Database != 0 || o.LogWhoAmIOnConnect)
                {
                    await WriteHandshakeAsync(stream, ct, write =>
                    {
                        var idx = 0;
                        if (o.Database != 0)
                            idx += RedisRespProtocol.WriteSelectCommand(write.Slice(idx), o.Database);
                        if (o.LogWhoAmIOnConnect)
                            idx += RedisRespProtocol.WriteAclWhoAmICommand(write.Slice(idx));
                        return idx;
                    }, () =>
                    {
                        var len = 0;
                        if (o.Database != 0) len += RedisRespProtocol.GetSelectCommandLength(o.Database);
                        if (o.LogWhoAmIOnConnect) len += RedisRespProtocol.GetAclWhoAmICommandLength();
                        return len;
                    }).ConfigureAwait(false);

                    if (o.Database != 0)
                    {
                        await RedisRespProtocol.ExpectOkAsync(stream, ct).ConfigureAwait(false);
                        NotifyDatabaseSelected(id, o.Database);
                    }

                    if (o.LogWhoAmIOnConnect)
                    {
                        var user = await RedisRespProtocol.ReadBulkStringAsync(stream, ct).ConfigureAwait(false);
                        logger.LogInformation("Redis ACL WHOAMI={User}", user);
                    }
                }

                NotifyAuthenticated(id, o.Username, usedFallback);
            }
        }
        else
        {
            // No auth: pipeline SELECT/WHOAMI if needed.
            if (o.Database != 0 || o.LogWhoAmIOnConnect)
            {
                await WriteHandshakeAsync(stream, ct, write =>
                {
                    var idx = 0;
                    if (o.Database != 0)
                        idx += RedisRespProtocol.WriteSelectCommand(write.Slice(idx), o.Database);
                    if (o.LogWhoAmIOnConnect)
                        idx += RedisRespProtocol.WriteAclWhoAmICommand(write.Slice(idx));
                    return idx;
                }, () =>
                {
                    var len = 0;
                    if (o.Database != 0) len += RedisRespProtocol.GetSelectCommandLength(o.Database);
                    if (o.LogWhoAmIOnConnect) len += RedisRespProtocol.GetAclWhoAmICommandLength();
                    return len;
                }).ConfigureAwait(false);

                if (o.Database != 0)
                {
                    await RedisRespProtocol.ExpectOkAsync(stream, ct).ConfigureAwait(false);
                    NotifyDatabaseSelected(id, o.Database);
                }
                if (o.LogWhoAmIOnConnect)
                {
                    var user = await RedisRespProtocol.ReadBulkStringAsync(stream, ct).ConfigureAwait(false);
                    logger.LogInformation("Redis ACL WHOAMI={User}", user);
                }
            }
        }
    }

    private static async Task<bool> AuthUsernamePasswordAsync(Stream stream, string username, string password, CancellationToken ct)
    {
        await WriteHandshakeAsync(
                stream,
                ct,
                write => RedisRespProtocol.WriteAuthCommand(write, username, password),
                () => RedisRespProtocol.GetAuthCommandLength(username, password))
            .ConfigureAwait(false);

        return await RedisRespProtocol.TryExpectOkAsync(stream, ct).ConfigureAwait(false);
    }

    private static async Task WriteHandshakeAsync(
        Stream stream,
        CancellationToken ct,
        Func<Span<byte>, int> write,
        Func<int> getLength)
    {
        var len = getLength();
        if (len <= 0) return;

        byte[]? rented = null;
        try
        {
            rented = ArrayPool<byte>.Shared.Rent(len);
            var written = write(rented.AsSpan(0, len));
            await stream.WriteAsync(rented.AsMemory(0, written), ct).ConfigureAwait(false);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void NotifyConnectAttempt(RedisConnectionOptions o)
    {
        if (_observers.Length == 0) return;
        var e = new RedisConnectAttempt(o.Host, o.Port, o.UseTls);
        foreach (var obs in _observers)
        {
            try { obs.OnConnectAttempt(in e); } catch { }
        }
    }

    private void NotifyConnected(long id, RedisConnectionOptions o, TimeSpan connectTime)
    {
        if (_observers.Length == 0) return;
        var e = new RedisConnected(id, o.Host, o.Port, o.UseTls, connectTime);
        foreach (var obs in _observers)
        {
            try { obs.OnConnected(in e); } catch { }
        }
    }

    private void NotifyConnectFailed(RedisConnectionOptions o, Exception ex)
    {
        if (_observers.Length == 0) return;
        var e = new RedisConnectFailed(o.Host, o.Port, o.UseTls, ex);
        foreach (var obs in _observers)
        {
            try { obs.OnConnectFailed(in e); } catch { }
        }
    }

    private void NotifyAuthenticated(long id, string? username, bool usedFallbackPasswordOnly)
    {
        if (_observers.Length == 0) return;
        var e = new RedisAuthenticated(id, username, usedFallbackPasswordOnly);
        foreach (var obs in _observers)
        {
            try { obs.OnAuthenticated(in e); } catch { }
        }
    }

    private void NotifyDatabaseSelected(long id, int database)
    {
        if (_observers.Length == 0) return;
        var e = new RedisDatabaseSelected(id, database);
        foreach (var obs in _observers)
        {
            try { obs.OnDatabaseSelected(in e); } catch { }
        }
    }

    internal static RedisConnectionOptions ResolveOptions(RedisConnectionOptions o)
    {
        // Resolve ConnectionString (when provided) first; then apply lightweight defaults to reduce unnecessary
        // TCP/application chatter without overriding explicit user configuration.

        var effective = o;

        if (!string.IsNullOrWhiteSpace(o.ConnectionString) &&
            RedisConnectionStringParser.TryParse(o.ConnectionString, out var parsed, out _))
        {
            effective = o with
            {
                Host = parsed.Host,
                Port = parsed.Port,
                Username = parsed.Username,
                Password = parsed.Password,
                Database = parsed.Database,
                UseTls = parsed.UseTls,
                TlsHost = parsed.TlsHost,
                AllowInvalidCert = parsed.AllowInvalidCert
            };
        }

        return ApplyTcpChatterOptimization(effective);
    }

    private static RedisConnectionOptions ApplyTcpChatterOptimization(RedisConnectionOptions o)
    {
        // Goal:
        // - Localhost: disable both TCP keepalive probes and borrow-time PING validation (no NAT/LB idleness).
        // - Non-localhost: prefer TCP keepalive and disable borrow-time PING validation (option A).
        // 
        // IMPORTANT: do not override explicit configuration; only adjust when defaults are still in place.

        var isLoopback = IsLoopbackHost(o.Host);

        // Option A: disable borrow-time PING when using default ValidateAfterIdle.
        if (o.ValidateAfterIdle == TimeSpan.FromSeconds(30))
            o = o with { ValidateAfterIdle = TimeSpan.Zero };

        if (!isLoopback)
            return o;

        // For loopback only, also disable keepalive when using default keepalive settings.
        if (o.EnableTcpKeepAlive &&
            o.TcpKeepAliveTime == TimeSpan.FromSeconds(30) &&
            o.TcpKeepAliveInterval == TimeSpan.FromSeconds(10))
        {
            o = o with { EnableTcpKeepAlive = false };
        }

        return o;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return false;

        // Common literals.
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        // IP literals.
        if (System.Net.IPAddress.TryParse(host, out var ip))
            return System.Net.IPAddress.IsLoopback(ip);

        // Treat .local as non-loopback; don't do DNS here.
        return false;
    }

    private static bool IsProductionEnvironment()
    {
        // Check standard .NET environment variables
        var aspNetEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var dotnetEnv = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        // Production if explicitly set to Production, or if not set to Development/Staging
        var env = aspNetEnv ?? dotnetEnv ?? "Production";
        return !string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(env, "Staging", StringComparison.OrdinalIgnoreCase);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }
}

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeaconServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

/// <summary>
/// Authenticated HTTP API for launcher-side snapshot management. Listens on
/// <see cref="BeaconServerOptions.HttpPort"/>, validates an HMAC-SHA256
/// signature against the per-instance HMAC key, then dispatches to the save
/// orchestrator.
///
/// Routes (all under <c>/api/v1</c>):
///   GET  /health                       — public, no auth
///   GET  /info                         — instance + version
///   GET  /snapshots                    — list snapshots (auth)
///   GET  /snapshots/{id}/download      — stream zip (auth)
///   POST /snapshots                    — upload zip body, store as snapshot (auth)
///   POST /snapshots/{id}/restore       — restore previously-stored snapshot (auth)
///   POST /snapshots/import-restore     — upload + restore in one shot (auth)
///
/// Auth: client computes
///   signature = HMAC_SHA256(key, method + "\n" + path + "\n" + ts + "\n" + body_sha256_hex)
/// and sends headers:
///   X-Beacon-Timestamp: &lt;unix seconds&gt;
///   X-Beacon-Signature: &lt;hex&gt;
/// Server rejects timestamps older than 5 minutes (replay window).
/// </summary>
public sealed class BeaconHttpService : BackgroundService
{
    private const int ReplayWindowSeconds = 300;
    private const int MaxConcurrentRequests = 8;

    private readonly ILogger<BeaconHttpService> _log;
    private readonly BeaconServerOptions _opts;
    private readonly SaveOrchestratorService _saves;
    private readonly PipeServerState _pipeState;
    private readonly InstanceIdentityProvider _identity;
    private readonly byte[] _authKey;
    private readonly SemaphoreSlim _requestLimiter = new(MaxConcurrentRequests, MaxConcurrentRequests);
    // Sliding window of signatures we've already accepted, so a captured
    // valid request inside the 5-minute replay window can't be re-sent to
    // double-trigger a restore or pile up snapshots.
    private readonly Dictionary<string, long> _seenSignatures = new();
    private readonly object _seenSignaturesLock = new();
    private HttpListener? _listener;

    public BeaconHttpService(
        ILogger<BeaconHttpService> log,
        IOptions<BeaconServerOptions> opts,
        SaveOrchestratorService saves,
        PipeServerState pipeState,
        InstanceIdentityProvider identity)
    {
        _log = log;
        _opts = opts.Value;
        _saves = saves;
        _pipeState = pipeState;
        _identity = identity;
        // The HTTP API auth secret is SHA256(RconPassword). Same trust tier
        // as RCON — if the customer has set an RCON password, they already
        // expose admin control of the world. Deriving from RconPassword
        // means there's no second secret to manage. If RconPassword is
        // empty, the HTTP API will not start.
        _authKey = string.IsNullOrEmpty(_opts.RconPassword)
            ? Array.Empty<byte>()
            : SHA256.HashData(Encoding.UTF8.GetBytes(_opts.RconPassword));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_opts.HttpPort <= 0)
        {
            _log.LogInformation("HTTP API disabled (HttpPort <= 0)");
            return;
        }
        if (_authKey.Length == 0)
        {
            _log.LogWarning("HTTP API disabled: RconPassword is empty (set it to enable launcher snapshot APIs)");
            return;
        }

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{_opts.HttpPort}/api/v1/");

        try
        {
            _listener.Start();
            _log.LogInformation("HTTP API bound to all interfaces on port {Port}", _opts.HttpPort);
        }
        catch (HttpListenerException ex) when (ex.ErrorCode == 5 /* access denied */)
        {
            // Wildcard bind requires either Administrator or a one-time
            // urlacl registration. Try once to add the urlacl ourselves
            // (will succeed if we happen to be elevated). If that fails,
            // fall back to localhost — but log a clear error so customers
            // know remote launcher transfers will fail until urlacl is
            // provisioned.
            _log.LogWarning(ex,
                "HttpListener bind +:{Port} denied. Attempting netsh urlacl auto-registration",
                _opts.HttpPort);
            TryRegisterUrlAcl(_opts.HttpPort);

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://+:{_opts.HttpPort}/api/v1/");
                _listener.Start();
                _log.LogInformation("HTTP API bound to all interfaces on port {Port} after urlacl fix", _opts.HttpPort);
            }
            catch (Exception retryEx)
            {
                _log.LogError(retryEx,
                    "HTTP API DEGRADED: bound to LOCALHOST ONLY on port {Port}. " +
                    "Remote launcher world transfers WILL FAIL. " +
                    "Fix: run as Administrator once, or pre-register with " +
                    "'netsh http add urlacl url=http://+:{Port}/ user=Everyone'",
                    _opts.HttpPort, _opts.HttpPort);
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_opts.HttpPort}/api/v1/");
                _listener.Prefixes.Add($"http://127.0.0.1:{_opts.HttpPort}/api/v1/");
                try { _listener.Start(); }
                catch (Exception innerEx)
                {
                    _log.LogError(innerEx, "HttpListener could not bind even to localhost:{Port}; HTTP API disabled", _opts.HttpPort);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "HttpListener failed to start on port {Port}; HTTP API disabled", _opts.HttpPort);
            return;
        }

        _log.LogInformation("HTTP API listening on {Prefixes}", string.Join(", ", _listener.Prefixes));

        // Outer loop: if the listener crashes for any reason other than
        // shutdown, log it and restart after a short backoff so a transient
        // OS-level error doesn't permanently disable the API.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested && _listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try
                    {
                        var getCtx = _listener.GetContextAsync();
                        var done = await Task.WhenAny(getCtx, Task.Delay(Timeout.Infinite, stoppingToken)).ConfigureAwait(false);
                        if (done != getCtx) break;
                        ctx = await getCtx.ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException) { break; }
                    catch (OperationCanceledException) { break; }

                    // Bounded concurrency: refuse new requests if too many
                    // are already in flight rather than spawning unbounded
                    // Task.Run instances.
                    if (!await _requestLimiter.WaitAsync(0).ConfigureAwait(false))
                    {
                        try
                        {
                            ctx.Response.StatusCode = 503;
                            ctx.Response.Headers["Retry-After"] = "5";
                            ctx.Response.Close();
                        }
                        catch { }
                        continue;
                    }

                    _ = Task.Run(async () =>
                    {
                        try { await HandleRequestAsync(ctx, stoppingToken).ConfigureAwait(false); }
                        finally { _requestLimiter.Release(); }
                    }, stoppingToken);
                }
            }
            catch (HttpListenerException ex)
            {
                _log.LogWarning(ex, "HttpListener accept loop crashed; restarting in 5s");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "HttpListener accept loop hit unexpected error; restarting in 5s");
            }

            if (stoppingToken.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            try
            {
                if (!_listener.IsListening) _listener.Start();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "HttpListener restart failed; will retry");
            }
        }

        try { _listener?.Stop(); } catch { }
        _log.LogInformation("HTTP API stopped");
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        var path = req.Url?.AbsolutePath ?? "";
        var method = req.HttpMethod.ToUpperInvariant();

        try
        {
            res.Headers["X-Beacon-Instance"] = _identity.InstanceId;

            // Public endpoints
            if (method == "GET" && path == "/api/v1/health")
            {
                await WriteJsonAsync(res, 200, new
                {
                    ok = true,
                    instance = _identity.InstanceId,
                    beacon_version = BeaconVersionInfo.BeaconVersion,
                    sn2_build = BeaconVersionInfo.Sn2Build,
                });
                return;
            }

            if (method == "GET" && path == "/api/v1/players")
            {
                // Public — same info Source A2S exposes, just as JSON so
                // the launcher hero panel doesn't have to parse A2S to
                // show 'who is online'. No auth required.
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var snap = _pipeState.Players;
                await WriteJsonAsync(res, 200, new
                {
                    instance = _identity.InstanceId,
                    count = snap.Count,
                    players = snap.Select(p => new
                    {
                        name = string.IsNullOrEmpty(p.DisplayName) ? p.BeaconUserId : p.DisplayName,
                        connected_seconds = Math.Max(0, (nowMs - p.ConnectedAtUnixMs) / 1000),
                        ping_ms = p.PingMs,
                    }),
                });
                return;
            }

            // Auth required for everything below. Body (if any) is streamed
            // to a temp file once and the resulting BufferedBody is passed
            // through to the route handler — no full-body MemoryStream and
            // no base64-via-header round trip (used to triple the memory
            // footprint per upload).
            BufferedBody? body;
            try
            {
                body = await ValidateAuthAndBufferAsync(req, ct).ConfigureAwait(false);
            }
            catch (BodyTooLargeException)
            {
                await WriteJsonAsync(res, 413, new { error = "payload too large" });
                return;
            }
            if (body is null)
            {
                await WriteJsonAsync(res, 401, new { error = "unauthorized" });
                return;
            }

            if (method == "GET" && path == "/api/v1/info")
            {
                body.Dispose();
                await WriteJsonAsync(res, 200, new
                {
                    instance = _identity.InstanceId,
                    beacon_version = BeaconVersionInfo.BeaconVersion,
                    sn2_build = BeaconVersionInfo.Sn2Build,
                    gameplay_port = _opts.GameplayPort,
                    query_port = _opts.QueryPort,
                    max_players = _opts.MaxPlayers,
                });
                return;
            }

            if (method == "GET" && path == "/api/v1/snapshots")
            {
                body.Dispose();
                var list = _saves.ListSnapshots();
                await WriteJsonAsync(res, 200, new
                {
                    snapshots = list.Select(s => new
                    {
                        id = s.SnapshotId,
                        taken_unix = s.TakenUnix,
                        size_bytes = s.SizeBytes,
                        sha256 = s.Sha256Hex,
                        retention_days = s.RetentionDays,
                    }),
                });
                return;
            }

            if (method == "GET" && path.StartsWith("/api/v1/snapshots/") && path.EndsWith("/download"))
            {
                body.Dispose();
                var id = path["/api/v1/snapshots/".Length..^"/download".Length];
                var record = _saves.ListSnapshots(int.MaxValue).FirstOrDefault(s => s.SnapshotId == id);
                if (record is null || !File.Exists(record.FilePath))
                {
                    await WriteJsonAsync(res, 404, new { error = "snapshot not found" });
                    return;
                }
                res.StatusCode = 200;
                res.ContentType = "application/zip";
                res.Headers["X-Beacon-Sha256"] = record.Sha256Hex;
                res.Headers["Content-Disposition"] = $"attachment; filename=\"{id}.zip\"";
                res.ContentLength64 = record.SizeBytes;
                await using (var fs = File.OpenRead(record.FilePath))
                {
                    await fs.CopyToAsync(res.OutputStream, ct).ConfigureAwait(false);
                }
                res.OutputStream.Close();
                return;
            }

            if (method == "POST" && path == "/api/v1/snapshots")
            {
                if (body.SizeBytes == 0)
                {
                    body.Dispose();
                    await WriteJsonAsync(res, 400, new { error = "empty body" });
                    return;
                }
                // The body is already on disk as a temp file. Just rename
                // it into SaveDir under the canonical snapshot name.
                Directory.CreateDirectory(_opts.SaveDir);
                var snapshotId = $"upload-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}".Substring(0, 36);
                var finalPath = Path.Combine(_opts.SaveDir, $"{snapshotId}.zip");
                File.Move(body.TempPath, finalPath);
                body.MarkConsumed(); // disposal won't try to delete the moved file

                var record = new Beacon.Persistence.SnapshotRecord(
                    SnapshotId: snapshotId,
                    TakenUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    FilePath: finalPath,
                    SizeBytes: new FileInfo(finalPath).Length,
                    Sha256Hex: body.Sha256Hex,
                    RetentionDays: 30);
                _saves.Database.RecordSnapshot(record);
                _saves.Database.Audit("http-api", "snapshot.upload", snapshotId,
                    $"{{\"bytes\":{record.SizeBytes}}}", record.TakenUnix);

                await WriteJsonAsync(res, 200, new
                {
                    snapshot = new
                    {
                        id = record.SnapshotId,
                        taken_unix = record.TakenUnix,
                        size_bytes = record.SizeBytes,
                        sha256 = record.Sha256Hex,
                    },
                });
                return;
            }

            if (method == "POST" && path.StartsWith("/api/v1/snapshots/") && path.EndsWith("/restore"))
            {
                body.Dispose();
                var id = path["/api/v1/snapshots/".Length..^"/restore".Length];
                var ok = await _saves.RestoreSnapshotAsync(id, "http-api", ct).ConfigureAwait(false);
                await WriteJsonAsync(res, ok ? 200 : 500, new { ok });
                return;
            }

            if (method == "POST" && path == "/api/v1/snapshots/import-restore")
            {
                if (body.SizeBytes == 0)
                {
                    body.Dispose();
                    await WriteJsonAsync(res, 400, new { error = "empty body" });
                    return;
                }
                var ok = await _saves.RestoreFromZipPathAsync(
                    body.TempPath, "http-api", "snapshot.import_restore", ct).ConfigureAwait(false);
                body.Dispose();
                await WriteJsonAsync(res, ok ? 200 : 500, new { ok });
                return;
            }

            body.Dispose();
            await WriteJsonAsync(res, 404, new { error = "not found", method, path });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "HTTP {Method} {Path} threw", method, path);
            try
            {
                await WriteJsonAsync(res, 500, new { error = ex.Message });
            }
            catch { }
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    /// <summary>
    /// Streams the request body (if any) to a temp file while computing
    /// SHA256 inline. On signature mismatch / replay reuse / cap overflow,
    /// the temp file is deleted and we return null. The body never lands
    /// in a MemoryStream — uploads up to <see cref="BeaconServerOptions.MaxUploadBytes"/>
    /// cost one temp-file's worth of disk and an HMAC state.
    /// </summary>
    private async Task<BufferedBody?> ValidateAuthAndBufferAsync(HttpListenerRequest req, CancellationToken ct)
    {
        var tsHeader = req.Headers["X-Beacon-Timestamp"];
        var sigHeader = req.Headers["X-Beacon-Signature"];
        if (string.IsNullOrWhiteSpace(tsHeader) || string.IsNullOrWhiteSpace(sigHeader))
            return null;
        if (!long.TryParse(tsHeader, out var ts)) return null;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - ts) > ReplayWindowSeconds) return null;

        if (req.ContentLength64 > _opts.MaxUploadBytes)
            throw new BodyTooLargeException();

        var path = req.Url?.AbsolutePath ?? "";
        var method = req.HttpMethod.ToUpperInvariant();

        var buffered = await BufferBodyToDiskAsync(req, _opts.MaxUploadBytes, ct).ConfigureAwait(false);
        // buffered.Sha256Hex is already lower-hex over the streamed bytes.

        var canonical = $"{method}\n{path}\n{ts}\n{buffered.Sha256Hex}";
        var expected = Convert.ToHexString(
            HMACSHA256.HashData(_authKey, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        var sigOk = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(sigHeader.ToLowerInvariant()));

        if (!sigOk)
        {
            buffered.Dispose();
            return null;
        }

        var sigKey = sigHeader.ToLowerInvariant();
        lock (_seenSignaturesLock)
        {
            if (_seenSignatures.Count > 0)
            {
                var cutoff = now - ReplayWindowSeconds;
                var toRemove = _seenSignatures
                    .Where(kv => kv.Value < cutoff)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (var k in toRemove) _seenSignatures.Remove(k);
            }
            if (_seenSignatures.ContainsKey(sigKey))
            {
                buffered.Dispose();
                return null;
            }
            _seenSignatures[sigKey] = ts;
        }

        return buffered;
    }

    /// <summary>
    /// Stream the request body into a temp file while hashing in 64 KB
    /// chunks. Never allocates a buffer the size of the body. Throws
    /// <see cref="BodyTooLargeException"/> if the stream exceeds the cap
    /// mid-read.
    /// </summary>
    private static async Task<BufferedBody> BufferBodyToDiskAsync(HttpListenerRequest req, long maxBytes, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "beacon-uploads");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, $"upload-{Guid.NewGuid():N}.bin");

        long total = 0;
        string sha;
        try
        {
            await using var fs = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            using var hasher = SHA256.Create();
            var buf = new byte[64 * 1024];
            if (req.ContentLength64 != 0)
            {
                while (true)
                {
                    int n = await req.InputStream.ReadAsync(buf, ct).ConfigureAwait(false);
                    if (n <= 0) break;
                    total += n;
                    if (total > maxBytes)
                        throw new BodyTooLargeException();
                    hasher.TransformBlock(buf, 0, n, null, 0);
                    await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            sha = Convert.ToHexString(hasher.Hash!).ToLowerInvariant();
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }

        return new BufferedBody(tempPath, sha, total);
    }

    /// <summary>
    /// Holds a temp-file path + SHA256 of a streamed request body. Disposal
    /// deletes the temp file unless the route handler called
    /// <see cref="MarkConsumed"/> (route renamed it into permanent storage
    /// and owns it from that point on).
    /// </summary>
    private sealed class BufferedBody : IDisposable
    {
        public string TempPath { get; }
        public string Sha256Hex { get; }
        public long SizeBytes { get; }
        private int _disposed;
        private int _consumed;

        public BufferedBody(string tempPath, string sha256Hex, long sizeBytes)
        {
            TempPath = tempPath;
            Sha256Hex = sha256Hex;
            SizeBytes = sizeBytes;
        }

        public void MarkConsumed() => Interlocked.Exchange(ref _consumed, 1);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_consumed != 0) return;
            try { if (File.Exists(TempPath)) File.Delete(TempPath); } catch { }
        }
    }

    private sealed class BodyTooLargeException : Exception { }

    /// <summary>
    /// Best-effort one-shot of
    /// <c>netsh http add urlacl url=http://+:{port}/ user=Everyone</c>.
    /// Succeeds when BeaconServer happens to be running with elevation;
    /// silently no-ops otherwise. The caller still retries the bind and
    /// drops to localhost if this didn't help.
    /// </summary>
    private void TryRegisterUrlAcl(int port)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("http");
            psi.ArgumentList.Add("add");
            psi.ArgumentList.Add("urlacl");
            psi.ArgumentList.Add($"url=http://+:{port}/");
            psi.ArgumentList.Add("user=Everyone");
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return;
            if (!p.WaitForExit(3_000))
            {
                try { p.Kill(true); } catch { }
                return;
            }
            if (p.ExitCode == 0)
                _log.LogInformation("netsh urlacl registered for port {Port}", port);
            else
                _log.LogInformation("netsh urlacl returned {Code} for port {Port} (likely needs elevation)", p.ExitCode, port);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "netsh urlacl probe failed (non-fatal)");
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, int status, object payload)
    {
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }
}

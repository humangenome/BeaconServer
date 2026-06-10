using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeaconServer.Configuration;
using Microsoft.Extensions.Configuration;
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
    private readonly IConfiguration _config;
    private readonly SaveOrchestratorService _saves;
    private readonly PipeServerState _pipeState;
    private readonly InstanceIdentityProvider _identity;
    private readonly ChatService _chat;
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
        IConfiguration config,
        SaveOrchestratorService saves,
        PipeServerState pipeState,
        InstanceIdentityProvider identity,
        ChatService chat)
    {
        _log = log;
        _opts = opts.Value;
        _config = config;
        _saves = saves;
        _pipeState = pipeState;
        _identity = identity;
        _chat = chat;
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
        _listener.Prefixes.Add($"http://+:{_opts.HttpPort}/map/");

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
                _listener.Prefixes.Add($"http://+:{_opts.HttpPort}/map/");
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
                _listener.Prefixes.Add($"http://localhost:{_opts.HttpPort}/map/");
                _listener.Prefixes.Add($"http://127.0.0.1:{_opts.HttpPort}/map/");
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
            // The live web map is opened from a file:// / localhost origin (the
            // launcher), so the browser needs CORS to read these JSON endpoints.
            // ACAO does not bypass server-side auth — sensitive routes stay HMAC-gated.
            res.Headers["Access-Control-Allow-Origin"] = "*";
            if (method == "OPTIONS")
            {
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                res.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                res.StatusCode = 204;
                res.OutputStream.Close();
                return;
            }

            // Public endpoints
            if (method == "GET" && path == "/api/v1/health")
            {
                var heartbeatTimeout = TimeSpan.FromSeconds(Math.Max(1, _opts.PluginHeartbeatTimeoutSeconds));
                var pluginOnline = _pipeState.HasFreshHeartbeat(heartbeatTimeout);
                int? heartbeatAgeSeconds = null;
                if (_pipeState.LastHeartbeatAt is DateTimeOffset lastHeartbeat)
                    heartbeatAgeSeconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - lastHeartbeat).TotalSeconds);

                await WriteJsonAsync(res, pluginOnline ? 200 : 503, new
                {
                    ok = pluginOnline,
                    instance = _identity.InstanceId,
                    server_name = string.IsNullOrWhiteSpace(_opts.ServerName) ? $"Beacon - {_identity.InstanceId}" : _opts.ServerName.Trim(),
                    beacon_version = BeaconVersionInfo.BeaconVersion,
                    sn2_build = BeaconVersionInfo.Sn2Build,
                    gameplay_port = _opts.GameplayPort,
                    query_port = _opts.QueryPort,
                    max_players = _opts.MaxPlayers,
                    player_count = _pipeState.EffectivePlayerCount,
                    plugin_connected = _pipeState.Connection is not null,
                    last_heartbeat_age_seconds = heartbeatAgeSeconds,
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
                        ping_ms = p.PingMs > 0 ? p.PingMs : (int?)null,
                    }),
                });
                return;
            }

            if (method == "GET" && path == "/api/v1/manifest")
            {
                // Public — players need this before they have RconPassword,
                // so HMAC is intentionally not enforced. Integrity rests on
                // per-mod sha256 pins in the payload. Spec:
                // protocol/manifest-v1.md.
                //
                // Read the Mods section fresh from IConfiguration so a
                // hoster editing appsettings.json never needs a restart.
                // ASP.NET Core's default appsettings provider has
                // reloadOnChange=true so this picks up edits within seconds.
                var mods = _config.GetSection("Beacon:Mods").Get<ModsOptions>() ?? new ModsOptions();
                // Gate the in-game chat overlay on Beacon:Chat:Enabled (default
                // true). When a hoster turns chat off, drop the beacon-hud entry
                // from the published manifest so joining clients never install or
                // load it — chat is off server-wide. Read fresh (reloadOnChange)
                // so the toggle applies without a restart.
                var chatEnabled = _config.GetValue<bool?>("Beacon:Chat:Enabled") ?? true;
                var requiredMods = (mods.Required ?? new())
                    .Where(m => chatEnabled || !string.Equals(m.Id, "beacon-hud", StringComparison.OrdinalIgnoreCase));
                await WriteJsonAsync(res, 200, new
                {
                    manifest_version = 1,
                    instance = _identity.InstanceId,
                    server_name = string.IsNullOrWhiteSpace(_opts.ServerName) ? $"Beacon - {_identity.InstanceId}" : _opts.ServerName.Trim(),
                    beacon_version = BeaconVersionInfo.BeaconVersion,
                    generated_unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    required = requiredMods.Select(SerializeEntry),
                    recommended = (mods.Recommended ?? new()).Select(SerializeEntry),
                    blocked = (mods.Blocked ?? new()).Select(b => new
                    {
                        id = b.Id ?? "",
                        reason = b.Reason ?? "",
                    }),
                });
                return;
            }

            // ----- Live web map (protocol/map-v1.md) -----
            //
            // BeaconRoster Lua mod writes roster.json (player list +
            // positions) every 5s and on K2_PostLogin/Logout.
            // /api/v1/map/state projects that into the SPA's expected
            // shape. Public when MapOptions.Public=true (community
            // dashboards) or HMAC-authed (launcher / private use) —
            // fall-through to the authed handler at the bottom of this
            // method covers the private path. The /map/ SPA HTML is
            // always public; if the state endpoint refuses the SPA's
            // poll the page renders "map is private" rather than failing
            // silently.

            // The HttpListener only routes the "/map/" prefix here (prefixes
            // must end in '/'), so every map asset arrives as /map/<path>.
            if (method == "GET" && path.StartsWith("/map/", StringComparison.Ordinal))
            {
                if (!_opts.Map.Enabled)
                {
                    await WriteJsonAsync(res, 404, new { error = "map disabled" });
                    return;
                }
                await ServeMapAssetAsync(res, path, ct).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/api/v1/map/state" && _opts.Map.Enabled && _opts.Map.Public)
            {
                await ServeMapStateAsync(res, ct).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path == "/api/v1/map/state" && !_opts.Map.Enabled)
            {
                await WriteJsonAsync(res, 404, new { error = "map disabled" });
                return;
            }

            // ----- Chat plane reads (protocol/chat-v1.md) -----
            //
            // Public on purpose. SN2 has no native chat, so the only thing
            // that ever lands in the ring buffer is admin broadcast
            // (RCON say/announce + HTTP /chat/say + scheduled warnings +
            // MOTD). Everything in the stream is already shouted at every
            // joined player, so a public read endpoint leaks nothing extra.
            //
            // The in-game BeaconHud overlay runs inside the player's SN2
            // process and has no access to RconPassword, so HMAC would
            // require shipping the secret to every client. If SN2 ever
            // grows native chat or DMs, the public surface stays
            // admin-broadcast-only and a separate authed /chat/recent/full
            // can carry private channels.
            if (method == "GET" && path == "/api/v1/chat/recent")
            {
                var sinceMs = ParseLongQuery(req.Url, "since", 0);
                var limit = (int)ParseLongQuery(req.Url, "limit", 100);
                var msgs = _chat.GetRecent(sinceMs, limit);
                await WriteJsonAsync(res, 200, new
                {
                    version = 1,
                    instance = _identity.InstanceId,
                    now_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    messages = msgs,
                });
                return;
            }

            if (method == "GET" && path == "/api/v1/chat/motd")
            {
                await WriteJsonAsync(res, 200, new
                {
                    instance = _identity.InstanceId,
                    msg = _chat.GetMotd(),
                });
                return;
            }

            // ----- Public chat ingest from player overlays -----
            //
            // SN2 has no native chat. Players type into the BeaconHud overlay's
            // ImGui InputText, the launcher relays the message here. Channel is
            // forced to "player" and target to "all" by ChatService.
            // BroadcastFromPlayer. Per-IP rate limit handled there.
            //
            // Body is bounded at 2 KB — bigger than any sensible chat message
            // (the body itself caps at 512 bytes via NormalizeBody) but small
            // enough that we can read it inline without the streaming buffer
            // the authed routes use.
            if (method == "POST" && path == "/api/v1/chat/player")
            {
                string rawBody;
                try { rawBody = await ReadShortBodyAsync(req, maxBytes: 2048, ct).ConfigureAwait(false); }
                catch (BodyTooLargeException)
                {
                    await WriteJsonAsync(res, 413, new { error = "payload too large" });
                    return;
                }
                ChatPlayerRequest? plReq;
                try
                {
                    plReq = JsonSerializer.Deserialize<ChatPlayerRequest>(rawBody, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                        PropertyNameCaseInsensitive = true,
                    });
                }
                catch (JsonException)
                {
                    await WriteJsonAsync(res, 400, new { error = "invalid json" });
                    return;
                }
                if (plReq is null || string.IsNullOrWhiteSpace(plReq.Msg))
                {
                    await WriteJsonAsync(res, 400, new { error = "msg required" });
                    return;
                }
                var clientIp = req.RemoteEndPoint?.Address?.ToString() ?? "";
                var entry = _chat.BroadcastFromPlayer(clientIp, plReq.Sender ?? "Player", plReq.Msg);
                if (entry is null)
                {
                    await WriteJsonAsync(res, 429, new { error = "rate limited" });
                    return;
                }
                await WriteJsonAsync(res, 200, new { ok = true, ts = entry.Ts });
                return;
            }

            // Public: a connected launcher pushes its own player's world position
            // for the live web map (the headless server can't read a remote
            // player's position — they're simulated client-side). No HMAC, same
            // trust model as /chat/player; positions expire server-side.
            if (method == "POST" && path == "/api/v1/map/position")
            {
                string rawBody;
                try { rawBody = await ReadShortBodyAsync(req, maxBytes: 1024, ct).ConfigureAwait(false); }
                catch (BodyTooLargeException)
                {
                    await WriteJsonAsync(res, 413, new { error = "payload too large" });
                    return;
                }
                MapPositionRequest? posReq;
                try
                {
                    posReq = JsonSerializer.Deserialize<MapPositionRequest>(rawBody, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                        PropertyNameCaseInsensitive = true,
                    });
                }
                catch (JsonException)
                {
                    await WriteJsonAsync(res, 400, new { error = "invalid json" });
                    return;
                }
                if (posReq is null || string.IsNullOrWhiteSpace(posReq.Id))
                {
                    await WriteJsonAsync(res, 400, new { error = "id required" });
                    return;
                }
                _pipeState.UpsertClientPosition(posReq.Id, posReq.Name ?? posReq.Id, posReq.X, posReq.Y, posReq.Z);
                await WriteJsonAsync(res, 200, new { ok = true });
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

            // Authed entry for /api/v1/map/state. If MapOptions.Public is
            // set the public block above returned early; this branch handles
            // launcher + dashboard clients that authenticate via HMAC.
            if (method == "GET" && path == "/api/v1/map/state")
            {
                body.Dispose();
                if (!_opts.Map.Enabled)
                {
                    await WriteJsonAsync(res, 404, new { error = "map disabled" });
                    return;
                }
                await ServeMapStateAsync(res, ct).ConfigureAwait(false);
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

            // ----- Chat plane writes (protocol/chat-v1.md) -----
            // GET /chat/recent + GET /chat/motd live above the auth gate.
            if (method == "POST" && path == "/api/v1/chat/say")
            {
                var sayReq = await ParseJsonBodyAsync<ChatSayRequest>(body, ct).ConfigureAwait(false);
                body.Dispose();
                if (sayReq is null || string.IsNullOrWhiteSpace(sayReq.Msg))
                {
                    await WriteJsonAsync(res, 400, new { error = "msg required" });
                    return;
                }
                var entry = _chat.BroadcastFromServer(
                    sayReq.Msg, sayReq.Channel, sayReq.Color, sayReq.Sender);
                await WriteJsonAsync(res, 200, new { ok = true, ts = entry.Ts });
                return;
            }

            if (method == "POST" && path == "/api/v1/chat/motd")
            {
                var motdReq = await ParseJsonBodyAsync<MotdRequest>(body, ct).ConfigureAwait(false);
                body.Dispose();
                if (motdReq is null)
                {
                    await WriteJsonAsync(res, 400, new { error = "json body required" });
                    return;
                }
                var ok = _chat.SetMotd(motdReq.Msg ?? "");
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

    // Root of the vendored joric map app, copied to output as map/ by the
    // BeaconServer.csproj Content glob.
    private static readonly string MapRoot =
        Path.Combine(AppContext.BaseDirectory, "map");

    /// <summary>
    /// Serves the vendored joric map SPA and its static assets under /map/.
    /// /map/ and /map/index.html resolve to index.html; everything else maps
    /// to a file under <see cref="MapRoot"/> with a path-traversal guard. The
    /// SPA itself pulls tiles + libs from our GitHub Pages / CDN; only the live
    /// player overlay polls this server's same-origin /api/v1/map/state.
    /// </summary>
    private async Task ServeMapAssetAsync(HttpListenerResponse res, string path, CancellationToken ct)
    {
        // Strip the "/map/" prefix; default to index.html for the bare dir.
        var rel = path.Substring("/map/".Length);
        if (rel.Length == 0 || rel.EndsWith('/'))
            rel += "index.html";
        rel = Uri.UnescapeDataString(rel).Replace('/', Path.DirectorySeparatorChar);

        var full = Path.GetFullPath(Path.Combine(MapRoot, rel));
        var rootFull = Path.GetFullPath(MapRoot) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.Ordinal) || !File.Exists(full))
        {
            await WriteJsonAsync(res, 404, new { error = "not found" });
            return;
        }

        res.StatusCode = 200;
        res.ContentType = MapContentType(full);
        // index.html stays fresh (live overlay wiring); hashed assets + data
        // can cache briefly. Keep it simple: no-store for html, short for rest.
        res.Headers["Cache-Control"] =
            full.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? "no-store" : "max-age=300";
        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        res.OutputStream.Close();
    }

    private static string MapContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js"   => "text/javascript; charset=utf-8",
            ".css"  => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".gz"   => "application/gzip",
            ".webp" => "image/webp",
            ".png"  => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg"  => "image/svg+xml",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            ".ttf"  => "font/ttf",
            _ => "application/octet-stream",
        };

    /// <summary>
    /// Reads roster.json (written by the BeaconRoster Lua mod every 5s,
    /// plus on K2_PostLogin/Logout) and projects it into the /api/v1/map/state
    /// response shape. Position fields (X/Y/Z, world.name) are populated by
    /// the same rescan that writes the player list, so map data is exactly
    /// as fresh as the roster snapshot.
    ///
    /// Why not a separate map-state.json: UE5 dedicated SN2 with zero
    /// connected players ticks at near-zero rate, so a 1Hz Lua LoopAsync
    /// fires every 10+ min in idle state. Roster's 5s tick is the only
    /// scheduler in the mod chain that catches both join (via K2_PostLogin
    /// fast path) and movement (via active-engine tick when a player is
    /// in-world), so we co-locate position publishing with it.
    /// </summary>
    private async Task ServeMapStateAsync(HttpListenerResponse res, CancellationToken ct)
    {
        // Players + freshness come from the in-memory pipe state, which the native
        // plugin pushes on its wall-clock heartbeat thread (PlayerListSnapshot) — so
        // the map survives the UE5 0-player engine-thread collapse that froze the old
        // roster.json path. World name is read best-effort from roster.json (cheap
        // side-channel; it rarely changes and isn't freshness-critical).
        long unixMs = _pipeState.LastHeartbeatAt?.ToUnixTimeMilliseconds() ?? 0;
        var stale = !_pipeState.HasFreshHeartbeat(TimeSpan.FromMilliseconds(_opts.Map.StaleAfterMs));

        // Prefer client-reported positions (real world coords + the launcher's
        // friendly name) — the headless server can't read remote-player positions
        // itself. Fall back to the native roster (count + names at 0,0,0) when no
        // launcher is pushing, so the endpoint is never empty while players are on.
        var clientPos = _pipeState.RecentClientPositions(
            TimeSpan.FromMilliseconds(Math.Max(_opts.Map.StaleAfterMs, 15000)));
        object[] players;
        if (clientPos.Count > 0)
        {
            players = clientPos.Select(p => (object)new
            {
                id    = p.Id,
                name  = string.IsNullOrEmpty(p.Name) ? p.Id : p.Name,
                x     = p.X,
                y     = p.Y,
                z     = p.Z,
                biome = "",
            }).ToArray();
        }
        else
        {
            players = _pipeState.Players.Select(p => (object)new
            {
                id    = p.BeaconUserId,
                name  = string.IsNullOrEmpty(p.DisplayName) ? p.BeaconUserId : p.DisplayName,
                x     = p.PosX,
                y     = p.PosY,
                z     = p.PosZ,
                biome = "",
            }).ToArray();
        }

        string worldName = "";
        var rosterPath = Path.Combine(AppContext.BaseDirectory, "roster.json");
        if (File.Exists(rosterPath))
        {
            try
            {
                var body = await File.ReadAllTextAsync(rosterPath, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("world", out var w) && w.ValueKind == JsonValueKind.Object
                    && w.TryGetProperty("name", out var wn))
                    worldName = wn.GetString() ?? "";
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _log.LogDebug(ex, "roster.json world-name read failed for map state");
            }
        }

        await WriteJsonAsync(res, 200, new
        {
            instance = _identity.InstanceId,
            beacon_version = BeaconVersionInfo.BeaconVersion,
            server_name = string.IsNullOrWhiteSpace(_opts.ServerName)
                ? $"Beacon - {_identity.InstanceId}"
                : _opts.ServerName.Trim(),
            unix_ms = unixMs,
            stale,
            world = new { name = worldName },
            players,
        });
    }

    private static long ParseLongQuery(Uri? url, string key, long defaultValue)
    {
        if (url is null) return defaultValue;
        var q = url.Query;
        if (string.IsNullOrEmpty(q)) return defaultValue;
        if (q[0] == '?') q = q[1..];
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (!pair.AsSpan(0, eq).SequenceEqual(key.AsSpan())) continue;
            var raw = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (long.TryParse(raw, out var v)) return v;
            return defaultValue;
        }
        return defaultValue;
    }

    private static async Task<T?> ParseJsonBodyAsync<T>(BufferedBody body, CancellationToken ct) where T : class
    {
        try
        {
            await using var fs = File.OpenRead(body.TempPath);
            return await JsonSerializer.DeserializeAsync<T>(fs, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private sealed class ChatSayRequest
    {
        public string Msg { get; set; } = "";
        public string? Channel { get; set; }
        public string? Color { get; set; }
        public string? Sender { get; set; }
    }

    private sealed class MotdRequest
    {
        public string? Msg { get; set; }
    }

    private sealed class ChatPlayerRequest
    {
        public string Msg { get; set; } = "";
        public string? Sender { get; set; }
    }

    private sealed class MapPositionRequest
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    private static async Task<string> ReadShortBodyAsync(HttpListenerRequest req, int maxBytes, CancellationToken ct)
    {
        if (!req.HasEntityBody) return "";
        if (req.ContentLength64 > maxBytes) throw new BodyTooLargeException();
        var buffer = new byte[Math.Min(maxBytes, 4096)];
        using var ms = new MemoryStream();
        int total = 0;
        var stream = req.InputStream;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > maxBytes) throw new BodyTooLargeException();
            ms.Write(buffer, 0, read);
        }
        return req.ContentEncoding.GetString(ms.ToArray());
    }

    private static object SerializeEntry(ModEntry e) => new
    {
        id = e.Id ?? "",
        name = e.Name ?? "",
        version = e.Version ?? "",
        url = e.Url ?? "",
        sha256 = string.IsNullOrEmpty(e.Sha256) ? null : e.Sha256,
        size_bytes = e.SizeBytes,
        install_root = e.InstallRoot ?? "",
        notes = string.IsNullOrEmpty(e.Notes) ? null : e.Notes,
    };
}

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
///   X-Beacon-Content-Sha256: &lt;body sha256 hex&gt;  (required for bodies over 64 KB)
/// Server rejects timestamps older than 5 minutes (replay window). The signature
/// is checked before any body over 64 KB is read.
/// </summary>
public sealed class BeaconHttpService : BackgroundService
{
    private const int ReplayWindowSeconds = 300;

    // Three separate pools, so no kind of request can starve another: the public
    // reads the app and the panel poll, signed admin calls, and big signed uploads.
    // A request takes one slot of its own pool; a full pool answers 503.
    internal const int PublicConcurrency = 16;
    internal const int SignedConcurrency = 8;
    internal const int UploadConcurrency = 2;

    // Signed bodies up to this size are read and then checked as before. A bigger
    // body must declare its SHA-256 in X-Beacon-Content-Sha256; the signature covers
    // it, so the signature is checked before the first byte of the body is read.
    internal const int SmallBodyMaxBytes = 64 * 1024;
    internal const string ContentShaHeader = "X-Beacon-Content-Sha256";
    internal const string MapKeyHeader = "X-Beacon-Map-Key";

    private readonly ILogger<BeaconHttpService> _log;
    private readonly BeaconServerOptions _opts;
    private readonly IConfiguration _config;
    private readonly SaveOrchestratorService _saves;
    private readonly PipeServerState _pipeState;
    private readonly InstanceIdentityProvider _identity;
    private readonly ChatService _chat;
    private readonly byte[] _authKey;
    private readonly SemaphoreSlim _publicLimiter = new(PublicConcurrency, PublicConcurrency);
    private readonly SemaphoreSlim _signedLimiter = new(SignedConcurrency, SignedConcurrency);
    private readonly SemaphoreSlim _uploadLimiter = new(UploadConcurrency, UploadConcurrency);

    // Tests run every request from 127.0.0.1; this lets them stand in for a remote caller.
    internal bool LoopbackIsLocal { get; set; } = true;
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

        ApplyListenerTimeouts(_listener);
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

                    // Bounded concurrency per pool: refuse new requests when their
                    // pool is full rather than spawning unbounded Task.Run instances.
                    // The method, path and headers are already parsed here; the body
                    // is not, so classifying costs nothing.
                    var limiter = LimiterFor(ClassifyRequest(
                        ctx.Request.HttpMethod, ctx.Request.Url?.AbsolutePath ?? "",
                        ctx.Request.HasEntityBody, ctx.Request.ContentLength64));
                    if (!await limiter.WaitAsync(0).ConfigureAwait(false))
                    {
                        try
                        {
                            ctx.Response.StatusCode = 503;
                            ctx.Response.Headers["Retry-After"] = "5";
                            ctx.Response.KeepAlive = false;
                            ctx.Response.Close();
                        }
                        catch { }
                        continue;
                    }

                    _ = Task.Run(async () =>
                    {
                        try { await HandleRequestAsync(ctx, stoppingToken).ConfigureAwait(false); }
                        finally { limiter.Release(); }
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

    // A restore stops the game to swap the world. Only a BeaconServer that starts the
    // game itself brings it back afterwards, so anywhere else a restore would leave the
    // server down.
    internal const string NoRelaunchMessage =
        "restore and import are off: this server's game is started by its host, not by BeaconServer";

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

                _pipeState.EvaluateChatGate();
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
                    // True when a snapshot restore or save import brings the world
                    // back up by itself (this BeaconServer starts the game). The app
                    // shows restore and import only when this is true.
                    world_restore = SnProcessSupervisorService.SupervisesGame(_opts),
                    // "ok", or "no_connections_seen" when players have been on for 10
                    // minutes and no game connection could be read from the game log
                    // (player chat and the map then refuse everyone).
                    chat_gate = _pipeState.ChatGate.State,
                    chat_gate_since_unix = _pipeState.ChatGate.DegradedSinceUnix,
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

            // Who may read the live map without the admin signature: anyone when the
            // operator made it public, players connected to this server (the app's Map
            // button opens it in the player's browser), and holders of the map key
            // (a hosting panel's relay). Everyone else falls through to the signed
            // handler below and gets 401.
            if (method == "GET" && path == "/api/v1/map/state" && _opts.Map.Enabled
                && (_opts.Map.Public || IsPlayerCaller(req) || MapKeyMatches(_opts.Map.AccessKey, PresentedMapKey(req))))
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
            // The ring carries player chat as well as admin broadcasts, so it is
            // for the players on this server: the app polls it for the in-game
            // BeaconHud overlay from the player's own PC, whose address is the
            // one the game connection came from. The player holds no admin
            // secret, so the address is what proves they are on the server. The
            // host machine itself and signed admin requests may read it too, and
            // an operator can make it public with Beacon:Chat:PublicRead.
            if (method == "GET" && path == "/api/v1/chat/recent")
            {
                if (!_opts.Chat.PublicRead && !IsPlayerCaller(req) && !HasValidSignedRead(req))
                {
                    await RefuseUnreadAsync(res, 403, new { error = "chat is only readable by players on this server" });
                    return;
                }
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

            // ----- Chat ingest from player overlays -----
            //
            // SN2 has no native chat. Players type into the BeaconHud overlay,
            // the launcher relays the message here. Only a caller whose address
            // belongs to a player connected to this server may post, and the
            // sender name comes from that connection (the name the player logged
            // in with), never from the request body; the body's name only picks
            // between several players behind one address. Channel is forced to
            // "player" and target to "all" by ChatService.BroadcastFromPlayer.
            // Per-IP rate limit handled there.
            //
            // Body is bounded at 2 KB and read only after the caller is known.
            if (method == "POST" && path == "/api/v1/chat/player")
            {
                var chatPeers = CallerPeers(req);
                var chatLocal = IsLocalCaller(req);
                if (chatPeers.Count == 0 && !chatLocal)
                {
                    await RefuseUnreadAsync(res, 403, new { error = "join the server to chat" });
                    return;
                }
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
                var sender = ResolveChatSender(chatPeers, plReq.Sender, chatLocal) ?? "Player";
                var entry = _chat.BroadcastFromPlayer(clientIp, sender, plReq.Msg);
                if (entry is null)
                {
                    await WriteJsonAsync(res, 429, new { error = "rate limited" });
                    return;
                }
                await WriteJsonAsync(res, 200, new { ok = true, ts = entry.Ts });
                return;
            }

            // A connected launcher pushes its own player's world position for the
            // live web map (the headless server can't read a remote player's
            // position — they're simulated client-side). Same rule as /chat/player:
            // only from a connected player's address, and the dot is filed under
            // that player's own id and name. Positions expire server-side.
            if (method == "POST" && path == "/api/v1/map/position")
            {
                var posPeers = CallerPeers(req);
                var posLocal = IsLocalCaller(req);
                if (posPeers.Count == 0 && !posLocal)
                {
                    await RefuseUnreadAsync(res, 403, new { error = "join the server to report a position" });
                    return;
                }
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
                var peer = ResolvePositionPeer(posPeers, posReq.Id, posReq.Name);
                if (peer is not null)
                    _pipeState.UpsertClientPosition(peer.Id, peer.Name, posReq.X, posReq.Y, posReq.Z);
                else if (posPeers.Count == 0 && posLocal)
                    _pipeState.UpsertClientPosition(posReq.Id, posReq.Name ?? posReq.Id, posReq.X, posReq.Y, posReq.Z);
                else
                {
                    await WriteJsonAsync(res, 403, new { error = "position is not for a player at this address" });
                    return;
                }
                await WriteJsonAsync(res, 200, new { ok = true });
                return;
            }

            // Auth required for everything below. Body (if any) is streamed
            // to a temp file once and the resulting BufferedBody is passed
            // through to the route handler — no full-body MemoryStream and
            // no base64-via-header round trip (used to triple the memory
            // footprint per upload).
            var auth = await AuthorizeAndBufferAsync(req, ct).ConfigureAwait(false);
            if (auth.Status != 200 || auth.Body is null)
            {
                auth.Body?.Dispose();
                var error = auth.Status switch
                {
                    411 => "content length required",
                    413 => "payload too large",
                    _ => "unauthorized",
                };
                await RefuseUnreadAsync(res, auth.Status == 200 ? 401 : auth.Status, new { error });
                return;
            }
            var body = auth.Body;

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
                if (!SnProcessSupervisorService.SupervisesGame(_opts))
                {
                    await WriteJsonAsync(res, 409, new { ok = false, error = NoRelaunchMessage });
                    return;
                }
                var id = path["/api/v1/snapshots/".Length..^"/restore".Length];
                var ok = await _saves.RestoreSnapshotAsync(id, "http-api", ct).ConfigureAwait(false);
                await WriteJsonAsync(res, ok ? 200 : 500, new { ok });
                return;
            }

            if (method == "POST" && path == "/api/v1/snapshots/import-restore")
            {
                if (!SnProcessSupervisorService.SupervisesGame(_opts))
                {
                    body.Dispose();
                    await WriteJsonAsync(res, 409, new { ok = false, error = NoRelaunchMessage });
                    return;
                }
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
            // GET /chat/recent and GET /chat/motd live above the admin auth gate
            // (chat/recent has its own players-only check there).
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
    /// The signed-route gate. Everything that can be checked from the headers is
    /// checked before any of the body is read: the timestamp window, the declared
    /// length (required; chunked bodies are refused), the size cap and, for a body
    /// over <see cref="SmallBodyMaxBytes"/>, the signature itself over the SHA-256
    /// the caller declares in <see cref="ContentShaHeader"/>. Only then is the body
    /// streamed to a temp file, and its real hash must match the declared one. A
    /// small body is read into memory (at most 64 KB) and checked as before. An
    /// unauthenticated caller therefore never gets a byte written to disk. Temp
    /// files are deleted on every failure path.
    /// </summary>
    private async Task<AuthResult> AuthorizeAndBufferAsync(HttpListenerRequest req, CancellationToken ct)
    {
        var tsHeader = req.Headers["X-Beacon-Timestamp"];
        var sigHeader = req.Headers["X-Beacon-Signature"];
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!SignatureHeadersPlausible(tsHeader, sigHeader, now, ReplayWindowSeconds, out var ts))
            return AuthResult.Refused(401);

        long length = 0;
        if (req.HasEntityBody)
        {
            length = req.ContentLength64;
            if (length < 0) return AuthResult.Refused(411);
        }
        if (length > _opts.MaxUploadBytes) return AuthResult.Refused(413);

        var path = req.Url?.AbsolutePath ?? "";
        var method = req.HttpMethod.ToUpperInvariant();

        if (length > SmallBodyMaxBytes)
        {
            var declared = (req.Headers[ContentShaHeader] ?? "").Trim().ToLowerInvariant();
            if (!IsSha256Hex(declared))
            {
                // An app from before the header existed, uploading on the host
                // machine itself, keeps the old order; nobody else does.
                if (!IsLocalCaller(req)) return AuthResult.Refused(401);
                var legacy = await BufferBodyToDiskAsync(req, Math.Min(length, _opts.MaxUploadBytes), ct).ConfigureAwait(false);
                if (!VerifySignature(_authKey, method, path, ts, sigHeader!, legacy.Sha256Hex) || !TryRegisterSignature(sigHeader!, ts, now))
                {
                    legacy.Dispose();
                    return AuthResult.Refused(401);
                }
                return AuthResult.Accepted(legacy);
            }
            if (!VerifySignature(_authKey, method, path, ts, sigHeader!, declared) || !TryRegisterSignature(sigHeader!, ts, now))
                return AuthResult.Refused(401);

            BufferedBody streamed;
            try
            {
                streamed = await BufferBodyToDiskAsync(req, length, ct).ConfigureAwait(false);
            }
            catch (BodyTooLargeException)
            {
                return AuthResult.Refused(413);
            }
            if (streamed.SizeBytes != length || !string.Equals(streamed.Sha256Hex, declared, StringComparison.Ordinal))
            {
                streamed.Dispose();
                return AuthResult.Refused(401);
            }
            return AuthResult.Accepted(streamed);
        }

        // A small body. When the caller declares its hash (current apps do), the
        // signature is checked first here too; otherwise (older apps) the body is
        // read, at most 64 KB and within 10 seconds, and then checked.
        var declaredSmall = (req.Headers[ContentShaHeader] ?? "").Trim().ToLowerInvariant();
        var preChecked = length > 0 && IsSha256Hex(declaredSmall);
        if (preChecked && (!VerifySignature(_authKey, method, path, ts, sigHeader!, declaredSmall) || !TryRegisterSignature(sigHeader!, ts, now)))
            return AuthResult.Refused(401);

        byte[] small;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            small = await ReadBoundedBodyAsync(req, (int)length, deadline.Token).ConfigureAwait(false);
        }
        catch (BodyTooLargeException)
        {
            return AuthResult.Refused(413);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return AuthResult.Refused(401);
        }
        var smallSha = Convert.ToHexString(SHA256.HashData(small)).ToLowerInvariant();
        if (preChecked)
        {
            if (small.Length != length || !string.Equals(smallSha, declaredSmall, StringComparison.Ordinal))
                return AuthResult.Refused(401);
        }
        else if (!VerifySignature(_authKey, method, path, ts, sigHeader!, smallSha) || !TryRegisterSignature(sigHeader!, ts, now))
        {
            return AuthResult.Refused(401);
        }
        return AuthResult.Accepted(BufferedBody.FromBytes(small, smallSha));
    }

    /// <summary>
    /// A signed GET with no body, for public routes that also accept the admin
    /// signature (chat history). Same checks and replay table as the signed routes.
    /// </summary>
    private bool HasValidSignedRead(HttpListenerRequest req)
    {
        if (req.HasEntityBody) return false;
        var tsHeader = req.Headers["X-Beacon-Timestamp"];
        var sigHeader = req.Headers["X-Beacon-Signature"];
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!SignatureHeadersPlausible(tsHeader, sigHeader, now, ReplayWindowSeconds, out var ts)) return false;
        var emptySha = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
        return VerifySignature(_authKey, req.HttpMethod.ToUpperInvariant(), req.Url?.AbsolutePath ?? "", ts, sigHeader!, emptySha)
            && TryRegisterSignature(sigHeader!, ts, now);
    }

    // Records an accepted signature; false when it was already used inside the window.
    private bool TryRegisterSignature(string sigHeader, long ts, long now)
    {
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
            if (_seenSignatures.ContainsKey(sigKey)) return false;
            _seenSignatures[sigKey] = ts;
        }
        return true;
    }

    internal static bool SignatureHeadersPlausible(string? tsHeader, string? sigHeader, long nowUnix, int windowSeconds, out long ts)
    {
        ts = 0;
        if (string.IsNullOrWhiteSpace(tsHeader) || string.IsNullOrWhiteSpace(sigHeader)) return false;
        if (!long.TryParse(tsHeader, out ts)) return false;
        if (Math.Abs(nowUnix - ts) > windowSeconds) return false;
        // HMAC-SHA256 as lower or upper hex: 64 characters.
        return sigHeader.Trim().Length == 64;
    }

    internal static bool VerifySignature(byte[] key, string method, string path, long ts, string sigHeader, string bodySha256Hex)
    {
        if (key.Length == 0) return false;
        var canonical = $"{method}\n{path}\n{ts}\n{bodySha256Hex}";
        var expected = Convert.ToHexString(
            HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(sigHeader.Trim().ToLowerInvariant()));
    }

    internal static bool IsSha256Hex(string value)
    {
        if (value.Length != 64) return false;
        foreach (var c in value)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        return true;
    }

    // ----- Request pools -----

    internal enum RequestPool { Public, Signed, Upload }

    private static readonly HashSet<string> PublicGetPaths = new(StringComparer.Ordinal)
    {
        "/api/v1/health", "/api/v1/players", "/api/v1/manifest",
        "/api/v1/map/state", "/api/v1/chat/recent", "/api/v1/chat/motd",
    };

    internal static RequestPool ClassifyRequest(string method, string path, bool hasBody, long contentLength)
    {
        method = (method ?? "").ToUpperInvariant();
        path ??= "";
        if (method == "OPTIONS") return RequestPool.Public;
        if (method == "GET" && (PublicGetPaths.Contains(path) || path.StartsWith("/map/", StringComparison.Ordinal)))
            return RequestPool.Public;
        if (method == "POST" && (path == "/api/v1/chat/player" || path == "/api/v1/map/position"))
            return RequestPool.Public;
        if (hasBody && (contentLength < 0 || contentLength > SmallBodyMaxBytes))
            return RequestPool.Upload;
        return RequestPool.Signed;
    }

    private SemaphoreSlim LimiterFor(RequestPool pool) => pool switch
    {
        RequestPool.Public => _publicLimiter,
        RequestPool.Upload => _uploadLimiter,
        _ => _signedLimiter,
    };

    // http.sys timeouts (Windows): a client that dribbles its headers or goes quiet
    // mid-body loses the connection instead of holding a slot for minutes.
    private void ApplyListenerTimeouts(HttpListener listener)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var t = listener.TimeoutManager;
            t.HeaderWait = TimeSpan.FromSeconds(15);
            t.EntityBody = TimeSpan.FromSeconds(30);
            t.DrainEntityBody = TimeSpan.FromSeconds(10);
            t.IdleConnection = TimeSpan.FromSeconds(60);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "HttpListener timeouts not applied");
        }
    }

    // ----- Callers -----

    private IPAddress? CallerIp(HttpListenerRequest req)
    {
        var ip = req.RemoteEndPoint?.Address;
        return ip is null ? null : PipeServerState.NormalizeIp(ip);
    }

    private bool IsLocalCaller(HttpListenerRequest req)
    {
        var ip = CallerIp(req);
        return LoopbackIsLocal && ip is not null && IPAddress.IsLoopback(ip);
    }

    private IReadOnlyList<PipeServerState.ConnectedPeer> CallerPeers(HttpListenerRequest req) =>
        _pipeState.PeersAt(CallerIp(req));

    private bool IsPlayerCaller(HttpListenerRequest req) => IsLocalCaller(req) || CallerPeers(req).Count > 0;

    private static string? PresentedMapKey(HttpListenerRequest req)
    {
        var header = req.Headers[MapKeyHeader];
        if (!string.IsNullOrEmpty(header)) return header;
        return req.QueryString["key"];
    }

    internal static bool MapKeyMatches(string? configured, string? presented)
    {
        if (string.IsNullOrEmpty(configured) || string.IsNullOrEmpty(presented)) return false;
        var a = Encoding.UTF8.GetBytes(configured);
        var b = Encoding.UTF8.GetBytes(presented.Trim());
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// The name a chat line from this caller carries: the name a player at the
    /// caller's address logged in with. The requested name only chooses between
    /// several players behind one address. With no player at the address, only
    /// the host machine itself may post, under the name it asks for. Null = refuse.
    /// </summary>
    internal static string? ResolveChatSender(IReadOnlyList<PipeServerState.ConnectedPeer> peers, string? requested, bool isLocal)
    {
        if (peers.Count == 0)
            return isLocal ? (string.IsNullOrWhiteSpace(requested) ? "Player" : requested.Trim()) : null;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var match = peers.FirstOrDefault(p => string.Equals(p.Name, requested.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match.Name;
        }
        return peers[0].Name;
    }

    /// <summary>
    /// The connected player a position report belongs to: the one at the caller's
    /// address whose id or name the report names, or the only one there. Null when
    /// several players share the address and the report names none of them.
    /// </summary>
    internal static PipeServerState.ConnectedPeer? ResolvePositionPeer(
        IReadOnlyList<PipeServerState.ConnectedPeer> peers, string? requestedId, string? requestedName)
    {
        if (peers.Count == 0) return null;
        foreach (var p in peers)
        {
            if (!string.IsNullOrWhiteSpace(requestedId)
                && (string.Equals(p.Id, requestedId.Trim(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Name, requestedId.Trim(), StringComparison.OrdinalIgnoreCase)))
                return p;
            if (!string.IsNullOrWhiteSpace(requestedName)
                && string.Equals(p.Name, requestedName.Trim(), StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return peers.Count == 1 ? peers[0] : null;
    }

    // A refusal sent without reading the body: close the connection instead of
    // letting the server drain an upload nobody asked for.
    private static async Task RefuseUnreadAsync(HttpListenerResponse res, int status, object payload)
    {
        try { res.KeepAlive = false; } catch { }
        await WriteJsonAsync(res, status, payload).ConfigureAwait(false);
    }

    private readonly record struct AuthResult(int Status, BufferedBody? Body)
    {
        public static AuthResult Refused(int status) => new(status, null);
        public static AuthResult Accepted(BufferedBody body) => new(200, body);
    }

    /// <summary>
    /// Reads a body of exactly <paramref name="length"/> bytes (at most
    /// <see cref="SmallBodyMaxBytes"/>) into memory.
    /// </summary>
    private static async Task<byte[]> ReadBoundedBodyAsync(HttpListenerRequest req, int length, CancellationToken ct)
    {
        if (length <= 0) return Array.Empty<byte>();
        if (length > SmallBodyMaxBytes) throw new BodyTooLargeException();
        var buf = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await req.InputStream.ReadAsync(buf.AsMemory(read, length - read), ct).ConfigureAwait(false);
            if (n <= 0) break;
            read += n;
        }
        if (read != length) Array.Resize(ref buf, read);
        return buf;
    }

    /// <summary>
    /// Stream the request body into a temp file while hashing in 64 KB
    /// chunks. Never allocates a buffer the size of the body. Throws
    /// <see cref="BodyTooLargeException"/> if the stream exceeds the cap
    /// mid-read. The temp file is deleted on any failure.
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

        /// <summary>A verified small body, written to a temp file so every route reads bodies one way.</summary>
        public static BufferedBody FromBytes(byte[] bytes, string sha256Hex)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "beacon-uploads");
            Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, $"upload-{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(tempPath, bytes);
            return new BufferedBody(tempPath, sha256Hex, bytes.Length);
        }

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

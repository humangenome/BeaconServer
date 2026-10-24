using Beacon.Protocol;
using Microsoft.Extensions.Logging;

namespace BeaconServer.Services;

/// <summary>
/// Shared state for the active plugin connection. Single-writer, multi-reader.
/// Beacon's design is one plugin connection per BeaconServer instance.
/// </summary>
public sealed class PipeServerState
{
    private readonly ILogger<PipeServerState> _log;
    private readonly object _gate = new();

    private PipeConnection? _connection;

    public PipeServerState(ILogger<PipeServerState> log) : this(log, () => DateTimeOffset.UtcNow) { }

    internal PipeServerState(ILogger<PipeServerState> log, Func<DateTimeOffset> clock)
    {
        _log = log;
        ChatGate = new ChatGateMonitor(clock);
    }

    /// <summary>
    /// Watches whether the connection list below is being fed. See <see cref="ChatGateMonitor"/>.
    /// </summary>
    public ChatGateMonitor ChatGate { get; }

    public PipeConnection? Connection
    {
        get { lock (_gate) return _connection; }
    }

    public void SetConnection(PipeConnection? connection)
    {
        lock (_gate) _connection = connection;
    }

    public DateTimeOffset? LastHeartbeatAt { get; set; }

    public int LastReportedPlayerCount { get; set; }

    public int EffectivePlayerCount => Players.Count;

    public bool HasFreshHeartbeat(TimeSpan maxAge)
    {
        var conn = Connection;
        var last = LastHeartbeatAt;
        if (conn is null || last is null) return false;
        return DateTimeOffset.UtcNow - last.Value <= maxAge;
    }

    // Native plugin reports auth state on every heartbeat (Beacon.dll
    // v0.2.17+). As of v0.3.55 password enforcement is Lua-only and the
    // supervisor emits ServerPassword="" to plugin-config.json, so the
    // expected steady-state on every endpoint is Configured=0/Ready=0.
    // HeartbeatWatchdogService.CheckServerPasswordReady fail-closes if
    // it ever sees Configured=1 — that indicates a stale plugin config
    // from a pre-v0.3.55 deploy or a manually-edited
    // plugin-config.json, both of which can reproduce the SN2 crash
    // loop if they race with the Lua gate. The legacy-plugin case
    // (both fields stay 0) is now the normal case.
    public int LastServerPasswordConfigured { get; set; }
    public int LastServerPasswordHookReady { get; set; }

    // Cached player list shipped over the IPC PlayerListSnapshot frame.
    // SourceQueryHostedService + the launcher's HTTP /players endpoint
    // read from this. Plugin enumerates SN2's PlayerController list and
    // ships a snapshot every few seconds.
    private List<PlayerSnapshot> _players = new();
    private readonly Dictionary<string, PlayerSnapshot> _logPlayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _playersGate = new();
    public IReadOnlyList<PlayerSnapshot> Players
    {
        get
        {
            lock (_playersGate)
            {
                var merged = new List<PlayerSnapshot>(_players.Count + _logPlayers.Count);
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var player in _players.Concat(_logPlayers.Values))
                {
                    var id = player.BeaconUserId ?? "";
                    var name = player.DisplayName ?? "";
                    if (id.Length > 0 && !seenIds.Add(id)) continue;
                    if (name.Length > 0 && !seenNames.Add(name)) continue;
                    merged.Add(player);
                }
                return merged;
            }
        }
    }
    public void SetPlayers(IEnumerable<PlayerSnapshot> players)
    {
        lock (_playersGate) _players = players?.ToList() ?? new();
        EvaluateChatGate();
    }

    // Players the host itself reports (native plugin snapshot or the Lua roster),
    // without the log-derived entries and without the server's own host identity.
    internal int HostReportedPlayerCount
    {
        get
        {
            lock (_playersGate)
                return _players.Count(p =>
                    !(p.BeaconUserId ?? "").StartsWith("server-", StringComparison.OrdinalIgnoreCase)
                    && !Sn2LogTailService.IsGeneratedPlayerName(p.DisplayName ?? ""));
        }
    }

    /// <summary>
    /// Re-checks the chat gate (players on, no game connection read). Logs once
    /// when it degrades and once when it recovers. Called on every player-list
    /// update and on every parsed connection; cheap enough for request paths.
    /// </summary>
    public void EvaluateChatGate()
    {
        int peers;
        lock (_peersGate) peers = _peers.Count;
        var change = ChatGate.Observe(HostReportedPlayerCount, peers);
        if (change == ChatGateMonitor.Change.Degraded)
        {
            _log.LogWarning(
                "Player chat and map access are being refused: players have been connected for 10 minutes but no game connections can be read from the game log, " +
                "so no caller can be matched to a player. The game log format may have changed. Stopgap: set Beacon:Chat:PublicRead to true " +
                "(chat history readable by anyone) and Beacon:Map:Public to true (live map readable by anyone); posting chat stays refused until connections are read again.");
        }
        else if (change == ChatGateMonitor.Change.Recovered)
        {
            _log.LogInformation("Game connections are being read from the game log again; player chat and map access work normally.");
        }
    }

    // ── Client-reported map positions ──────────────────────────────────────
    // A headless SN2 server can't read a remote player's world position (the
    // player is simulated client-side), so each connected launcher pushes its
    // own {id, name, x, y, z} to POST /api/v1/map/position. The live web map
    // reads these via /api/v1/map/state. Entries expire so a disconnected
    // client drops off the map.
    public readonly record struct ClientPosition(
        string Id, string Name, double X, double Y, double Z, long UnixMs);
    private readonly Dictionary<string, ClientPosition> _clientPositions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _clientPosGate = new();

    // POST /api/v1/map/position is public + unauthenticated (clients report their
    // own position), so an attacker could POST unlimited unique ids. Bound the
    // store and the inputs so it can't be a memory-exhaustion lever and so a
    // write-only flood can't bloat /map/state output. Real co-op never nears 512.
    private const int MaxClientPositions = 512;
    private const int MaxPositionIdLength = 96;
    private const int MaxPositionNameLength = 64;
    private static readonly long PositionFloodTtlMs = (long)TimeSpan.FromSeconds(30).TotalMilliseconds;

    public void UpsertClientPosition(string id, string name, double x, double y, double z)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxPositionIdLength) return;
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return;
        if (name is { Length: > MaxPositionNameLength }) name = name[..MaxPositionNameLength];
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_clientPosGate)
        {
            if (!_clientPositions.ContainsKey(id) && _clientPositions.Count >= MaxClientPositions)
            {
                // Full of new ids — prune stale here (the read path also prunes,
                // but a write-only flood never triggers a read), then refuse if
                // still full of live entries rather than grow unbounded.
                var cutoff = now - PositionFloodTtlMs;
                foreach (var key in _clientPositions
                             .Where(kv => kv.Value.UnixMs < cutoff)
                             .Select(kv => kv.Key).ToList())
                    _clientPositions.Remove(key);
                if (_clientPositions.Count >= MaxClientPositions) return;
            }
            _clientPositions[id] = new ClientPosition(id, name ?? "", x, y, z, now);
        }
    }

    public IReadOnlyList<ClientPosition> RecentClientPositions(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)maxAge.TotalMilliseconds;
        lock (_clientPosGate)
        {
            foreach (var key in _clientPositions
                         .Where(kv => kv.Value.UnixMs < cutoff)
                         .Select(kv => kv.Key).ToList())
                _clientPositions.Remove(key);
            return _clientPositions.Values.ToList();
        }
    }

    // ── Connected players by network address ───────────────────────────────
    // The game log names the address of every accepted connection and the name
    // and id it logs in with (Sn2LogTailService pairs them). The player-facing
    // HTTP routes (chat read and send, the live map, position reports) answer
    // only callers whose address belongs to a player on this server, and the
    // name a chat line or a map dot carries comes from here, never from the
    // request body.
    public sealed record ConnectedPeer(string Address, System.Net.IPAddress Ip, string Id, string Name);

    private readonly Dictionary<string, ConnectedPeer> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _peersGate = new();
    private const int MaxPeers = 256;

    /// <summary>Record (or refresh) the player logged in from <paramref name="address"/> ("ip:port").</summary>
    public void NotePeer(string address, string id, string name)
    {
        if (!TryParsePeerIp(address, out var ip)) return;
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_peersGate)
        {
            if (!_peers.ContainsKey(address) && _peers.Count >= MaxPeers) return;
            _peers[address] = new ConnectedPeer(address, ip,
                string.IsNullOrWhiteSpace(id) ? $"sn2:{name}" : id.Trim(), name.Trim());
        }
        EvaluateChatGate();
    }

    public void ForgetPeer(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return;
        lock (_peersGate) _peers.Remove(address);
    }

    public void ForgetPeersNamed(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_peersGate)
        {
            foreach (var key in _peers
                         .Where(kv => string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase))
                         .Select(kv => kv.Key).ToList())
                _peers.Remove(key);
        }
    }

    /// <summary>Players currently connected from <paramref name="ip"/> (a household can have several).</summary>
    public IReadOnlyList<ConnectedPeer> PeersAt(System.Net.IPAddress? ip)
    {
        if (ip is null) return Array.Empty<ConnectedPeer>();
        ip = NormalizeIp(ip);
        lock (_peersGate)
            return _peers.Values.Where(p => p.Ip.Equals(ip)).ToList();
    }

    internal static System.Net.IPAddress NormalizeIp(System.Net.IPAddress ip) =>
        ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    internal static bool TryParsePeerIp(string? address, out System.Net.IPAddress ip)
    {
        ip = System.Net.IPAddress.None;
        if (string.IsNullOrWhiteSpace(address)) return false;
        var a = address.Trim();
        if (System.Net.IPEndPoint.TryParse(a, out var ep))
        {
            ip = NormalizeIp(ep.Address);
            return true;
        }
        var colon = a.LastIndexOf(':');
        if (colon > 0 && System.Net.IPAddress.TryParse(a[..colon], out var bare))
        {
            ip = NormalizeIp(bare);
            return true;
        }
        return false;
    }

    public void UpsertLogPlayer(string beaconUserId, string displayName, int pingMs = 0)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return;
        var id = string.IsNullOrWhiteSpace(beaconUserId) ? $"sn2:{displayName}" : beaconUserId;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_playersGate)
        {
            var connectedAt = _logPlayers.TryGetValue(id, out var existing)
                ? existing.ConnectedAtUnixMs
                : now;
            _logPlayers[id] = new PlayerSnapshot(id, displayName, connectedAt, now, pingMs);
        }
    }

    public void RemoveLogPlayer(string beaconUserId)
    {
        if (string.IsNullOrWhiteSpace(beaconUserId)) return;
        lock (_playersGate)
        {
            _logPlayers.Remove(beaconUserId);
            _players = _players
                .Where(player => !string.Equals(player.BeaconUserId, beaconUserId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public void RemoveLogPlayerByDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return;
        lock (_playersGate)
        {
            foreach (var key in _logPlayers
                         .Where(kv => string.Equals(kv.Value.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                         .Select(kv => kv.Key)
                         .ToList())
            {
                _logPlayers.Remove(key);
            }
            _players = _players
                .Where(player => !string.Equals(player.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public void ClearLogPlayers()
    {
        lock (_playersGate) _logPlayers.Clear();
    }

    internal void SetLogPlayerForTest(PlayerSnapshot player)
    {
        lock (_playersGate) _logPlayers[player.BeaconUserId] = player;
    }

    public void ClearLogPlayersIfOnlyOne()
    {
        lock (_playersGate)
        {
            var mergedCount = _players
                .Concat(_logPlayers.Values)
                .GroupBy(player => !string.IsNullOrWhiteSpace(player.DisplayName)
                    ? $"name:{player.DisplayName}"
                    : $"id:{player.BeaconUserId}", StringComparer.OrdinalIgnoreCase)
                .Count();
            if (mergedCount <= 1)
            {
                _logPlayers.Clear();
                _players.Clear();
            }
        }
    }

}

/// <summary>
/// The player routes (chat, live map, positions) recognise a player by the
/// address their game connection came from, read from the game log. If the host
/// reports players but the log yields no connection for <see cref="Window"/>, the
/// log is probably unreadable (a game update can change its format) and every
/// player is being refused. This tracks that state for the health endpoint
/// ("chat_gate") and for one warning per episode. It never degrades while no
/// players are on, and it clears the moment a connection is read again.
/// </summary>
public sealed class ChatGateMonitor
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public const string Ok = "ok";
    public const string NoConnectionsSeen = "no_connections_seen";

    public enum Change { None, Degraded, Recovered }

    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private DateTimeOffset? _blindSince;
    private DateTimeOffset? _degradedSince;

    public ChatGateMonitor(Func<DateTimeOffset> clock) => _clock = clock;

    /// <summary>"ok" or "no_connections_seen".</summary>
    public string State { get { lock (_gate) return _degradedSince is null ? Ok : NoConnectionsSeen; } }

    /// <summary>Unix seconds when players-on-but-nothing-read began, while degraded; otherwise null.</summary>
    public long? DegradedSinceUnix { get { lock (_gate) return _degradedSince?.ToUnixTimeSeconds(); } }

    public Change Observe(int hostReportedPlayers, int parsedConnections)
    {
        var now = _clock();
        lock (_gate)
        {
            if (parsedConnections > 0)
            {
                _blindSince = null;
                if (_degradedSince is null) return Change.None;
                _degradedSince = null;
                return Change.Recovered;
            }
            if (hostReportedPlayers <= 0)
            {
                // Nobody on: nothing to learn. The clock restarts with the next player;
                // an episode already reported stays until a connection is read.
                _blindSince = null;
                return Change.None;
            }
            _blindSince ??= now;
            if (_degradedSince is null && now - _blindSince.Value >= Window)
            {
                _degradedSince = _blindSince;
                return Change.Degraded;
            }
            return Change.None;
        }
    }
}

/// <summary>
/// Wraps the per-plugin connection: send queue, sequence counter, codec.
/// </summary>
public sealed class PipeConnection
{
    private readonly FrameCodec _codec;
    private readonly Func<byte[], CancellationToken, Task> _write;
    private uint _sequence;

    public PipeConnection(string instanceId, int pluginPid, string pluginVersion, FrameCodec codec, Func<byte[], CancellationToken, Task> write)
    {
        InstanceId = instanceId;
        PluginPid = pluginPid;
        PluginVersion = pluginVersion;
        _codec = codec;
        _write = write;
    }

    public string InstanceId { get; }
    public int PluginPid { get; }
    public string PluginVersion { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    public Task SendAsync<T>(FrameType type, T payload, CancellationToken ct = default)
    {
        var seq = Interlocked.Increment(ref _sequence);
        var bytes = _codec.Encode(type, FrameFlags.None, seq, payload);
        return _write(bytes, ct);
    }
}

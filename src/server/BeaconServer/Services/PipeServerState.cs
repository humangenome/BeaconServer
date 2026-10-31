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

    // Native plugin reports its join-password state on every heartbeat: configured (a password was loaded) and ready (the
    // ApproveLogin check is armed). HeartbeatWatchdogService.CheckServerPasswordReady compares them with BeaconAuthPassword.
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
            lock (_playersGate) return MergePlayersLocked();
        }
    }

    // The host's own list comes first and every entry in it is a player: two people can share a
    // display name, and each has their own id there. A log entry for a name the host list holds is
    // the same player under the log's id, one for one, so a second player of that name who is in
    // the log but not yet in the host list still counts. Inside the log list, a player who
    // reconnects with another character before the old connection is dropped keeps one entry (same
    // account, same name), and an entry with no real id cannot be told apart from one with the name.
    private List<PlayerSnapshot> MergePlayersLocked()
    {
        var merged = new List<PlayerSnapshot>(_players.Count + _logPlayers.Count);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hostNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var noIdNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in _players)
        {
            var id = player.BeaconUserId ?? "";
            var name = player.DisplayName ?? "";
            if (id.Length > 0 ? !seenIds.Add(id) : (name.Length > 0 && !noIdNames.Add(name))) continue;
            merged.Add(player);
            if (name.Length > 0) hostNames[name] = hostNames.GetValueOrDefault(name) + 1;
        }

        var logAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Real ids first, so an entry with no real id is judged against them whatever the order.
        foreach (var player in _logPlayers.Values.OrderBy(p => IsNameOnlyLogId(p) ? 1 : 0))
        {
            var id = player.BeaconUserId ?? "";
            var name = player.DisplayName ?? "";
            if (id.Length > 0 && !seenIds.Add(id)) continue;
            if (IsNameOnlyLogId(player) && (logNames.Contains(name) || hostNames.ContainsKey(name))) continue;
            if (!logAccounts.Add(name + "\n" + LogAccount(id))) continue;
            logNames.Add(name);
            if (name.Length > 0 && hostNames.TryGetValue(name, out var left) && left > 0)
            {
                hostNames[name] = left - 1;
                continue;
            }
            merged.Add(player);
        }
        return merged;
    }

    private static bool IsNameOnlyLogId(PlayerSnapshot player) =>
        (player.BeaconUserId ?? "").StartsWith("sn2:", StringComparison.OrdinalIgnoreCase);

    // A log id is <SteamID64>_<character hash>; the part before the _ is the account.
    private static string LogAccount(string id)
    {
        var cut = id.IndexOf('_');
        return cut > 0 ? id[..cut] : id;
    }

    public void SetPlayers(IEnumerable<PlayerSnapshot> players)
    {
        lock (_playersGate) _players = players?.ToList() ?? new();
        EvaluateChatGate();
    }

    // Two sources report the host's list and each names a player its own way: the Lua roster by
    // PlayerState, the native plugin by a number. They replaced each other every few seconds, so a
    // player's id, position and time online flipped on every poll (the map dot was redrawn each time).
    // The roster is the list. The plugin's only counts while the roster has gone quiet.
    internal static readonly TimeSpan RosterFreshFor = TimeSpan.FromSeconds(30);
    private DateTimeOffset _rosterFileAt = DateTimeOffset.MinValue;

    // A roster file is the game's own list, written every few seconds while it runs. One from a game that has ended
    // must not come back: the last file is written up to 5 s before the game dies, and the watcher reads it after
    // GameConnectionEnded cleared the list (lab 10/31: the last player stayed listed 3+ minutes after the game died, and a
    // freshly started BeaconServer listed him again from a 5-minute-old file). So a file counts only while the game is
    // connected, only when it was written after the last game ended, and only when it was written no more than
    // RosterBeforeConnectSlack before this connection began (a BeaconServer restart with the game still up reconnects
    // within seconds of the game's last write).
    internal static readonly TimeSpan RosterBeforeConnectSlack = TimeSpan.FromSeconds(10);
    private DateTimeOffset _gameEndedAt = DateTimeOffset.MinValue;

    public bool SetPlayersFromRosterFile(IEnumerable<PlayerSnapshot> players, DateTimeOffset writtenAt, DateTimeOffset? now = null)
    {
        var conn = Connection;
        lock (_playersGate)
        {
            if (conn is null) return false;
            if (writtenAt <= _gameEndedAt) return false;
            if (writtenAt < conn.ConnectedAt - RosterBeforeConnectSlack) return false;
            _rosterFileAt = now ?? DateTimeOffset.UtcNow;
        }
        SetPlayers(players);
        return true;
    }

    public void SetPlayersFromPlugin(IEnumerable<PlayerSnapshot> players, DateTimeOffset? now = null)
    {
        lock (_playersGate)
        {
            if ((now ?? DateTimeOffset.UtcNow) - _rosterFileAt < RosterFreshFor) return;
        }
        SetPlayers(players);
    }

    /// <summary>
    /// The game process is gone (its pipe closed). BeaconServer outlives a game crash, so without this
    /// its last players stayed on the list and their addresses stayed allowed until the next start.
    /// </summary>
    public void GameConnectionEnded()
    {
        lock (_playersGate)
        {
            _players = new();
            _logPlayers.Clear();
            _rosterFileAt = DateTimeOffset.MinValue;
            _gameEndedAt = DateTimeOffset.UtcNow;
        }
        lock (_peersGate) _peers.Clear();
        LastReportedPlayerCount = 0;
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

    public int PeerCountNamed(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        lock (_peersGate)
            return _peers.Values.Count(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
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
            var mergedCount = MergePlayersLocked().Count;
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

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

    public PipeServerState(ILogger<PipeServerState> log) => _log = log;

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

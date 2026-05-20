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

    // Native plugin reports auth state on every heartbeat (Beacon.dll
    // v0.2.17+). On a legacy plugin both fields stay 0 and we treat the
    // server as not-known-bad. HeartbeatWatchdogService fail-closes only
    // when ServerPasswordConfigured==1 && ServerPasswordHookReady==0,
    // i.e. the plugin has explicitly told us "password required but my
    // native gate is down". See HeartbeatMessage doc comment.
    public int LastServerPasswordConfigured { get; set; }
    public int LastServerPasswordHookReady { get; set; }

    // Cached player list shipped over the IPC PlayerListSnapshot frame.
    // SourceQueryHostedService + the launcher's HTTP /players endpoint
    // read from this. Plugin enumerates SN2's PlayerController list and
    // ships a snapshot every few seconds.
    private List<PlayerSnapshot> _players = new();
    private readonly object _playersGate = new();
    public IReadOnlyList<PlayerSnapshot> Players
    {
        get { lock (_playersGate) return _players.ToList(); }
    }
    public void SetPlayers(IEnumerable<PlayerSnapshot> players)
    {
        lock (_playersGate) _players = players?.ToList() ?? new();
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

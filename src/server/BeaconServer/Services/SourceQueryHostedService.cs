using Beacon.SourceQuery;
using BeaconServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

/// <summary>
/// Runs the Source A2S query responder. Pulls live snapshot data from the plugin
/// (via <see cref="PipeServerState"/>) on every query, so external tools like
/// GameTracker and ServerMonkey see real player counts.
/// </summary>
public sealed class SourceQueryHostedService : IHostedService, IAsyncDisposable
{
    private readonly ILogger<SourceQueryHostedService> _log;
    private readonly BeaconServerOptions _opts;
    private readonly PipeServerState _state;
    private SourceQueryServer? _server;

    public SourceQueryHostedService(
        ILogger<SourceQueryHostedService> log,
        IOptions<BeaconServerOptions> opts,
        PipeServerState state)
    {
        _log = log;
        _opts = opts.Value;
        _state = state;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _server = new SourceQueryServer(
            _opts.QueryPort,
            BuildInfo,
            BuildPlayers,
            BuildRules);
        _log.LogInformation("Source A2S query listening on UDP {Port}", _server.BoundPort);
        return _server.StartAsync(ct);
    }

    public async Task StopAsync(CancellationToken _)
    {
        if (_server is not null) await _server.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.StopAsync().ConfigureAwait(false);
    }

    private ServerInfoSnapshot BuildInfo() => new(
        Name: string.IsNullOrWhiteSpace(_opts.ServerName) ? $"Beacon — {_opts.InstanceId}" : _opts.ServerName.Trim(),
        Map: "Awake",
        Folder: "Beacon",
        Game: "Subnautica 2",
        SteamAppId: 1962700,                          // real Steam AppID — written as 64-bit GameID in EDF
        PlayerCount: _state.LastReportedPlayerCount,
        MaxPlayers: _opts.MaxPlayers,
        PasswordRequired: !string.IsNullOrEmpty(_opts.ServerPassword),
        VacSecured: false,
        Version: $"beacon-{BeaconVersionInfo.BeaconVersion}/sn2-{BeaconVersionInfo.Sn2Build}",
        GameplayPort: _opts.GameplayPort,
        Keywords: $"beacon,sn2,beacon={BeaconVersionInfo.BeaconVersion},sn2build={BeaconVersionInfo.Sn2Build}");

    private IReadOnlyList<PlayerInfoEntry> BuildPlayers()
    {
        // Source A2S player list. We populate from cached
        // PlayerListSnapshot frames the plugin ships over IPC. Each
        // entry maps to (DisplayName, Score=0, ConnectionSeconds since
        // ConnectedAtUnixMs). If the plugin hasn't sent a snapshot yet
        // (or no players are connected) the list is empty — gametracker
        // / panel tools show 'no players online' rather than a faked
        // count.
        var snap = _state.Players;
        if (snap.Count == 0) return Array.Empty<PlayerInfoEntry>();

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = new List<PlayerInfoEntry>(snap.Count);
        foreach (var p in snap)
        {
            var ageMs = nowMs - p.ConnectedAtUnixMs;
            var ageSec = ageMs > 0 ? (float)(ageMs / 1000.0) : 0f;
            result.Add(new PlayerInfoEntry(
                Name: string.IsNullOrEmpty(p.DisplayName) ? p.BeaconUserId : p.DisplayName,
                Score: 0,
                ConnectSeconds: ageSec));
        }
        return result;
    }

    private IReadOnlyList<KeyValuePair<string, string>> BuildRules() => new[]
    {
        new KeyValuePair<string, string>("instance", _opts.InstanceId),
        new KeyValuePair<string, string>("gameplay_port", _opts.GameplayPort.ToString()),
        new KeyValuePair<string, string>("beacon_version", BeaconVersionInfo.BeaconVersion),
        new KeyValuePair<string, string>("sn2_build", BeaconVersionInfo.Sn2Build),
    };
}

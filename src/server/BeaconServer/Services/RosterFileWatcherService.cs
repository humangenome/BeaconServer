using System.Text.Json;
using System.Text.Json.Serialization;
using Beacon.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeaconServer.Services;

/// <summary>
/// Polls <c>roster.json</c> next to BeaconServer.exe and pushes the
/// snapshot into <see cref="PipeServerState"/> so the A2S info response
/// (<see cref="SourceQueryHostedService"/>) and the HTTP players endpoint
/// (<see cref="BeaconHttpService"/>) reflect live player counts and names.
///
/// The producer is the server-side <c>BeaconRoster</c> UE4SS Lua mod
/// (<c>dist/ue4ss-server/Mods/BeaconRoster/Scripts/main.lua</c>). It hooks
/// <c>GameModeBase:K2_PostLogin</c> + <c>GameModeBase:Logout</c> and
/// rewrites the JSON atomically every 5 seconds (also on join/leave).
///
/// File-based exchange was chosen over an FFI Lua → native plugin → IPC
/// bridge because (a) the Beacon.dll plugin has no UE reflection access
/// to <c>GameState.PlayerArray</c>, (b) Lua → C# round-trip is single
/// reader / single writer / single instance, and (c) atomic rename
/// (<c>roster.json.tmp → roster.json</c>) gives us a clean reader path.
/// </summary>
public sealed class RosterFileWatcherService : BackgroundService
{
    private readonly ILogger<RosterFileWatcherService> _log;
    private readonly PipeServerState _state;
    private readonly string _rosterPath;
    private DateTimeOffset _lastReadAt = DateTimeOffset.MinValue;
    private long _lastFileSize = -1;

    public RosterFileWatcherService(ILogger<RosterFileWatcherService> log, PipeServerState state)
    {
        _log = log;
        _state = state;
        // Lua mod writes to <BeaconServer dir>\roster.json. BeaconServer's
        // cwd is its own install directory, so the relative path resolves.
        _rosterPath = Path.Combine(AppContext.BaseDirectory, "roster.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Roster watcher started: path={Path}", _rosterPath);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ReadIfChanged();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Roster read failed");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
        _log.LogInformation("Roster watcher stopping");
    }

    private void ReadIfChanged()
    {
        if (!File.Exists(_rosterPath)) return;
        var info = new FileInfo(_rosterPath);
        if (info.Length == _lastFileSize && info.LastWriteTimeUtc <= _lastReadAt) return;
        _lastFileSize = info.Length;
        _lastReadAt = info.LastWriteTimeUtc;

        string json;
        try
        {
            json = File.ReadAllText(_rosterPath);
        }
        catch (IOException) { return; }   // race with Lua's atomic write — try again next tick

        RosterFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RosterFile>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            _log.LogDebug(ex, "Roster JSON parse failed");
            return;
        }
        if (parsed?.Players is null) return;

        var snapshots = new List<PlayerSnapshot>(parsed.Players.Count);
        foreach (var p in parsed.Players)
        {
            snapshots.Add(new PlayerSnapshot(
                BeaconUserId: p.BeaconUserId ?? "",
                DisplayName: p.DisplayName ?? "Unknown",
                ConnectedAtUnixMs: p.ConnectedAtUnixMs,
                LastPacketUnixMs: p.LastPacketUnixMs,
                PingMs: p.PingMs));
        }
        _state.SetPlayers(snapshots);
        _state.LastReportedPlayerCount = snapshots.Count;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class RosterFile
    {
        [JsonPropertyName("unix_ms")]
        public long UnixMs { get; set; }

        [JsonPropertyName("players")]
        public List<RosterPlayer>? Players { get; set; }
    }

    private sealed class RosterPlayer
    {
        public string? BeaconUserId { get; set; }
        public string? DisplayName { get; set; }
        public long ConnectedAtUnixMs { get; set; }
        public long LastPacketUnixMs { get; set; }
        public int PingMs { get; set; }
    }
}

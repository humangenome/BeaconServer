using BeaconServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

/// <summary>
/// Runs commands in the game through the server mods (BeaconWorldSave). Null = the
/// command is not registered (the module is not loaded).
/// </summary>
public interface IHostCommandRunner
{
    Task<string?> RunAsync(string name, string[] args, CancellationToken ct);
}

/// <summary>Host commands go through ModKit's command queue as the "host" caller.</summary>
public sealed class ModKitHostCommandRunner : IHostCommandRunner
{
    private readonly CommandDispatchService _commands;
    public ModKitHostCommandRunner(CommandDispatchService commands) => _commands = commands;

    public Task<string?> RunAsync(string name, string[] args, CancellationToken ct) =>
        _commands.DispatchAsync(name, args, (name + " " + string.Join(' ', args)).Trim(), CallerInfo.Host(), ct);
}

/// <summary>
/// World saves made by Beacon: before every stop (RCON <c>save world</c>, which a hosting panel
/// calls before it ends the game, and BeaconServer's own stop when it runs the game), and with
/// <c>Beacon:Saves:HostSaveEveryMinutes</c> set, in place of the game's own 2-minute autosave.
/// The rules are in <see cref="WorldSavePolicy"/>; this service feeds it every 2 seconds and
/// carries out what it returns. The game-side half is the BeaconWorldSave server mod.
/// </summary>
public sealed class WorldSaveService : BackgroundService
{
    internal const string SaveCommand = "beacon_world_save";
    internal const string AutosaveCommand = "beacon_autosave";

    private readonly ILogger<WorldSaveService> _log;
    private readonly BeaconServerOptions _opts;
    private readonly PipeServerState _state;
    private readonly GameSaveSignals _signals;
    private readonly IHostCommandRunner _runner;
    private readonly WorldSavePolicy _policy;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    internal Func<bool> GameRunning { get; set; }
    internal TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan WaitPoll { get; set; } = TimeSpan.FromMilliseconds(250);
    internal WorldSavePolicy Policy => _policy;

    public WorldSaveService(
        ILogger<WorldSaveService> log,
        IOptions<BeaconServerOptions> opts,
        PipeServerState state,
        GameSaveSignals signals,
        IHostCommandRunner runner)
    {
        _log = log;
        _opts = opts.Value;
        _state = state;
        _signals = signals;
        _runner = runner;
        var s = _opts.Saves ?? new SavesOptions();
        _policy = new WorldSavePolicy(s.HostSaveEveryMinutes, s.AutosaveSwitch, s.SaveConfirmSeconds);
        GameRunning = () => _state.Connection is not null;
    }

    public override Task StartAsync(CancellationToken ct)
    {
        _log.LogInformation(_policy.Enabled
            ? "[World save] Beacon takes over saving from the game once it has autosaved by itself (every {Minutes} minutes, switch: {Switch})"
            : "[World save] the game's own autosave stays on (Saves:HostSaveEveryMinutes = 0); Beacon saves before a stop",
            Math.Clamp(_opts.Saves?.HostSaveEveryMinutes ?? 0, 5, 60), _policy.Switch);
        return base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickOnceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "[World save] tick failed"); }
            try { await Task.Delay(TickInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Save before BeaconServer stops a game it runs itself. A game started some other way is
    /// stopped by whatever started it, which asks first (RCON <c>save world</c>).
    /// </summary>
    public override async Task StopAsync(CancellationToken ct)
    {
        try
        {
            if (SnProcessSupervisorService.SupervisesGame(_opts) && GameRunning())
            {
                var limit = TimeSpan.FromSeconds(Math.Clamp(_opts.Saves?.SaveBeforeStopTimeoutSeconds ?? 20, 5, 20));
                var outcome = await SaveNowAsync("BeaconServer is stopping", limit, ct).ConfigureAwait(false);
                if (!outcome.Saved)
                    _log.LogWarning("[World save] stopping without a fresh save: {How}", outcome.How);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[World save] save before stop failed");
        }
        await base.StopAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The shortest gap between two saves an owner asks for (RCON from outside this machine).</summary>
    public static readonly TimeSpan OwnerSaveGap = TimeSpan.FromSeconds(30);
    private DateTimeOffset? _lastOwnerAskAt;

    /// <summary>
    /// Ask the game to save now and wait until the save lands (or the limit passes). Only one save is
    /// ever in flight: a call while one runs joins it and gets its answer. <paramref name="ownerAsked"/>
    /// (RCON from outside this machine) makes a new save at most once per <see cref="OwnerSaveGap"/>;
    /// inside the gap the answer says when the world was last saved and nothing new is asked of the
    /// game, so RCON cannot keep the disk busy. BeaconServer's own saves and a hosting panel's save
    /// before a stop (RCON from this machine) have no gap.
    /// </summary>
    public async Task<SaveOutcome> SaveNowAsync(string reason, TimeSpan limit, CancellationToken ct = default, bool ownerAsked = false)
    {
        if (!GameRunning())
            return new SaveOutcome(false, reason, TimeSpan.Zero, "the game is not running");

        var started = Clock();
        WorldSaveAction? action = null;
        DateTimeOffset key;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_policy.SavePending)
            {
                key = _policy.PendingSince!.Value;
            }
            else if (ownerAsked && _lastOwnerAskAt is { } last && started - last < OwnerSaveGap)
            {
                var saved = _policy.LastSaveOkAt;
                if (saved is { } at && started - at < OwnerSaveGap)
                    return new SaveOutcome(true, reason, TimeSpan.Zero, "a save on request less than 30 s ago",
                        SaveOutcomeKind.Recent, Ago: started - at);
                return new SaveOutcome(false, reason, TimeSpan.Zero,
                    $"one save on request per {(int)OwnerSaveGap.TotalSeconds} s; the world was last saved {Describe(saved ?? _signals.LastAutosaveAt)}",
                    SaveOutcomeKind.TooSoon, NextIn: OwnerSaveGap - (started - last));
            }
            else
            {
                action = _policy.BeginSave(started, reason);
                key = _policy.PendingSince ?? started;
                if (ownerAsked) _lastOwnerAskAt = started;
            }
        }
        finally { _gate.Release(); }

        if (action is not null) await RunActionAsync(action, ct).ConfigureAwait(false);

        while (true)
        {
            SaveOutcome? done;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { done = _policy.OutcomeFor(key); }
            finally { _gate.Release(); }
            if (done is not null) return done with { Reason = reason };
            var waited = Clock() - started;
            if (waited >= limit)
                return new SaveOutcome(false, reason, waited,
                    $"not confirmed within {limit.TotalSeconds:0} s; the world was last saved {Describe(_policy.LastSaveOkAt ?? _signals.LastAutosaveAt)}");
            await Task.Delay(WaitPoll, ct).ConfigureAwait(false);
            await TickOnceAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One line for an admin (RCON "save status"): who keeps the world saved right now and what the
    /// game itself reports. Changes nothing.
    /// </summary>
    public async Task<string> StatusAsync(CancellationToken ct = default)
    {
        string mine;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var every = _opts.Saves?.HostSaveEveryMinutes ?? 0;
            mine = !_policy.Enabled
                ? "Beacon saves before a stop; the game's own autosave keeps the world otherwise"
                : _policy.TookOver
                    ? $"Beacon saves every {Math.Clamp(every, 5, 60)} minutes (the game's autosave was switched off, {_policy.Switch})"
                    : _policy.Fallback
                        ? "a Beacon save failed this run; the game's own autosave keeps the world"
                        : "waiting for the game to autosave by itself before Beacon takes over";
            mine += $"; last save {Describe(_policy.LastSaveOkAt ?? _signals.LastAutosaveAt)}";
        }
        finally { _gate.Release(); }

        if (!GameRunning()) return mine + "; the game is not running";
        var game = await _runner.RunAsync(AutosaveCommand, new[] { "status" }, ct).ConfigureAwait(false);
        return mine + "; game: " + (game is null ? "the BeaconWorldSave server mod is not loaded" : Trim(game));
    }

    internal async Task TickOnceAsync(CancellationToken ct)
    {
        WorldSavePolicy.TickResult result;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var input = new WorldSavePolicy.TickInput(
                _state.EffectivePlayerCount,
                _signals.DrainAutosaves(),
                _signals.RunGeneration,
                SaveFileWrittenAt());
            result = _policy.Tick(Clock(), input);
        }
        finally { _gate.Release(); }

        Write(result.Notes);
        foreach (var action in result.Actions)
            await RunActionAsync(action, ct).ConfigureAwait(false);
    }

    private async Task RunActionAsync(WorldSaveAction action, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case WorldSaveActionKind.Save:
            {
                var reply = await _runner.RunAsync(SaveCommand, Array.Empty<string>(), ct).ConfigureAwait(false);
                if (reply is not null && reply.StartsWith("ok", StringComparison.OrdinalIgnoreCase)) return;
                var why = reply is null
                    ? "the BeaconWorldSave server mod is not loaded"
                    : $"the game did not take the request ({Trim(reply)})";
                await Feed(p => p.SaveRequestFailed(Clock(), why), ct).ConfigureAwait(false);
                return;
            }
            case WorldSaveActionKind.AutosaveOff:
            case WorldSaveActionKind.AutosaveOn:
            {
                var mode = action.Method == WorldSavePolicy.SettingSwitch
                    ? (action.Kind == WorldSaveActionKind.AutosaveOff ? "off" : "on")
                    : "toggle";
                var reply = await _runner.RunAsync(AutosaveCommand, new[] { mode }, ct).ConfigureAwait(false);
                var answer = reply is null
                    ? SwitchAnswer.NotApplied
                    : reply.StartsWith("ok", StringComparison.OrdinalIgnoreCase) ? SwitchAnswer.Applied
                    : reply.StartsWith("fail", StringComparison.OrdinalIgnoreCase) ? SwitchAnswer.NotApplied
                    : SwitchAnswer.Unknown;
                var detail = reply is null ? "the BeaconWorldSave server mod is not loaded" : Trim(reply);
                await Feed(p => p.SwitchResult(Clock(), action.Kind, answer, detail), ct).ConfigureAwait(false);
                return;
            }
        }
    }

    private async Task Feed(Func<WorldSavePolicy, WorldSavePolicy.TickResult> call, CancellationToken ct)
    {
        WorldSavePolicy.TickResult result;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { result = call(_policy); }
        finally { _gate.Release(); }
        Write(result.Notes);
        foreach (var next in result.Actions)
            await RunActionAsync(next, ct).ConfigureAwait(false);
    }

    private DateTimeOffset? SaveFileWrittenAt()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_opts.SnUserDir)) return null;
            var path = Path.Combine(_opts.SnUserDir, "Saved", "SaveGames", SnProcessSupervisorService.Sn2CanonicalSaveSlot + ".sav");
            if (!File.Exists(path)) return null;
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private void Write(IReadOnlyList<WorldSaveNote> notes)
    {
        foreach (var n in notes)
        {
            switch (n.Level)
            {
                case WorldSaveNoteLevel.Error: _log.LogError("[World save] {Text}", n.Text); break;
                case WorldSaveNoteLevel.Warning: _log.LogWarning("[World save] {Text}", n.Text); break;
                default: _log.LogInformation("[World save] {Text}", n.Text); break;
            }
        }
    }

    private static string Trim(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > 160 ? s[..160] : s;
    }

    private static string Describe(DateTimeOffset? at) =>
        at is { } t ? $"at {t.UtcDateTime:HH:mm:ss} UTC" : "at an unknown time";
}

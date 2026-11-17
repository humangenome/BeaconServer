namespace BeaconServer.Services;

/// <summary>
/// When Beacon saves the world and when it switches the game's own autosave off or back on.
/// Pure: no clock, no files, no game calls; <see cref="WorldSaveService"/> feeds it and carries
/// out what it returns, and the tests drive it with a fake clock.
///
/// Why: the game autosaves every 2 minutes, and each save holds the server for a moment. With
/// <c>HostSaveEveryMinutes</c> set, Beacon saves instead, less often and when nobody is mid-play:
///
///   1. Nothing happens until the game autosaves by itself once (a line in its log). That line is
///      the proof its autosave is on, and that the world and its save system are up.
///   2. Beacon then saves once itself. Only when that save lands (a fresh save line or a newer
///      save file) does it switch the game's autosave off. So Beacon never takes over saving
///      unless its own save has just worked on this run.
///   3. From then on: a save every N minutes while players have been on, one after players
///      leave (15 seconds after the last of a batch), and one before every stop.
///   4. If the game autosaves again after the switch, the switch did not hold: it is sent again
///      (only right after such a line, at most every 3 minutes, 3 tries, then Beacon leaves the
///      game's autosave on for the run).
///   5. If one of Beacon's saves does not land, the game's autosave goes back on for the rest of
///      the run and Beacon stops scheduling saves. A new game run starts over at 1.
///
/// The game writes the same line for its own autosave and for a save Beacon asks for. A line
/// within <see cref="OwnSaveWindow"/> after Beacon's last request is Beacon's.
///
/// "console" switching is the game's ToggleAutoSave command: a toggle, so Beacon sends it only
/// while it knows the state (just after a line proved autosave on, or to undo its own switch
/// when no line has come since). "setting" sets the game's autosave setting directly and is
/// safe to repeat.
/// </summary>
public sealed class WorldSavePolicy
{
    public static readonly TimeSpan OwnSaveWindow = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromSeconds(180);
    public static readonly TimeSpan LeaveBatch = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan AutosaveBackWithin = TimeSpan.FromSeconds(300);
    public const int MaxSwitchOffTries = 3;

    public const string ConsoleSwitch = "console";
    public const string SettingSwitch = "setting";

    private readonly TimeSpan? _every;
    private readonly TimeSpan _confirmWithin;

    private int? _runGeneration;
    private Pending? _pending;
    private DateTimeOffset? _lastRequestAt;
    private DateTimeOffset? _lastSaveOkAt;
    private DateTimeOffset? _lastScheduleBase;
    private bool _playedSinceLastSave;
    private int _lastPlayers;
    private DateTimeOffset? _leaveSaveDueAt;

    private bool _tookOver;
    private bool _offRequested;
    private bool _fallback;
    private bool _gaveUp;
    private bool _switchOffNow;
    private int _offTries;
    private DateTimeOffset? _lastOffAt;
    private DateTimeOffset? _lastGameAutosaveAt;
    private DateTimeOffset? _backOnAt;
    private bool _backOnSeen;
    private bool _backOnWarned;

    public WorldSavePolicy(int hostSaveEveryMinutes, string? autosaveSwitch, int saveConfirmSeconds = 60)
    {
        _every = hostSaveEveryMinutes <= 0
            ? null
            : TimeSpan.FromMinutes(Math.Clamp(hostSaveEveryMinutes, 5, 60));
        Switch = string.Equals(autosaveSwitch?.Trim(), SettingSwitch, StringComparison.OrdinalIgnoreCase)
            ? SettingSwitch
            : ConsoleSwitch;
        _confirmWithin = TimeSpan.FromSeconds(Math.Clamp(saveConfirmSeconds, 15, 300));
    }

    /// <summary>"console" or "setting".</summary>
    public string Switch { get; }

    /// <summary>Beacon saves on its own schedule (the setting is on and nothing has failed this run).</summary>
    public bool Enabled => _every is not null;

    /// <summary>Beacon has switched the game's autosave off and saves on its own schedule.</summary>
    public bool TookOver => _tookOver;

    /// <summary>One of Beacon's saves failed this run; the game's own autosave keeps the world.</summary>
    public bool Fallback => _fallback;

    public bool OffRequested => _offRequested;
    public DateTimeOffset? LastSaveOkAt => _lastSaveOkAt;
    public bool SavePending => _pending is not null;
    public DateTimeOffset? PendingSince => _pending?.At;

    // ------------------------------------------------------------------
    // Inputs
    // ------------------------------------------------------------------

    /// <param name="Players">Players online now (the host excluded).</param>
    /// <param name="SaveLines">Finished-save lines from the game log since the last tick, oldest first.</param>
    /// <param name="RunGeneration">Counts up when the game starts a new run.</param>
    /// <param name="SaveFileWrittenAt">The world file's last write time, if it exists.</param>
    public sealed record TickInput(
        int Players,
        IReadOnlyList<DateTimeOffset> SaveLines,
        int RunGeneration,
        DateTimeOffset? SaveFileWrittenAt);

    public sealed record TickResult(IReadOnlyList<WorldSaveAction> Actions, IReadOnlyList<WorldSaveNote> Notes);

    private sealed record Pending(string Reason, DateTimeOffset At, bool Proving, bool Explicit);

    // ------------------------------------------------------------------
    // The tick
    // ------------------------------------------------------------------

    public TickResult Tick(DateTimeOffset now, TickInput input)
    {
        var actions = new List<WorldSaveAction>();
        var notes = new List<WorldSaveNote>();

        if (_runGeneration is null)
        {
            _runGeneration = input.RunGeneration;
        }
        else if (_runGeneration != input.RunGeneration)
        {
            _runGeneration = input.RunGeneration;
            var wasOn = _tookOver;
            ResetForNewRun();
            if (wasOn)
                notes.Add(Info("The game started a new run with its own autosave on; Beacon waits for its first autosave again."));
        }

        var freshGameLine = false;
        foreach (var at in input.SaveLines)
        {
            if (IsOwnLine(at))
            {
                if (_pending is not null) Confirm(at, "the game's save line", notes);
                continue;
            }

            freshGameLine = true;
            _lastGameAutosaveAt = at;
            if (_backOnAt is not null && !_backOnSeen && at >= _backOnAt)
            {
                _backOnSeen = true;
                notes.Add(Info("The game's own autosave is back on (it saved by itself)."));
            }
            if (_offRequested && _lastOffAt is not null && at >= _lastOffAt)
            {
                // The switch did not hold: the game is autosaving.
                _offRequested = false;
                notes.Add(Warn("The game autosaved by itself after its autosave was switched off; the switch did not hold."));
            }
        }

        if (_pending is not null && input.SaveFileWrittenAt is { } written && written >= _pending.At)
            Confirm(now, "the world file", notes);

        if (_pending is not null && now - _pending.At > _confirmWithin)
            Fail($"the world was not written within {(int)_confirmWithin.TotalSeconds} s", actions, notes);

        if (input.Players < _lastPlayers) _leaveSaveDueAt = now + LeaveBatch;
        if (input.Players > 0) _playedSinceLastSave = true;
        _lastPlayers = input.Players;

        if (_backOnAt is not null && !_backOnSeen && !_backOnWarned && now - _backOnAt > AutosaveBackWithin)
        {
            _backOnWarned = true;
            notes.Add(Error("The game has not autosaved since its autosave was switched back on " +
                            $"({(int)AutosaveBackWithin.TotalMinutes} minutes). The world was last saved {Describe(_lastSaveOkAt ?? _lastGameAutosaveAt)}."));
        }

        if (_every is null) return new TickResult(actions, notes);

        if (!_tookOver && !_fallback && !_gaveUp)
        {
            if (_switchOffNow)
            {
                _switchOffNow = false;
                SwitchOff(now, first: true, actions, notes);
            }
            else if (freshGameLine && _pending is null)
            {
                // Step 2: prove Beacon's own save works on this run before switching anything.
                Request(now, "first save by Beacon before it takes over saving", proving: true, explicitSave: false, actions);
            }
            return new TickResult(actions, notes);
        }

        if (_tookOver && !_fallback)
        {
            if (freshGameLine && !_offRequested && _lastOffAt is not null && now - _lastOffAt >= RecheckAfter)
            {
                if (_offTries >= MaxSwitchOffTries)
                {
                    _gaveUp = true;
                    _tookOver = false;
                    notes.Add(Warn($"The game's autosave stayed on after {MaxSwitchOffTries} tries to switch it off; " +
                                   "the game keeps saving every 2 minutes and Beacon stops its own schedule for this run."));
                }
                else
                {
                    SwitchOff(now, first: false, actions, notes);
                }
            }

            if (_tookOver && _pending is null)
            {
                if (_leaveSaveDueAt is { } due && now >= due)
                {
                    Request(now, "players left", proving: false, explicitSave: false, actions);
                }
                else if (_playedSinceLastSave && now - (_lastScheduleBase ?? now) >= _every)
                {
                    Request(now, $"every {(int)_every.Value.TotalMinutes} minutes", proving: false, explicitSave: false, actions);
                }
            }
        }

        return new TickResult(actions, notes);
    }

    /// <summary>
    /// A save asked for from outside the schedule (before a stop, or by an admin). Joins a save
    /// already in flight instead of asking the game twice.
    /// </summary>
    public WorldSaveAction? BeginSave(DateTimeOffset now, string reason)
    {
        if (_pending is not null) return null;
        var actions = new List<WorldSaveAction>();
        Request(now, reason, proving: false, explicitSave: true, actions);
        return actions[0];
    }

    /// <summary>The game-side module refused or could not make the save request.</summary>
    public TickResult SaveRequestFailed(DateTimeOffset now, string why)
    {
        var actions = new List<WorldSaveAction>();
        var notes = new List<WorldSaveNote>();
        if (_pending is not null) Fail(why, actions, notes);
        return new TickResult(actions, notes);
    }

    /// <summary>
    /// The answer to an autosave switch Beacon sent. <see cref="SwitchAnswer.Unknown"/> (no answer from the
    /// game-side module) is treated as possibly applied: Beacon keeps its own schedule, and the next autosave
    /// line, or its absence, tells the truth.
    /// </summary>
    public TickResult SwitchResult(DateTimeOffset now, WorldSaveActionKind kind, SwitchAnswer answer, string detail)
    {
        var notes = new List<WorldSaveNote>();
        if (kind == WorldSaveActionKind.AutosaveOff)
        {
            switch (answer)
            {
                case SwitchAnswer.Applied:
                    notes.Add(Info($"The game's autosave switch answered: {detail}."));
                    break;
                case SwitchAnswer.NotApplied:
                    // Nothing was switched, so the game's autosave is still on.
                    _offRequested = false;
                    _tookOver = false;
                    _gaveUp = true;
                    notes.Add(Warn($"The game's autosave could not be switched off ({detail}); the game keeps saving every 2 minutes for this run."));
                    break;
                default:
                    notes.Add(Warn($"The game's autosave switch did not answer ({detail}). Beacon keeps saving on its own schedule " +
                                   "and switches again only if the game autosaves by itself."));
                    break;
            }
        }
        else if (kind == WorldSaveActionKind.AutosaveOn)
        {
            notes.Add(answer == SwitchAnswer.Applied
                ? Info($"The game's autosave switch answered: {detail}.")
                : Error($"The game's autosave may not be back on ({detail}). The world was last saved {Describe(_lastSaveOkAt ?? _lastGameAutosaveAt)}."));
        }
        return new TickResult(Array.Empty<WorldSaveAction>(), notes);
    }

    /// <summary>How the save requested at <paramref name="requestedAt"/> ended, once it has.</summary>
    public SaveOutcome? OutcomeFor(DateTimeOffset requestedAt) =>
        _outcomes.TryGetValue(requestedAt, out var o) ? o : null;

    private readonly Dictionary<DateTimeOffset, SaveOutcome> _outcomes = new();

    private void Record(Pending p, SaveOutcome outcome)
    {
        _outcomes[p.At] = outcome;
        if (_outcomes.Count > 32)
            foreach (var old in _outcomes.Keys.OrderBy(k => k).Take(_outcomes.Count - 32).ToList())
                _outcomes.Remove(old);
    }

    // ------------------------------------------------------------------

    private bool IsOwnLine(DateTimeOffset at) =>
        _lastRequestAt is { } req && at >= req && at - req <= OwnSaveWindow;

    private void Request(DateTimeOffset now, string reason, bool proving, bool explicitSave, List<WorldSaveAction> actions)
    {
        _pending = new Pending(reason, now, proving, explicitSave);
        _lastRequestAt = now;
        if (_tookOver) _lastScheduleBase = now;
        _leaveSaveDueAt = null;
        actions.Add(new WorldSaveAction(WorldSaveActionKind.Save, reason));
    }

    private void Confirm(DateTimeOffset at, string how, List<WorldSaveNote> notes)
    {
        var p = _pending!;
        _pending = null;
        _lastSaveOkAt = at;
        _playedSinceLastSave = false;
        if (_tookOver) _lastScheduleBase = p.At;
        var took = at - p.At;
        if (took < TimeSpan.Zero) took = TimeSpan.Zero;
        Record(p, new SaveOutcome(true, p.Reason, took, how));
        notes.Add(Info($"World saved ({p.Reason}): {how} confirmed it {took.TotalSeconds:0.0} s after the request."));
        if (p.Proving && !_fallback && !_gaveUp && _every is not null) _switchOffNow = true;
    }

    private void Fail(string why, List<WorldSaveAction> actions, List<WorldSaveNote> notes)
    {
        var p = _pending!;
        _pending = null;
        Record(p, new SaveOutcome(false, p.Reason, TimeSpan.Zero, why));

        if (p.Proving)
        {
            _gaveUp = true;
            notes.Add(Warn($"Beacon's first save on this run did not land ({why}). The game's own autosave stays on for this run."));
            return;
        }
        if (!_tookOver)
        {
            notes.Add(Warn($"World save ({p.Reason}) not confirmed: {why}. The game's own autosave is on, last save {Describe(_lastSaveOkAt ?? _lastGameAutosaveAt)}."));
            return;
        }

        _fallback = true;
        _tookOver = false;
        notes.Add(Error($"World save ({p.Reason}) failed: {why}. The world was last saved {Describe(_lastSaveOkAt ?? _lastGameAutosaveAt)}. " +
                        "Switching the game's own 2-minute autosave back on for the rest of this run."));

        // A toggle is only sent back when Beacon is sure it is off: Beacon switched it and no
        // autosave line has come since. The setting is safe to set either way.
        var send = Switch == SettingSwitch || _offRequested;
        _offRequested = false;
        if (send)
        {
            _backOnAt = p.At + _confirmWithin;
            _backOnSeen = false;
            _backOnWarned = false;
            actions.Add(new WorldSaveAction(WorldSaveActionKind.AutosaveOn, "a world save failed", Switch));
        }
    }

    private void SwitchOff(DateTimeOffset now, bool first, List<WorldSaveAction> actions, List<WorldSaveNote> notes)
    {
        _offTries++;
        _offRequested = true;
        _lastOffAt = now;
        if (!_tookOver)
        {
            _tookOver = true;
            _lastScheduleBase = now;
        }
        notes.Add(first
            ? Info($"The game autosaved by itself and Beacon's own save worked: switching the game's 2-minute autosave off. " +
                   $"Beacon now saves every {(int)_every!.Value.TotalMinutes} minutes while players are on, after players leave and before every stop.")
            : Info($"Switching the game's autosave off again (try {_offTries} of {MaxSwitchOffTries})."));
        actions.Add(new WorldSaveAction(WorldSaveActionKind.AutosaveOff,
            first ? "Beacon takes over saving" : "the game autosaved after the switch", Switch));
    }

    private void ResetForNewRun()
    {
        _pending = null;
        _lastRequestAt = null;
        _lastScheduleBase = null;
        _playedSinceLastSave = false;
        _leaveSaveDueAt = null;
        _tookOver = false;
        _offRequested = false;
        _fallback = false;
        _gaveUp = false;
        _switchOffNow = false;
        _offTries = 0;
        _lastOffAt = null;
        _backOnAt = null;
        _backOnSeen = false;
        _backOnWarned = false;
    }

    private static string Describe(DateTimeOffset? at) =>
        at is { } t ? $"at {t.UtcDateTime:HH:mm:ss} UTC" : "at an unknown time (no save seen since Beacon started)";

    private static WorldSaveNote Info(string text) => new(WorldSaveNoteLevel.Info, text);
    private static WorldSaveNote Warn(string text) => new(WorldSaveNoteLevel.Warning, text);
    private static WorldSaveNote Error(string text) => new(WorldSaveNoteLevel.Error, text);
}

public enum WorldSaveActionKind { Save, AutosaveOff, AutosaveOn }

public enum SwitchAnswer { Applied, NotApplied, Unknown }

/// <param name="Method">For a switch: "console" or "setting".</param>
public sealed record WorldSaveAction(WorldSaveActionKind Kind, string Reason, string Method = "");

public enum WorldSaveNoteLevel { Info, Warning, Error }

public sealed record WorldSaveNote(WorldSaveNoteLevel Level, string Text);

/// <param name="How">What confirmed it, or why it failed.</param>
/// <param name="Kind">Asked: a save was made (or joined). Recent / TooSoon: an owner's request inside the
/// 30-second gap, so no new save (Recent = a save landed <paramref name="Ago"/> ago).</param>
public sealed record SaveOutcome(bool Saved, string Reason, TimeSpan Took, string How,
    SaveOutcomeKind Kind = SaveOutcomeKind.Asked, TimeSpan? Ago = null, TimeSpan? NextIn = null);

public enum SaveOutcomeKind { Asked, Recent, TooSoon }

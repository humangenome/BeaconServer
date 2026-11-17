namespace BeaconServer.Services;

/// <summary>
/// What the game's own log says about saving, handed from <see cref="Sn2LogTailService"/> to
/// <see cref="WorldSaveService"/>. Only live lines count (never the old log read at start-up).
///
/// Subnautica 2 writes one line per finished autosave, from its analytics:
/// <c>LogUWEAnalytics: Not logged in to GPP. Queuing up event 'autosave_completed'.</c>
/// The game's own 2-minute autosave and a save Beacon asks for both write it. A new game run
/// starts with <c>LogSaveSystem: Save System initializing: ... Autosave enabled: true</c>.
/// </summary>
public sealed class GameSaveSignals
{
    private readonly object _gate = new();
    private readonly List<DateTimeOffset> _autosaves = new();
    private int _runGeneration;
    private bool? _autosaveEnabledAtStart;
    private DateTimeOffset? _lastAutosaveAt;

    internal const string AutosaveMarker = "autosave_completed";
    internal const string SaveSystemInitMarker = "Save System initializing";

    /// <summary>Called for each live game-log line. Returns true when the line was a save signal.</summary>
    public bool Observe(string line, DateTimeOffset at)
    {
        if (string.IsNullOrEmpty(line)) return false;
        if (line.Contains(AutosaveMarker, StringComparison.Ordinal))
        {
            lock (_gate)
            {
                _autosaves.Add(at);
                if (_autosaves.Count > 64) _autosaves.RemoveAt(0);
                _lastAutosaveAt = at;
            }
            return true;
        }
        if (line.Contains(SaveSystemInitMarker, StringComparison.Ordinal))
        {
            lock (_gate)
            {
                _runGeneration++;
                _autosaves.Clear();
                var i = line.IndexOf("Autosave enabled:", StringComparison.OrdinalIgnoreCase);
                _autosaveEnabledAtStart = i < 0
                    ? null
                    : line[(i + "Autosave enabled:".Length)..].TrimStart().StartsWith("true", StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }
        return false;
    }

    /// <summary>Counts up each time the game starts a new run (its save system starts again).</summary>
    public int RunGeneration { get { lock (_gate) return _runGeneration; } }

    /// <summary>The game's own "Autosave enabled" at the start of this run, when its log said.</summary>
    public bool? AutosaveEnabledAtStart { get { lock (_gate) return _autosaveEnabledAtStart; } }

    /// <summary>When the game last logged a finished save (its own or one Beacon asked for).</summary>
    public DateTimeOffset? LastAutosaveAt { get { lock (_gate) return _lastAutosaveAt; } }

    /// <summary>The finished-save lines seen since the last call, oldest first.</summary>
    public IReadOnlyList<DateTimeOffset> DrainAutosaves()
    {
        lock (_gate)
        {
            if (_autosaves.Count == 0) return Array.Empty<DateTimeOffset>();
            var copy = _autosaves.ToArray();
            _autosaves.Clear();
            return copy;
        }
    }
}

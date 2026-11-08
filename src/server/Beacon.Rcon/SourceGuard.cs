using System.Net;

namespace Beacon.Rcon;

/// <summary>
/// Per-address limits for the two admin listeners (RCON and the signed HTTP routes).
/// Failed logins: an address that fails the password or the signature five times in ten
/// minutes is turned away for a minute, then two, four, up to fifteen. The count is shared by
/// both listeners, so guessing on RCON also closes HTTP to that address. Open connections: one
/// address may hold only a few at once, so a single source cannot take every slot. Loopback is
/// never limited. Pure bookkeeping, no IO: the listeners decide what to do with the answers.
/// </summary>
public sealed class SourceGuard
{
    public const int FailuresBeforeBlock = 5;
    public const long FailureWindowMs = 10 * 60 * 1000;
    public const long FirstBlockMs = 60 * 1000;
    public const long MaxBlockMs = 15 * 60 * 1000;
    // The failure table makes room at MaxTrackedSources (see MakeRoom). It grows past that only
    // while every address in it is blocked, and never past HardCeiling.
    private const int MaxTrackedSources = 4096;
    private const int HardCeiling = 65536;
    private const int RoomBatch = MaxTrackedSources / 16;

    private sealed class FailState
    {
        public int Failures;
        public long WindowStartMs;
        public long BlockedUntilMs;
        public int Strikes;
        public long LastFailureMs;
    }

    private static readonly Dictionary<string, FailState> Fails = new(StringComparer.Ordinal);
    private static readonly object FailLock = new();
    private static int _lookAgainAt = MaxTrackedSources;
    private static long _lookAgainMs = long.MaxValue;
    private static bool _allBlockedNoted;
    private static string? _notice;

    private readonly int _maxPerSource;
    private readonly Dictionary<string, int> _open = new(StringComparer.Ordinal);
    private readonly object _openLock = new();

    public SourceGuard(int maxPerSource) => _maxPerSource = Math.Max(1, maxPerSource);

    public static string SourceOf(EndPoint? endPoint)
    {
        if (endPoint is not IPEndPoint ip) return "?";
        var address = ip.Address;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }

    public static bool IsLoopback(string source) =>
        IPAddress.TryParse(source, out var address) && IPAddress.IsLoopback(address);

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Seconds left on a block; 0 when the source may talk.</summary>
    public static long BlockedSeconds(string source, long nowMs)
    {
        if (IsLoopback(source)) return 0;
        lock (FailLock)
        {
            if (!Fails.TryGetValue(source, out var state)) return 0;
            var left = state.BlockedUntilMs - nowMs;
            return left > 0 ? (left + 999) / 1000 : 0;
        }
    }

    /// <summary>Records a failed login; true when this failure starts a block.</summary>
    public static bool Failure(string source, long nowMs)
    {
        if (IsLoopback(source)) return false;
        lock (FailLock)
        {
            if (!Fails.TryGetValue(source, out var state))
            {
                if (Fails.Count >= MaxTrackedSources && (Fails.Count >= _lookAgainAt || nowMs >= _lookAgainMs))
                    MakeRoom(nowMs);
                state = new FailState { WindowStartMs = nowMs };
                Fails[source] = state;
            }
            state.LastFailureMs = nowMs;
            if (nowMs - state.WindowStartMs > FailureWindowMs)
            {
                state.Failures = 0;
                state.WindowStartMs = nowMs;
            }
            state.Failures++;
            if (state.Failures < FailuresBeforeBlock) return false;

            var block = FirstBlockMs;
            for (var i = 0; i < state.Strikes && block < MaxBlockMs; i++) block *= 2;
            if (block > MaxBlockMs) block = MaxBlockMs;
            state.BlockedUntilMs = nowMs + block;
            state.Strikes++;
            state.Failures = 0;
            state.WindowStartMs = nowMs;
            return true;
        }
    }

    public static void Success(string source)
    {
        lock (FailLock) Fails.Remove(source);
    }

    /// <summary>
    /// The one line to log when the failure table first fills with blocks, with no address in it;
    /// null otherwise. A door calls this after Failure and logs what it gets.
    /// </summary>
    public static string? TakeNotice()
    {
        lock (FailLock)
        {
            var notice = _notice;
            _notice = null;
            return notice;
        }
    }

    /// <summary>
    /// Makes room in a full failure table for a new address. First go the addresses that are
    /// neither blocked nor inside a live failure window, then the longest-quiet addresses that are
    /// not blocked, a batch at a time. An active block is never dropped to make room: when every
    /// address left is blocked, the table keeps them all and grows (one log line, no addresses),
    /// up to HardCeiling. Only at the ceiling does the block nearest to running out make way.
    /// Runs once per batch of new addresses or when the next block runs out, not on every failure.
    /// </summary>
    private static void MakeRoom(long nowMs)
    {
        var quiet = new List<string>();
        var open = new List<KeyValuePair<string, long>>();
        string? soonest = null;
        var soonestUntil = long.MaxValue;
        foreach (var (key, state) in Fails)
        {
            if (state.BlockedUntilMs > nowMs)
            {
                if (state.BlockedUntilMs < soonestUntil)
                {
                    soonestUntil = state.BlockedUntilMs;
                    soonest = key;
                }
            }
            else if (nowMs - state.WindowStartMs > FailureWindowMs) quiet.Add(key);
            else open.Add(new KeyValuePair<string, long>(key, state.LastFailureMs));
        }
        foreach (var key in quiet) Fails.Remove(key);

        var excess = Fails.Count - (MaxTrackedSources - RoomBatch);
        if (excess > 0 && open.Count > 0)
        {
            open.Sort((a, b) => a.Value.CompareTo(b.Value));
            for (var i = 0; i < excess && i < open.Count; i++) Fails.Remove(open[i].Key);
        }

        if (Fails.Count < MaxTrackedSources)
        {
            _lookAgainAt = MaxTrackedSources;
            _lookAgainMs = long.MaxValue;
            _allBlockedNoted = false;
            return;
        }

        // Every address left is an active block.
        if (!_allBlockedNoted)
        {
            _allBlockedNoted = true;
            _notice = $"auth table full: all {Fails.Count} tracked addresses are blocked; " +
                      $"every block is kept (up to {HardCeiling})";
        }
        if (Fails.Count >= HardCeiling && soonest is not null)
        {
            Fails.Remove(soonest);
            _lookAgainAt = HardCeiling;
        }
        else
        {
            _lookAgainAt = Math.Min(HardCeiling, Fails.Count + RoomBatch);
        }
        _lookAgainMs = soonestUntil;
    }

    public bool TryOpen(string source)
    {
        if (IsLoopback(source)) return true;
        lock (_openLock)
        {
            _open.TryGetValue(source, out var count);
            if (count >= _maxPerSource) return false;
            _open[source] = count + 1;
            return true;
        }
    }

    public void Close(string source)
    {
        if (IsLoopback(source)) return;
        lock (_openLock)
        {
            if (!_open.TryGetValue(source, out var count)) return;
            if (count <= 1) _open.Remove(source);
            else _open[source] = count - 1;
        }
    }

    /// <summary>Test seam: the failure table is shared and outlives instances.</summary>
    public static void ResetForTests()
    {
        lock (FailLock)
        {
            Fails.Clear();
            _lookAgainAt = MaxTrackedSources;
            _lookAgainMs = long.MaxValue;
            _allBlockedNoted = false;
            _notice = null;
        }
    }
}

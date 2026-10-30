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
    private const int MaxTrackedSources = 4096;

    private sealed class FailState
    {
        public int Failures;
        public long WindowStartMs;
        public long BlockedUntilMs;
        public int Strikes;
    }

    private static readonly Dictionary<string, FailState> Fails = new(StringComparer.Ordinal);
    private static readonly object FailLock = new();

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
                if (Fails.Count >= MaxTrackedSources) Prune(nowMs);
                state = new FailState { WindowStartMs = nowMs };
                Fails[source] = state;
            }
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

    // Drop sources that are neither blocked nor inside a live failure window.
    private static void Prune(long nowMs)
    {
        foreach (var key in Fails
                     .Where(kv => kv.Value.BlockedUntilMs <= nowMs && nowMs - kv.Value.WindowStartMs > FailureWindowMs)
                     .Select(kv => kv.Key).ToList())
            Fails.Remove(key);
        if (Fails.Count >= MaxTrackedSources) Fails.Clear();
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
        lock (FailLock) Fails.Clear();
    }
}

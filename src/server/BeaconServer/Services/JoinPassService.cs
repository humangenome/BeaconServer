using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BeaconServer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

public enum JoinPassOutcome
{
    Issued,
    BadRequest,
    RateLimited,
    Refused,      // Steam said no
    Unavailable,  // Steam, the Steam login or the game's key is not there: fail closed
}

public sealed record JoinPassResult(JoinPassOutcome Outcome, string? Pass = null, int ExpiresInSeconds = 0);

/// <summary>
/// Steam identity check at join (0.3.140). The Beacon app sends the player's Steam session ticket and Steam ID; Steam
/// is asked whether that ticket proves that ID (<see cref="ISteamTicketVerifier"/>); on Steam's OK this issues a short,
/// one-use join pass tied to that Steam ID, which the app hands to the game and Beacon.dll's login check verifies.
///
/// Pass: <c>1.&lt;steamId&gt;.&lt;expiryUnix&gt;.&lt;nonce&gt;.&lt;mac&gt;</c>, mac = HMAC-SHA256(key,
/// "beacon-join-pass|1|steamId|expiry|nonce") in lowercase hex. The key is made by Beacon.dll inside the game process
/// once per boot and sent over the pipe; it lives in memory only on both sides and is never logged. A pass lives
/// <see cref="Configuration.IdentityOptions.PassLifetimeSeconds"/> (120 s). Beacon.dll keeps the used nonces, so a pass
/// lets one join in: the game checks a join twice, about a second apart, and the second check passes only for the same
/// Steam ID within 10 s of the first.
///
/// Logs name this server and the reason only: never the ticket, the pass, the Steam ID or a player name.
/// </summary>
public sealed class JoinPassService
{
    internal const string MacLabel = "beacon-join-pass|1|";
    private static readonly Regex SteamId64 = new("^7656119[0-9]{10}$", RegexOptions.CultureInvariant);

    private readonly IdentityOptions _opts;
    private readonly PipeServerState _state;
    private readonly InstanceIdentityProvider _identity;
    private readonly ISteamTicketVerifier _steam;
    private readonly ILogger<JoinPassService> _log;
    private readonly Func<DateTimeOffset> _now;

    private readonly object _rateGate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _perAddress = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _all = new();
    private DateTimeOffset _lastRateLog = DateTimeOffset.MinValue;

    public JoinPassService(
        IOptions<BeaconServerOptions> options,
        PipeServerState state,
        InstanceIdentityProvider identity,
        ISteamTicketVerifier steam,
        ILogger<JoinPassService> log,
        Func<DateTimeOffset>? now = null)
    {
        _opts = options.Value.Identity;
        _state = state;
        _identity = identity;
        _steam = steam;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>"on" when the game's login hook has the check on, the game's key is here and Steam's login is up.</summary>
    public string Status =>
        _state.LastIdentityCheckReady != 0 && _state.IdentityKeyCopy() is not null && _steam.Ready ? "on" : "not_active";

    public async Task<JoinPassResult> RequestAsync(string clientAddress, string? steamId, string? ticketHex, CancellationToken ct)
    {
        // Every request counts against the limits, a malformed one included.
        if (!TryTakeRateSlot(clientAddress ?? ""))
        {
            var now = _now();
            if (now - _lastRateLog > TimeSpan.FromMinutes(1))
            {
                _lastRateLog = now;
                _log.LogWarning("Identity: join pass requests rate limited on {Instance}", _identity.InstanceId);
            }
            return new JoinPassResult(JoinPassOutcome.RateLimited);
        }

        var ticket = ParseTicketHex(ticketHex);
        if (steamId is null || !SteamId64.IsMatch(steamId) || ticket is null)
        {
            _log.LogInformation("Identity: join pass refused on {Instance}: malformed request", _identity.InstanceId);
            return new JoinPassResult(JoinPassOutcome.BadRequest);
        }

        var key = _state.IdentityKeyCopy();
        if (key is null)
        {
            _log.LogWarning("Identity: join pass unavailable on {Instance}: no key from the game yet", _identity.InstanceId);
            return new JoinPassResult(JoinPassOutcome.Unavailable);
        }

        SteamTicketResult result;
        try { result = await _steam.VerifyAsync(steamId, ticket, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            result = new SteamTicketResult(SteamTicketOutcome.Unavailable, "Steam check failed");
        }

        switch (result.Outcome)
        {
            case SteamTicketOutcome.Ok:
                var lifetime = Math.Clamp(_opts.PassLifetimeSeconds, 60, 180);
                var expiry = _now().ToUnixTimeSeconds() + lifetime;
                var pass = Issue(key, steamId, expiry, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
                _log.LogInformation("Identity: join pass issued on {Instance}", _identity.InstanceId);
                return new JoinPassResult(JoinPassOutcome.Issued, pass, lifetime);
            case SteamTicketOutcome.Refused:
                _log.LogWarning("Identity: join pass refused on {Instance}: {Reason}", _identity.InstanceId, result.Detail);
                return new JoinPassResult(JoinPassOutcome.Refused);
            default:
                _log.LogWarning("Identity: join pass unavailable on {Instance}: {Reason}", _identity.InstanceId, result.Detail);
                return new JoinPassResult(JoinPassOutcome.Unavailable);
        }
    }

    /// <summary>A pass for <paramref name="steamId"/>; the format Beacon.dll verifies (see the class summary).</summary>
    public static string Issue(byte[] key, string steamId, long expiryUnix, string nonceHex) =>
        $"1.{steamId}.{expiryUnix}.{nonceHex}.{Mac(key, steamId, expiryUnix, nonceHex)}";

    public static string Mac(byte[] key, string steamId, long expiryUnix, string nonceHex) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes($"{MacLabel}{steamId}|{expiryUnix}|{nonceHex}")));

    /// <summary>32 bytes from 64 hex characters, or null.</summary>
    public static byte[]? TryParseKeyHex(string? hex)
    {
        if (hex is null || hex.Length != 64) return null;
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }

    /// <summary>Ticket bytes from hex: 16..4096 bytes, or null.</summary>
    internal static byte[]? ParseTicketHex(string? hex)
    {
        if (hex is null || hex.Length < 32 || hex.Length > 8192 || hex.Length % 2 != 0) return null;
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }

    private bool TryTakeRateSlot(string address)
    {
        var now = _now();
        var window = TimeSpan.FromMinutes(1);
        var perAddress = Math.Max(1, _opts.RequestsPerAddressPerMinute);
        var overall = Math.Max(1, _opts.RequestsPerMinute);
        lock (_rateGate)
        {
            while (_all.Count > 0 && now - _all.Peek() >= window) _all.Dequeue();
            if (_perAddress.Count > 4096)
            {
                foreach (var stale in _perAddress.Where(kv => kv.Value.Count == 0 || now - kv.Value.Last() >= window).Select(kv => kv.Key).ToList())
                    _perAddress.Remove(stale);
            }
            if (!_perAddress.TryGetValue(address, out var q)) _perAddress[address] = q = new Queue<DateTimeOffset>();
            while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue();
            if (q.Count >= perAddress || _all.Count >= overall) return false;
            q.Enqueue(now);
            _all.Enqueue(now);
            return true;
        }
    }
}

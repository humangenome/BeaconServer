using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Beacon.SourceQuery;

/// <summary>
/// Source A2S query responder. Implements the subset of the Source query
/// protocol GameTracker, ServerMonkey, BattleMetrics, and stock browsers rely on:
/// A2S_INFO, A2S_PLAYER, A2S_RULES, all gated by the standard challenge-response
/// anti-spoof handshake (A2S_INFO too, since 2026-11: Valve's 2020 change; before it a
/// 25-byte spoofed info request got the full reply, which made the port a reflector).
/// Each source address gets at most <see cref="MaxRepliesPerWindow"/> replies per
/// <see cref="ReplyWindow"/>, challenges included.
///
/// Spec: https://developer.valvesoftware.com/wiki/Server_queries
/// </summary>
public sealed class SourceQueryServer : IDisposable
{
    private readonly UdpClient _udp;
    private readonly Func<ServerInfoSnapshot> _infoProvider;
    private readonly Func<IReadOnlyList<PlayerInfoEntry>> _playerProvider;
    private readonly Func<IReadOnlyList<KeyValuePair<string, string>>> _rulesProvider;
    private readonly Func<bool> _availabilityProvider;
    private readonly Dictionary<IPEndPoint, int> _challenges = new();
    // A2S challenge requests come from unauthenticated public UDP; without a cap
    // _challenges grows per unique source forever. Bound it — clear when full so
    // a flood can't exhaust memory (legit clients simply re-challenge).
    private const int MaxChallenges = 1024;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Per source ADDRESS (not address:port) reply budget. The Beacon app asks about the selected server every 10 s
    // and the whole list every 30 s, two replies each with the challenge; the panel asks once a minute. 60 per 10 s
    // leaves a household of players behind one address far below it and stops one address from using the port as
    // a stream. Bounded like the challenge table.
    internal static readonly TimeSpan ReplyWindow = TimeSpan.FromSeconds(10);
    internal const int MaxRepliesPerWindow = 60;
    private const int MaxTrackedAddresses = 4096;
    private readonly Dictionary<IPAddress, (long WindowStart, int Count)> _replyBudget = new();
    private readonly Func<long> _clock;

    // Windows turns an ICMP "port unreachable" for an earlier reply into a "connection reset" error on a later
    // receive (SIO_UDP_CONNRESET), and a TTL-expired one into "network reset" (SIO_UDP_NETRESET). A query responder
    // has no connection to reset, so both reports are switched off: a reply that lands on a closed port is simply
    // lost, as on Linux. Same fix as Cauldron 997c3dd (a 50 ms pause per report left that responder deaf for ~2 s
    // after 40 replies to closed ports, and made it miss real queries under a stream of them).
    private const int SioUdpConnReset = unchecked((int)0x9800000C);
    private const int SioUdpNetReset = unchecked((int)0x9800000F);
    private int _closedPortReports;

    /// <summary>Closed-port reports that still reached the receive loop (0 on Windows with the reports off).</summary>
    internal int ClosedPortReports => Volatile.Read(ref _closedPortReports);

    public SourceQueryServer(
        int port,
        Func<ServerInfoSnapshot> infoProvider,
        Func<IReadOnlyList<PlayerInfoEntry>> playerProvider,
        Func<IReadOnlyList<KeyValuePair<string, string>>> rulesProvider,
        Func<bool>? availabilityProvider = null)
        : this(port, infoProvider, playerProvider, rulesProvider, availabilityProvider, clock: null, ignoreClosedPortReports: true)
    {
    }

    internal SourceQueryServer(
        int port,
        Func<ServerInfoSnapshot> infoProvider,
        Func<IReadOnlyList<PlayerInfoEntry>> playerProvider,
        Func<IReadOnlyList<KeyValuePair<string, string>>> rulesProvider,
        Func<bool>? availabilityProvider,
        Func<long>? clock,
        bool ignoreClosedPortReports)
    {
        _udp = new UdpClient(port);
        if (ignoreClosedPortReports && OperatingSystem.IsWindows())
        {
            try
            {
                _udp.Client.IOControl(SioUdpConnReset, new byte[4], null);
                _udp.Client.IOControl(SioUdpNetReset, new byte[4], null);
            }
            catch (SocketException) { /* the loop below still goes straight on past a report */ }
        }
        _infoProvider = infoProvider;
        _playerProvider = playerProvider;
        _rulesProvider = rulesProvider;
        _availabilityProvider = availabilityProvider ?? (() => true);
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public int BoundPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _udp.Dispose();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try { res = await _udp.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.NetworkReset)
            {
                // A report about an earlier reply that landed on a closed port (only if switching the reports
                // off failed): nothing is wrong with this socket, so go straight on, never pause.
                Interlocked.Increment(ref _closedPortReports);
                continue;
            }
            catch (SocketException)
            {
                // Any other receive error: a short pause so a broken socket cannot spin the loop. Before
                // 0.3.13x an exception here ended the loop for good, with nothing logged.
                try { await Task.Delay(50, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            try
            {
                var reply = HandleQuery(res.RemoteEndPoint, res.Buffer);
                if (reply is not null && reply.Length > 0)
                    await _udp.SendAsync(reply, reply.Length, res.RemoteEndPoint).ConfigureAwait(false);
            }
            catch
            {
                // Malformed UDP query — drop silently.
            }
        }
    }

    internal byte[]? HandleQuery(IPEndPoint from, byte[] payload)
    {
        if (payload.Length < 5) return null;
        var marker = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (marker != -1) return null;
        if (!_availabilityProvider()) return null;
        var header = payload[4];
        if (header is not (0x54 or 0x55 or 0x56)) return null;
        if (!TakeReplyBudget(from.Address)) return null;

        switch (header)
        {
            case 0x54:
                // "TSource Engine Query\0" then, from a client that has one, the 4-byte challenge (Valve, Dec 2020).
                // No challenge or a wrong one gets the 9-byte challenge reply, never the info: the reply to an
                // unproven address is smaller than the request, so the port is no use as a reflector.
                // A request without the query text is not a Source query at all (Valve's servers ignore it too).
                if (!HasInfoQueryText(payload)) return null;
                if (!InfoChallengeMatches(from, payload)) return ChallengeReply(from);
                return BuildInfoReply();
            case 0x55:
                if (payload.Length < 9) return null;
                return BuildPlayerOrRulesReply(from, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5, 4)), isRules: false);
            case 0x56:
                if (payload.Length < 9) return null;
                return BuildPlayerOrRulesReply(from, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5, 4)), isRules: true);
        }
        return null;
    }

    private static readonly byte[] InfoQueryText = Encoding.ASCII.GetBytes("Source Engine Query\0");

    private static bool HasInfoQueryText(byte[] payload) =>
        payload.Length >= 5 + InfoQueryText.Length && payload.AsSpan(5, InfoQueryText.Length).SequenceEqual(InfoQueryText);

    private bool InfoChallengeMatches(IPEndPoint from, byte[] payload)
    {
        var at = 5 + InfoQueryText.Length;
        if (payload.Length < at + 4) return false;
        var sent = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at, 4));
        return _challenges.TryGetValue(from, out var expected) && expected == sent;
    }

    // One budget per source address: a reply is sent only while that address has budget left in this window.
    private bool TakeReplyBudget(IPAddress address)
    {
        var now = _clock();
        var window = (long)ReplyWindow.TotalMilliseconds;
        if (_replyBudget.TryGetValue(address, out var b) && now - b.WindowStart < window)
        {
            if (b.Count >= MaxRepliesPerWindow) return false;
            _replyBudget[address] = (b.WindowStart, b.Count + 1);
            return true;
        }
        if (_replyBudget.Count >= MaxTrackedAddresses && !_replyBudget.ContainsKey(address))
        {
            foreach (var key in _replyBudget.Where(kv => now - kv.Value.WindowStart >= window).Select(kv => kv.Key).ToList())
                _replyBudget.Remove(key);
            if (_replyBudget.Count >= MaxTrackedAddresses) _replyBudget.Clear();
        }
        _replyBudget[address] = (now, 1);
        return true;
    }

    private byte[] BuildInfoReply()
    {
        var info = _infoProvider();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(-1);
        w.Write((byte)0x49);                          // I = info
        w.Write((byte)17);                            // protocol version
        WriteCString(w, info.Name);
        WriteCString(w, info.Map);
        WriteCString(w, info.Folder);
        WriteCString(w, info.Game);

        // Legacy 16-bit AppID = low 16 bits of the Steam AppID (ushort, NOT a
        // signed modulo). Modern clients ignore this in favor of the EDF
        // GameID below, which carries the full 64-bit AppID.
        var legacyAppId = (ushort)(info.SteamAppId & 0xFFFF);
        w.Write(legacyAppId);

        w.Write((byte)info.PlayerCount);
        w.Write((byte)info.MaxPlayers);
        w.Write((byte)0);                             // bots
        w.Write((byte)'d');                           // dedicated
        w.Write((byte)(OperatingSystem.IsLinux() ? 'l' : 'w'));
        w.Write((byte)(info.PasswordRequired ? 1 : 0));
        w.Write((byte)(info.VacSecured ? 1 : 0));
        WriteCString(w, info.Version);

        // EDF: port(0x80) + keywords(0x20) + gameid(0x01)
        const byte edf = 0x80 | 0x20 | 0x01;
        w.Write(edf);
        w.Write((short)info.GameplayPort);
        WriteCString(w, info.Keywords);
        w.Write((ulong)info.SteamAppId);              // 64-bit GameID = real Steam AppID
        return ms.ToArray();
    }

    private byte[] BuildPlayerOrRulesReply(IPEndPoint from, int challenge, bool isRules)
    {
        if (challenge == -1) return ChallengeReply(from);
        if (!_challenges.TryGetValue(from, out var expected) || expected != challenge)
            return Array.Empty<byte>();

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(-1);

        if (!isRules)
        {
            var players = _playerProvider();
            w.Write((byte)0x44);                      // D = player
            w.Write((byte)players.Count);
            byte idx = 0;
            foreach (var p in players)
            {
                w.Write(idx++);
                WriteCString(w, p.Name);
                w.Write(p.Score);
                w.Write(p.ConnectSeconds);
            }
        }
        else
        {
            var rules = _rulesProvider();
            w.Write((byte)0x45);                      // E = rules
            w.Write((short)rules.Count);
            foreach (var (k, v) in rules)
            {
                WriteCString(w, k);
                WriteCString(w, v);
            }
        }
        return ms.ToArray();
    }

    private byte[] ChallengeReply(IPEndPoint from)
    {
        var issued = NewChallenge();
        if (_challenges.Count >= MaxChallenges && !_challenges.ContainsKey(from))
            _challenges.Clear();
        _challenges[from] = issued;
        var reply = new byte[9];
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(0, 4), -1);
        reply[4] = 0x41;                              // A = challenge
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(5, 4), issued);
        return reply;
    }

    // The challenge is the whole proof that a reply goes to the address that asked, so nobody may predict the one
    // another address will get from the ones they were given: it comes from the OS's cryptographic generator, never
    // System.Random (Security 2026-11-14). -1 is never issued: it is the "send me a challenge" value of A2S_PLAYER and
    // A2S_RULES, so a client holding it would only ask again.
    internal static int NewChallenge()
    {
        Span<byte> four = stackalloc byte[4];
        while (true)
        {
            RandomNumberGenerator.Fill(four);
            var value = BinaryPrimitives.ReadInt32LittleEndian(four);
            if (value != -1) return value;
        }
    }

    private static void WriteCString(BinaryWriter w, string value)
    {
        w.Write(Encoding.UTF8.GetBytes(value));
        w.Write((byte)0);
    }
}

public readonly record struct ServerInfoSnapshot(
    string Name,
    string Map,
    string Folder,
    string Game,
    long SteamAppId,
    int PlayerCount,
    int MaxPlayers,
    bool PasswordRequired,
    bool VacSecured,
    string Version,
    int GameplayPort,
    string Keywords);

public readonly record struct PlayerInfoEntry(
    string Name,
    int Score,
    float ConnectSeconds);

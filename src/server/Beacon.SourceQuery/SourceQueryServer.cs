using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Beacon.SourceQuery;

/// <summary>
/// Source A2S query responder. Implements the subset of the Source query
/// protocol GameTracker, ServerMonkey, BattleMetrics, and stock browsers rely on:
/// A2S_INFO, A2S_PLAYER, A2S_RULES, all gated by the standard challenge-response
/// anti-spoof handshake.
///
/// Spec: https://developer.valvesoftware.com/wiki/Server_queries
/// </summary>
public sealed class SourceQueryServer : IDisposable
{
    private readonly UdpClient _udp;
    private readonly Func<ServerInfoSnapshot> _infoProvider;
    private readonly Func<IReadOnlyList<PlayerInfoEntry>> _playerProvider;
    private readonly Func<IReadOnlyList<KeyValuePair<string, string>>> _rulesProvider;
    private readonly Dictionary<IPEndPoint, int> _challenges = new();
    private readonly Random _challengeRng = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SourceQueryServer(
        int port,
        Func<ServerInfoSnapshot> infoProvider,
        Func<IReadOnlyList<PlayerInfoEntry>> playerProvider,
        Func<IReadOnlyList<KeyValuePair<string, string>>> rulesProvider)
    {
        _udp = new UdpClient(port);
        _infoProvider = infoProvider;
        _playerProvider = playerProvider;
        _rulesProvider = rulesProvider;
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
        var header = payload[4];

        switch (header)
        {
            case 0x54: return BuildInfoReply();
            case 0x55:
                if (payload.Length < 9) return null;
                return BuildPlayerOrRulesReply(from, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5, 4)), isRules: false);
            case 0x56:
                if (payload.Length < 9) return null;
                return BuildPlayerOrRulesReply(from, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5, 4)), isRules: true);
        }
        return null;
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
        if (challenge == -1)
        {
            var issued = _challengeRng.Next();
            _challenges[from] = issued;
            using var msc = new MemoryStream();
            using var wc = new BinaryWriter(msc);
            wc.Write(-1);
            wc.Write((byte)0x41);                     // A = challenge
            wc.Write(issued);
            return msc.ToArray();
        }
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

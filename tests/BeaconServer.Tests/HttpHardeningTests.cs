using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BeaconServer.Configuration;
using BeaconServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeaconServer.Tests;

/// <summary>
/// The admin HTTP API checks who is asking before it reads a body, keeps the
/// player-facing routes for players on the server, and keeps its request pools
/// apart. Pure helpers are tested directly; the routes are tested against a real
/// listener on a free port (every request comes from 127.0.0.1, so the service is
/// told to treat loopback as a remote caller).
/// </summary>
[Collection("ChatFiles")]
public class HttpHardeningTests
{
    // ---------- pools ----------

    [Theory]
    [InlineData("GET", "/api/v1/health", false, 0, "Public")]
    [InlineData("GET", "/api/v1/chat/recent", false, 0, "Public")]
    [InlineData("GET", "/map/index.html", false, 0, "Public")]
    [InlineData("POST", "/api/v1/chat/player", true, 100, "Public")]
    [InlineData("POST", "/api/v1/map/position", true, 100, "Public")]
    [InlineData("OPTIONS", "/api/v1/snapshots", false, 0, "Public")]
    [InlineData("GET", "/api/v1/snapshots", false, 0, "Signed")]
    [InlineData("POST", "/api/v1/chat/say", true, 200, "Signed")]
    [InlineData("POST", "/api/v1/snapshots", true, 104857600, "Upload")]
    [InlineData("POST", "/api/v1/snapshots/import-restore", true, -1, "Upload")]
    public void Requests_are_sorted_into_separate_pools(string method, string path, bool hasBody, long length, string pool)
    {
        BeaconHttpService.ClassifyRequest(method, path, hasBody, length).ToString().Should().Be(pool);
    }

    // ---------- signatures ----------

    [Fact]
    public void Signature_headers_are_checked_before_anything_else()
    {
        var now = 1_800_000_000L;
        var sig = new string('a', 64);
        BeaconHttpService.SignatureHeadersPlausible(now.ToString(), sig, now, 300, out _).Should().BeTrue();
        BeaconHttpService.SignatureHeadersPlausible(null, sig, now, 300, out _).Should().BeFalse();
        BeaconHttpService.SignatureHeadersPlausible(now.ToString(), null, now, 300, out _).Should().BeFalse();
        BeaconHttpService.SignatureHeadersPlausible("abc", sig, now, 300, out _).Should().BeFalse();
        BeaconHttpService.SignatureHeadersPlausible((now - 301).ToString(), sig, now, 300, out _).Should().BeFalse();
        BeaconHttpService.SignatureHeadersPlausible(now.ToString(), "short", now, 300, out _).Should().BeFalse();
    }

    [Fact]
    public void Signature_covers_method_path_time_and_body_hash()
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("secret"));
        var sha = Sha("hello");
        var sig = Sign(key, "POST", "/api/v1/chat/say", 100, sha);
        BeaconHttpService.VerifySignature(key, "POST", "/api/v1/chat/say", 100, sig, sha).Should().BeTrue();
        BeaconHttpService.VerifySignature(key, "POST", "/api/v1/chat/say", 100, sig.ToUpperInvariant(), sha).Should().BeTrue();
        BeaconHttpService.VerifySignature(key, "POST", "/api/v1/chat/say", 100, sig, Sha("other")).Should().BeFalse();
        BeaconHttpService.VerifySignature(key, "POST", "/api/v1/chat/motd", 100, sig, sha).Should().BeFalse();
        BeaconHttpService.VerifySignature(Array.Empty<byte>(), "POST", "/api/v1/chat/say", 100, sig, sha).Should().BeFalse();
        BeaconHttpService.IsSha256Hex(sha).Should().BeTrue();
        BeaconHttpService.IsSha256Hex(sha.ToUpperInvariant()).Should().BeFalse();
        BeaconHttpService.IsSha256Hex("xyz").Should().BeFalse();
    }

    [Fact]
    public void Map_key_must_match_exactly()
    {
        BeaconHttpService.MapKeyMatches("", "anything").Should().BeFalse();
        BeaconHttpService.MapKeyMatches("k1", null).Should().BeFalse();
        BeaconHttpService.MapKeyMatches("k1", "k1").Should().BeTrue();
        BeaconHttpService.MapKeyMatches("k1", "k2").Should().BeFalse();
        BeaconHttpService.MapKeyMatches("k1", "k1x").Should().BeFalse();
    }

    // ---------- who is a player ----------

    private static PipeServerState.ConnectedPeer Peer(string addr, string id, string name)
    {
        PipeServerState.TryParsePeerIp(addr, out var ip).Should().BeTrue();
        return new PipeServerState.ConnectedPeer(addr, ip, id, name);
    }

    [Fact]
    public void Chat_sender_comes_from_the_connection_not_the_body()
    {
        var none = Array.Empty<PipeServerState.ConnectedPeer>();
        BeaconHttpService.ResolveChatSender(none, "Admin", isLocal: false).Should().BeNull();
        BeaconHttpService.ResolveChatSender(none, "Host", isLocal: true).Should().Be("Host");
        BeaconHttpService.ResolveChatSender(none, "", isLocal: true).Should().Be("Player");

        var one = new[] { Peer("5.6.7.8:2222", "765_a", "Alice") };
        BeaconHttpService.ResolveChatSender(one, "Admin", isLocal: false).Should().Be("Alice");
        BeaconHttpService.ResolveChatSender(one, null, isLocal: false).Should().Be("Alice");

        var two = new[] { Peer("5.6.7.8:2222", "765_a", "Alice"), Peer("5.6.7.8:3333", "765_b", "Bob") };
        BeaconHttpService.ResolveChatSender(two, "bob", isLocal: false).Should().Be("Bob");
        BeaconHttpService.ResolveChatSender(two, "Mallory", isLocal: false).Should().Be("Alice");
    }

    [Fact]
    public void Position_reports_are_filed_under_the_reporting_player()
    {
        var one = new[] { Peer("5.6.7.8:2222", "765_a", "Alice") };
        BeaconHttpService.ResolvePositionPeer(one, "Someone Else", "Someone Else")!.Name.Should().Be("Alice");

        var two = new[] { Peer("5.6.7.8:2222", "765_a", "Alice"), Peer("5.6.7.8:3333", "765_b", "Bob") };
        BeaconHttpService.ResolvePositionPeer(two, "765_b", null)!.Name.Should().Be("Bob");
        BeaconHttpService.ResolvePositionPeer(two, "Bob", "Bob")!.Id.Should().Be("765_b");
        BeaconHttpService.ResolvePositionPeer(two, "Mallory", "Mallory").Should().BeNull();
        BeaconHttpService.ResolvePositionPeer(Array.Empty<PipeServerState.ConnectedPeer>(), "x", "x").Should().BeNull();
    }

    [Fact]
    public void Connected_peers_are_found_by_address()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        state.NotePeer("5.6.7.8:2222", "765_a", "Alice");
        state.NotePeer("[::1]:7777", "765_l", "Local");
        state.NotePeer("not an address", "x", "Ghost");
        state.NotePeer("9.9.9.9:1", "x", "");

        state.PeersAt(IPAddress.Parse("5.6.7.8")).Select(p => p.Name).Should().Equal("Alice");
        state.PeersAt(IPAddress.Parse("::ffff:5.6.7.8")).Should().HaveCount(1);
        state.PeersAt(IPAddress.IPv6Loopback).Select(p => p.Name).Should().Equal("Local");
        state.PeersAt(IPAddress.Parse("9.9.9.9")).Should().BeEmpty();
        state.PeersAt(null).Should().BeEmpty();

        state.ForgetPeer("5.6.7.8:2222");
        state.PeersAt(IPAddress.Parse("5.6.7.8")).Should().BeEmpty();
        state.ForgetPeersNamed("local");
        state.PeersAt(IPAddress.IPv6Loopback).Should().BeEmpty();
    }

    [Fact]
    public async Task Log_tail_records_and_forgets_the_players_address()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-peer-tail-" + Guid.NewGuid().ToString("N"));
        var logDir = Path.Combine(root, "Saved", "Logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "Subnautica2.log");
        await File.WriteAllTextAsync(logPath, "");
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        var tail = new Sn2LogTailService(NullLogger<Sn2LogTailService>.Instance,
            Options.Create(new BeaconServerOptions { SnUserDir = root }), state);
        await tail.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(300);
            await File.AppendAllTextAsync(logPath, string.Join(Environment.NewLine, new[]
            {
                "[2026.05.22-21.00.35:371][265]LogNet: NotifyAcceptingConnection accepted from: 5.6.7.8:2222",
                "[2026.05.22-21.00.35:672][274]LogNet: Login request: ?Name=HumanGenome??SonarPlayerId=76561197966093888_A4E6F67EAD6BCF60?PlatformUserId=76561197966093888?PlatformProvider=STEAM userId: NULL:RYZEN platform: NULL",
                "[2026.05.22-21.00.36:662][303]LogNet: Join succeeded: HumanGenome",
                "",
            }));
            await WaitUntilAsync(() => state.PeersAt(IPAddress.Parse("5.6.7.8")).Count == 1, TimeSpan.FromSeconds(6));
            var peer = state.PeersAt(IPAddress.Parse("5.6.7.8")).Single();
            peer.Name.Should().Be("HumanGenome");
            peer.Id.Should().Be("76561197966093888_A4E6F67EAD6BCF60");

            await File.AppendAllTextAsync(logPath,
                "[2026.05.22-21.10.00:000][900]LogNet: UNetDriver::RemoveClientConnection - Removed address 5.6.7.8:2222" + Environment.NewLine);
            await WaitUntilAsync(() => state.PeersAt(IPAddress.Parse("5.6.7.8")).Count == 0, TimeSpan.FromSeconds(6));
        }
        finally
        {
            await tail.StopAsync(CancellationToken.None);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ---------- the routes, on a real listener ----------

    private sealed class Harness : IAsyncDisposable
    {
        public readonly BeaconHttpService Http;
        public readonly PipeServerState State;
        public readonly ChatService Chat;
        public readonly int Port;
        public readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("secret"));
        private readonly string _dir;

        public Harness(bool mapPublic = false, string mapKey = "k1", Func<DateTimeOffset>? clock = null)
        {
            _dir = Path.Combine(Path.GetTempPath(), "beacon-http-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_dir, "data"));
            Port = FreePort();
            var opts = new BeaconServerOptions
            {
                HttpPort = Port,
                RconPassword = "secret",
                SaveDir = Path.Combine(_dir, "Saves"),
                HmacKeyPath = Path.Combine(_dir, "data", "hmac.key"),
                SnInstallRoot = "",
                SnUserDir = "",
                Map = new MapOptions { Enabled = true, Public = mapPublic, AccessKey = mapKey },
            };
            var o = Options.Create(opts);
            var monitor = new Monitor<BeaconServerOptions>(opts);
            State = clock is null
                ? new PipeServerState(NullLogger<PipeServerState>.Instance)
                : new PipeServerState(NullLogger<PipeServerState>.Instance, clock);
            var coordinator = new Sn2RestartCoordinator(NullLogger<Sn2RestartCoordinator>.Instance, o);
            var saves = new SaveOrchestratorService(NullLogger<SaveOrchestratorService>.Instance, o, State, coordinator);
            var dispatch = new CommandDispatchService(NullLogger<CommandDispatchService>.Instance, monitor);
            Chat = new ChatService(NullLogger<ChatService>.Instance, monitor, dispatch);
            Http = new BeaconHttpService(NullLogger<BeaconHttpService>.Instance, o,
                new ConfigurationBuilder().Build(), saves, State, new InstanceIdentityProvider(o), Chat)
            {
                LoopbackIsLocal = false,
            };
        }

        public async Task StartAsync()
        {
            await Http.StartAsync(CancellationToken.None);
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until)
            {
                try { await probe.GetAsync($"http://127.0.0.1:{Port}/api/v1/players"); return; }
                catch { await Task.Delay(100); }
            }
            throw new InvalidOperationException("listener did not start");
        }

        public HttpRequestMessage Signed(HttpMethod method, string path, byte[]? body = null, bool declareSha = true, string? declaredSha = null)
        {
            body ??= Array.Empty<byte>();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sha = declaredSha ?? Sha(body);
            var req = new HttpRequestMessage(method, $"http://127.0.0.1:{Port}{path}");
            req.Headers.Add("X-Beacon-Timestamp", ts.ToString());
            req.Headers.Add("X-Beacon-Signature", Sign(Key, method.Method, path, ts, sha));
            if (body.Length > 0)
            {
                if (declareSha) req.Headers.Add("X-Beacon-Content-Sha256", sha);
                req.Content = new ByteArrayContent(body);
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            return req;
        }

        public async ValueTask DisposeAsync()
        {
            try { await Http.StopAsync(CancellationToken.None); } catch { }
            Http.Dispose();
            foreach (var f in new[] { "chat-outbound.json", "chat-inbound.json", "chat-history.json", "motd.txt" })
                try { File.Delete(Path.Combine(AppContext.BaseDirectory, f)); } catch { }
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Chat_history_is_for_players_and_admins_only()
    {
        await using var h = new Harness();
        await h.StartAsync();
        using var http = new HttpClient();
        var url = $"http://127.0.0.1:{h.Port}/api/v1/chat/recent";

        (await http.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await http.SendAsync(h.Signed(HttpMethod.Get, "/api/v1/chat/recent"))).StatusCode.Should().Be(HttpStatusCode.OK);

        h.State.NotePeer("127.0.0.1:5555", "765_a", "Alice");
        (await http.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK);

        // MOTD and the player list stay public
        (await http.GetAsync($"http://127.0.0.1:{h.Port}/api/v1/chat/motd")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await http.GetAsync($"http://127.0.0.1:{h.Port}/api/v1/players")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Player_chat_needs_a_connected_player_and_carries_their_name()
    {
        await using var h = new Harness();
        await h.StartAsync();
        using var http = new HttpClient();
        var url = $"http://127.0.0.1:{h.Port}/api/v1/chat/player";
        var body = new StringContent("{\"sender\":\"Server\",\"msg\":\"restarting now\"}", Encoding.UTF8, "application/json");

        (await http.PostAsync(url, body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        h.Chat.GetRecent(0, 100).Should().BeEmpty();

        h.State.NotePeer("127.0.0.1:5555", "765_a", "Alice");
        var ok = await http.PostAsync(url, new StringContent("{\"sender\":\"Server\",\"msg\":\"hello\"}", Encoding.UTF8, "application/json"));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var last = h.Chat.GetRecent(0, 100).Last();
        last.Sender.Should().Be("Alice");
        last.Msg.Should().Be("hello");
    }

    [Fact]
    public async Task Map_positions_need_a_connected_player_and_are_filed_under_them()
    {
        await using var h = new Harness();
        await h.StartAsync();
        using var http = new HttpClient();
        var url = $"http://127.0.0.1:{h.Port}/api/v1/map/position";
        string Pos(string id) => "{\"id\":\"" + id + "\",\"name\":\"" + id + "\",\"x\":1,\"y\":2,\"z\":3}";

        (await http.PostAsync(url, new StringContent(Pos("Fake"), Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        h.State.RecentClientPositions(TimeSpan.FromMinutes(1)).Should().BeEmpty();

        h.State.NotePeer("127.0.0.1:5555", "765_a", "Alice");
        (await http.PostAsync(url, new StringContent(Pos("Fake"), Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var p = h.State.RecentClientPositions(TimeSpan.FromMinutes(1)).Single();
        p.Id.Should().Be("765_a");
        p.Name.Should().Be("Alice");
    }

    [Fact]
    public async Task Live_map_is_private_unless_player_key_signature_or_public()
    {
        await using var h = new Harness();
        await h.StartAsync();
        using var http = new HttpClient();
        var url = $"http://127.0.0.1:{h.Port}/api/v1/map/state";

        (await http.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var keyed = new HttpRequestMessage(HttpMethod.Get, url);
        keyed.Headers.Add("X-Beacon-Map-Key", "k1");
        (await http.SendAsync(keyed)).StatusCode.Should().Be(HttpStatusCode.OK);

        var wrong = new HttpRequestMessage(HttpMethod.Get, url);
        wrong.Headers.Add("X-Beacon-Map-Key", "k2");
        (await http.SendAsync(wrong)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await http.SendAsync(h.Signed(HttpMethod.Get, "/api/v1/map/state"))).StatusCode.Should().Be(HttpStatusCode.OK);

        h.State.NotePeer("127.0.0.1:5555", "765_a", "Alice");
        (await http.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK);

        await using var pub = new Harness(mapPublic: true, mapKey: "");
        await pub.StartAsync();
        (await http.GetAsync($"http://127.0.0.1:{pub.Port}/api/v1/map/state")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unsigned_upload_is_refused_before_its_body_is_read()
    {
        await using var h = new Harness();
        await h.StartAsync();
        var uploads = Path.Combine(Path.GetTempPath(), "beacon-uploads");
        var before = Directory.Exists(uploads) ? Directory.GetFiles(uploads).Length : 0;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Made-up signature, a 100 MB declared body, then only 1 KB sent: the answer must
        // come back without waiting for (or storing) the rest.
        foreach (var withHash in new[] { true, false })
        {
            var head = "POST /api/v1/snapshots HTTP/1.1\r\n"
                + $"Host: 127.0.0.1:{h.Port}\r\n"
                + "Content-Length: 104857600\r\n"
                + $"X-Beacon-Timestamp: {now}\r\n"
                + $"X-Beacon-Signature: {new string('0', 64)}\r\n"
                + (withHash ? $"X-Beacon-Content-Sha256: {new string('0', 64)}\r\n" : "")
                + "\r\n";
            var status = await RawStatusAsync(h.Port, head, new byte[1024]);
            status.Should().Be(401);
        }
        var after = Directory.Exists(uploads) ? Directory.GetFiles(uploads).Length : 0;
        after.Should().BeLessThanOrEqualTo(before);

        // A body with no declared length is refused outright.
        var chunked = "POST /api/v1/snapshots HTTP/1.1\r\n"
            + $"Host: 127.0.0.1:{h.Port}\r\n"
            + "Transfer-Encoding: chunked\r\n"
            + $"X-Beacon-Timestamp: {now}\r\n"
            + $"X-Beacon-Signature: {new string('0', 64)}\r\n"
            + "\r\n";
        (await RawStatusAsync(h.Port, chunked, Encoding.ASCII.GetBytes("400\r\n"))).Should().BeOneOf(401, 411);
    }

    [Fact]
    public async Task Signed_calls_still_work_small_and_large()
    {
        await using var h = new Harness();
        await h.StartAsync();
        using var http = new HttpClient();

        var small = Encoding.UTF8.GetBytes("{\"msg\":\"hi there\"}");
        (await http.SendAsync(h.Signed(HttpMethod.Post, "/api/v1/chat/say", small))).StatusCode.Should().Be(HttpStatusCode.OK);
        // an older app sends no content hash on a small body (bodies differ so the
        // signatures differ: the same signature twice is a replay)
        var small2 = Encoding.UTF8.GetBytes("{\"msg\":\"hi again\"}");
        (await http.SendAsync(h.Signed(HttpMethod.Post, "/api/v1/chat/say", small2, declareSha: false))).StatusCode.Should().Be(HttpStatusCode.OK);

        // over 64 KB: needs the declared hash, and the body must match it
        byte[] Big(char c) => Encoding.UTF8.GetBytes("{\"msg\":\"" + new string(c, 100_000) + "\"}");
        (await http.SendAsync(h.Signed(HttpMethod.Post, "/api/v1/chat/say", Big('a')))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await http.SendAsync(h.Signed(HttpMethod.Post, "/api/v1/chat/say", Big('b'), declareSha: false))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var liar = h.Signed(HttpMethod.Post, "/api/v1/chat/say", Big('c'), declaredSha: Sha("something else"));
        (await http.SendAsync(liar)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // a replayed signature is refused
        var once = h.Signed(HttpMethod.Get, "/api/v1/info");
        var again = new HttpRequestMessage(HttpMethod.Get, once.RequestUri);
        foreach (var hd in once.Headers) again.Headers.TryAddWithoutValidation(hd.Key, hd.Value);
        (await http.SendAsync(once)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await http.SendAsync(again)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------- chat gate (players on, no game connection read) ----------

    private sealed class FakeClock
    {
        public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        public void Advance(TimeSpan t) => Now += t;
    }

    [Fact]
    public void Chat_gate_degrades_only_after_ten_minutes_of_players_with_nothing_read()
    {
        var clock = new FakeClock();
        var gate = new ChatGateMonitor(() => clock.Now);
        var start = clock.Now;

        gate.Observe(2, 0).Should().Be(ChatGateMonitor.Change.None);
        clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        gate.Observe(2, 0).Should().Be(ChatGateMonitor.Change.None);
        gate.State.Should().Be("ok");
        gate.DegradedSinceUnix.Should().BeNull();

        clock.Advance(TimeSpan.FromSeconds(1));
        gate.Observe(2, 0).Should().Be(ChatGateMonitor.Change.Degraded);
        gate.State.Should().Be("no_connections_seen");
        gate.DegradedSinceUnix.Should().Be(start.ToUnixTimeSeconds());

        // once per episode
        clock.Advance(TimeSpan.FromMinutes(5));
        gate.Observe(2, 0).Should().Be(ChatGateMonitor.Change.None);
        gate.Observe(0, 0).Should().Be(ChatGateMonitor.Change.None);
        gate.State.Should().Be("no_connections_seen");

        // the first parsed connection clears it
        gate.Observe(2, 1).Should().Be(ChatGateMonitor.Change.Recovered);
        gate.State.Should().Be("ok");
        gate.DegradedSinceUnix.Should().BeNull();
        gate.Observe(2, 1).Should().Be(ChatGateMonitor.Change.None);
    }

    [Fact]
    public void Chat_gate_never_degrades_with_nobody_on_and_restarts_its_clock()
    {
        var clock = new FakeClock();
        var gate = new ChatGateMonitor(() => clock.Now);
        for (var i = 0; i < 12; i++)
        {
            gate.Observe(0, 0).Should().Be(ChatGateMonitor.Change.None);
            clock.Advance(TimeSpan.FromMinutes(5));
        }
        gate.State.Should().Be("ok");

        gate.Observe(1, 0);
        clock.Advance(TimeSpan.FromMinutes(6));
        gate.Observe(0, 0);                       // everyone left
        clock.Advance(TimeSpan.FromMinutes(1));
        gate.Observe(1, 0);                       // back: the clock starts again
        clock.Advance(TimeSpan.FromMinutes(6));
        gate.Observe(1, 0).Should().Be(ChatGateMonitor.Change.None);
        gate.State.Should().Be("ok");
        clock.Advance(TimeSpan.FromMinutes(4));
        gate.Observe(1, 0).Should().Be(ChatGateMonitor.Change.Degraded);
    }

    [Fact]
    public void Chat_gate_counts_host_reported_players_not_the_host_itself()
    {
        var clock = new FakeClock();
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance, () => clock.Now);
        var host = new Beacon.Protocol.PlayerSnapshot("server-1a2b3c4d", "server-1a2b3c4d", 0, 0, 0);
        state.SetPlayers(new[] { host });
        clock.Advance(TimeSpan.FromMinutes(30));
        state.SetPlayers(new[] { host });
        state.ChatGate.State.Should().Be("ok");

        var alice = new Beacon.Protocol.PlayerSnapshot("76561197966093888_A4E6", "Alice", 0, 0, 0);
        state.SetPlayers(new[] { host, alice });
        clock.Advance(TimeSpan.FromMinutes(10));
        state.SetPlayers(new[] { host, alice });
        state.ChatGate.State.Should().Be("no_connections_seen");

        state.NotePeer("5.6.7.8:2222", "76561197966093888_A4E6", "Alice");
        state.ChatGate.State.Should().Be("ok");
    }

    [Fact]
    public async Task Health_reports_the_chat_gate()
    {
        var clock = new FakeClock();
        await using var h = new Harness(clock: () => clock.Now);
        await h.StartAsync();
        using var http = new HttpClient();
        var url = $"http://127.0.0.1:{h.Port}/api/v1/health";

        async Task<System.Text.Json.JsonElement> Health()
        {
            var resp = await http.GetAsync(url);   // 503 without a game plugin, body still complete
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.Clone();
        }

        var first = await Health();
        first.GetProperty("chat_gate").GetString().Should().Be("ok");
        first.GetProperty("chat_gate_since_unix").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);

        var start = clock.Now;
        var alice = new Beacon.Protocol.PlayerSnapshot("76561197966093888_A4E6", "Alice", 0, 0, 0);
        h.State.SetPlayers(new[] { alice });
        clock.Advance(TimeSpan.FromMinutes(10));
        var degraded = await Health();
        degraded.GetProperty("chat_gate").GetString().Should().Be("no_connections_seen");
        degraded.GetProperty("chat_gate_since_unix").GetInt64().Should().Be(start.ToUnixTimeSeconds());

        h.State.NotePeer("127.0.0.1:5555", "76561197966093888_A4E6", "Alice");
        var cleared = await Health();
        cleared.GetProperty("chat_gate").GetString().Should().Be("ok");
        cleared.GetProperty("chat_gate_since_unix").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Lab_switch_leaves_the_connection_list_empty()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-peer-lab-" + Guid.NewGuid().ToString("N"));
        var logDir = Path.Combine(root, "Saved", "Logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "Subnautica2.log");
        await File.WriteAllTextAsync(logPath, "");
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        var tail = new Sn2LogTailService(NullLogger<Sn2LogTailService>.Instance,
            Options.Create(new BeaconServerOptions { SnUserDir = root, SimulateUnreadableGameLog = true }), state);
        await tail.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(300);
            await File.AppendAllTextAsync(logPath, string.Join(Environment.NewLine, new[]
            {
                "[2026.05.22-21.00.35:371][265]LogNet: NotifyAcceptingConnection accepted from: 5.6.7.8:2222",
                "[2026.05.22-21.00.35:672][274]LogNet: Login request: ?Name=HumanGenome??SonarPlayerId=76561197966093888_A4E6F67EAD6BCF60?PlatformUserId=76561197966093888?PlatformProvider=STEAM userId: NULL:RYZEN platform: NULL",
                "[2026.05.22-21.00.36:662][303]LogNet: Join succeeded: HumanGenome",
                "",
            }));
            await WaitUntilAsync(() => state.EffectivePlayerCount == 1, TimeSpan.FromSeconds(6));
            state.PeersAt(IPAddress.Parse("5.6.7.8")).Should().BeEmpty();
        }
        finally
        {
            await tail.StopAsync(CancellationToken.None);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ---------- helpers ----------

    private static async Task<int> RawStatusAsync(int port, string head, byte[] partialBody)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var bytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(bytes);
        await stream.WriteAsync(partialBody);
        await stream.FlushAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buf = new byte[256];
        var n = await stream.ReadAsync(buf, cts.Token);
        var line = Encoding.ASCII.GetString(buf, 0, n);
        // "HTTP/1.1 401 Unauthorized"
        var parts = line.Split(' ', 3);
        return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : -1;
    }

    private static string Sha(string s) => Sha(Encoding.UTF8.GetBytes(s));
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string Sign(byte[] key, string method, string path, long ts, string sha) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{method}\n{path}\n{ts}\n{sha}"))).ToLowerInvariant();

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> cond, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (cond()) return;
            await Task.Delay(100);
        }
        cond().Should().BeTrue("condition was not met in time");
    }

    private sealed class Monitor<T> : IOptionsMonitor<T>
    {
        public Monitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => new Nothing();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}

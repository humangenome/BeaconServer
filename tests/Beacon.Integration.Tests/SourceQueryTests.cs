using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Beacon.SourceQuery;
using FluentAssertions;
using Xunit;

namespace Beacon.Integration.Tests;

public class SourceQueryTests
{
    [Fact]
    public async Task A2S_INFO_answers_with_a_challenge_first_then_the_info()
    {
        var info = new ServerInfoSnapshot("Beacon Test", "Awake", "Beacon", "Subnautica 2",
            1962700, 0, 8, false, false, "0.1.0", 27015, "beacon,sn2");
        using var server = new SourceQueryServer(0,
            () => info,
            () => Array.Empty<PlayerInfoEntry>(),
            () => Array.Empty<KeyValuePair<string, string>>());
        await server.StartAsync(CancellationToken.None);

        try
        {
            using var client = new UdpClient(0);
            var req = InfoRequest(null);
            await client.SendAsync(req, req.Length, "127.0.0.1", server.BoundPort);

            var first = await ReceiveWithin(client, 2000);
            first.Buffer.Length.Should().Be(9);
            first.Buffer[4].Should().Be(0x41, "an info request without a challenge gets only the challenge");
            var challenge = BinaryPrimitives.ReadInt32LittleEndian(first.Buffer.AsSpan(5, 4));

            var withChallenge = InfoRequest(challenge);
            await client.SendAsync(withChallenge, withChallenge.Length, "127.0.0.1", server.BoundPort);
            var res = await ReceiveWithin(client, 2000);

            BinaryPrimitives.ReadInt32LittleEndian(res.Buffer.AsSpan(0, 4)).Should().Be(-1);
            res.Buffer[4].Should().Be(0x49);
            res.Buffer[5].Should().Be(17, "protocol version");

            // First C-string after protocol version byte is server name.
            var i = 6;
            var nameEnd = Array.IndexOf<byte>(res.Buffer, 0, i);
            Encoding.UTF8.GetString(res.Buffer, i, nameEnd - i).Should().Be("Beacon Test");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public void A2S_INFO_from_an_unproven_address_never_gets_more_bytes_than_it_sent()
    {
        var info = new ServerInfoSnapshot(new string('x', 60), "Awake", "Beacon", "Subnautica 2",
            1962700, 3, 8, false, false, "beacon-0.3.142/sn2-Subnautica2-CL-131439", 27015, "beacon,sn2,beacon=0.3.142");
        using var server = new SourceQueryServer(0, () => info,
            () => Array.Empty<PlayerInfoEntry>(), () => Array.Empty<KeyValuePair<string, string>>());
        var spoofed = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 27005);

        foreach (var req in new[] { InfoRequest(null), InfoRequest(0), InfoRequest(12345) })
        {
            var reply = server.HandleQuery(spoofed, req);
            reply.Should().NotBeNull();
            reply![4].Should().Be(0x41);
            reply.Length.Should().BeLessThanOrEqualTo(req.Length, "a reflector multiplies what it is sent");
        }
        server.HandleQuery(spoofed, Header(0x54)).Should().BeNull("a bare 5-byte info header is not a Source query");
    }

    [Fact]
    public void A2S_INFO_challenge_counts_only_for_the_address_it_was_given_to()
    {
        var info = new ServerInfoSnapshot("X", "Y", "Beacon", "SN2", 0L, 0, 8, false, false, "0", 27015, "");
        using var server = new SourceQueryServer(0, () => info,
            () => Array.Empty<PlayerInfoEntry>(), () => Array.Empty<KeyValuePair<string, string>>());
        var a = new IPEndPoint(IPAddress.Parse("203.0.113.7"), 27005);
        var b = new IPEndPoint(IPAddress.Parse("203.0.113.8"), 27005);

        var challenge = BinaryPrimitives.ReadInt32LittleEndian(server.HandleQuery(a, InfoRequest(null))!.AsSpan(5, 4));
        server.HandleQuery(b, InfoRequest(challenge))![4].Should().Be(0x41, "another address must prove itself");
        server.HandleQuery(a, InfoRequest(challenge))![4].Should().Be(0x49);
        server.HandleQuery(a, InfoRequest(challenge))![4].Should().Be(0x49, "a proven address keeps its challenge");
    }

    [Fact]
    public void Each_address_gets_a_limited_number_of_replies_per_window()
    {
        var info = new ServerInfoSnapshot("X", "Y", "Beacon", "SN2", 0L, 0, 8, false, false, "0", 27015, "");
        long now = 1_000_000;
        using var server = new SourceQueryServer(0, () => info,
            () => Array.Empty<PlayerInfoEntry>(), () => Array.Empty<KeyValuePair<string, string>>(),
            availabilityProvider: null, clock: () => now, ignoreClosedPortReports: true);
        var flooder = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40000);
        var other = new IPEndPoint(IPAddress.Parse("198.51.100.4"), 27005);

        for (var i = 0; i < SourceQueryServer.MaxRepliesPerWindow; i++)
            server.HandleQuery(new IPEndPoint(flooder.Address, 40000 + i), InfoRequest(null)).Should().NotBeNull($"reply {i + 1}");
        server.HandleQuery(flooder, InfoRequest(null)).Should().BeNull("over the budget, from any port of that address");
        server.HandleQuery(other, InfoRequest(null)).Should().NotBeNull("another address has its own budget");

        now += (long)SourceQueryServer.ReplyWindow.TotalMilliseconds;
        server.HandleQuery(flooder, InfoRequest(null)).Should().NotBeNull("a new window, a new budget");
    }

    [Fact]
    public async Task Replies_that_land_on_closed_ports_never_make_the_responder_miss_a_real_query()
    {
        var info = new ServerInfoSnapshot("Beacon Test", "Awake", "Beacon", "Subnautica 2",
            1962700, 0, 8, false, false, "0.1.0", 27015, "beacon,sn2");
        using var server = new SourceQueryServer(0, () => info,
            () => Array.Empty<PlayerInfoEntry>(), () => Array.Empty<KeyValuePair<string, string>>());
        await server.StartAsync(CancellationToken.None);
        try
        {
            // 40 askers that close at once: each challenge reply lands on a closed port (on Windows the ICMP
            // answer would come back as a "connection reset" on a later receive, if the reports were on).
            for (var i = 0; i < 40; i++)
            {
                using var gone = new UdpClient(0);
                var req = InfoRequest(null);
                await gone.SendAsync(req, req.Length, "127.0.0.1", server.BoundPort);
            }

            using var client = new UdpClient(0);
            var first = InfoRequest(null);
            await client.SendAsync(first, first.Length, "127.0.0.1", server.BoundPort);
            var challenge = await ReceiveWithin(client, 30_000);
            challenge.Buffer[4].Should().Be(0x41);
            var second = InfoRequest(BinaryPrimitives.ReadInt32LittleEndian(challenge.Buffer.AsSpan(5, 4)));
            await client.SendAsync(second, second.Length, "127.0.0.1", server.BoundPort);
            (await ReceiveWithin(client, 30_000)).Buffer[4].Should().Be(0x49);

            if (OperatingSystem.IsWindows())
                server.ClosedPortReports.Should().Be(0, "the reports are switched off on the socket");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task The_responder_goes_on_past_closed_port_reports_that_reach_it()
    {
        // The reports are left on here (the public constructor turns them off), so on Windows each reply that
        // lands on a closed port comes back as a "connection reset" on a later receive. The loop must skip
        // them and still answer a real query. Same check as Cauldron's 997c3dd second test.
        var info = new ServerInfoSnapshot("Beacon Test", "Awake", "Beacon", "Subnautica 2",
            1962700, 0, 8, false, false, "0.1.0", 27015, "beacon,sn2");
        using var server = new SourceQueryServer(0, () => info,
            () => Array.Empty<PlayerInfoEntry>(), () => Array.Empty<KeyValuePair<string, string>>(),
            availabilityProvider: null, clock: null, ignoreClosedPortReports: false);
        await server.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 40; i++)
            {
                using var gone = new UdpClient(0);
                var req = InfoRequest(null);
                await gone.SendAsync(req, req.Length, "127.0.0.1", server.BoundPort);
            }

            if (OperatingSystem.IsWindows())
            {
                // Wait until the error path has really run before the real query goes in.
                var until = DateTime.UtcNow.AddSeconds(30);
                while (server.ClosedPortReports == 0 && DateTime.UtcNow < until) await Task.Delay(20);
                server.ClosedPortReports.Should().BeGreaterThan(0, "with the reports left on, Windows reports replies to closed ports");
            }

            using var client = new UdpClient(0);
            var first = InfoRequest(null);
            await client.SendAsync(first, first.Length, "127.0.0.1", server.BoundPort);
            var challenge = await ReceiveWithin(client, 30_000);
            challenge.Buffer[4].Should().Be(0x41);
            var second = InfoRequest(BinaryPrimitives.ReadInt32LittleEndian(challenge.Buffer.AsSpan(5, 4)));
            await client.SendAsync(second, second.Length, "127.0.0.1", server.BoundPort);
            (await ReceiveWithin(client, 30_000)).Buffer[4].Should().Be(0x49);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public void Challenges_come_from_the_cryptographic_generator_never_System_Random()
    {
        // Nothing in the responder holds a System.Random (a seeded generator lets one address work out the next
        // challenge another address gets from the ones it was given).
        var fields = typeof(SourceQueryServer).GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        fields.Should().NotContain(f => typeof(Random).IsAssignableFrom(f.FieldType), "the challenge must not come from System.Random");

        // The one method that makes a challenge calls RandomNumberGenerator, read from its compiled body.
        var make = typeof(SourceQueryServer).GetMethod("NewChallenge", BindingFlags.Static | BindingFlags.NonPublic)!;
        make.Should().NotBeNull();
        CalledMethods(make).Should().Contain(m => m.DeclaringType == typeof(RandomNumberGenerator),
            "NewChallenge must draw from the cryptographic generator");
        CalledMethods(make).Should().NotContain(m => m.DeclaringType == typeof(Random));

        // And the challenge a client is given is that method's: 2,000 replies, none -1, practically all different.
        var info = new ServerInfoSnapshot("X", "Y", "Beacon", "SN2", 0L, 0, 8, false, false, "0", 27015, "");
        using var server = new SourceQueryServer(0, () => info,
            () => Array.Empty<PlayerInfoEntry>(), () => Array.Empty<KeyValuePair<string, string>>());
        var seen = new HashSet<int>();
        for (var i = 0; i < 2000; i++)
        {
            var from = new IPEndPoint(new IPAddress(new byte[] { 10, (byte)(i >> 16), (byte)(i >> 8), (byte)i }), 27005);
            var reply = server.HandleQuery(from, InfoRequest(null))!;
            var challenge = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(5, 4));
            challenge.Should().NotBe(-1);
            seen.Add(challenge);
        }
        seen.Count.Should().BeGreaterThan(1990);
    }

    // The methods a method's compiled body calls (call / callvirt / newobj tokens resolved against its module).
    private static List<MethodBase> CalledMethods(MethodInfo method)
    {
        var il = method.GetMethodBody()!.GetILAsByteArray()!;
        var found = new List<MethodBase>();
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (0x28 or 0x6F or 0x73)) continue;
            var token = BitConverter.ToInt32(il, i + 1);
            try { if (method.Module.ResolveMethod(token) is { } m) found.Add(m); }
            catch (ArgumentException) { }
            catch (BadImageFormatException) { }
        }
        return found;
    }

    private static byte[] Header(byte h)
    {
        var req = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(req.AsSpan(0, 4), -1);
        req[4] = h;
        return req;
    }

    private static byte[] InfoRequest(int? challenge)
    {
        var text = Encoding.ASCII.GetBytes("Source Engine Query\0");
        var req = new byte[5 + text.Length + (challenge is null ? 0 : 4)];
        BinaryPrimitives.WriteInt32LittleEndian(req.AsSpan(0, 4), -1);
        req[4] = 0x54;
        text.CopyTo(req, 5);
        if (challenge is int c) BinaryPrimitives.WriteInt32LittleEndian(req.AsSpan(5 + text.Length, 4), c);
        return req;
    }

    private static async Task<UdpReceiveResult> ReceiveWithin(UdpClient client, int ms)
    {
        var receiveTask = client.ReceiveAsync();
        var done = await Task.WhenAny(receiveTask, Task.Delay(ms));
        done.Should().Be((Task)receiveTask, $"server should respond within {ms} ms");
        return await receiveTask;
    }

    [Fact]
    public async Task A2S_INFO_drops_when_unavailable()
    {
        var info = new ServerInfoSnapshot("Beacon Test", "Awake", "Beacon", "Subnautica 2",
            1962700, 0, 8, false, false, "0.1.0", 27015, "beacon,sn2");
        using var server = new SourceQueryServer(0,
            () => info,
            () => Array.Empty<PlayerInfoEntry>(),
            () => Array.Empty<KeyValuePair<string, string>>(),
            () => false);
        await server.StartAsync(CancellationToken.None);

        try
        {
            using var client = new UdpClient(0);
            var req = new byte[29];
            BinaryPrimitives.WriteInt32LittleEndian(req.AsSpan(0, 4), -1);
            req[4] = 0x54;
            Encoding.ASCII.GetBytes("Source Engine Query\0").CopyTo(req, 5);
            await client.SendAsync(req, req.Length, "127.0.0.1", server.BoundPort);

            var receiveTask = client.ReceiveAsync();
            var done = await Task.WhenAny(receiveTask, Task.Delay(300));
            done.Should().NotBe((Task)receiveTask, "unavailable BeaconServer should look offline to A2S callers");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task A2S_PLAYER_requires_challenge_then_returns_list()
    {
        var info = new ServerInfoSnapshot("X", "Y", "Beacon", "SN2", 0L, 1, 8, false, false, "0", 27015, "");
        var players = new List<PlayerInfoEntry> { new("alice", 0, 12.5f) };
        using var server = new SourceQueryServer(0,
            () => info, () => players, () => Array.Empty<KeyValuePair<string, string>>());
        await server.StartAsync(CancellationToken.None);

        try
        {
            using var client = new UdpClient(0);
            // Step 1: get challenge
            var req = new byte[9];
            BinaryPrimitives.WriteInt32LittleEndian(req.AsSpan(0, 4), -1);
            req[4] = 0x55;
            BinaryPrimitives.WriteInt32LittleEndian(req.AsSpan(5, 4), -1);
            await client.SendAsync(req, req.Length, "127.0.0.1", server.BoundPort);

            client.Client.ReceiveTimeout = 2000;
            var first = await client.ReceiveAsync();
            first.Buffer[4].Should().Be(0x41);
            var challenge = BinaryPrimitives.ReadInt32LittleEndian(first.Buffer.AsSpan(5, 4));

            // Step 2: real query
            BinaryPrimitives.WriteInt32LittleEndian(req.AsSpan(5, 4), challenge);
            await client.SendAsync(req, req.Length, "127.0.0.1", server.BoundPort);
            var second = await client.ReceiveAsync();
            second.Buffer[4].Should().Be(0x44);
            second.Buffer[5].Should().Be(1, "one player");

            // index byte (0), then null-terminated name "alice"
            var nameStart = 7;
            var nameEnd = Array.IndexOf<byte>(second.Buffer, 0, nameStart);
            Encoding.UTF8.GetString(second.Buffer, nameStart, nameEnd - nameStart).Should().Be("alice");
        }
        finally
        {
            await server.StopAsync();
        }
    }
}

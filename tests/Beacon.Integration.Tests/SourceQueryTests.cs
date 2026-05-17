using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Beacon.SourceQuery;
using FluentAssertions;
using Xunit;

namespace Beacon.Integration.Tests;

public class SourceQueryTests
{
    [Fact]
    public async Task A2S_INFO_returns_valid_reply()
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
            var req = new byte[29];
            BinaryPrimitives.WriteInt32LittleEndian(req.AsSpan(0, 4), -1);
            req[4] = 0x54;
            Encoding.ASCII.GetBytes("Source Engine Query\0").CopyTo(req, 5);
            await client.SendAsync(req, req.Length, "127.0.0.1", server.BoundPort);

            client.Client.ReceiveTimeout = 2000;
            var receiveTask = client.ReceiveAsync();
            var done = await Task.WhenAny(receiveTask, Task.Delay(2000));
            done.Should().Be((Task)receiveTask, "server should respond within 2s");
            var res = await receiveTask;

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

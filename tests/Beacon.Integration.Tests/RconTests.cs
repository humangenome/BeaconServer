using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Beacon.Rcon;
using FluentAssertions;
using Xunit;

namespace Beacon.Integration.Tests;

public class RconTests
{
    [Fact]
    public async Task Auth_then_command_round_trips()
    {
        using var server = new RconServer(0, "secret",
            cmd => Task.FromResult(cmd == "status" ? "ok" : "?"));
        server.Start(CancellationToken.None);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.BoundPort);
            var stream = client.GetStream();

            await WritePacketAsync(stream, 1, 3, "secret");
            // First reply: type=0 empty body
            var first = await ReadPacketAsync(stream);
            first.Should().NotBeNull();
            first!.Value.type.Should().Be(0);

            // Second reply: type=2 with id == request id == 1 = success
            var second = await ReadPacketAsync(stream);
            second.Should().NotBeNull();
            second!.Value.type.Should().Be(2);
            second.Value.id.Should().Be(1);

            // Run a command
            await WritePacketAsync(stream, 7, 2, "status");
            var resp = await ReadPacketAsync(stream);
            resp.Should().NotBeNull();
            resp!.Value.id.Should().Be(7);
            resp.Value.body.Should().Be("ok");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task Wrong_password_closes_connection_with_neg1()
    {
        using var server = new RconServer(0, "secret", _ => Task.FromResult(""));
        server.Start(CancellationToken.None);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", server.BoundPort);
            var stream = client.GetStream();

            await WritePacketAsync(stream, 1, 3, "wrong");
            var first = await ReadPacketAsync(stream);
            first.Should().NotBeNull();
            first!.Value.type.Should().Be(0);
            var second = await ReadPacketAsync(stream);
            second.Should().NotBeNull();
            second!.Value.type.Should().Be(2);
            second.Value.id.Should().Be(-1, "rejected auth uses id=-1");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    private static async Task WritePacketAsync(NetworkStream s, int id, int type, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var size = 4 + 4 + bodyBytes.Length + 2;
        var buf = new byte[4 + size];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), size);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), id);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), type);
        Buffer.BlockCopy(bodyBytes, 0, buf, 12, bodyBytes.Length);
        await s.WriteAsync(buf);
        await s.FlushAsync();
    }

    private static async Task<(int id, int type, string body)?> ReadPacketAsync(NetworkStream s)
    {
        var sizeBuf = new byte[4];
        if (!await ReadExactAsync(s, sizeBuf)) return null;
        var size = BinaryPrimitives.ReadInt32LittleEndian(sizeBuf);
        var rest = new byte[size];
        if (!await ReadExactAsync(s, rest)) return null;
        var id = BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(0, 4));
        var type = BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(4, 4));
        var bodyEnd = Array.IndexOf<byte>(rest, 0, 8);
        if (bodyEnd < 0) bodyEnd = rest.Length - 1;
        var body = Encoding.UTF8.GetString(rest, 8, bodyEnd - 8);
        return (id, type, body);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream s, byte[] buf)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(read));
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}

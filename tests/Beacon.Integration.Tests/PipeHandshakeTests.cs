using System.IO.Pipes;
using System.Security.Cryptography;
using Beacon.Protocol;
using BeaconServer.Configuration;
using BeaconServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Beacon.Integration.Tests;

/// <summary>
/// Live end-to-end test: spin up the real NamedPipeServerService against a
/// disposable temp pipe, connect a client that mimics what Beacon.dll does,
/// confirm the handshake completes and player events route correctly.
///
/// Validates the symmetry of the .NET FrameCodec + the wire contract Beacon.dll
/// will be implementing in C++. Runs on Linux too — .NET 8 named-pipe API
/// emulates Windows pipes via Unix domain sockets when not on Windows.
/// </summary>
public class PipeHandshakeTests
{
    private static (NamedPipeServerService server, PipeServerState state, byte[] hmacKey, string pipeName) StartServer()
    {
        var pipeName = $"Beacon.Test.{Guid.NewGuid():N}";
        var opts = Options.Create(new BeaconServerOptions
        {
            InstanceId = "AdminTest",
            PipeName = pipeName,
            HmacKeyPath = Path.Combine(Path.GetTempPath(), $"hmac-{Guid.NewGuid():N}.key"),
            PluginHeartbeatTimeoutSeconds = 30,
        });
        var hmac = new HmacKeyService(opts, NullLogger<HmacKeyService>.Instance);
        var identity = new InstanceIdentityProvider(opts);
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        var server = new NamedPipeServerService(NullLogger<NamedPipeServerService>.Instance, identity, hmac, state);
        return (server, state, hmac.Key, pipeName);
    }

    [Fact]
    public async Task Handshake_completes_and_heartbeat_updates_state()
    {
        var (server, state, hmacKey, pipeName) = StartServer();
        using var serverCts = new CancellationTokenSource();
        await server.StartAsync(serverCts.Token);
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5_000);

            var codec = new FrameCodec(hmacKey);

            var handshake = new HandshakeMessage(
                ProtocolVersion.Major, ProtocolVersion.Minor, ProtocolVersion.Patch,
                "AdminTest", "0.0.1-test", 12345);
            var hsBytes = codec.Encode(FrameType.Handshake, FrameFlags.RequiresAck, 1, handshake);
            await client.WriteAsync(hsBytes);
            await client.FlushAsync();

            var ackBuf = new byte[256];
            var read = await client.ReadAsync(ackBuf);
            read.Should().BeGreaterThan(0);

            var ok = codec.TryDecode(ackBuf.AsSpan(0, read), out _, out var type, out _, out _, out var payload);
            ok.Should().BeTrue();
            type.Should().Be(FrameType.HandshakeAck);
            var ack = codec.DeserializePayload<HandshakeAckMessage>(payload);
            ack.Accepted.Should().BeTrue();

            var hb = new HeartbeatMessage(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 3, 60);
            var hbBytes = codec.Encode(FrameType.Heartbeat, FrameFlags.None, 2, hb);
            await client.WriteAsync(hbBytes);
            await client.FlushAsync();

            await WaitFor(() => state.Connection is not null && state.LastReportedPlayerCount == 3, TimeSpan.FromSeconds(2));
            state.Connection!.InstanceId.Should().Be("AdminTest");
            state.Connection.PluginPid.Should().Be(12345);
            state.LastReportedPlayerCount.Should().Be(3);
        }
        finally
        {
            serverCts.Cancel();
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Mismatched_instance_id_is_rejected()
    {
        var (server, state, hmacKey, pipeName) = StartServer();
        using var serverCts = new CancellationTokenSource();
        await server.StartAsync(serverCts.Token);
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5_000);
            var codec = new FrameCodec(hmacKey);

            // Configured InstanceId is "AdminTest", plugin claims "Imposter".
            var hs = new HandshakeMessage(
                ProtocolVersion.Major, ProtocolVersion.Minor, ProtocolVersion.Patch,
                "Imposter", "0.0.1-test", 9999);
            await client.WriteAsync(codec.Encode(FrameType.Handshake, FrameFlags.RequiresAck, 1, hs));
            await client.FlushAsync();

            var ackBuf = new byte[256];
            var read = await client.ReadAsync(ackBuf);
            var ok = codec.TryDecode(ackBuf.AsSpan(0, read), out _, out var type, out _, out _, out var payload);
            ok.Should().BeTrue();
            type.Should().Be(FrameType.HandshakeAck);
            var ack = codec.DeserializePayload<HandshakeAckMessage>(payload);
            ack.Accepted.Should().BeFalse();
            ack.Reason.Should().Contain("instance id mismatch");
            state.Connection.Should().BeNull();
        }
        finally
        {
            serverCts.Cancel();
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Wrong_hmac_breaks_connection()
    {
        var (server, state, hmacKey, pipeName) = StartServer();
        using var serverCts = new CancellationTokenSource();
        await server.StartAsync(serverCts.Token);
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(5_000);

            var wrongKey = RandomNumberGenerator.GetBytes(32);
            var badCodec = new FrameCodec(wrongKey);
            var handshake = new HandshakeMessage(
                ProtocolVersion.Major, ProtocolVersion.Minor, ProtocolVersion.Patch,
                "AdminTest", "0.0.1-test", 12345);
            var hsBytes = badCodec.Encode(FrameType.Handshake, FrameFlags.RequiresAck, 1, handshake);
            await client.WriteAsync(hsBytes);
            await client.FlushAsync();

            // Server should close the pipe upon HMAC failure. Read should return 0 or throw.
            var ackBuf = new byte[256];
            int read = 0;
            try { read = await client.ReadAsync(ackBuf).AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception) { /* expected — connection closed */ }
            (read == 0 || !client.IsConnected).Should().BeTrue();
            state.Connection.Should().BeNull();
        }
        finally
        {
            serverCts.Cancel();
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitFor(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
        if (!predicate())
            throw new TimeoutException("predicate did not become true within timeout");
    }
}

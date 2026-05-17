using System.Buffers;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Beacon.Protocol;
using BeaconServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

/// <summary>
/// Hosted service that listens for Beacon.dll plugin connections on a per-instance
/// Windows named pipe. Handles handshake, frame parsing, dispatch to handlers, and
/// connection lifecycle.
///
/// Single-connection: Beacon expects exactly one game process per instance, so the
/// server accepts one connection at a time. New connection from a different PID
/// replaces the old one (game crashed and restarted).
/// </summary>
public sealed class NamedPipeServerService : BackgroundService
{
    private readonly ILogger<NamedPipeServerService> _log;
    private readonly InstanceIdentityProvider _identity;
    private readonly HmacKeyService _hmac;
    private readonly PipeServerState _state;

    public NamedPipeServerService(
        ILogger<NamedPipeServerService> log,
        InstanceIdentityProvider identity,
        HmacKeyService hmac,
        PipeServerState state)
    {
        _log = log;
        _identity = identity;
        _hmac = hmac;
        _state = state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var codec = new FrameCodec(_hmac.Key);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = CreatePipe(_identity.PipeName);
                _log.LogInformation("Named pipe listening on {Pipe}", _identity.PipeName);

                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                _log.LogInformation("Plugin connected to pipe");

                await ServeConnectionAsync(pipe, codec, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Pipe server loop error — restarting after 1s");
                try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        _log.LogInformation("Named pipe server stopped");
    }

    private NamedPipeServerStream CreatePipe(string name)
    {
        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(
                name,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

        // Two pipe instances: one for the active connection, one staged so
        // a fresh SN2 process can connect immediately after the prior plugin
        // dies without waiting for the old handle to close. We still only
        // serve one connection at a time logically.
        var security = new PipeSecurity();
        var self = WindowsIdentity.GetCurrent().Owner!;
        security.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            name,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 2,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            pipeSecurity: security);
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream pipe, FrameCodec codec, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var pending = new MemoryStream();
            PipeConnection? connection = null;
            var writeLock = new SemaphoreSlim(1, 1);

            async Task WriteAsync(byte[] data, CancellationToken token)
            {
                await writeLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await pipe.WriteAsync(data, token).ConfigureAwait(false);
                    await pipe.FlushAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    writeLock.Release();
                }
            }

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var read = await pipe.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0) break;
                pending.Write(buffer, 0, read);

                while (TryConsumeOneFrame(pending, codec,
                           out var type, out var flags, out var seq, out var payload))
                {
                    connection = await DispatchAsync(connection, codec, type, flags, seq, payload, WriteAsync, ct).ConfigureAwait(false);
                }
            }

            if (connection is not null)
            {
                _log.LogInformation("Plugin disconnected: {Instance} (pid {Pid})", connection.InstanceId, connection.PluginPid);
                _state.SetConnection(null);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryConsumeOneFrame(
        MemoryStream pending,
        FrameCodec codec,
        out FrameType type,
        out FrameFlags flags,
        out uint seq,
        out byte[] payload)
    {
        var pendingBytes = pending.GetBuffer().AsSpan(0, (int)pending.Length);
        if (!codec.TryDecode(pendingBytes, out var consumed, out type, out flags, out seq, out payload))
            return false;

        var remaining = (int)pending.Length - consumed;
        if (remaining > 0)
        {
            var tail = pending.GetBuffer().AsSpan(consumed, remaining).ToArray();
            pending.SetLength(0);
            pending.Write(tail, 0, remaining);
        }
        else
        {
            pending.SetLength(0);
        }
        return true;
    }

    private async Task<PipeConnection?> DispatchAsync(
        PipeConnection? connection,
        FrameCodec codec,
        FrameType type,
        FrameFlags flags,
        uint seq,
        byte[] payload,
        Func<byte[], CancellationToken, Task> write,
        CancellationToken ct)
    {
        switch (type)
        {
            case FrameType.Handshake:
                {
                    var hs = codec.DeserializePayload<HandshakeMessage>(payload);
                    string? reject = null;
                    if (hs.ProtocolMajor != ProtocolVersion.Major)
                        reject = $"protocol major mismatch — server {ProtocolVersion.Major}, plugin {hs.ProtocolMajor}";
                    else if (!string.Equals(hs.InstanceId, _identity.InstanceId, StringComparison.Ordinal))
                        reject = $"instance id mismatch — server '{_identity.InstanceId}', plugin '{hs.InstanceId}'";

                    var accepted = reject is null;
                    var ack = new HandshakeAckMessage(
                        Accepted: accepted,
                        Reason: reject,
                        ServerProtocolMajor: ProtocolVersion.Major,
                        ServerProtocolMinor: ProtocolVersion.Minor);
                    var ackBytes = codec.Encode(FrameType.HandshakeAck, FrameFlags.IsAck, seq, ack);
                    await write(ackBytes, ct).ConfigureAwait(false);
                    if (!accepted)
                    {
                        _log.LogWarning("Handshake rejected: {Reason}", reject);
                        return null;
                    }
                    var newConn = new PipeConnection(hs.InstanceId, hs.Pid, hs.PluginVersion, codec, write);
                    _state.SetConnection(newConn);
                    _state.LastHeartbeatAt = DateTimeOffset.UtcNow;
                    _log.LogInformation("Handshake accepted: instance={Instance} pid={Pid} ver={Ver} proto={Proto}",
                        hs.InstanceId, hs.Pid, hs.PluginVersion, $"{hs.ProtocolMajor}.{hs.ProtocolMinor}.{hs.ProtocolPatch}");
                    return newConn;
                }

            case FrameType.Heartbeat:
                {
                    var hb = codec.DeserializePayload<HeartbeatMessage>(payload);
                    _state.LastHeartbeatAt = DateTimeOffset.UtcNow;
                    _state.LastReportedPlayerCount = hb.InGamePlayerCount;
                    return connection;
                }

            case FrameType.LogForward:
                {
                    var lf = codec.DeserializePayload<LogForwardMessage>(payload);
                    _log.Log(ParseLogLevel(lf.Level), "[plugin {Source}] {Message}", lf.Source, lf.Message);
                    return connection;
                }

            case FrameType.PlayerJoined:
                {
                    var pj = codec.DeserializePayload<PlayerJoinedMessage>(payload);
                    _log.LogInformation("Player joined: {User} ({Name}) from {Addr}", pj.BeaconUserId, pj.DisplayName, pj.ClientAddress);
                    return connection;
                }

            case FrameType.PlayerLeft:
                {
                    var pl = codec.DeserializePayload<PlayerLeftMessage>(payload);
                    _log.LogInformation("Player left: {User} reason={Reason}", pl.BeaconUserId, pl.Reason);
                    return connection;
                }

            case FrameType.PlayerListSnapshot:
                {
                    var ps = codec.DeserializePayload<PlayerListSnapshotMessage>(payload);
                    _state.SetPlayers(ps.Players ?? new List<PlayerSnapshot>());
                    return connection;
                }

            case FrameType.Goodbye:
                {
                    var gb = codec.DeserializePayload<GoodbyeMessage>(payload);
                    _log.LogInformation("Plugin sent goodbye: {Reason}", gb.Reason);
                    return connection;
                }

            default:
                _log.LogDebug("Unhandled frame type {Type} seq={Seq}", type, seq);
                return connection;
        }
    }

    private static LogLevel ParseLogLevel(string level) => level.ToLowerInvariant() switch
    {
        "trace" or "tr" => LogLevel.Trace,
        "debug" or "dbg" => LogLevel.Debug,
        "info" or "inf" => LogLevel.Information,
        "warn" or "wrn" => LogLevel.Warning,
        "error" or "err" => LogLevel.Error,
        "fatal" or "ftl" or "critical" => LogLevel.Critical,
        _ => LogLevel.Information,
    };
}

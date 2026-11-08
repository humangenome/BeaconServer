using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Beacon.Rcon;

/// <summary>
/// Source RCON over TCP. Spec: https://developer.valvesoftware.com/wiki/Source_RCON_Protocol
///
/// Packet structure:
///   [int32 size][int32 id][int32 type][string body \0][\0 pad]
/// size = 4 (id) + 4 (type) + body.Length + 2 (null terminators).
///
/// Types:
///   3 = SERVERDATA_AUTH (auth request)
///   2 = SERVERDATA_EXECCOMMAND (request) / SERVERDATA_AUTH_RESPONSE (reply)
///   0 = SERVERDATA_RESPONSE_VALUE
///
/// On auth: server replies with one type=0 packet (empty body), then a type=2
/// packet with id = request id on success, id = -1 on failure.
/// </summary>
public sealed class RconServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly string _password;
    private readonly Func<string, Task<string>> _commandExecutor;
    private readonly ILogger? _log;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private static readonly TimeSpan LoginDeadline = TimeSpan.FromSeconds(10);
    private readonly SourceGuard _guard = new(maxPerSource: 3);

    public RconServer(int port, string password, Func<string, Task<string>> commandExecutor, ILogger? log = null)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _password = password;
        _commandExecutor = commandExecutor;
        _log = log;
    }

    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    public void Dispose() { _cts?.Cancel(); try { _listener.Stop(); } catch { } }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                // A connection reset while it waited to be accepted lands here. It used to end the
                // accept loop for good, so RCON stopped answering until the next restart.
                if (ct.IsCancellationRequested) return;
                _log?.LogWarning("RCON accept failed: {Error}", ex.SocketErrorCode);
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            var source = SourceGuard.SourceOf(client.Client.RemoteEndPoint);
            if (SourceGuard.BlockedSeconds(source, SourceGuard.NowMs()) > 0 || !_guard.TryOpen(source))
            {
                client.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try { await HandleClientAsync(client, source, ct).ConfigureAwait(false); }
                finally { _guard.Close(source); }
            }, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, string source, CancellationToken ct)
    {
        using (client)
        {
            var stream = client.GetStream();
            // No password set means nobody gets in, never everybody.
            var authenticated = false;
            // A connection that never logs in does not get to hold its slot.
            using var loginDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            loginDeadline.CancelAfter(LoginDeadline);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var packet = await ReadPacketAsync(stream, authenticated ? ct : loginDeadline.Token).ConfigureAwait(false);
                    if (packet is null) return;
                    var (id, type, body) = packet.Value;

                    if (type == 3)
                    {
                        authenticated = PasswordMatches(body);
                        if (authenticated) SourceGuard.Success(source);
                        else SourceGuard.Failure(source, SourceGuard.NowMs());
                        if (SourceGuard.TakeNotice() is { } tableNotice) _log?.LogWarning("{Notice}", tableNotice);
                        await WritePacketAsync(stream, id, 0, "", ct).ConfigureAwait(false);
                        await WritePacketAsync(stream, authenticated ? id : -1, 2, "", ct).ConfigureAwait(false);
                        if (!authenticated) return;
                        continue;
                    }

                    if (!authenticated)
                    {
                        await WritePacketAsync(stream, -1, 2, "", ct).ConfigureAwait(false);
                        return;
                    }

                    if (type == 2)
                    {
                        var output = await _commandExecutor(body).ConfigureAwait(false);
                        await WritePacketAsync(stream, id, 0, output, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await WritePacketAsync(stream, id, 0, $"unknown rcon type {type}", ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _log?.LogDebug(ex, "RCON client disconnected");
            }
        }
    }

    private bool PasswordMatches(string given)
    {
        if (string.IsNullOrEmpty(_password)) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(given ?? "")),
            SHA256.HashData(Encoding.UTF8.GetBytes(_password)));
    }

    private static async Task<(int id, int type, string body)?> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var sizeBuf = new byte[4];
        if (!await ReadExactAsync(stream, sizeBuf, ct).ConfigureAwait(false)) return null;
        var size = BinaryPrimitives.ReadInt32LittleEndian(sizeBuf);
        if (size < 10 || size > 4096) return null;
        var rest = new byte[size];
        if (!await ReadExactAsync(stream, rest, ct).ConfigureAwait(false)) return null;
        var id = BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(0, 4));
        var type = BinaryPrimitives.ReadInt32LittleEndian(rest.AsSpan(4, 4));
        var bodyEnd = Array.IndexOf<byte>(rest, 0, 8);
        if (bodyEnd < 0) bodyEnd = rest.Length - 1;
        var body = Encoding.UTF8.GetString(rest, 8, bodyEnd - 8);
        return (id, type, body);
    }

    private static async Task WritePacketAsync(NetworkStream stream, int id, int type, string body, CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var size = 4 + 4 + bodyBytes.Length + 2;
        var buf = new byte[4 + size];
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(0, 4), size);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4, 4), id);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8, 4), type);
        Buffer.BlockCopy(bodyBytes, 0, buf, 12, bodyBytes.Length);
        await stream.WriteAsync(buf, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}

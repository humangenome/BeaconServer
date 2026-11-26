using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using BeaconServer.Configuration;
using BeaconServer.Services;
using Xunit;

namespace BeaconServer.Tests;

/// <summary>
/// A backup refused for room answers with one sentence, and a restore, upload or world copy with the other, word
/// for word. Any other failure never gets either sentence.
/// </summary>
[Collection("ChatFiles")]
public sealed class NoRoomWordsTests : IDisposable
{
    private const string BackupWords = "Your other backups are safe. This one wasn't saved because the server's drive is almost full. Free up space, or ask your host.";
    private const string RestoreWords = "Your world is safe. It wasn't replaced because the server's drive is too full to back it up first. Free up space, or ask your host.";
    private const string Password = "test-only";
    private const long Floor = 2L * 1024 * 1024 * 1024;

    private readonly RestoreTestServer _s = new();

    public NoRoomWordsTests()
    {
        _s.Options.RestoreFreeSpaceFloorBytes = Floor;
        Directory.CreateDirectory(_s.SaveDir);
    }

    public void Dispose() => _s.Dispose();

    private void FullDrive() => _s.Service.FreeSpaceProbe = _ => Floor - 1;

    private async Task<string> Rcon(params object[] args)
    {
        var rcon = (RconHostedService)RuntimeHelpers.GetUninitializedObject(typeof(RconHostedService));
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(RconHostedService).GetField("_saves", Private)!.SetValue(rcon, _s.Service);
        return await (Task<string>)typeof(RconHostedService).GetMethod("HandleSaveAsync", Private)!.Invoke(rcon, args)!;
    }

    [Fact]
    public async Task A_backup_refused_for_room_says_so()
    {
        FullDrive();

        Assert.Equal(BackupWords, await Rcon("snapshot", "test"));
        Assert.Empty(Directory.GetFileSystemEntries(_s.SaveDir));
    }

    [Fact]
    public async Task A_backup_that_failed_for_another_reason_does_not_blame_room()
    {
        FullDrive();
        Directory.Delete(_s.Tree, recursive: true);

        Assert.NotEqual(BackupWords, await Rcon("snapshot", "test"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_restore_with_no_room_for_its_copy_gives_that_reason(bool oneDrive)
    {
        var zip = _s.Zip("incoming.zip", withWorld: true);
        _s.Service.FreeSpaceProbe = path =>
            oneDrive || path.StartsWith(_s.SaveDir, StringComparison.Ordinal) ? Floor - 1 : 100 * Floor;
        var noRoom = false;

        Assert.False(await _s.Service.RestoreFromZipPathAsync(zip, "test", "test", default, () => noRoom = true));

        Assert.True(noRoom);
        _s.LiveWorldUntouched();
    }

    [Fact]
    public async Task A_bad_zip_does_not_blame_room()
    {
        var zip = _s.Zip("incoming.zip", withWorld: true, (RestoreTestServer.Sub + "big.bin", 576 * RestoreTestServer.KiB));
        FullDrive();
        var noRoom = false;

        Assert.False(await _s.Service.RestoreFromZipPathAsync(zip, "test", "test", default, () => noRoom = true));

        Assert.False(noRoom);
    }

    [Fact]
    public async Task No_room_to_unpack_is_not_no_room_for_the_copy()
    {
        var zip = _s.Zip("incoming.zip", withWorld: true, (RestoreTestServer.Sub + "more.bin", 64 * RestoreTestServer.KiB));
        _s.Service.FreeSpaceProbe = _ => Floor + 32 * RestoreTestServer.KiB;
        var noRoom = false;

        Assert.False(await _s.Service.RestoreFromZipPathAsync(zip, "test", "test", default, () => noRoom = true));

        Assert.False(noRoom);
    }

    [Theory]
    [InlineData("/api/v1/snapshots", BackupWords)]
    [InlineData("/api/v1/snapshots/upload-kept/restore", RestoreWords)]
    [InlineData("/api/v1/snapshots/import-restore", RestoreWords)]
    public async Task The_admin_API_gives_the_reason(string path, string words)
    {
        // Upload, restore and import are all refused where the host starts the game (upload since 8a5c4b8), and
        // BeaconServer starts the game only on Windows: the Windows run checks all three words.
        if (!OperatingSystem.IsWindows()) return;
        _s.Options.SnExecutablePath = Path.Combine(_s.Root, "game.exe");
        var port = FreePort();
        _s.Options.HttpPort = port;
        _s.Options.RconPassword = Password;
        _s.Options.InstanceId = "no-room-test";
        var zip = _s.Zip("incoming.zip", withWorld: true);
        var byId = path.EndsWith("/restore", StringComparison.Ordinal);
        if (byId) _s.Service.Database.RecordSnapshot(new("upload-kept", 1, zip, new FileInfo(zip).Length, "00", 30));
        FullDrive();

        var options = Options.Create(_s.Options);
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        var monitor = new FixedMonitor<BeaconServerOptions>(_s.Options);
        var chat = new ChatService(NullLogger<ChatService>.Instance, monitor,
            new CommandDispatchService(NullLogger<CommandDispatchService>.Instance, monitor));
        var http = new BeaconHttpService(NullLogger<BeaconHttpService>.Instance, options, new ConfigurationBuilder().Build(),
            _s.Service, state, new InstanceIdentityProvider(options),
            chat);
        await http.StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            await Until(client, $"http://127.0.0.1:{port}/api/v1/players");
            var body = byId ? Array.Empty<byte>() : await File.ReadAllBytesAsync(zip);
            var sha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(Password));
            var signature = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"POST\n{path}\n{ts}\n{sha}"))).ToLowerInvariant();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{path}");
            request.Headers.Add("X-Beacon-Timestamp", ts.ToString());
            request.Headers.Add("X-Beacon-Signature", signature);
            request.Headers.Add("X-Beacon-Content-Sha256", sha);
            if (body.Length > 0) request.Content = new ByteArrayContent(body);

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.InsufficientStorage, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(words, json.RootElement.GetProperty("error").GetString());
            Assert.Equal("live world", _s.LiveText());
            Assert.Empty(Directory.GetFileSystemEntries(_s.SaveDir));
        }
        finally
        {
            await http.StopAsync(CancellationToken.None);
            http.Dispose();
        }
    }

    private static async Task Until(HttpClient client, string url)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                using var _ = await client.GetAsync(url);
                return;
            }
            catch (HttpRequestException) when (i < 100)
            {
                await Task.Delay(100);
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

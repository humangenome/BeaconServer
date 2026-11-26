using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeaconServer.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BeaconServer.Tests;

// A route that throws answers 500 with a fixed error, never the exception's text (that text can
// carry a path on the server's machine, and the HTTP door answers anyone), and the signed body the
// route was given is still deleted from the upload temp folder, so a throw never leaves an upload
// of up to MaxUploadBytes behind. An upload that reached the save folder but never made the backup
// list is removed again, because rotation only knows listed backups. Where the host starts the game
// (our rented servers), an upload is refused as restore and import are, and nothing of it is kept.
// Uses the HTTP hardening harness and shares the upload temp folder with those tests, so it runs
// in their collection.
[Collection("ChatFiles")]
public sealed class HttpErrorReplyTests
{
    private static readonly string UploadTempDir = Path.Combine(Path.GetTempPath(), "beacon-uploads");

    [Fact]
    public async Task A_route_that_throws_answers_a_fixed_error_and_leaves_no_upload_behind()
    {
        // The upload route only runs where BeaconServer starts the game, which is Windows only.
        if (!OperatingSystem.IsWindows()) return;
        await using var h = new HttpHardeningTests.Harness(selfHosted: true);
        await h.StartAsync();

        // The save folder is a file now, so the upload route's folder step throws an IOException
        // whose message names the path.
        File.WriteAllText(h.SaveDir, "not a folder");

        var body = new byte[4096];
        RandomNumberGenerator.Fill(body);
        var bodySha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var resp = await http.SendAsync(h.Signed(HttpMethod.Post, "/api/v1/snapshots", body));
        var text = Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync());

        ((int)resp.StatusCode).Should().Be(500);
        text.Should().Be("{\"error\":\"internal error\"}");
        text.Should().NotContain(h.SaveDir).And.NotContain("Saves");

        // The body was written to the upload temp folder before the route ran; it must not stay there.
        var left = true;
        for (var i = 0; i < 50 && left; i++)
        {
            left = UploadTempHolds(bodySha);
            if (left) await Task.Delay(50);
        }
        left.Should().BeFalse("a route that throws still deletes the signed body it was given");
    }

    [Fact]
    public async Task An_upload_whose_backup_list_entry_fails_leaves_no_zip_in_the_save_folder()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var h = new HttpHardeningTests.Harness(selfHosted: true);
        await h.StartAsync();

        // The upload reaches the save folder, then its backup-list entry can't be written (as on a full disk):
        // the list's own database refuses every new backup row.
        var dbPath = Path.Combine(Path.GetDirectoryName(h.SaveDir)!, "data", "beacon.db");
        using (var c = new SqliteConnection($"Data Source={dbPath};Cache=Shared;Pooling=True"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER refuse_backup_rows BEFORE INSERT ON save_snapshots " +
                              "BEGIN SELECT RAISE(ABORT, 'database or disk is full'); END;";
            cmd.ExecuteNonQuery();
        }

        var body = new byte[4096];
        RandomNumberGenerator.Fill(body);
        var bodySha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var resp = await http.SendAsync(h.Signed(HttpMethod.Post, "/api/v1/snapshots", body));
        var text = Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync());

        ((int)resp.StatusCode).Should().Be(500);
        text.Should().Be("{\"error\":\"internal error\"}");

        // Rotation only knows listed backups, so a zip left here would never be removed.
        var zips = Directory.Exists(h.SaveDir) ? Directory.GetFiles(h.SaveDir, "upload-*.zip") : Array.Empty<string>();
        zips.Should().BeEmpty("an upload that never made the backup list is removed again");
        UploadTempHolds(bodySha).Should().BeFalse("the body was moved, so nothing of it stays in the temp folder");
    }

    [Fact]
    public async Task An_upload_where_the_host_starts_the_game_is_refused_and_nothing_is_kept()
    {
        // As on our rented servers: the host starts the game, not BeaconServer (any OS).
        await using var h = new HttpHardeningTests.Harness();
        await h.StartAsync();

        var body = new byte[4096];
        RandomNumberGenerator.Fill(body);
        var bodySha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var resp = await http.SendAsync(h.Signed(HttpMethod.Post, "/api/v1/snapshots", body));
        var text = Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync());

        // The same answer restore and import give there, word for word.
        ((int)resp.StatusCode).Should().Be(409);
        using (var reply = JsonDocument.Parse(text))
        {
            reply.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
            reply.RootElement.GetProperty("error").GetString().Should().Be(BeaconHttpService.NoRelaunchMessage);
        }

        // Nothing of the upload is kept: no zip in the save folder, no row in the backup list, and the
        // body is gone from the upload temp folder.
        var zips = Directory.Exists(h.SaveDir) ? Directory.GetFiles(h.SaveDir) : Array.Empty<string>();
        zips.Should().BeEmpty("a refused upload never reaches the save folder");
        using (var list = await http.SendAsync(h.Signed(HttpMethod.Get, "/api/v1/snapshots")))
        {
            ((int)list.StatusCode).Should().Be(200);
            (await list.Content.ReadAsStringAsync()).Should().NotContain("upload-");
        }
        var left = true;
        for (var i = 0; i < 50 && left; i++)
        {
            left = UploadTempHolds(bodySha);
            if (left) await Task.Delay(50);
        }
        left.Should().BeFalse("a refused upload's body is deleted from the temp folder");
    }

    [Fact]
    public async Task An_upload_where_BeaconServer_starts_the_game_is_still_kept()
    {
        // Self-hosting, where BeaconServer starts the game: Windows only.
        if (!OperatingSystem.IsWindows()) return;
        await using var h = new HttpHardeningTests.Harness(selfHosted: true);
        await h.StartAsync();

        var body = new byte[4096];
        RandomNumberGenerator.Fill(body);
        var bodySha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var resp = await http.SendAsync(h.Signed(HttpMethod.Post, "/api/v1/snapshots", body));

        ((int)resp.StatusCode).Should().Be(200);
        var zips = Directory.GetFiles(h.SaveDir, "upload-*.zip");
        zips.Should().ContainSingle();
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zips[0]))).ToLowerInvariant().Should().Be(bodySha);
    }

    private static bool UploadTempHolds(string sha)
    {
        if (!Directory.Exists(UploadTempDir)) return false;
        foreach (var f in Directory.GetFiles(UploadTempDir))
        {
            try
            {
                if (new FileInfo(f).Length != 4096) continue;
                var s = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant();
                if (s == sha) return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }
}

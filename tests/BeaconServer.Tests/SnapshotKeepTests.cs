using Beacon.Persistence;
using BeaconServer.Configuration;
using BeaconServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeaconServer.Tests;

/// <summary>
/// Only a backup the server stamped as automatic when it took it may expire. One asked for, an upload, a copy taken
/// before a restore, and every one whose kind cannot be told (all those stored before the stamp) is kept.
/// </summary>
public sealed class SnapshotKeepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "snapshot-keep-" + Guid.NewGuid().ToString("N"));
    private readonly string _saveDir;
    private readonly SaveOrchestratorService _service;
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public SnapshotKeepTests()
    {
        var userDir = Path.Combine(_root, "userdir");
        var world = Path.Combine(userDir, "Saved", "SaveGames");
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "savegame_0.sav"), "live world");
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        _saveDir = Path.Combine(_root, "saves");
        Directory.CreateDirectory(_saveDir);
        var options = Options.Create(new BeaconServerOptions
        {
            SnUserDir = userDir,
            SaveDir = _saveDir,
            HmacKeyPath = Path.Combine(_root, "data", "hmac.key"),
            SnapshotsEnabled = false,
        });
        _service = new SaveOrchestratorService(NullLogger<SaveOrchestratorService>.Instance, options,
            new PipeServerState(NullLogger<PipeServerState>.Instance),
            new Sn2RestartCoordinator(NullLogger<Sn2RestartCoordinator>.Instance, options));
    }

    public void Dispose()
    {
        _service.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Stored(string id, double ageDays, string? fileName = null)
    {
        var path = Path.Combine(_saveDir, fileName ?? id + ".zip");
        File.WriteAllText(path, "zip");
        _service.Database.RecordSnapshot(new SnapshotRecord(id, _now - (long)(ageDays * 86_400), path, 3, "00112233445566778899AABB", 30));
        return path;
    }

    [Theory]
    [InlineData("rcon")]
    [InlineData("pre-restore:http-api")]
    [InlineData("pre-restore:rcon")]
    [InlineData("auto:filewatcher")]
    public async Task The_first_run_after_the_update_removes_none_of_720_stored_backups(string requestedBy)
    {
        // As stored before the stamp: backups by hand, automatic ones and pre-restore copies all read "snap-", uploads "upload-".
        var before = Enumerable.Range(0, 720)
            .Select(i => Stored($"{(i % 4 == 0 ? "upload" : "snap")}-20250101T000000Z-{i:x15}", i * 0.6))
            .ToList();

        Assert.NotNull(await _service.SnapshotAsync(requestedBy));

        Assert.All(before, path => Assert.True(File.Exists(path), path));
        Assert.Equal(721, _service.Database.ListSnapshots(int.MaxValue).Count);
    }

    [Theory]
    [InlineData("snap-by-hand")]
    [InlineData("upload-by-owner")]
    [InlineData("AUTO-upper-case")]
    [InlineData("auto_underscore")]
    [InlineData("old-auto-kind")]
    [InlineData("something-else")]
    public async Task A_backup_of_unknown_or_owner_kind_never_expires(string id)
    {
        var path = Stored(id, 3650);

        Assert.NotNull(await _service.SnapshotAsync("rcon"));

        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("rcon", "snap-")]
    [InlineData("pre-restore:http-api", "snap-")]
    [InlineData("test", "snap-")]
    [InlineData("auto:filewatcher:other", "snap-")]
    [InlineData("auto:filewatcher", "auto-")]
    public async Task Only_an_automatic_backup_is_stamped_automatic(string requestedBy, string prefix)
    {
        var record = await _service.SnapshotAsync(requestedBy);

        Assert.NotNull(record);
        Assert.StartsWith(prefix, record!.SnapshotId);
        Assert.Equal(36, record.SnapshotId.Length);
        Assert.Equal(record.SnapshotId + ".zip", Path.GetFileName(record.FilePath));
    }

    [Fact]
    public async Task An_automatic_backup_still_expires_after_30_days_and_not_before()
    {
        var expired = Stored("auto-expired", 31);
        var recent = Stored("auto-recent", 29);

        Assert.NotNull(await _service.SnapshotAsync("rcon"));

        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task An_automatic_row_cannot_expire_another_backups_file()
    {
        var owners = Stored("auto-expired", 31, fileName: "upload-by-owner.zip");

        Assert.NotNull(await _service.SnapshotAsync("rcon"));

        Assert.True(File.Exists(owners));
    }
}

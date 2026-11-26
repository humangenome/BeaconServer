using Xunit;

namespace BeaconServer.Tests;

/// <summary>
/// At a start the server lists any backup zip it finds in its folder that is not in its list, as one of unknown
/// kind, which is never removed.
/// </summary>
public sealed class FoundBackupsTests : IDisposable
{
    private readonly RestoreTestServer _s = new(liveWorld: false);

    public void Dispose() => _s.Dispose();

    private string OnDisk(string name, int ageDays = 40)
    {
        Directory.CreateDirectory(_s.SaveDir);
        var path = Path.Combine(_s.SaveDir, name);
        File.WriteAllText(path, "zip " + name);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }

    private async Task<string[]> Listed(int wanted)
    {
        for (var i = 0; i < 100 && _s.Service.Database.ListSnapshots(int.MaxValue).Count < wanted; i++) await Task.Delay(50);
        return _s.Service.Database.ListSnapshots(int.MaxValue).Select(r => r.FilePath).OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task A_backup_found_on_disk_is_listed_as_unknown_and_never_expires()
    {
        var found = new[] { OnDisk("auto-moved-here.zip"), OnDisk("my world.zip"), OnDisk("upload-old.zip") }
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();

        await _s.Service.StartAsync(CancellationToken.None);

        Assert.Equal(found, await Listed(3));
        Assert.All(_s.Service.Database.ListSnapshots(int.MaxValue), r =>
        {
            Assert.StartsWith("found-", r.SnapshotId);
            Assert.Equal(36, r.SnapshotId.Length);
            Assert.Equal(new FileInfo(r.FilePath).Length, r.SizeBytes);
            Assert.True(r.RetentionDays >= 36_500, "an older build expires a row by its own days");
        });

        _s.Service.RotateOldSnapshots();

        Assert.All(found, path => Assert.True(File.Exists(path), path));
        Assert.Equal(3, _s.Service.Database.ListSnapshots(int.MaxValue).Count);
    }

    [Fact]
    public async Task One_already_listed_and_what_is_not_a_plain_zip_are_left_alone()
    {
        var known = OnDisk("snap-known.zip", 3);
        _s.Service.Database.RecordSnapshot(new("snap-known", 1, known, 4, "00", 30));
        OnDisk("half-written.zip.tmp");
        OnDisk("notes.txt");
        Directory.CreateDirectory(Path.Combine(_s.SaveDir, "folder.zip"));
        _s.MakeFolderLink(Path.Combine(_s.SaveDir, "link.zip"), _s.Outside);
        var found = OnDisk("found.zip");

        await _s.Service.StartAsync(CancellationToken.None);

        Assert.Equal(new[] { found, known }.OrderBy(p => p, StringComparer.Ordinal), await Listed(2));
    }
}

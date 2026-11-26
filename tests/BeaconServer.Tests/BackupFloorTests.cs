using BeaconServer.Configuration;
using Xunit;

namespace BeaconServer.Tests;

/// <summary>
/// No backup is written that would leave the drive with less free space than RestoreFreeSpaceFloorBytes (2 GiB).
/// It is refused, and nothing stored is removed to make room for it.
/// </summary>
public sealed class BackupFloorTests : IDisposable
{
    private const long Floor = 2L * 1024 * 1024 * 1024;
    private readonly RestoreTestServer _s = new();
    private readonly string _stored;
    private readonly long _needed;

    public BackupFloorTests()
    {
        _s.Options.RestoreFreeSpaceFloorBytes = Floor;
        Directory.CreateDirectory(_s.SaveDir);
        _stored = Path.Combine(_s.SaveDir, "upload-kept.zip");
        File.WriteAllText(_stored, "kept");
        _s.Service.Database.RecordSnapshot(new("upload-kept", 1, _stored, 4, "00", 30));
        _needed = Directory.GetFiles(_s.Tree, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
    }

    public void Dispose() => _s.Dispose();

    private void NothingWrittenOrRemoved()
    {
        Assert.Equal(new[] { _stored }, Directory.GetFileSystemEntries(_s.SaveDir));
        Assert.Single(_s.Service.Database.ListSnapshots(int.MaxValue));
    }

    [Fact]
    public void The_floor_is_2_GiB() => Assert.Equal(Floor, new BeaconServerOptions().RestoreFreeSpaceFloorBytes);

    [Theory]
    [InlineData("rcon")]
    [InlineData("auto:filewatcher")]
    [InlineData("pre-restore:http-api")]
    public async Task A_backup_that_would_leave_less_than_the_floor_is_refused(string requestedBy)
    {
        _s.Service.FreeSpaceProbe = _ => Floor + _needed - 1;

        Assert.Null(await _s.Service.SnapshotAsync(requestedBy));

        NothingWrittenOrRemoved();
    }

    [Fact]
    public async Task A_backup_that_leaves_exactly_the_floor_is_taken()
    {
        _s.Service.FreeSpaceProbe = _ => Floor + _needed;

        Assert.NotNull(await _s.Service.SnapshotAsync("rcon"));

        Assert.True(File.Exists(_stored));
    }

    [Fact]
    public async Task The_backups_own_drive_is_the_one_measured()
    {
        _s.Service.FreeSpaceProbe = path => path.StartsWith(_s.SaveDir, StringComparison.Ordinal) ? Floor - 1 : 100 * Floor;

        Assert.Null(await _s.Service.SnapshotAsync("rcon"));

        NothingWrittenOrRemoved();
    }

    [Fact]
    public async Task A_restore_with_no_room_for_its_copy_is_refused_and_changes_nothing()
    {
        var zip = _s.Zip("incoming.zip", withWorld: true);
        _s.Service.FreeSpaceProbe = path => path.StartsWith(_s.SaveDir, StringComparison.Ordinal) ? Floor - 1 : 100 * Floor;

        Assert.False(await _s.Restore(zip));

        Assert.Equal("live world", _s.LiveText());
        NothingWrittenOrRemoved();
    }
}

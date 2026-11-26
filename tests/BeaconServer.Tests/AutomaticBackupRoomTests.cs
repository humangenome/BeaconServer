using BeaconServer.Configuration;
using BeaconServer.Services;
using Xunit;

namespace BeaconServer.Tests;

/// <summary>
/// The room is for automatic backups alone: 512 MB, or a tenth of the drive's free space when that is less.
/// What an owner made, and what can't be told, takes none of it and is never evicted.
/// </summary>
public sealed class AutomaticBackupRoomTests : IDisposable
{
    private const long KiB = RestoreTestServer.KiB;
    private readonly RestoreTestServer _s = new();
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public void Dispose() => _s.Dispose();

    private string Stored(string id, int minutesAgo, long bytes)
    {
        Directory.CreateDirectory(_s.SaveDir);
        var path = Path.Combine(_s.SaveDir, id + ".zip");
        File.WriteAllBytes(path, new byte[16]);
        _s.Service.Database.RecordSnapshot(new(id, _now - minutesAgo * 60, path, bytes, "00", 30));
        return path;
    }

    [Fact]
    public void The_room_is_512_MB_and_has_no_count()
    {
        Assert.Equal(512L * 1024 * 1024, new BeaconServerOptions().MaxSnapshotBytes);
        Assert.Equal(0, new BeaconServerOptions().MaxSnapshotsKept);
    }

    [Fact]
    public void A_tenth_of_the_free_space_is_the_room_when_that_is_less()
    {
        _s.Options.MaxSnapshotBytes = 10_000 * KiB;
        _s.Service.FreeSpaceProbe = _ => 2_500 * KiB;
        var oldest = Stored("auto-1", 3, 100 * KiB);
        var middle = Stored("auto-2", 2, 100 * KiB);
        var newest = Stored("auto-3", 1, 100 * KiB);

        _s.Service.RotateOldSnapshots();

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void Owner_made_and_unknown_backups_take_none_of_the_room()
    {
        _s.Options.MaxSnapshotsKept = 0;
        _s.Options.MaxSnapshotBytes = 500 * KiB;
        _s.Service.FreeSpaceProbe = _ => null;
        var owners = Enumerable.Range(0, 40).Select(i => Stored(new[] { "snap-", "upload-", "unknown-" }[i % 3] + i, 100 + i, 10_000 * KiB)).ToList();
        var automatic = new[] { Stored("auto-1", 2, 200 * KiB), Stored("auto-2", 1, 200 * KiB) };

        _s.Service.RotateOldSnapshots();

        Assert.All(owners.Concat(automatic), path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public async Task Only_automatic_backups_are_ever_removed_by_the_room_or_the_floor()
    {
        // An owner's backup and a copy taken before a restore, made the real way (both are stamped "snap-"), and an
        // upload stored as the upload route stores it ("upload-"), which alone is twenty times the whole room.
        _s.Options.MaxSnapshotsKept = 0;
        _s.Options.MaxSnapshotBytes = 500 * KiB;
        _s.Service.FreeSpaceProbe = _ => null;
        var oldestAuto = Stored("auto-20250101T000000Z-1", 30, 300 * KiB);
        var middleAuto = Stored("auto-20250101T000000Z-2", 20, 300 * KiB);
        var newestAuto = Stored("auto-20250101T000000Z-3", 10, 300 * KiB);
        var upload = Stored("upload-20250101T000000Z-by-owner", 40, 10_000 * KiB);
        var owner = await _s.Service.SnapshotAsync("rcon");
        var preRestore = await _s.Service.SnapshotAsync("pre-restore:http-api");
        Assert.NotNull(owner);
        Assert.NotNull(preRestore);
        Assert.StartsWith("snap-", owner!.SnapshotId);
        Assert.StartsWith("snap-", preRestore!.SnapshotId);
        var owners = new[] { owner.FilePath, preRestore.FilePath, upload };

        // The room: automatic backups past 500 KB go, oldest first, and the newest automatic one stays.
        _s.Service.RotateOldSnapshots();
        Assert.False(File.Exists(oldestAuto));
        Assert.False(File.Exists(middleAuto));
        Assert.True(File.Exists(newestAuto));
        Assert.All(owners, path => Assert.True(File.Exists(path), path));

        // The floor: with 1 GiB free and the 2 GiB floor, a new backup of either kind and an upload are refused,
        // and nothing is removed to make room for them.
        _s.Options.RestoreFreeSpaceFloorBytes = 2L * 1024 * 1024 * 1024;
        _s.Service.FreeSpaceProbe = _ => 1024L * 1024 * 1024;
        var refused = 0;
        Assert.Null(await _s.Service.SnapshotAsync("rcon", noRoom: () => refused++));
        Assert.Null(await _s.Service.SnapshotAsync("auto:filewatcher", noRoom: () => refused++));
        Assert.False(_s.Service.HasRoomForBackup(10 * KiB));
        _s.Service.RotateOldSnapshots();
        Assert.Equal(2, refused);
        Assert.True(File.Exists(newestAuto));
        Assert.All(owners, path => Assert.True(File.Exists(path), path));
        Assert.Equal(4, _s.Service.Database.ListSnapshots(int.MaxValue).Count);
    }
}

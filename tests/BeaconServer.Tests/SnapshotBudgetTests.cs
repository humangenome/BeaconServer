using Beacon.Persistence;
using BeaconServer.Services;

namespace BeaconServer.Tests;

/// <summary>
/// Stored world backups (snapshots, uploads and the copy each restore takes) have a room (2026-11-18): the newest
/// MaxSnapshotsKept within MaxSnapshotBytes, and 30 days. The newest one and one a restore is reading always stay;
/// nothing outside the snapshot folder is ever removed.
/// </summary>
public sealed class SnapshotBudgetTests : IDisposable
{
    private const long KiB = RestoreTestServer.KiB;
    private readonly RestoreTestServer _s = new();
    private readonly long _now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public void Dispose() => _s.Dispose();

    // ---- the rule itself ----

    private static SnapshotBudget.Item Item(int n, long taken, long bytes = 1000, long days = 30) =>
        new($"s{n:D2}", taken, bytes, days, $"/snapshots/s{n:D2}.zip");

    private List<SnapshotBudget.Item> Items(int count, long bytes = 1000) =>
        Enumerable.Range(0, count).Select(i => Item(i, _now - 1000 + i, bytes)).ToList();

    private static string[] Ids(IEnumerable<SnapshotBudget.Item> items) => items.Select(i => i.Id).OrderBy(i => i).ToArray();

    [Fact]
    public void Keeps_the_newest_by_count_and_removes_the_oldest()
    {
        var gone = SnapshotBudget.ToRemove(Items(25), _now, maxCount: 20, maxBytes: 0, Array.Empty<string>());

        Assert.Equal(new[] { "s00", "s01", "s02", "s03", "s04" }, Ids(gone));
    }

    [Fact]
    public void Keeps_the_room_in_bytes_counted_from_the_newest()
    {
        var gone = SnapshotBudget.ToRemove(Items(5, bytes: 300), _now, maxCount: 0, maxBytes: 1000, Array.Empty<string>());

        Assert.Equal(new[] { "s00", "s01" }, Ids(gone));
    }

    [Fact]
    public void The_newest_stays_even_when_it_alone_is_over_the_room()
    {
        var items = Items(3);
        items.Add(Item(9, _now, bytes: 5000));

        var gone = SnapshotBudget.ToRemove(items, _now, maxCount: 20, maxBytes: 1000, Array.Empty<string>());

        Assert.Equal(new[] { "s00", "s01", "s02" }, Ids(gone));
    }

    [Fact]
    public void A_snapshot_a_restore_is_reading_stays_whatever_the_limits_say()
    {
        var gone = SnapshotBudget.ToRemove(Items(25), _now, maxCount: 20, maxBytes: 0, new[] { "/snapshots/s00.zip" });

        Assert.Equal(new[] { "s01", "s02", "s03", "s04", "s05" }, Ids(gone));
    }

    [Fact]
    public void Age_still_removes_old_ones_but_never_the_newest()
    {
        var items = new List<SnapshotBudget.Item>
        {
            Item(0, _now - 40 * 86_400),
            Item(1, _now - 31 * 86_400),
            Item(2, _now - 29 * 86_400),
        };
        Assert.Equal(new[] { "s00", "s01" }, Ids(SnapshotBudget.ToRemove(items, _now, 20, 0, Array.Empty<string>())));

        var allOld = new List<SnapshotBudget.Item> { Item(0, _now - 40 * 86_400), Item(1, _now - 35 * 86_400) };
        Assert.Equal(new[] { "s00" }, Ids(SnapshotBudget.ToRemove(allOld, _now, 20, 0, Array.Empty<string>())));
    }

    [Fact]
    public void A_limit_of_zero_is_off()
    {
        Assert.Empty(SnapshotBudget.ToRemove(Items(50), _now, maxCount: 0, maxBytes: 0, Array.Empty<string>()));
    }

    // ---- the server applies it ----

    private string Stored(string id, long taken, long bytes, string? path = null)
    {
        Directory.CreateDirectory(_s.SaveDir);
        path ??= Path.Combine(_s.SaveDir, id + ".zip");
        if (!File.Exists(path)) File.WriteAllBytes(path, new byte[bytes]);
        _s.Service.Database.RecordSnapshot(new SnapshotRecord(id, taken, path, bytes, "00", 30));
        return path;
    }

    private string[] RowIds() => _s.Service.Database.ListSnapshots(int.MaxValue).Select(r => r.SnapshotId).OrderBy(i => i).ToArray();

    [Fact]
    public void Rotation_removes_the_oldest_automatic_ones_with_their_rows()
    {
        _s.Options.MaxSnapshotsKept = 20;
        var paths = Enumerable.Range(0, 25).Select(i => Stored($"auto-{i:D2}", _now - 1000 + i, KiB)).ToList();

        _s.Service.RotateOldSnapshots();

        Assert.All(paths.Take(5), p => Assert.False(File.Exists(p)));
        Assert.All(paths.Skip(5), p => Assert.True(File.Exists(p)));
        Assert.Equal(Enumerable.Range(5, 20).Select(i => $"auto-{i:D2}").ToArray(), RowIds());
    }

    [Fact]
    public void Rotation_keeps_the_room_in_bytes_and_always_the_newest()
    {
        _s.Options.MaxSnapshotsKept = 20;
        _s.Options.MaxSnapshotBytes = 500 * KiB;
        var older = Enumerable.Range(0, 3).Select(i => Stored($"auto-{i:D2}", _now - 1000 + i, 200 * KiB)).ToList();
        var newest = Stored("auto-99", _now, 600 * KiB);

        _s.Service.RotateOldSnapshots();

        Assert.True(File.Exists(newest));
        Assert.All(older, p => Assert.False(File.Exists(p)));
        Assert.Equal(new[] { "auto-99" }, RowIds());
    }

    [Fact]
    public void Rotation_never_removes_a_file_outside_the_snapshot_folder()
    {
        var live = Path.Combine(_s.Tree, RestoreTestServer.LiveFile);
        Stored("auto-01", _now - 40 * 86_400, 10, path: live);
        Stored("auto-02", _now, KiB);

        _s.Service.RotateOldSnapshots();

        Assert.Equal("live world", File.ReadAllText(live));
        Assert.Contains("auto-01", RowIds());
    }

    [Fact]
    public async Task Restoring_the_oldest_of_a_full_set_keeps_it_while_it_is_read()
    {
        _s.Options.MaxSnapshotsKept = 19;
        var paths = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            var zip = _s.Zip($"w{i:D2}.zip", withWorld: true);
            var stored = Path.Combine(_s.SaveDir, $"auto-{i:D2}.zip");
            Directory.CreateDirectory(_s.SaveDir);
            File.Move(zip, stored);
            paths.Add(Stored($"auto-{i:D2}", _now - 1000 + i, new FileInfo(stored).Length, stored));
        }

        // The rotation the restore runs must not take the zip being restored. The pre-restore copy takes no room here.
        Assert.True(await _s.Service.RestoreSnapshotAsync("auto-00", "test"));

        Assert.Equal("restored world", _s.RestoredText());
        Assert.True(File.Exists(paths[0]), "the zip being restored stayed");
        Assert.False(File.Exists(paths[1]), "the next oldest made room instead");
        Assert.Equal(20, RowIds().Length);
    }
}

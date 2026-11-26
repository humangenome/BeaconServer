namespace BeaconServer.Tests;

/// <summary>A snapshot that fails leaves no part-written file in the snapshot folder.</summary>
public sealed class SnapshotFailureTests : IDisposable
{
    private readonly RestoreTestServer _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public async Task A_save_file_that_cannot_be_read_leaves_nothing_in_the_snapshot_folder()
    {
        var live = Path.Combine(_s.Tree, RestoreTestServer.LiveFile);
        // Held the way another program holds a file it is writing: no sharing, on Windows and on Linux alike.
        using (new FileStream(live, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(await _s.Service.SnapshotAsync("test"));
        }

        Assert.Empty(Directory.GetFileSystemEntries(_s.SaveDir));
        Assert.Empty(_s.Service.ListSnapshots());
        Assert.NotNull(await _s.Service.SnapshotAsync("test"));
    }
}

using System.IO.Compression;
using BeaconServer.Configuration;
using BeaconServer.Services;

namespace BeaconServer.Tests;

/// <summary>
/// A world zip is capped on upload (MaxUploadBytes) but used to be unpacked with no limit, so a small zip could
/// unpack to far more and fill the disk every server on the machine shares. Everything a zip can be refused for is
/// now checked before the game is stopped (the caps, paths that leave the folder, names Windows treats specially,
/// data that is not the size it states), and the unpack counts the bytes it really writes as a second line. The
/// same checks as the sibling servers (2026-11-17 Security set), ported 2026-11-18.
/// </summary>
public sealed class RestoreZipLimitsTests : IDisposable
{
    private const long KiB = RestoreTestServer.KiB;
    private const string Sub = RestoreTestServer.Sub;
    private readonly RestoreTestServer _s = new();

    public void Dispose() => _s.Dispose();

    [Fact]
    public async Task A_tiny_zip_that_unpacks_huge_is_refused_before_the_game_is_stopped()
    {
        var zip = _s.Zip("bomb.zip", withWorld: true, (Sub + "a.bin", 400 * KiB), (Sub + "b.bin", 400 * KiB),
            (Sub + "c.bin", 400 * KiB));
        Assert.True(new FileInfo(zip).Length < 16 * KiB, "the zip itself is tiny");

        Assert.False(await _s.Restore(zip));

        Assert.Equal(0, _s.Coordinator.KillRequests);
        _s.LiveWorldUntouched();
    }

    [Fact]
    public async Task Too_many_entries_are_refused_before_the_game_is_stopped()
    {
        var many = Enumerable.Range(0, 25).Select(i => (Sub + $"f{i}.bin", 1L)).ToArray();
        var zip = _s.Zip("many.zip", withWorld: true, many);

        Assert.False(await _s.Restore(zip));

        Assert.Equal(0, _s.Coordinator.KillRequests);
        _s.LiveWorldUntouched();
    }

    [Fact]
    public async Task One_oversized_entry_is_refused_before_the_game_is_stopped()
    {
        var zip = _s.Zip("one-big.zip", withWorld: true, (Sub + "big.bin", 600 * KiB));

        Assert.False(await _s.Restore(zip));

        Assert.Equal(0, _s.Coordinator.KillRequests);
        _s.LiveWorldUntouched();
    }

    [Fact]
    public async Task An_unpack_that_would_leave_too_little_free_space_is_refused_before_the_game_is_stopped()
    {
        _s.Options.RestoreFreeSpaceFloorBytes = 512 * KiB;
        _s.Service.FreeSpaceProbe = _ => 800 * KiB;
        var zip = _s.Zip("fits-caps.zip", withWorld: true, (Sub + "x.bin", 400 * KiB));

        Assert.False(await _s.Restore(zip));

        Assert.Equal(0, _s.Coordinator.KillRequests);
        _s.LiveWorldUntouched();
    }

    [Fact]
    public async Task A_zip_with_no_world_is_refused_before_the_game_is_stopped()
    {
        var zip = _s.Zip("no-world.zip", withWorld: false, ("Config/Engine.ini", 2 * KiB));

        Assert.False(await _s.Restore(zip));

        Assert.Equal(0, _s.Coordinator.KillRequests);
        _s.LiveWorldUntouched();
    }

    [Fact]
    public async Task A_zip_that_cannot_be_opened_is_refused_before_the_game_is_stopped()
    {
        var zip = Path.Combine(_s.Root, "not-a-zip.zip");
        File.WriteAllText(zip, "this is not a zip");

        Assert.False(await _s.Restore(zip));

        Assert.Equal(0, _s.Coordinator.KillRequests);
        _s.LiveWorldUntouched();
    }

    [Fact]
    public async Task A_world_inside_the_caps_still_restores()
    {
        _s.Options.RestoreFreeSpaceFloorBytes = 512 * KiB;
        _s.Service.FreeSpaceProbe = _ => 100 * 1024 * KiB;
        var zip = _s.Zip("ok.zip", withWorld: true, (Sub + "x.bin", 400 * KiB), (Sub + "deeper/more/y.bin", 2 * KiB));

        Assert.True(await _s.Restore(zip));

        Assert.Equal(1, _s.Coordinator.KillRequests);
        Assert.Equal("restored world", _s.RestoredText());
        Assert.Equal(400 * KiB, new FileInfo(Path.Combine(_s.Tree, Sub + "x.bin")).Length);
        Assert.Equal(2 * KiB, new FileInfo(Path.Combine(_s.Tree, Sub + "deeper/more/y.bin")).Length);
        Assert.Empty(Directory.GetDirectories(_s.SavedDir, "*.incoming-*"));
        Assert.Empty(Directory.GetDirectories(_s.SavedDir, "*.old-*"));
    }

    [Theory]
    [InlineData(CompressionLevel.SmallestSize)]
    [InlineData(CompressionLevel.NoCompression)]
    public async Task A_zip_that_states_small_sizes_never_writes_past_what_it_stated(CompressionLevel level)
    {
        var zip = Path.Combine(_s.Root, "liar.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry(RestoreTestServer.WorldEntry).Open())) w.Write("restored world");
            using var s = archive.CreateEntry(Sub + "liar.bin", level).Open();
            s.Write(new byte[3 * 1024 * KiB]);
        }
        RestoreTestServer.StateSize(zip, Sub + "liar.bin", 1000);
        using (var probe = ZipFile.OpenRead(zip))
            Assert.Equal(1000, probe.GetEntry(Sub + "liar.bin")!.Length);

        Assert.False(await _s.Restore(zip));

        Assert.Equal(0, _s.Coordinator.KillRequests);
        _s.LiveWorldUntouched();
        var written = Directory.GetFiles(_s.SavedDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Assert.True(written < 64 * KiB, $"nothing near the 3 MB the zip holds was written ({written} bytes)");
    }

    [Fact]
    public void The_unpack_writes_nothing_when_the_entries_state_more_than_the_cap()
    {
        var zip = _s.Zip("two-mid.zip", withWorld: true, (Sub + "a.bin", 300 * KiB), (Sub + "b.bin", 300 * KiB));
        var dest = Path.Combine(_s.Root, "unpack");
        Directory.CreateDirectory(dest);
        var caps = new RestoreZipLimits.Caps(400 * KiB, 20, 512 * KiB, 0);

        Assert.Throws<InvalidDataException>(() => RestoreZipLimits.ExtractWithinCaps(zip, dest, caps, null));

        Assert.Empty(Directory.GetFiles(dest, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(CompressionLevel.SmallestSize)]
    [InlineData(CompressionLevel.NoCompression)]
    public void The_unpack_itself_stops_at_what_an_entry_states(CompressionLevel level)
    {
        // The service checks the data before the stop; the unpack's own running count is the second line, for a
        // caller that skipped that check. Nothing past the stated size is ever written.
        var zip = Path.Combine(_s.Root, "liar-unpack.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var s = archive.CreateEntry("liar.bin", level).Open())
            s.Write(new byte[3 * 1024 * KiB]);
        RestoreTestServer.StateSize(zip, "liar.bin", 1000);
        var dest = Path.Combine(_s.Root, "unpack-liar");
        Directory.CreateDirectory(dest);
        var caps = new RestoreZipLimits.Caps(1024 * KiB, 20, 512 * KiB, 0);

        try { RestoreZipLimits.ExtractWithinCaps(zip, dest, caps, null); }
        catch (InvalidDataException) { }

        var written = Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        Assert.True(written <= 1000, $"the unpack wrote {written} bytes for an entry that states 1000");
    }

    [Theory]
    [InlineData("x/World.sav:hidden")]
    [InlineData("x/what?.sav")]
    [InlineData("x/a|b.sav")]
    public async Task An_entry_name_Windows_cannot_store_is_refused_before_the_game_is_stopped(string name)
    {
        var zip = _s.Zip("odd-name.zip", withWorld: true, (name, 1 * KiB));

        Assert.False(await _s.Restore(zip));

        Assert.Equal(0, _s.Coordinator.KillRequests);
        _s.LiveWorldUntouched();
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("x/../../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("\\outside.txt")]
    public async Task An_entry_whose_path_leaves_the_folder_is_refused_before_the_game_is_stopped(string name)
    {
        var zip = _s.Zip("slip-early.zip", withWorld: true, (name, 1 * KiB));

        Assert.False(await _s.Restore(zip));

        Assert.Equal(0, _s.Coordinator.KillRequests);
        _s.LiveWorldUntouched();
        Assert.False(File.Exists(Path.Combine(_s.Root, "outside.txt")));
        Assert.False(File.Exists(Path.Combine(_s.SavedDir, "outside.txt")));
    }

    [Theory]
    [InlineData(CompressionLevel.SmallestSize, 1000)]
    [InlineData(CompressionLevel.NoCompression, 1000)]
    [InlineData(CompressionLevel.SmallestSize, 400 * 1024)]
    [InlineData(CompressionLevel.NoCompression, 400 * 1024)]
    public async Task A_zip_whose_data_is_not_the_size_it_states_is_refused_before_the_game_is_stopped(CompressionLevel level, int stated)
    {
        // The entry holds 300 KiB. Stating 1000 bytes is the bomb shape; stating 400 KiB is a short entry, which
        // .NET's reader would have unpacked as a cut file.
        var zip = Path.Combine(_s.Root, "false-size.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry(RestoreTestServer.WorldEntry).Open())) w.Write("restored world");
            using var s = archive.CreateEntry(Sub + "held.bin", level).Open();
            s.Write(new byte[300 * KiB]);
        }
        RestoreTestServer.StateSize(zip, Sub + "held.bin", (uint)stated);

        Assert.False(await _s.Restore(zip));

        Assert.Equal(0, _s.Coordinator.KillRequests);
        _s.LiveWorldUntouched();
    }

    [Fact]
    public async Task A_damaged_entry_is_refused_before_the_game_is_stopped()
    {
        // One byte of a stored entry's data changed after the zip was made: right size, wrong checksum.
        var zip = Path.Combine(_s.Root, "damaged.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry(RestoreTestServer.WorldEntry).Open())) w.Write("restored world");
            using var s = archive.CreateEntry(Sub + "data.bin", CompressionLevel.NoCompression).Open();
            s.Write(Enumerable.Repeat((byte)'A', 64 * (int)KiB).ToArray());
        }
        var bytes = File.ReadAllBytes(zip);
        var at = bytes.AsSpan().IndexOf(Enumerable.Repeat((byte)'A', 1024).ToArray());
        Assert.True(at > 0, "the stored data is in the zip");
        bytes[at + 500] = (byte)'B';
        File.WriteAllBytes(zip, bytes);

        Assert.False(await _s.Restore(zip));

        Assert.Equal(0, _s.Coordinator.KillRequests);
        _s.LiveWorldUntouched();
    }

    [Theory]
    [InlineData("x/CON")]
    [InlineData("x/nul.sav")]
    [InlineData("x/Com1.txt")]
    [InlineData("x/LPT9")]
    [InlineData("x/con .sav")]
    [InlineData("AUX/x.sav")]
    [InlineData("x/World.sav.")]
    [InlineData("x/World.sav ")]
    [InlineData("x/dir./x.sav")]
    [InlineData("x/dir /x.sav")]
    public async Task An_entry_name_Windows_treats_specially_is_refused_before_the_game_is_stopped(string name)
    {
        var zip = _s.Zip("device-name.zip", withWorld: true, (name, 1 * KiB));

        Assert.False(await _s.Restore(zip));

        Assert.Equal(0, _s.Coordinator.KillRequests);
        _s.LiveWorldUntouched();
    }

    [Theory]
    [InlineData("x/World.sav", false)]
    [InlineData("./x/World.sav", false)]
    [InlineData("x/../y/World.sav", false)]
    [InlineData("x/", false)]
    [InlineData("x/../../y.sav", true)]
    [InlineData("..", true)]
    [InlineData("C:x.sav", true)]
    [InlineData("\\\\server\\share\\x.sav", true)]
    public void Knows_the_paths_that_leave_the_folder(string name, bool expected)
    {
        Assert.Equal(expected, RestoreZipLimits.LeavesTheFolder(name));
    }

    [Theory]
    [InlineData("x/CONSOLE.sav", false)]
    [InlineData("x/COM10", false)]
    [InlineData("x/con_x.sav", false)]
    [InlineData("x/.hidden", false)]
    [InlineData("x/PRN.sav", true)]
    [InlineData("x/conout$", true)]
    [InlineData("x/COM¹.txt", true)]
    [InlineData("x\\LPT1\\y.sav", true)]
    [InlineData("x/y.sav.", true)]
    public void Knows_the_names_Windows_treats_specially(string name, bool expected)
    {
        Assert.Equal(expected, RestoreZipLimits.HasNameWindowsTreatsSpecially(name));
    }

    // ---- 2026-11-18: the unpack never writes through a link ----

    [Fact]
    public void The_unpack_refuses_a_staging_folder_that_is_a_link()
    {
        var zip = _s.Zip("plain.zip", withWorld: true);
        var dest = Path.Combine(_s.Root, "staging-link");
        _s.MakeFolderLink(dest, _s.Outside);
        var caps = new RestoreZipLimits.Caps(1024 * KiB, 20, 512 * KiB, 0);

        Assert.Throws<IOException>(() => RestoreZipLimits.ExtractWithinCaps(zip, dest, caps, null));

        _s.OutsideUntouched();
    }

    [Fact]
    public void The_unpack_never_writes_into_a_link_inside_the_staging_folder()
    {
        var zip = _s.Zip("into-link.zip", withWorld: true, ("linked/x.bin", 2 * KiB), ("linked/deeper/y.bin", 2 * KiB));
        var dest = Path.Combine(_s.Root, "unpack-into-link");
        Directory.CreateDirectory(dest);
        _s.MakeFolderLink(Path.Combine(dest, "linked"), _s.Outside);
        var caps = new RestoreZipLimits.Caps(1024 * KiB, 20, 512 * KiB, 0);

        Assert.Throws<IOException>(() => RestoreZipLimits.ExtractWithinCaps(zip, dest, caps, null));

        _s.OutsideUntouched();
    }

    [Fact]
    public void The_defaults_fit_a_real_save_folder_and_an_upload_at_its_cap()
    {
        var d = new BeaconServerOptions();
        Assert.True(d.MaxRestoreUnpackedBytes >= d.MaxUploadBytes);
        Assert.True(d.MaxRestoreEntryBytes <= d.MaxRestoreUnpackedBytes);
        Assert.True(d.MaxRestoreEntries >= 1_000);
        Assert.True(d.RestoreFreeSpaceFloorBytes > 0);
    }
}

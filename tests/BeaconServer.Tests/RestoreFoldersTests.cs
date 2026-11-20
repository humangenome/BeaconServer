using BeaconServer.Services;

namespace BeaconServer.Tests;

/// <summary>
/// A world restore never writes through a link (2026-11-18). The Saved folder and every folder above it must be
/// real folders, except the server's own folder kept on a second drive; the live save folder and the folders the
/// restore makes beside it must be real folders. Checked before the game is stopped, and again right before every
/// folder is made, renamed or deleted.
/// </summary>
public sealed class RestoreFoldersTests : IDisposable
{
    private const long KiB = RestoreTestServer.KiB;
    private readonly List<RestoreTestServer> _servers = new();

    public void Dispose()
    {
        foreach (var s in _servers) s.Dispose();
    }

    private RestoreTestServer Server(Func<string, string>? userDir = null, bool liveWorld = true)
    {
        var s = new RestoreTestServer(userDir, liveWorld);
        _servers.Add(s);
        return s;
    }

    // ---- refused before the stop ----

    [Fact]
    public async Task A_Saved_folder_that_is_a_link_is_refused_before_the_game_is_stopped()
    {
        var s = Server(liveWorld: false);
        Directory.CreateDirectory(s.UserDir);
        s.MakeFolderLink(s.SavedDir, s.Outside);

        Assert.False(await s.Restore(s.Zip("w.zip", withWorld: true)));

        Assert.Equal(0, s.Coordinator.KillRequests);
        s.OutsideUntouched();
        Assert.Empty(s.Service.ListSnapshots());
    }

    [Fact]
    public async Task A_user_folder_that_is_a_link_is_refused_before_the_game_is_stopped()
    {
        var s = Server(liveWorld: false);
        Directory.CreateDirectory(Path.GetDirectoryName(s.UserDir)!);
        s.MakeFolderLink(s.UserDir, s.Outside);

        Assert.False(await s.Restore(s.Zip("w.zip", withWorld: true)));

        Assert.Equal(0, s.Coordinator.KillRequests);
        s.OutsideUntouched();
    }

    [Fact]
    public async Task A_live_save_folder_that_is_a_link_is_refused_before_the_game_is_stopped()
    {
        var s = Server(liveWorld: false);
        Directory.CreateDirectory(s.SavedDir);
        s.MakeFolderLink(s.Tree, s.Outside);

        Assert.False(await s.Restore(s.Zip("w.zip", withWorld: true)));

        Assert.Equal(0, s.Coordinator.KillRequests);
        s.OutsideUntouched();
        Assert.Empty(Directory.GetDirectories(s.SavedDir, "*.incoming-*"));
    }

    // ---- a link that appears after the first check ----

    [Fact]
    public async Task A_link_put_in_place_of_the_live_save_folder_before_it_is_moved_aside_stops_the_restore()
    {
        var s = Server();
        var real = Path.Combine(s.Root, "real-tree");
        s.Service.TestBeforeMove = step =>
        {
            if (step != "move-live-aside") return;
            Directory.Move(s.Tree, real);
            s.MakeFolderLink(s.Tree, s.Outside);
        };

        Assert.False(await s.Restore(s.Zip("w.zip", withWorld: true)));

        Assert.Equal(1, s.Coordinator.KillRequests);
        s.OutsideUntouched();
        Assert.True(RestoreFolders.IsLink(s.Tree), "the link was left where it was, not renamed");
        Assert.Empty(Directory.GetDirectories(s.SavedDir, "*.old-*"));
        Assert.Empty(Directory.GetDirectories(s.SavedDir, "*.incoming-*"));
        Assert.Equal("live world", File.ReadAllText(Path.Combine(real, RestoreTestServer.LiveFile)));
    }

    [Fact]
    public async Task A_link_put_in_place_of_the_Saved_folder_before_the_new_world_moves_in_stops_the_restore()
    {
        var s = Server();
        var realSaved = Path.Combine(s.Root, "real-saved");
        s.Service.TestBeforeMove = step =>
        {
            if (step != "move-incoming-in") return;
            Directory.Move(s.SavedDir, realSaved);
            s.MakeFolderLink(s.SavedDir, s.Outside);
        };

        Assert.False(await s.Restore(s.Zip("w.zip", withWorld: true)));

        Assert.Equal(1, s.Coordinator.KillRequests);
        s.OutsideUntouched();
        // Nothing went through the link: the moved-aside world and the unpacked one are both still in the real
        // folder, for staff to put back (the rollback refuses to rename through the link too).
        Assert.Single(Directory.GetDirectories(realSaved, "*.old-*"));
    }

    [Fact]
    public async Task A_link_put_in_the_staging_folder_before_the_unpack_stops_it()
    {
        var s = Server();
        s.Service.TestBeforeMove = step =>
        {
            if (step != "unpack") return;
            var staging = Directory.GetDirectories(s.SavedDir, "*.incoming-*").Single();
            s.MakeFolderLink(Path.Combine(staging, "linked"), s.Outside);
        };

        Assert.False(await s.Restore(s.Zip("w.zip", withWorld: true, ("linked/x.bin", 2 * KiB))));

        Assert.Equal(1, s.Coordinator.KillRequests);
        s.OutsideUntouched();
        Assert.Equal("live world", s.LiveText());
        Assert.Empty(Directory.GetDirectories(s.SavedDir, "*.incoming-*"));
    }

    // ---- the server's own folder on a second drive ----

    // Two stand-in "drives" inside the test folder: C\servers\game\1001 is a link to D\servers\game\1001, and the
    // user folder sits under it, as a hosting panel keeps a server on a second drive.
    private static string DriveC(string root) => Path.Combine(root, "C") + Path.DirectorySeparatorChar;
    private static string DriveD(string root) => Path.Combine(root, "D") + Path.DirectorySeparatorChar;
    private static string ServerC(string root) => Path.Combine(DriveC(root), "servers", "game", "1001");

    private static SecondDriveRule TwoDrives(string root, string serverFolder) => new()
    {
        ServerFolder = serverFolder,
        DriveOf = p => p.StartsWith(DriveC(root), StringComparison.Ordinal) ? DriveC(root)
            : p.StartsWith(DriveD(root), StringComparison.Ordinal) ? DriveD(root) : null,
        IsFixedDrive = _ => true,
    };

    private RestoreTestServer SecondDriveServer()
    {
        var s = Server(root => Path.Combine(ServerC(root), "userdir"), liveWorld: false);
        var real = Path.Combine(DriveD(s.Root), "servers", "game", "1001");
        var live = Path.Combine(real, "userdir", "Saved", "SaveGames", RestoreTestServer.LiveFile);
        Directory.CreateDirectory(Path.GetDirectoryName(live)!);
        File.WriteAllText(live, "live world");
        Directory.CreateDirectory(Path.GetDirectoryName(ServerC(s.Root))!);
        s.MakeFolderLink(ServerC(s.Root), real);
        s.Service.FolderRule = _ => TwoDrives(s.Root, ServerC(s.Root));
        return s;
    }

    [Fact]
    public async Task A_server_folder_kept_on_a_second_drive_still_restores_in_its_real_folder()
    {
        var s = SecondDriveServer();

        Assert.True(await s.Restore(s.Zip("w.zip", withWorld: true)));

        Assert.Equal(1, s.Coordinator.KillRequests);
        var realTree = Path.Combine(DriveD(s.Root), "servers", "game", "1001", "userdir", "Saved", "SaveGames");
        Assert.Equal("restored world", File.ReadAllText(Path.Combine(realTree, RestoreTestServer.WorldEntry)));
        Assert.True(RestoreFolders.IsLink(ServerC(s.Root)), "the server folder link is still the link");
        Assert.Single(s.Service.ListSnapshots());
    }

    [Fact]
    public async Task Without_the_second_drive_rule_the_same_link_is_refused_before_the_game_is_stopped()
    {
        var s = SecondDriveServer();
        s.Service.FolderRule = _ => TwoDrives(s.Root, Path.Combine(s.Root, "some-other-folder"));

        Assert.False(await s.Restore(s.Zip("w.zip", withWorld: true)));

        Assert.Equal(0, s.Coordinator.KillRequests);
    }

    [Fact]
    public void A_server_folder_link_to_another_servers_folder_is_refused()
    {
        var s = Server(r => Path.Combine(ServerC(r), "userdir"), liveWorld: false);
        var other = Path.Combine(DriveD(s.Root), "servers", "game", "2002");
        Directory.CreateDirectory(Path.Combine(other, "userdir", "Saved"));
        Directory.CreateDirectory(Path.GetDirectoryName(ServerC(s.Root))!);
        s.MakeFolderLink(ServerC(s.Root), other);

        Assert.NotNull(RestoreFolders.Problem(s.SavedDir, TwoDrives(s.Root, ServerC(s.Root)), out _));
    }

    [Fact]
    public void A_link_one_level_above_the_server_folder_is_refused()
    {
        var s = Server(liveWorld: false);
        var realGame = Path.Combine(s.Root, "elsewhere", "game");
        Directory.CreateDirectory(Path.Combine(realGame, "1001", "userdir", "Saved"));
        var gameFolder = Path.Combine(s.Root, "servers", "game");
        Directory.CreateDirectory(Path.GetDirectoryName(gameFolder)!);
        s.MakeFolderLink(gameFolder, realGame);
        var saved = Path.Combine(gameFolder, "1001", "userdir", "Saved");

        var why = RestoreFolders.Problem(saved, SecondDriveRule.ForUserFolder(Path.Combine(gameFolder, "1001", "userdir")), out _);

        Assert.NotNull(why);
        Assert.Contains("link", why);
    }

    [Fact]
    public void Folders_that_do_not_exist_yet_are_fine_and_kept_as_written()
    {
        var s = Server(liveWorld: false);

        var why = RestoreFolders.Problem(s.SavedDir, SecondDriveRule.ForUserFolder(s.UserDir), out var real);

        Assert.Null(why);
        Assert.Equal(Path.GetFullPath(s.SavedDir), real);
    }

    [Fact]
    public void A_file_where_a_folder_should_be_is_refused()
    {
        var s = Server(liveWorld: false);
        Directory.CreateDirectory(s.UserDir);
        File.WriteAllText(s.SavedDir, "a file, not a folder");

        Assert.NotNull(RestoreFolders.Problem(s.SavedDir, SecondDriveRule.ForUserFolder(s.UserDir), out _));
        Assert.NotNull(RestoreFolders.EntryProblem(s.SavedDir));
    }

    // ---- the restore's own clean-up never goes through a link ----

    [Fact]
    public void Deleting_a_restore_folder_removes_a_link_inside_it_and_never_what_it_points_at()
    {
        var s = Server(liveWorld: false);
        var folder = Path.Combine(s.Root, "SaveGames.old-test");
        Directory.CreateDirectory(Path.Combine(folder, "deeper"));
        File.WriteAllText(Path.Combine(folder, "deeper", "a.sav"), "a");
        s.MakeFolderLink(Path.Combine(folder, "deeper", "linked"), s.Outside);

        RestoreFolders.DeleteTree(folder);

        Assert.False(Directory.Exists(folder));
        s.OutsideUntouched();
    }

    [Fact]
    public void A_restore_folder_that_is_itself_a_link_is_not_deleted()
    {
        var s = Server(liveWorld: false);
        var link = Path.Combine(s.Root, "SaveGames.old-link");
        s.MakeFolderLink(link, s.Outside);

        Assert.Throws<IOException>(() => RestoreFolders.DeleteTree(link));

        s.OutsideUntouched();
        Assert.True(RestoreFolders.IsLink(link));
    }
}

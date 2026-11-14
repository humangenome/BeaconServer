using System.Diagnostics;
using System.IO.Compression;
using BeaconServer.Services;

namespace BeaconServer.Tests;

/// <summary>
/// A snapshot never follows a link: a folder link (a junction on Windows) or a file link inside the
/// save folder stays out of the zip (so does a file with a second name, a hard link), and a save folder
/// that is itself a link is refused with no zip.
/// </summary>
public sealed class SnapshotWalkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "snapwalk-" + Guid.NewGuid().ToString("N"));
    private string Save => Path.Combine(_root, "save");
    private string Outside => Path.Combine(_root, "outside");
    private string Zip => Path.Combine(_root, "snapshot.zip");

    public SnapshotWalkTests()
    {
        Directory.CreateDirectory(Path.Combine(Save, "slot", "deeper"));
        File.WriteAllText(Path.Combine(Save, "slot", "world.sav"), "world");
        File.WriteAllText(Path.Combine(Save, "slot", "deeper", "part.sav"), "part");
        File.WriteAllText(Path.Combine(Save, "top.sav"), "top");
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "outside.txt"), "not part of the save");
    }

    public void Dispose()
    {
        // Every link this class made goes first, by itself (the link only, never what it points at).
        foreach (var link in new[] { Path.Combine(Save, "slot", "linked"), Path.Combine(_root, "linked-save"), Path.Combine(Save, "linked-file.txt") })
        {
            try
            {
                var info = new FileInfo(link);
                if ((int)info.Attributes == -1 || !info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (OperatingSystem.IsWindows() && info.Attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(link, recursive: false);
                else File.Delete(link);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void WithoutLinksEveryFileIsInTheSnapshot()
    {
        var left = SaveOrchestratorService.CreateSaveGamesZip(Save, Zip);

        Assert.Empty(left);
        Assert.Equal(new[] { "slot/deeper/part.sav", "slot/world.sav", "top.sav" }, EntryNames());
    }

    [Fact]
    public void AFolderLinkInTheSaveFolderStaysOutOfTheSnapshot()
    {
        MakeFolderLink(Path.Combine(Save, "slot", "linked"), Outside);

        var left = SaveOrchestratorService.CreateSaveGamesZip(Save, Zip);

        Assert.Equal(new[] { "slot/linked" }, left);
        Assert.Equal(new[] { "slot/deeper/part.sav", "slot/world.sav", "top.sav" }, EntryNames());
        Assert.True(File.Exists(Path.Combine(Outside, "outside.txt")));
    }

    [Fact]
    public void AFileLinkInTheSaveFolderStaysOutOfTheSnapshot()
    {
        var link = Path.Combine(Save, "linked-file.txt");
        try { File.CreateSymbolicLink(link, Path.Combine(Outside, "outside.txt")); }
        catch (Exception e) when (OperatingSystem.IsWindows() && (e is UnauthorizedAccessException || e is IOException))
        {
            return; // this Windows account may not make file links; the folder-link tests still run
        }

        var left = SaveOrchestratorService.CreateSaveGamesZip(Save, Zip);

        Assert.Equal(new[] { "linked-file.txt" }, left);
        Assert.DoesNotContain("linked-file.txt", EntryNames());
    }

    [Fact]
    public void AFileWithASecondNameStaysOutOfTheSnapshot()
    {
        if (!OperatingSystem.IsWindows()) return; // the name count is read on Windows only (the servers run there)
        var second = Path.Combine(Save, "linked-hard.txt");
        RunCmd("mklink", "/H", second, Path.Combine(Outside, "outside.txt"));
        Assert.Equal(2u, SnapshotWalk.HardLinkCount(second));
        Assert.Equal(1u, SnapshotWalk.HardLinkCount(Path.Combine(Save, "top.sav")));

        var left = SaveOrchestratorService.CreateSaveGamesZip(Save, Zip);

        Assert.Equal(new[] { "linked-hard.txt" }, left);
        Assert.DoesNotContain("linked-hard.txt", EntryNames());
    }

    [Fact]
    public void ASaveFolderThatIsALinkIsRefusedAndNoZipIsMade()
    {
        var linkedSave = Path.Combine(_root, "linked-save");
        MakeFolderLink(linkedSave, Save);

        var e = Assert.Throws<IOException>(() => SaveOrchestratorService.CreateSaveGamesZip(linkedSave, Zip));

        Assert.Contains("link", e.Message);
        Assert.False(File.Exists(Zip));
    }

    [Fact]
    public void HiddenFilesStayInTheSnapshot()
    {
        var hidden = Path.Combine(Save, "slot", ".hidden.sav");
        File.WriteAllText(hidden, "hidden");
        if (OperatingSystem.IsWindows()) File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        SaveOrchestratorService.CreateSaveGamesZip(Save, Zip);

        Assert.Contains("slot/.hidden.sav", EntryNames());
    }
    // The server's own folder kept on a second drive, with two stand-in "drives" inside the test folder:
    // C\servers\game\1001 is a link to D\servers\game\1001, and the save folder sits under it.
    private string DriveC => Path.Combine(_root, "C") + Path.DirectorySeparatorChar;
    private string DriveD => Path.Combine(_root, "D") + Path.DirectorySeparatorChar;
    private string ServerC => Path.Combine(DriveC, "servers", "game", "1001");

    private SecondDriveRule TwoDrives(bool targetFixed = true, string? serverFolder = null) => new()
    {
        ServerFolder = serverFolder ?? ServerC,
        DriveOf = p => p.StartsWith(DriveC, StringComparison.Ordinal) ? DriveC : p.StartsWith(DriveD, StringComparison.Ordinal) ? DriveD : null,
        IsFixedDrive = d => d != DriveD || targetFixed,
    };

    // ServerC links to D\servers\<game>\<number>; returns the save folder seen through ServerC.
    private string MakeSecondDriveServer(string game = "game", string number = "1001")
    {
        var real = Path.Combine(DriveD, "servers", game, number);
        Directory.CreateDirectory(Path.Combine(real, "UserDir", "Saved", "SaveGames", "slot"));
        File.WriteAllText(Path.Combine(real, "UserDir", "Saved", "SaveGames", "slot", "world.sav"), "world");
        Directory.CreateDirectory(Path.GetDirectoryName(ServerC)!);
        MakeFolderLink(ServerC, real);
        return Path.Combine(ServerC, "UserDir", "Saved", "SaveGames");
    }

    [Fact]
    public void TheServersOwnFolderOnASecondDriveIsAccepted()
    {
        var save = MakeSecondDriveServer();
        var left = new List<string>();

        var files = SnapshotWalk.PlainFiles(save, left, out var walked, rule: TwoDrives());

        Assert.Empty(left);
        Assert.Equal(Path.Combine(DriveD, "servers", "game", "1001", "UserDir", "Saved", "SaveGames"), walked);
        Assert.Equal(new[] { "slot/world.sav" }, files.Select(f => Path.GetRelativePath(walked, f).Replace('\\', '/')).ToArray());
    }

    [Fact]
    public void BackupsStillWorkWhenTheServerFolderIsOnASecondDriveAndLinksInsideStayOut()
    {
        var save = MakeSecondDriveServer();
        MakeFolderLink(Path.Combine(save, "slot", "linked"), Outside);

        var left = SaveOrchestratorService.CreateSaveGamesZip(save, Zip, TwoDrives());

        Assert.Equal(new[] { "slot/linked" }, left);
        Assert.Equal(new[] { "slot/world.sav" }, EntryNames());
    }

    [Fact]
    public void WithoutTheServerFolderNamedNoLinkAboveTheSaveFolderIsAccepted()
    {
        var save = MakeSecondDriveServer();

        Assert.Throws<IOException>(() => SaveOrchestratorService.CreateSaveGamesZip(save, Zip));
        Assert.False(File.Exists(Zip));
    }

    [Theory]
    [InlineData("game", "1002")]
    [InlineData("other", "1001")]
    public void AServerFolderLinkToAnotherServersFolderIsRefused(string game, string number)
    {
        var save = MakeSecondDriveServer(game, number);

        Assert.Throws<IOException>(() => SnapshotWalk.PlainFiles(save, new List<string>(), out _, rule: TwoDrives()));
    }

    [Fact]
    public void AServerFolderLinkToADriveThatIsNotFixedIsRefused()
    {
        var save = MakeSecondDriveServer();

        Assert.Throws<IOException>(() => SnapshotWalk.PlainFiles(save, new List<string>(), out _, rule: TwoDrives(targetFixed: false)));
    }

    [Fact]
    public void AServerFolderLinkWhoseFolderIsItselfALinkIsRefused()
    {
        var hop = Path.Combine(_root, "hop");
        Directory.CreateDirectory(Path.Combine(hop, "UserDir", "Saved", "SaveGames"));
        var target = Path.Combine(DriveD, "servers", "game", "1001");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        MakeFolderLink(target, hop);
        Directory.CreateDirectory(Path.GetDirectoryName(ServerC)!);
        MakeFolderLink(ServerC, target);

        Assert.Throws<IOException>(() => SnapshotWalk.PlainFiles(Path.Combine(ServerC, "UserDir", "Saved", "SaveGames"), new List<string>(), out _, rule: TwoDrives()));
    }

    [Fact]
    public void ALinkAnywhereElseAboveTheSaveFolderIsRefused()
    {
        // the server folder is real, but its UserDir links to the same path on the other drive
        var real = Path.Combine(DriveD, "servers", "game", "1001");
        Directory.CreateDirectory(Path.Combine(real, "UserDir", "Saved", "SaveGames"));
        Directory.CreateDirectory(ServerC);
        MakeFolderLink(Path.Combine(ServerC, "UserDir"), Path.Combine(real, "UserDir"));

        Assert.Throws<IOException>(() => SnapshotWalk.PlainFiles(Path.Combine(ServerC, "UserDir", "Saved", "SaveGames"), new List<string>(), out _, rule: TwoDrives()));
    }

    [Fact]
    public void ALinkOneLevelAboveTheServerFolderIsRefused()
    {
        // C\servers\game links to D\servers\game: the same path on another drive, but not the server's own folder
        var realGame = Path.Combine(DriveD, "servers", "game");
        Directory.CreateDirectory(Path.Combine(realGame, "1001", "UserDir", "Saved", "SaveGames"));
        Directory.CreateDirectory(Path.Combine(DriveC, "servers"));
        MakeFolderLink(Path.Combine(DriveC, "servers", "game"), realGame);

        Assert.Throws<IOException>(() => SnapshotWalk.PlainFiles(Path.Combine(ServerC, "UserDir", "Saved", "SaveGames"), new List<string>(), out _, rule: TwoDrives()));
    }

    [Fact]
    public void ASecondLinkUnderTheServerFolderLinkIsRefused()
    {
        var save = MakeSecondDriveServer();
        var realSaved = Path.Combine(DriveD, "servers", "game", "1001", "UserDir", "Saved");
        var elsewhere = Path.Combine(_root, "elsewhere-saved");
        Directory.Move(realSaved, elsewhere);
        MakeFolderLink(realSaved, elsewhere);

        Assert.Throws<IOException>(() => SnapshotWalk.PlainFiles(save, new List<string>(), out _, rule: TwoDrives()));
    }

    private string[] EntryNames()
    {
        using var zip = ZipFile.OpenRead(Zip);
        return zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    // A junction on Windows (no special right needed), a symbolic link elsewhere.
    private static void MakeFolderLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        RunCmd("mklink", "/J", link, target);
    }

    private static void RunCmd(params string[] words)
    {
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "/d", "/c" }.Concat(words)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(30_000);
        if (p.ExitCode != 0) throw new IOException(string.Join(' ', words.Take(2)) + " failed: " + p.StandardError.ReadToEnd());
    }
}

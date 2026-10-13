using System.IO.Compression;
using BeaconServer.Configuration;
using BeaconServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BeaconServer.Tests;

public sealed class RestoreTests
{
    [Fact]
    public async Task Restore_DoesNotTurnOnAutoSnapshots_WhenTheyAreDisabled()
    {
        using var temp = new RestoreTempDir();
        WriteLiveSave(temp, "savegame_0.sav", "live world");
        var zip = WriteZip(temp, ("savegame_0.sav", "restored world"));

        using var svc = NewService(temp, snapshotsEnabled: false);
        (await svc.RestoreFromZipPathAsync(zip, "test", "restore:test")).Should().BeTrue();

        File.ReadAllText(Path.Combine(temp.SaveGames, "savegame_0.sav")).Should().Be("restored world");
        svc.ListSnapshots().Should().HaveCount(1, "only the pre-restore snapshot is taken");
        WatcherOf(svc).Should().BeNull("a restore must not arm the autosave watcher when snapshots are off");
    }

    [Fact]
    public async Task Restore_RearmsAutoSnapshots_WhenTheyAreEnabled()
    {
        using var temp = new RestoreTempDir();
        WriteLiveSave(temp, "savegame_0.sav", "live world");
        var zip = WriteZip(temp, ("savegame_0.sav", "restored world"));

        using var svc = NewService(temp, snapshotsEnabled: true);
        (await svc.RestoreFromZipPathAsync(zip, "test", "restore:test")).Should().BeTrue();

        WatcherOf(svc).Should().NotBeNull("auto-snapshots stay on after a restore when the host turned them on");
    }

    [Fact]
    public async Task Import_OfAWorldInAnotherSlot_BecomesSavegame0()
    {
        using var temp = new RestoreTempDir();
        WriteLiveSave(temp, "savegame_0.sav", "live world");
        // A SaveGames folder from a player's own PC can hold its world in any slot.
        var zip = WriteZip(temp, ("savegame_2.sav", "imported world"), ("FrontendSavedState.sav", "menu"));

        using var svc = NewService(temp, snapshotsEnabled: false);
        (await svc.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore")).Should().BeTrue();

        File.ReadAllText(Path.Combine(temp.SaveGames, "savegame_0.sav")).Should().Be("imported world");
        File.Exists(Path.Combine(temp.SaveGames, "savegame_2.sav")).Should().BeFalse();
        File.ReadAllText(Path.Combine(temp.SaveGames, "FrontendSavedState.sav")).Should().Be("menu");
    }

    [Fact]
    public async Task Import_WithNoSaveAtTheRoot_IsRefused_AndTheLiveWorldStays()
    {
        using var temp = new RestoreTempDir();
        WriteLiveSave(temp, "savegame_0.sav", "live world");
        var zip = WriteZip(temp, ("SaveGames/savegame_0.sav", "nested world"));

        using var svc = NewService(temp, snapshotsEnabled: false);
        (await svc.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore")).Should().BeFalse();

        File.ReadAllText(Path.Combine(temp.SaveGames, "savegame_0.sav")).Should().Be("live world");
    }

    [Fact]
    public void SupervisesGame_IsFalse_WhenTheHostStartsTheGame()
    {
        var hostManaged = new BeaconServerOptions { SnInstallRoot = "", SnExecutablePath = "" };
        SnProcessSupervisorService.SupervisesGame(hostManaged).Should().BeFalse();
    }

    [Fact]
    public void SupervisesGame_FollowsTheConfiguredGame_OnWindows()
    {
        var selfHosted = new BeaconServerOptions { SnInstallRoot = @"C:\Beacon\game" };
        var byExe = new BeaconServerOptions { SnInstallRoot = "", SnExecutablePath = @"C:\Games\SN2\Subnautica2-Win64-Shipping.exe" };
        SnProcessSupervisorService.SupervisesGame(selfHosted).Should().Be(OperatingSystem.IsWindows());
        SnProcessSupervisorService.SupervisesGame(byExe).Should().Be(OperatingSystem.IsWindows());
    }

    private static SaveOrchestratorService NewService(RestoreTempDir temp, bool snapshotsEnabled)
    {
        var opts = new BeaconServerOptions
        {
            SnInstallRoot = "",
            SnExecutablePath = "",
            SnUserDir = temp.UserDir,
            SaveDir = Path.Combine(temp.Path, "Saves"),
            HmacKeyPath = Path.Combine(temp.Path, "data", "hmac.key"),
            Sn2PidFile = "",
            SnapshotsEnabled = snapshotsEnabled,
        };
        Directory.CreateDirectory(Path.Combine(temp.Path, "data"));
        var coordinator = new Sn2RestartCoordinator(NullLogger<Sn2RestartCoordinator>.Instance, Options.Create(opts));
        return new SaveOrchestratorService(NullLogger<SaveOrchestratorService>.Instance, Options.Create(opts),
            new PipeServerState(NullLogger<PipeServerState>.Instance), coordinator);
    }

    private static object? WatcherOf(SaveOrchestratorService svc) =>
        typeof(SaveOrchestratorService)
            .GetField("_watcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(svc);

    private static void WriteLiveSave(RestoreTempDir temp, string name, string text)
    {
        Directory.CreateDirectory(temp.SaveGames);
        File.WriteAllText(Path.Combine(temp.SaveGames, name), text);
    }

    private static string WriteZip(RestoreTempDir temp, params (string Name, string Text)[] entries)
    {
        var zip = Path.Combine(temp.Path, "incoming-" + Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(text);
        }
        return zip;
    }

    private sealed class RestoreTempDir : IDisposable
    {
        public RestoreTempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "beacon-restore-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string UserDir => System.IO.Path.Combine(Path, "UserDir");
        public string SaveGames => System.IO.Path.Combine(UserDir, "Saved", "SaveGames");

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}

using BeaconServer.Services;
using BeaconServer.Configuration;

namespace BeaconServer.Tests;

public sealed class SnProcessSupervisorServiceTests
{
    [Fact]
    public void BuildHostTravelUrlPinsCanonicalSaveSlot()
    {
        var url = SnProcessSupervisorService.BuildHostTravelUrl();

        Assert.Equal(
            "/Game/Maps/Awake?listen?slotname=savegame_0?SlotName=savegame_0?SaveSlot=savegame_0?LoadGame=savegame_0?SaveGame=savegame_0",
            url);
    }

    [Fact]
    public void BuildHostTravelOptionsEscapesCustomSaveSlot()
    {
        var options = SnProcessSupervisorService.BuildHostTravelOptions("save slot 1");

        Assert.Equal(
            "?listen?slotname=save%20slot%201?SlotName=save%20slot%201?SaveSlot=save%20slot%201?LoadGame=save%20slot%201?SaveGame=save%20slot%201",
            options);
    }

    [Fact]
    public void ResolveSn2ExecutablePathDetectsWinGdkLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-tests", Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(root, "Subnautica 2", "Content", "Subnautica2", "Binaries", "WinGDK", "Subnautica2-WinGDK-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");

        try
        {
            var resolved = SnProcessSupervisorService.ResolveSn2ExecutablePath(new BeaconServerOptions
            {
                SnInstallRoot = root,
            });

            Assert.Equal(exe, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ResolveSn2ExecutablePathPrefersExplicitExecutablePath()
    {
        var explicitPath = Path.Combine(Path.GetTempPath(), "Subnautica2-WinGDK-Shipping.exe");

        var resolved = SnProcessSupervisorService.ResolveSn2ExecutablePath(new BeaconServerOptions
        {
            SnInstallRoot = @"C:\Beacon\game",
            SnExecutablePath = explicitPath,
        });

        Assert.Equal(Path.GetFullPath(explicitPath), resolved);
    }
}

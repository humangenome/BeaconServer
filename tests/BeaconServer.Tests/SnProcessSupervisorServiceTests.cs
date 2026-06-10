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
            "/Game/Maps/Awake?listen?LaunchType=LoadGame?SaveSlotName=savegame_0",
            url);
    }

    [Fact]
    public void BuildHostTravelOptionsEscapesCustomSaveSlot()
    {
        var options = SnProcessSupervisorService.BuildHostTravelOptions("save slot 1");

        Assert.Equal(
            "?listen?LaunchType=LoadGame?SaveSlotName=save%20slot%201",
            options);
    }

    [Fact]
    public void BuildEngineIniContentIncludesSn2InternetBandwidthBudget()
    {
        var ini = SnProcessSupervisorService.BuildEngineIniContent(10177, "?listen?LaunchType=LoadGame?SaveSlotName=save%20slot%201");

        Assert.Contains("[/Script/OnlineSubsystemUtils.IpNetDriver]", ini);
        Assert.Contains("NetServerMaxTickRate=30", ini);
        Assert.Contains("MaxClientRate=2097152", ini);
        Assert.Contains("MaxInternetClientRate=2097152", ini);
        Assert.Contains("[/Script/Engine.Player]", ini);
        Assert.Contains("ConfiguredInternetSpeed=2097152", ini);
        Assert.Contains("ConfiguredLanSpeed=2097152", ini);
        Assert.Contains("[/Script/Engine.GameNetworkManager]", ini);
        Assert.Contains("TotalNetBandwidth=16777216", ini);
        Assert.Contains("MaxDynamicBandwidth=2097152", ini);
        Assert.Contains("MinDynamicBandwidth=524288", ini);
        Assert.Contains("net.UseAdaptiveNetUpdateFrequency=1", ini);
        Assert.Contains("LocalMapOptions=?listen?LaunchType=LoadGame?SaveSlotName=save%20slot%201", ini);
        Assert.Contains("Port=10177", ini);
    }

    [Fact]
    public void BuildEngineIniContentDisablesSteamEosAndFmodForHeadless()
    {
        // A headless / GPU-less host must disable Steam (SteamAPI_RestartAppIfNecessary
        // force-exits when Steam isn't running) + EOS, and use FMOD TYPE_NOSOUND.
        // Without these the standalone server boot-loops. Keep parity with the panel.
        var ini = SnProcessSupervisorService.BuildEngineIniContent(10177, "?listen");

        Assert.Contains("[OnlineSubsystemSteam]", ini);
        Assert.Contains("bEnabled=False", ini);
        Assert.Contains("SteamDevAppId=0", ini);
        Assert.Contains("[OnlineSubsystemEOS]", ini);
        Assert.Contains("[/Script/FMODStudio.FMODSettings]", ini);
        Assert.Contains("OutputType=TYPE_NOSOUND", ini);
        Assert.Contains("[OnlineSubsystem]", ini);
        Assert.Contains("DefaultPlatformService=Null", ini);
    }

    [Fact]
    public void PatchUe4ssServerSettingsForcesHeadlessValues()
    {
        // A headless dedicated server must not pop a UE4SS console/GUI window;
        // existing keys are overwritten and missing ones appended.
        var ini = "[General]\r\nConsoleEnabled = 1\r\nGuiConsoleEnabled = 1\r\nGraphicsAPI = dx12\r\n";
        var patched = SnProcessSupervisorService.PatchUe4ssServerSettings(ini);

        Assert.Contains("ConsoleEnabled = 0", patched);
        Assert.Contains("GuiConsoleEnabled = 0", patched);
        Assert.Contains("GuiConsoleVisible = 0", patched); // appended — not in the source
        Assert.Contains("GraphicsAPI = d3d11", patched);
        Assert.DoesNotContain("ConsoleEnabled = 1", patched);
        Assert.DoesNotContain("GraphicsAPI = dx12", patched);
    }

    [Fact]
    public void ResolveSn2ExecutablePathDetectsWinGdkLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-tests", Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(root, "Content", "Subnautica2", "Binaries", "WinGDK", "Subnautica2-WinGDK-Shipping.exe");
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

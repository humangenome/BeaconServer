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
    public void BuildEngineIniContentCapsFrameRateWithoutFixedTimestep()
    {
        // t.MaxFPS caps host CPU while still passing REAL frame deltas.
        // bUseFixedFrameRate must never appear: a fixed timestep makes slow
        // frames dilate game time (slow motion) instead of dropping rate.
        var ini = SnProcessSupervisorService.BuildEngineIniContent(10177, "?listen");

        Assert.Contains("t.MaxFPS=30", ini);
        // Key=value forms only — the explanatory ini comment mentions the
        // key names without '='.
        Assert.DoesNotContain("bUseFixedFrameRate=", ini);
        Assert.DoesNotContain("\nFixedFrameRate=", ini);
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
    public void PatchUe4ssServerSettingsHealsClientProfileToServerSemantics()
    {
        // If the slimmed CLIENT UE4SS profile (hot-path hooks off, object
        // cache off) ever gets staged onto a self-host, the patch must flip
        // back the keys the server stack depends on: BeaconAuth/BeaconRoster
        // RegisterHook on K2_PostLogin needs ProcessInternal +
        // ProcessLocalScriptFunction; ExecuteInGameThread needs EngineTick;
        // server mods need the UObject array cache for cheap FindAllOf.
        var ini = "[General]\r\nbUseUObjectArrayCache = false\r\n" +
                  "[Hooks]\r\nHookProcessInternal = 0\r\nHookProcessLocalScriptFunction = 0\r\nHookEngineTick = 0\r\n";
        var patched = SnProcessSupervisorService.PatchUe4ssServerSettings(ini);

        Assert.Contains("bUseUObjectArrayCache = true", patched);
        Assert.Contains("HookProcessInternal = 1", patched);
        Assert.Contains("HookProcessLocalScriptFunction = 1", patched);
        Assert.Contains("HookEngineTick = 1", patched);
        Assert.DoesNotContain("bUseUObjectArrayCache = false", patched);
        Assert.DoesNotContain("HookProcessInternal = 0", patched);
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

    [Fact]
    public void PlatformCheckExitIsRecognisedFromTheGameLog()
    {
        // A host whose Steam client cannot confirm the copy of the game: Steam
        // refuses, the game asks to quit on its own, and the world never comes up.
        var tail = string.Join("\n",
            "LogSteamShared: Warning: SteamAPI failed to initialize, conditions not met.",
            "LogOnline: OSS: Created online subsystem instance for: NULL",
            "LogWorld: Bringing World /Game/Maps/L_ClientLobby.L_ClientLobby up for play",
            "LogGenericPlatformMisc: FPlatformMisc::RequestExit(0, <NoCallSiteInfo>)");

        Assert.True(SnProcessSupervisorService.IsPlatformCheckExitLog(tail));
    }

    [Fact]
    public void PlatformCheckExitIsRecognisedFromTheMessageItself()
    {
        Assert.True(SnProcessSupervisorService.IsPlatformCheckExitLog(
            "The game was not started via the platform launcher and will be closed."));
    }

    [Fact]
    public void AHealthyHostIsNotMistakenForThePlatformCheck()
    {
        // Steam is unavailable here too, but the world came up, so this is a normal
        // headless run and the supervisor must keep its usual restart behaviour.
        var tail = string.Join("\n",
            "LogSteamShared: Warning: SteamAPI failed to initialize, conditions not met.",
            "LogWorld: Bringing World /Game/Maps/Main/L_Main up for play",
            "LogGenericPlatformMisc: FPlatformMisc::RequestExit(0, <NoCallSiteInfo>)");

        Assert.False(SnProcessSupervisorService.IsPlatformCheckExitLog(tail));
    }

    [Fact]
    public void ACrashIsNotMistakenForThePlatformCheck()
    {
        Assert.False(SnProcessSupervisorService.IsPlatformCheckExitLog(
            "LogWindows: Error: === Critical error: ===\nAssertion failed"));
        Assert.False(SnProcessSupervisorService.IsPlatformCheckExitLog(string.Empty));
    }

    [Fact]
    public void Sn2SteamAppIdIsTheShippingApplicationId()
    {
        Assert.Equal("1962700", SnProcessSupervisorService.Sn2SteamAppId);
    }
}

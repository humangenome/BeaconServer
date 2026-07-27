using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using BeaconServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

/// <summary>
/// Launches Subnautica 2 with UE4SS + Beacon.dll attached, patches the
/// user-scope Engine.ini for the OSS=Null + IpNetDriver override on every
/// launch, watches the process, restarts on unexpected exit.
///
/// Crash policy: if Subnautica 2 exits within MinHealthyUptimeSeconds, treat as
/// "boot loop" and back off exponentially. After a stable run, exit codes
/// reset the backoff.
/// </summary>
public sealed class SnProcessSupervisorService : BackgroundService
{
    private readonly ILogger<SnProcessSupervisorService> _log;
    private readonly BeaconServerOptions _opts;
    private readonly HmacKeyService _hmac;
    private readonly Sn2RestartCoordinator _coordinator;

    private const int MinHealthyUptimeSeconds = 60;
    private const int MaxBackoffSeconds = 300;
    public const string Sn2CanonicalSaveSlot = "savegame_0";
    // Subnautica 2's Steam application id. Subnautica 2 asks the Steam client to
    // confirm the copy it is running, and that call only works when the app id is
    // discoverable. Steam normally supplies it as an environment variable when it
    // starts the game; a headless host started by BeaconServer has to supply it
    // itself, either through steam_appid.txt next to the executable or through the
    // same environment variables. Beacon writes both.
    internal const string Sn2SteamAppId = "1962700";
    private const string Sn2MapPath = "/Game/Maps/Awake";
    private static readonly string[][] Sn2ExeRelativePaths =
    [
        ["Subnautica2", "Binaries", "Win64", "Subnautica2-Win64-Shipping.exe"],
        ["Binaries", "Win64", "Subnautica2-Win64-Shipping.exe"],
        ["Subnautica 2", "Binaries", "Win64", "Subnautica2-Win64-Shipping.exe"],
        ["Subnautica2", "Content", "Subnautica2", "Binaries", "WinGDK", "Subnautica2-WinGDK-Shipping.exe"],
        ["Subnautica 2", "Content", "Subnautica2", "Binaries", "WinGDK", "Subnautica2-WinGDK-Shipping.exe"],
        ["Content", "Subnautica2", "Binaries", "WinGDK", "Subnautica2-WinGDK-Shipping.exe"],
        ["Subnautica2", "Binaries", "WinGDK", "Subnautica2-WinGDK-Shipping.exe"],
        ["Binaries", "WinGDK", "Subnautica2-WinGDK-Shipping.exe"],
        ["Subnautica2-WinGDK-Shipping.exe"],
        ["Subnautica2-Win64-Shipping.exe"],
    ];
    public SnProcessSupervisorService(
        ILogger<SnProcessSupervisorService> log,
        IOptions<BeaconServerOptions> opts,
        HmacKeyService hmac,
        Sn2RestartCoordinator coordinator)
    {
        _log = log;
        _opts = opts.Value;
        _hmac = hmac;
        _coordinator = coordinator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if ((string.IsNullOrEmpty(_opts.SnInstallRoot) && string.IsNullOrEmpty(_opts.SnExecutablePath))
            || !OperatingSystem.IsWindows())
        {
            _log.LogWarning("Process supervisor idle: Subnautica 2 executable not configured or not on Windows");
            return;
        }

        var backoffSeconds = 1;
        var consecutiveBootFailures = 0;
        const int maxConsecutiveBootFailures = 6;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for any in-flight restore to finish before relaunching.
                // The restore code holds the gate while it mutates SaveGames;
                // launching Subnautica 2 while that's in progress would race the file
                // copy and corrupt the world.
                await _coordinator.WaitForNoRestoreAsync(stoppingToken).ConfigureAwait(false);

                ApplyEngineIniPatch();
                StageUe4ss();
                WriteSteamAppIdFile();
                EmitPluginConfig();
                var start = DateTime.UtcNow;

                using var proc = LaunchGame();
                _log.LogInformation("Subnautica 2 launched: pid={Pid}", proc.Id);
                while (!stoppingToken.IsCancellationRequested && !proc.HasExited)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                }
                if (stoppingToken.IsCancellationRequested)
                {
                    if (!proc.HasExited)
                    {
                        _log.LogInformation("Stopping — sending Ctrl+C / Close to Subnautica 2 (pid={Pid})", proc.Id);
                        try { proc.CloseMainWindow(); } catch { }
                        if (!proc.WaitForExit(10_000)) proc.Kill(true);
                    }
                    return;
                }

                var uptime = DateTime.UtcNow - start;
                _log.LogWarning("Subnautica 2 exited code={Code} uptime={Uptime}s", proc.ExitCode, (int)uptime.TotalSeconds);

                if (uptime.TotalSeconds >= MinHealthyUptimeSeconds)
                {
                    backoffSeconds = 1; // stable run — reset backoff
                    consecutiveBootFailures = 0;
                }
                else
                {
                    consecutiveBootFailures++;
                    if (LooksLikePlatformCheckExit())
                    {
                        // Subnautica 2 closed itself because the Steam client could not
                        // confirm the copy of the game on this machine. Relaunching
                        // cannot fix that, so stop instead of looping forever and say
                        // exactly what has to change.
                        _log.LogError(
                            "Subnautica 2 closed itself with \"The game was not started via the platform launcher\". " +
                            "The game asks the Steam client to confirm this copy and the answer did not come back. " +
                            "For a headless host that means all of the following have to be true on the server machine: " +
                            "the Steam client is running (offline mode is fine) and signed in to an account that owns " +
                            "Subnautica 2, it is signed in as the same Windows user and in the same session that runs " +
                            "BeaconServer, and steam_appid.txt sits next to Subnautica2-Win64-Shipping.exe. " +
                            "Beacon writes steam_appid.txt for you, so the part left to check is the Steam client. " +
                            "Not relaunching — start Steam on the server and restart BeaconServer.");
                        return;
                    }
                    if (consecutiveBootFailures >= maxConsecutiveBootFailures)
                    {
                        // Hard stop instead of relaunching forever. After the headless
                        // launch args (-nullrhi) + Steam/EOS-disabled Engine.ini, an
                        // immediate exit this many times means a real problem the loop
                        // can't fix — most often the game files aren't installed.
                        _log.LogError(
                            "Subnautica 2 exited in under {Healthy}s {Count} times in a row — aborting the relaunch loop. " +
                            "Most common cause on a headless server: the game files are not installed under SnInstallRoot " +
                            "(install Subnautica 2 there via SteamCMD app 1962700, or copy the steamapps\\common\\Subnautica2 tree). " +
                            "Fix the underlying issue and restart BeaconServer.",
                            MinHealthyUptimeSeconds, consecutiveBootFailures);
                        return;
                    }
                    backoffSeconds = Math.Min(MaxBackoffSeconds, backoffSeconds * 2);
                    _log.LogWarning("Boot-loop suspected ({Count}/{Max}) — backing off {Seconds}s before restart",
                        consecutiveBootFailures, maxConsecutiveBootFailures, backoffSeconds);
                    try { await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Supervisor loop error — retry in 5s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private Process LaunchGame()
    {
        var exe = ResolveSn2ExecutablePath(_opts);
        if (!File.Exists(exe))
            throw new FileNotFoundException($"Subnautica 2 binary not found at {exe}");

        var args = string.Join(' ',
            $"-USERDIR={EscapeArg(_opts.SnUserDir)}",
            "-unattended",
            $"-port={_opts.GameplayPort}",
            "-log",
            // Headless server args. -nullrhi lets SN2 boot without a D3D12 adapter
            // on a GPU-less host (VPS / dedicated box) — without it UE5.6 RHI init
            // fails ("Failed to choose a D3D12 Adapter") and the game exits in ~1s,
            // which the supervisor would relaunch forever. -SaveToUserDir keeps
            // saves under -USERDIR; -NoVerifyGC matches the panel launch path.
            "-nullrhi",
            "-NoSplash",
            "-SaveToUserDir",
            "-NoVerifyGC",
            BuildHostTravelUrl());

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.EnvironmentVariables["BEACON_INSTANCE"] = _opts.InstanceId;
        // Tell the Steam client which application this process is. Steam sets these
        // when it starts a game; a headless host launched by BeaconServer has to set
        // them itself or the game's ownership check gets no answer and Subnautica 2
        // closes with "The game was not started via the platform launcher".
        // The environment variables work regardless of the working directory;
        // steam_appid.txt (written by WriteSteamAppIdFile) covers the same ground for
        // builds that only read the file.
        psi.EnvironmentVariables["SteamAppId"] = Sn2SteamAppId;
        psi.EnvironmentVariables["SteamGameId"] = Sn2SteamAppId;
        return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
    }

    // Write steam_appid.txt next to the Subnautica 2 executable. Steam reads it from
    // the game's working directory, and BeaconServer launches the game with its own
    // folder as the working directory, so that is where it goes. Idempotent: only
    // writes when the file is missing or holds a different id.
    private void WriteSteamAppIdFile()
    {
        string path;
        try
        {
            path = Path.Combine(Path.GetDirectoryName(ResolveSn2ExecutablePath(_opts))!, "steam_appid.txt");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "steam_appid.txt: cannot resolve the game folder yet");
            return;
        }

        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() == Sn2SteamAppId) return;
            File.WriteAllText(path, Sn2SteamAppId);
            _log.LogInformation("Wrote steam_appid.txt ({AppId}) at {Path}", Sn2SteamAppId, path);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Could not write steam_appid.txt at {Path}. Without it Subnautica 2 may close with " +
                "\"The game was not started via the platform launcher\". Create the file by hand with the " +
                "single line {AppId}.", path, Sn2SteamAppId);
        }
    }

    // Read the tail of Subnautica 2's own log and decide whether the last exit was the
    // storefront check closing the game rather than a crash or a missing install.
    private bool LooksLikePlatformCheckExit()
    {
        if (string.IsNullOrEmpty(_opts.SnUserDir)) return false;
        try
        {
            var logDir = Path.Combine(_opts.SnUserDir, "Saved", "Logs");
            var log = new DirectoryInfo(logDir).Exists
                ? new DirectoryInfo(logDir).GetFiles("Subnautica2*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
            if (log is null) return false;
            using var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > 512 * 1024) stream.Seek(-512 * 1024, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            return IsPlatformCheckExitLog(reader.ReadToEnd());
        }
        catch { return false; }
    }

    // Pure form of the log check so it can be tested without a game install.
    // The storefront exit has a recognisable shape: Steam could not confirm the copy,
    // the game asked to quit on its own, and the world never came up.
    internal static bool IsPlatformCheckExitLog(string logTail)
    {
        if (string.IsNullOrEmpty(logTail)) return false;
        if (logTail.Contains("not started via the platform launcher", StringComparison.OrdinalIgnoreCase)) return true;
        var steamRefused = logTail.Contains("SteamAPI failed to initialize", StringComparison.OrdinalIgnoreCase);
        var selfRequestedExit = logTail.Contains("RequestExit", StringComparison.Ordinal);
        var reachedTheWorld = logTail.Contains("/Game/Maps/Main", StringComparison.OrdinalIgnoreCase);
        return steamRefused && selfRequestedExit && !reachedTheWorld;
    }

    private void ApplyEngineIniPatch()
    {
        // Refuse to patch into a user directory that overlaps a vanilla SN2
        // install. This catches the case where SnUserDir was accidentally
        // pointed at the customer's Steam/Epic Subnautica 2 root and would otherwise
        // overwrite their vanilla Engine.ini.
        if (LooksLikeVanillaSn2Path(_opts.SnUserDir))
        {
            _log.LogError("Engine.ini patch refused: SnUserDir={Dir} looks like a vanilla Subnautica 2 install path. " +
                          "Beacon's user dir must be a separate folder (e.g. C:\\Beacon\\UserDir).",
                          _opts.SnUserDir);
            return;
        }

        var configDir = Path.Combine(_opts.SnUserDir, "Saved", "Config", "Windows");
        Directory.CreateDirectory(configDir);
        var enginePath = Path.Combine(configDir, "Engine.ini");

        var content = BuildEngineIniContent(_opts.GameplayPort, BuildHostTravelOptions());
        File.WriteAllText(enginePath, content);
        _log.LogInformation("Patched Engine.ini at {Path}", enginePath);
    }

    // Stage the UE4SS install + dwmapi proxy + Beacon.dll into the game install so
    // SN2 loads UE4SS -> BeaconLoader -> Beacon.dll (password gate, roster, chat,
    // lifepod fix). Mirrors the managed-host deploy. Panel deploys keep
    // SnInstallRoot empty so this service is idle there; this only runs for
    // standalone self-hosts. Idempotent — safe to re-run on every launch.
    private void StageUe4ss()
    {
        string exe, gameBinDir;
        try
        {
            exe = ResolveSn2ExecutablePath(_opts);
            gameBinDir = Path.GetDirectoryName(exe)!;
        }
        catch (Exception ex) { _log.LogError(ex, "UE4SS staging: cannot resolve game path"); return; }

        // The Beacon server package extracts as <root>\BeaconServer\BeaconServer.exe
        // with the ue4ss\ folder + Beacon.dll alongside at <root>\.
        var bundleRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        var srcUe4ss = Path.Combine(bundleRoot, "ue4ss");
        var srcProxy = Path.Combine(srcUe4ss, "dwmapi.dll");
        var srcBeaconDll = Path.Combine(bundleRoot, "Beacon.dll");

        if (!File.Exists(Path.Combine(srcUe4ss, "UE4SS.dll")))
        {
            _log.LogError(
                "UE4SS not found at {Dir} — Beacon's runtime (password gate, roster, chat, lifepod fix) will NOT load and " +
                "the server runs as a plain Subnautica 2 listen server. Re-extract the full Beacon-Server zip so the ue4ss\\ " +
                "folder + Beacon.dll sit next to the BeaconServer\\ folder.", srcUe4ss);
            return;
        }

        try
        {
            // 1. UE4SS install (UE4SS.dll + settings + Mods) -> <gameBinDir>\ue4ss\
            var dstUe4ss = Path.Combine(gameBinDir, "ue4ss");
            CopyDirectory(srcUe4ss, dstUe4ss);

            // 2. Server-tune the staged UE4SS settings (no console/GUI on a headless host).
            var settingsPath = Path.Combine(dstUe4ss, "UE4SS-settings.ini");
            if (File.Exists(settingsPath))
                File.WriteAllText(settingsPath, PatchUe4ssServerSettings(File.ReadAllText(settingsPath)));

            // 3. dwmapi proxy -> adjacent to the game exe (the hijack that loads UE4SS).
            if (File.Exists(srcProxy))
                File.Copy(srcProxy, Path.Combine(gameBinDir, "dwmapi.dll"), overwrite: true);
            else
                _log.LogError("UE4SS dwmapi proxy missing at {Proxy} — UE4SS cannot load. Re-extract the full server zip.", srcProxy);

            // 4. Beacon.dll -> the install root, where BeaconLoader walks up to find it
            //    (<root>\Subnautica2\Binaries\Win64 -> <root>\Beacon.dll).
            var installRoot = Path.GetFullPath(Path.Combine(gameBinDir, "..", "..", ".."));
            if (File.Exists(srcBeaconDll))
                File.Copy(srcBeaconDll, Path.Combine(installRoot, "Beacon.dll"), overwrite: true);
            else
                _log.LogError("Beacon.dll missing at {Src} — runtime hooks cannot load. Re-extract the full server zip.", srcBeaconDll);

            _log.LogInformation("Staged UE4SS + Beacon.dll into {GameBinDir}", gameBinDir);
        }
        catch (Exception ex) { _log.LogError(ex, "UE4SS staging failed"); }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // Pure transform (testable). Force the UE4SS console/GUI off and a safe
    // graphics API for a headless dedicated server, matching the managed host.
    // Also re-asserts the keys the SERVER profile depends on, so a client-
    // profile UE4SS-settings.ini (older bundle layouts shipped one file for
    // both roles; the client profile disables hot-path hooks) staged onto a
    // self-host is healed back to server semantics:
    //   * bUseUObjectArrayCache=true — server Lua mods poll FindAllOf/
    //     FindFirstOf; without the cache every call walks the full
    //     GUObjectArray on the game thread.
    //   * HookProcessInternal / HookProcessLocalScriptFunction — BeaconAuth's
    //     K2_PostLogin password gate and BeaconRoster's join/leave hooks are
    //     script-function RegisterHooks; Beacon.dll's lifepod-gate override
    //     also jumps through UE4SS's ProcessInternal detour. Without these
    //     the auth hook fails and the fail-closed watchdog kills SN2.
    //   * HookEngineTick — ExecuteInGameThread (EngineTick method) queue.
    internal static string PatchUe4ssServerSettings(string iniText)
    {
        var server = new (string Key, string Val)[]
        {
            ("ConsoleEnabled", "0"),
            ("GuiConsoleEnabled", "0"),
            ("GuiConsoleVisible", "0"),
            ("GraphicsAPI", "d3d11"),
            ("bUseUObjectArrayCache", "true"),
            ("HookProcessInternal", "1"),
            ("HookProcessLocalScriptFunction", "1"),
            ("HookEngineTick", "1"),
        };
        var text = iniText;
        foreach (var (k, v) in server)
        {
            var pattern = $@"(?m)^\s*{Regex.Escape(k)}\s*=.*$";
            text = Regex.IsMatch(text, pattern)
                ? Regex.Replace(text, pattern, $"{k} = {v}")
                : text + Environment.NewLine + $"{k} = {v}";
        }
        return text;
    }

    internal static string BuildEngineIniContent(int gameplayPort, string hostTravelOptions)
    {
        const string driver = "/Script/OnlineSubsystemUtils.IpNetDriver";
        return $"""
        ; Beacon-managed Engine.ini override — rewritten on every host launch.
        [OnlineSubsystem]
        DefaultPlatformService=Null

        [OnlineSubsystemNull]
        bSimulateForwarded=true

        ; Disable the Steam + EOS online subsystems. The SN2 binary has a Steam
        ; app id baked in; OnlineSubsystemSteam's startup calls
        ; SteamAPI_RestartAppIfNecessary when Steam isn't running, which force-exits
        ; a headless server. Disabling Steam (and EOS) is required for a
        ; Steam-less / headless host to boot.
        [OnlineSubsystemSteam]
        bEnabled=False
        SteamDevAppId=0

        [OnlineSubsystemEOS]
        bEnabled=False

        ; FMOD on a host with no audio device: force a silent mixer (TYPE_NOSOUND)
        ; so FMOD Studio initializes instead of crashing in fmodstudio.dll on the
        ; first played event. Do NOT pass the -nosound CLI flag — that skips Studio
        ; init entirely and NPEs on first play; TYPE_NOSOUND is the correct path.
        [/Script/FMODStudio.FMODSettings]
        OutputType=TYPE_NOSOUND
        bLoadAllBanks=False
        bLoadAllSampleData=False
        bEnableLiveUpdate=False

        [/Script/Engine.GameEngine]
        !NetDriverDefinitions=ClearArray
        +NetDriverDefinitions=(DefName="GameNetDriver",DriverClassName="{driver}",DriverClassNameFallback="{driver}")

        [/Script/EngineSettings.GameMapsSettings]
        LocalMapOptions={hostTravelOptions}

        [/Script/UWESaveSystem.UWESaveGameSubsystem]
        slotname={Sn2CanonicalSaveSlot}
        SlotName={Sn2CanonicalSaveSlot}
        SaveSlot={Sn2CanonicalSaveSlot}
        DefaultSlotName={Sn2CanonicalSaveSlot}
        DefaultSaveSlot={Sn2CanonicalSaveSlot}
        LoadGame={Sn2CanonicalSaveSlot}

        [/Script/UWELobby.UWELobbyGameMode]
        slotname={Sn2CanonicalSaveSlot}
        SlotName={Sn2CanonicalSaveSlot}
        SaveSlot={Sn2CanonicalSaveSlot}
        LoadGame={Sn2CanonicalSaveSlot}

        [/Script/OnlineSubsystemUtils.IpNetDriver]
        AllowPeerConnections=false
        AllowPeerVoice=false
        bClampListenServerTickRate=true
        NetServerMaxTickRate=30
        MaxClientRate=2097152
        MaxInternetClientRate=2097152
        ServerDesiredSocketReceiveBufferBytes=4194304
        ServerDesiredSocketSendBufferBytes=4194304
        ClientDesiredSocketReceiveBufferBytes=2097152
        ClientDesiredSocketSendBufferBytes=2097152
        NetConnectionTimeout=300
        InitialConnectTimeout=300.0
        ConnectionTimeout=300.0
        ServerTravelPause=4

        [/Script/Engine.Player]
        ConfiguredInternetSpeed=2097152
        ConfiguredLanSpeed=2097152

        [/Script/Engine.GameNetworkManager]
        TotalNetBandwidth=16777216
        MaxDynamicBandwidth=2097152
        MinDynamicBandwidth=524288

        [ConsoleVariables]
        ; Frame cap WITHOUT a fixed timestep. Deliberately no
        ; bUseFixedFrameRate/FixedFrameRate here (parity with the managed
        ; host): a fixed timestep hands the game a constant delta even when
        ; a frame runs long, so slow frames dilate game time (slow motion)
        ; instead of passing real deltas. t.MaxFPS only sleeps when frames
        ; are fast, so the host idles without ever distorting time.
        t.MaxFPS=30
        net.UseAdaptiveNetUpdateFrequency=1
        net.TrackQueuedActorThreshold=1
        net.TrackQueuedActorThresholdOwner=1

        [URL]
        Port={gameplayPort}
        """;
    }

    private void EmitPluginConfig()
    {
        var exe = ResolveSn2ExecutablePath(_opts);
        var pluginDir = Path.Combine(Path.GetDirectoryName(exe)!,
            "Mods", "Beacon");
        Directory.CreateDirectory(pluginDir);
        var configPath = Path.Combine(pluginDir, "beacon.config.json");

        // ServerPassword is INTENTIONALLY emitted empty as of v0.3.55.
        // The native Beacon.dll ApproveLogin hook would otherwise try to
        // enforce, but SN2's stock GameMode races with it and fatal-
        // crashes the dedicated process on host spawn (the ?Password=
        // option in the launch URL is dropped by SN2's internal map
        // travel from Awake -> L_Main, leaving the host's URL with no
        // password while the native check still expects one). Password
        // enforcement now lives in BeaconAuth.lua's K2_PostLogin hook,
        // which gates remote clients only and exempts the listen host.
        // Plugin sees no ServerPassword -> treats the server as open ->
        // no native enforcement -> no crash loop. _opts.ServerPassword
        // is the legacy field; _opts.BeaconAuthPassword is what
        // BeaconAuth.lua reads from appsettings.json directly.
        var payload = new
        {
            InstanceId = _opts.InstanceId,
            PipePath = $@"\\.\pipe\{_opts.PipeName}",
            HmacKeyHex = Convert.ToHexString(_hmac.Key),
            ServerPassword = "",
        };
        File.WriteAllText(configPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        _log.LogInformation("Emitted plugin config at {Path}", configPath);
    }

    private static string EscapeArg(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    internal static string ResolveSn2ExecutablePath(BeaconServerOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.SnExecutablePath))
        {
            return Path.GetFullPath(opts.SnExecutablePath);
        }

        foreach (var relativePath in Sn2ExeRelativePaths)
        {
            var candidate = Path.Combine([opts.SnInstallRoot, .. relativePath]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine([opts.SnInstallRoot, .. Sn2ExeRelativePaths[0]]);
    }

    public static string BuildHostTravelUrl(string saveSlot = Sn2CanonicalSaveSlot)
        => Sn2MapPath + BuildHostTravelOptions(saveSlot);

    public static string BuildHostTravelOptions(string saveSlot = Sn2CanonicalSaveSlot)
    {
        var slot = string.IsNullOrWhiteSpace(saveSlot) ? Sn2CanonicalSaveSlot : saveSlot.Trim();
        var escapedSlot = Uri.EscapeDataString(slot);
        return $"?listen?LaunchType=LoadGame?SaveSlotName={escapedSlot}";
    }

    /// <summary>
    /// Heuristic: does this path look like a Steam / Epic / MS Store install
    /// root for vanilla SN2? Used to refuse Engine.ini / plugin-config writes
    /// that would corrupt a vanilla install if SnUserDir/SnInstallRoot were
    /// misconfigured.
    /// </summary>
    internal static bool LooksLikeVanillaSn2Path(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        // Check the resolved real target too — a customer can junction
        // their Beacon user dir at C:\Beacon\userdir over a vanilla
        // Subnautica 2 install and the literal-string check passes while the
        // Engine.ini write lands inside the vanilla folder.
        return MatchesVanillaSubstring(path)
            || MatchesVanillaSubstring(TryResolveSymlinkTarget(path));
    }

    private static bool MatchesVanillaSubstring(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Replace('/', '\\').ToLowerInvariant();
        return p.Contains(@"\steamapps\common\")
            || p.Contains(@"\steamlibrary\")
            || p.Contains(@"\epicgameslauncher\")
            || p.Contains(@"\epic games\")
            || p.Contains(@"\windowsapps\");
    }

    private static string? TryResolveSymlinkTarget(string path)
    {
        try
        {
            // DirectoryInfo.LinkTarget on .NET 6+ returns the immediate
            // target of a junction/symlink; null otherwise. Path.GetFullPath
            // canonicalises any '..' segments. ResolveLinkTarget(true)
            // walks the chain (multiple junctions) but isn't strictly
            // needed for the common case.
            var di = new DirectoryInfo(path);
            if (!di.Exists) return null;
            var resolved = di.ResolveLinkTarget(true);
            return resolved?.FullName;
        }
        catch { return null; }
    }
}

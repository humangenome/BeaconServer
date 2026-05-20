using System.Diagnostics;
using System.Text.Json;
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
    private const string Sn2MapPath = "/Game/Maps/Awake";
    private static readonly string[] Sn2SaveSlotUrlKeys =
    [
        "slotname",
        "SlotName",
        "SaveSlot",
        "LoadGame",
        "SaveGame",
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
        if (string.IsNullOrEmpty(_opts.SnInstallRoot) || !OperatingSystem.IsWindows())
        {
            _log.LogWarning("Process supervisor idle: Subnautica 2 install root not configured or not on Windows");
            return;
        }

        var backoffSeconds = 1;
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
                }
                else
                {
                    backoffSeconds = Math.Min(MaxBackoffSeconds, backoffSeconds * 2);
                    _log.LogWarning("Boot-loop suspected — backing off {Seconds}s before restart", backoffSeconds);
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
        var exe = Path.Combine(_opts.SnInstallRoot,
            "Subnautica2", "Binaries", "Win64", "Subnautica2-Win64-Shipping.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException($"Subnautica 2 binary not found at {exe}");

        var args = string.Join(' ',
            $"-USERDIR={EscapeArg(_opts.SnUserDir)}",
            "-unattended",
            $"-port={_opts.GameplayPort}",
            "-log",
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
        return Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
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
                          "Beacon's user dir must be a separate folder (e.g. C:\\Beacon\\userdir).",
                          _opts.SnUserDir);
            return;
        }

        var configDir = Path.Combine(_opts.SnUserDir, "Saved", "Config", "Windows");
        Directory.CreateDirectory(configDir);
        var enginePath = Path.Combine(configDir, "Engine.ini");

        const string driver = "/Script/OnlineSubsystemUtils.IpNetDriver";
        var content = $"""
        ; Beacon-managed Engine.ini override — rewritten on every host launch.
        [OnlineSubsystem]
        DefaultPlatformService=Null

        [OnlineSubsystemNull]
        bSimulateForwarded=true

        [/Script/Engine.GameEngine]
        !NetDriverDefinitions=ClearArray
        +NetDriverDefinitions=(DefName="GameNetDriver",DriverClassName="{driver}",DriverClassNameFallback="{driver}")

        [/Script/EngineSettings.GameMapsSettings]
        LocalMapOptions={BuildHostTravelOptions()}

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
        NetServerMaxTickRate=60
        MaxClientRate=15000
        MaxInternetClientRate=10000
        NetConnectionTimeout=60
        ServerTravelPause=4

        [URL]
        Port={_opts.GameplayPort}
        """;
        File.WriteAllText(enginePath, content);
        _log.LogInformation("Patched Engine.ini at {Path}", enginePath);
    }

    private void EmitPluginConfig()
    {
        var pluginDir = Path.Combine(_opts.SnInstallRoot,
            "Subnautica2", "Binaries", "Win64", "Mods", "Beacon");
        Directory.CreateDirectory(pluginDir);
        var configPath = Path.Combine(pluginDir, "beacon.config.json");

        var payload = new
        {
            InstanceId = _opts.InstanceId,
            PipePath = $@"\\.\pipe\{_opts.PipeName}",
            HmacKeyHex = Convert.ToHexString(_hmac.Key),
            ServerPassword = _opts.ServerPassword,
        };
        File.WriteAllText(configPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        _log.LogInformation("Emitted plugin config at {Path}", configPath);
    }

    private static string EscapeArg(string s) => s.Contains(' ') ? $"\"{s}\"" : s;

    public static string BuildHostTravelUrl(string saveSlot = Sn2CanonicalSaveSlot)
        => Sn2MapPath + BuildHostTravelOptions(saveSlot);

    public static string BuildHostTravelOptions(string saveSlot = Sn2CanonicalSaveSlot)
    {
        var slot = string.IsNullOrWhiteSpace(saveSlot) ? Sn2CanonicalSaveSlot : saveSlot.Trim();
        var escapedSlot = Uri.EscapeDataString(slot);
        return "?listen" + string.Concat(Sn2SaveSlotUrlKeys.Select(key => $"?{key}={escapedSlot}"));
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

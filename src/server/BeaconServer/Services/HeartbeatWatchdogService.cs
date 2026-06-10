using System.Diagnostics;
using BeaconServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

/// <summary>
/// Watches plugin heartbeats. If we haven't heard from the plugin in
/// <see cref="BeaconServerOptions.PluginHeartbeatTimeoutSeconds"/>, log a warning;
/// future work hooks process supervision into this to restart the Subnautica 2 instance.
///
/// Password enforcement model (v0.3.55+):
///   Password enforcement lives in BeaconAuth.lua's K2_PostLogin hook on
///   incoming remote clients. The native Beacon.dll ApproveLogin hook is
///   INTENTIONALLY disabled (see SnProcessSupervisorService.EmitPluginConfig
///   for the SN2 crash-loop rationale). EmitPluginConfig always emits an
///   empty ServerPassword to plugin-config.json, so the native gate has
///   nothing to enforce.
///
///   The legacy <see cref="CheckServerPasswordReady"/> path is kept as a
///   defensive bug-detector: if the plugin EVER reports
///   ServerPasswordConfigured=1, something has gone wrong upstream (a
///   stale plugin-config.json carrying a non-empty ServerPassword from a
///   pre-v0.3.55 deploy, or a manually-edited plugin config). In that
///   case the native and Lua paths could race and reproduce the SN2
///   crash loop, so we kill SN2 loudly with a CRITICAL log rather than
///   let a customer hit it. The expected steady-state is
///   Configured=0 + HookReady=0 across the entire fleet.
/// </summary>
public sealed class HeartbeatWatchdogService : BackgroundService
{
    private readonly ILogger<HeartbeatWatchdogService> _log;
    private readonly PipeServerState _state;
    private readonly Sn2RestartCoordinator _coordinator;
    private readonly BeaconServerOptions _opts;
    private readonly TimeSpan _timeout;

    // Grace period after we first see the plugin connect before we'll
    // start fail-closing. The plugin's bootstrap thread installs the
    // ApproveLogin hook AFTER the pipe handshake, so the first few
    // heartbeats can legitimately report hook-not-ready while the
    // bootstrap is still racing.
    private static readonly TimeSpan AuthGraceWindow = TimeSpan.FromSeconds(20);

    // Throttle for the "configured but native gate down" warning so we
    // don't spam the log every ~10s while waiting on the supervisor.
    private DateTimeOffset _lastFailClosedWarnAt = DateTimeOffset.MinValue;

    public HeartbeatWatchdogService(
        ILogger<HeartbeatWatchdogService> log,
        PipeServerState state,
        Sn2RestartCoordinator coordinator,
        IOptions<BeaconServerOptions> options)
    {
        _log = log;
        _state = state;
        _coordinator = coordinator;
        _opts = options.Value;
        _timeout = TimeSpan.FromSeconds(options.Value.PluginHeartbeatTimeoutSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, _timeout.TotalSeconds / 3));
        using var timer = new PeriodicTimer(period);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                CheckHeartbeatStale();
                CheckServerPasswordReady();
                CheckBeaconAuthLuaReady();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "HeartbeatWatchdog tick error");
            }
        }
    }

    private static readonly TimeSpan LuaStatusBootstrapGrace = TimeSpan.FromSeconds(45);
    // Wider grace for the no-connection-at-all case: covers the cold-
    // start window where SN2 process is launching but Beacon.dll hasn't
    // connected the pipe yet. SN2 cold launches can take 30-60s on a
    // panel-managed Win Server box. After this window without a pipe,
    // BeaconServer concludes UE4SS / BeaconLoader / Beacon.dll never
    // loaded successfully and fail-closes.
    private static readonly TimeSpan NoConnectionFailClosedGrace = TimeSpan.FromSeconds(180);
    private DateTimeOffset _lastLuaFailClosedWarnAt = DateTimeOffset.MinValue;
    private readonly DateTimeOffset _serverStartedAt = DateTimeOffset.UtcNow;

    // The "no-connection-window-started-at" anchor. Reset whenever we observe
    // a live connection. Without this reset, a long-lived BeaconServer that
    // briefly loses its pipe (e.g. SN2 process crash + relaunch driven by
    // BeaconServer's own supervisor, or by the panel's PowerShell) would
    // skip the cold-start grace on the relaunch and fail-closed immediately
    // even though the new SN2 process needs its own 30-60s cold launch.
    // Initialized to _serverStartedAt so the very first SN2 launch gets the
    // full grace window from BeaconServer process startup.
    private DateTimeOffset _noConnSince = DateTimeOffset.UtcNow;

    private void CheckBeaconAuthLuaReady()
    {
        // Fail-closed for the Lua-only password-enforcement model
        // (v0.3.55+). If BeaconAuthPassword is configured but the Lua
        // gate isn't reporting ready, kill SN2 to prevent the server
        // from running open without a gate. BeaconAuth.lua writes
        // BeaconServer/.beacon-auth-status with key=value pairs:
        //   ready=0|1
        //   passwordConfigured=0|1
        //   updated=<unix>
        //   reason=<short string>
        //
        // Fail-closed paths:
        //   1. No pipe connection at all (UE4SS never loaded, BeaconLoader
        //      failed, Beacon.dll never connected) past NoConnectionFailClosedGrace
        //      since BeaconServer process start. Use path-scoped KillSn2
        //      (only effective on standalone — panel deploys log loudly).
        //   2. Pipe connection exists but BeaconAuth status is not ready
        //      past LuaStatusBootstrapGrace since handshake. Use BOTH
        //      path-scoped KillSn2 AND PluginPid-based kill.
        if (string.IsNullOrEmpty(_opts.BeaconAuthPassword)) return;

        var conn = _state.Connection;
        if (conn is not null)
        {
            // Live connection observed. Reset the no-connection anchor so
            // that any FUTURE drop gets a fresh 180s grace window. This is
            // the round 5 fix for Codex Blocker 1: previously the anchor
            // was _serverStartedAt (DI construction time), which meant a
            // long-running BeaconServer would skip grace on a later SN2
            // restart and immediately fail-closed during the new SN2's
            // legitimate cold-launch window.
            _noConnSince = DateTimeOffset.UtcNow;
        }
        if (conn is null)
        {
            // Grace window since we last had a connection (or since
            // BeaconServer process startup if we've never had one).
            var sinceConnLost = DateTimeOffset.UtcNow - _noConnSince;
            if (sinceConnLost < NoConnectionFailClosedGrace) return;

            var noConnNow = DateTimeOffset.UtcNow;
            var noConnSinceWarn = noConnNow - _lastLuaFailClosedWarnAt;
            if (noConnSinceWarn > TimeSpan.FromSeconds(15))
            {
                _log.LogCritical(
                    "FAIL-CLOSED: BeaconAuthPassword is configured but Beacon.dll has NEVER connected to BeaconServer's IPC " +
                    "pipe past {Grace}s grace. UE4SS / BeaconLoader / Beacon.dll likely did not load. Without the plugin and " +
                    "the Lua gate, the passworded server cannot enforce its password. Stopping SN2 (path-scoped only — " +
                    "panel-managed deploys with empty SnInstallRoot will see this log but require panel-side recovery, " +
                    "PluginPid path is unavailable without a connection). " +
                    "Diagnose: check sn2-stdout.log for UE4SS bootstrap, BeaconAuth.log absent, BeaconServerRuntime.log absent.",
                    NoConnectionFailClosedGrace.TotalSeconds);
                _lastLuaFailClosedWarnAt = noConnNow;
            }
            try
            {
                _coordinator.KillSn2(TimeSpan.FromSeconds(10));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "FAIL-CLOSED (no-conn): KillSn2 threw");
            }
            return;
        }
        var sinceConnect = DateTimeOffset.UtcNow - conn.ConnectedAt;
        if (sinceConnect < LuaStatusBootstrapGrace) return;

        // Status file lives next to appsettings.json, which sits in
        // BeaconServer's working dir. We can derive it from the
        // running process. On panel deploys the panel starts
        // BeaconServer.exe with `-WorkingDirectory $beaconDir` so the
        // exe directory IS the appsettings directory. On standalone
        // self-hosts the assumption may not hold; ContentRootPath
        // would be more correct in the future, but adding a host
        // dependency here is more risk than it's worth right now.
        string statusPath;
        try
        {
            var baseDir = AppContext.BaseDirectory;
            statusPath = Path.Combine(baseDir, ".beacon-auth-status");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "BeaconAuth status path resolve failed");
            return;
        }

        var (ready, passwordConfigured, reason, exists) = ReadLuaStatus(statusPath);
        // Stale-file protection: BeaconServerRuntime/main.lua atomically
        // overwrites .beacon-auth-status with ready=0,reason=runtime_init
        // BEFORE BeaconAuth runs, on every SN2 process start (see the
        // write_not_ready_at pass at the top of that script). If runtime
        // can't overwrite (file lock + all retries fail), it calls
        // error(...) to abort BeaconServerRuntime entirely — BeaconLoader
        // and BeaconAuth never load, Beacon.dll never injects, no pipe
        // handshake happens, and the no-connection fail-closed path in
        // CheckBeaconAuthLuaReady (above) takes SN2 down via pid-file.
        // So any .beacon-auth-status the watchdog reads here with
        // ready=1 + passwordConfigured=1 was written by THIS SN2
        // process's BeaconAuth.lua post-handshake; no file mtime
        // freshness check needed.
        if (exists && passwordConfigured && ready) return;

        var now = DateTimeOffset.UtcNow;
        var sinceWarn = now - _lastLuaFailClosedWarnAt;
        if (sinceWarn > TimeSpan.FromSeconds(15))
        {
            _log.LogCritical(
                "FAIL-CLOSED: BeaconAuthPassword is configured but BeaconAuth.lua status is not ready " +
                "(exists={Exists} ready={Ready} passwordConfigured={PwConfigured} reason='{Reason}' path={Path}). " +
                "Stopping Subnautica 2 to prevent the passworded server from running open without the Lua password gate. " +
                "Investigate UE4SS load failure / BeaconAuth.lua install / mods.txt ordering; the supervisor will " +
                "relaunch SN2 on the next loop.",
                exists, ready, passwordConfigured, reason, statusPath);
            _lastLuaFailClosedWarnAt = now;
        }

        try
        {
            // Path-scoped KillSn2 is best-effort on panel-managed deploys
            // (SnInstallRoot is empty), so also try the PID-based kill
            // path via the active pipe connection. PluginPid is the SN2
            // process that loaded Beacon.dll (Beacon.dll calls
            // GetCurrentProcessId in its handshake), so if we have an
            // active pipe handshake, we know the exact PID to kill.
            _coordinator.KillSn2(TimeSpan.FromSeconds(10));
            TryKillByPluginPid(conn.PluginPid);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FAIL-CLOSED (Lua): KillSn2 / KillByPluginPid threw");
        }
    }

    private (bool ready, bool passwordConfigured, string reason, bool exists) ReadLuaStatus(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (false, false, "file_missing", false);
            }
            var lines = File.ReadAllLines(path);
            bool ready = false;
            bool passwordConfigured = false;
            string reason = "";
            foreach (var line in lines)
            {
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "ready": ready = value == "1"; break;
                    case "passwordConfigured": passwordConfigured = value == "1"; break;
                    case "reason": reason = value; break;
                }
            }
            return (ready, passwordConfigured, reason, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "BeaconAuth status read failed: {Path}", path);
            return (false, false, "read_error", false);
        }
    }

    private static readonly string[] Sn2KillProcessNameWhitelist = new[]
    {
        // Process names are sometimes case-insensitive on Windows;
        // Process.ProcessName has no .exe suffix. The plugin is loaded
        // into one of these SN2 binaries.
        "Subnautica2-Win64-Shipping",
        "Subnautica2-WinGDK-Shipping",
        "Subnautica2",
    };

    private void TryKillByPluginPid(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return;

            // PID-reuse guard. Beacon.dll is loaded into the SN2 process,
            // so the PluginPid reported on handshake must belong to one
            // of the SN2 binaries. If the PID was recycled between the
            // handshake and the kill, the new owner's process name will
            // not match the SN2 whitelist; refusing to kill prevents us
            // from terminating an unrelated user process. Case-insensitive
            // because Windows process names round-trip case unpredictably.
            var name = p.ProcessName ?? "";
            var isWhitelistedSn2 = false;
            foreach (var allowed in Sn2KillProcessNameWhitelist)
            {
                if (string.Equals(name, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    isWhitelistedSn2 = true;
                    break;
                }
            }
            if (!isWhitelistedSn2)
            {
                _log.LogWarning(
                    "FAIL-CLOSED (Lua): PluginPid={Pid} now belongs to '{Name}' (not SN2); refusing to kill",
                    pid, name);
                return;
            }

            _log.LogWarning("FAIL-CLOSED (Lua): killing SN2 plugin host pid={Pid} name={Name} via PluginPid path", pid, name);
            p.Kill(entireProcessTree: false);
        }
        catch (ArgumentException)
        {
            // pid no longer exists — race with normal supervisor exit
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FAIL-CLOSED (Lua): KillByPluginPid({Pid}) failed", pid);
        }
    }

    private void CheckHeartbeatStale()
    {
        var conn = _state.Connection;
        if (conn is null) return;
        var last = _state.LastHeartbeatAt;
        if (last is null) return;
        var age = DateTimeOffset.UtcNow - last.Value;
        if (age > _timeout)
        {
            _log.LogWarning("Plugin heartbeat stale: instance={Instance} pid={Pid} age={Age}s",
                conn.InstanceId, conn.PluginPid, (int)age.TotalSeconds);
        }
    }

    private void CheckServerPasswordReady()
    {
        // Steady-state under the v0.3.55 Lua-only enforcement model is
        // ServerPasswordConfigured=0 across the entire fleet. The native
        // ApproveLogin hook is intentionally disabled (plugin-config.json
        // always has an empty ServerPassword), so if the plugin EVER
        // reports Configured=1, something upstream is broken and we
        // could be heading for the SN2 crash loop that necessitated the
        // pivot to Lua-only enforcement in the first place. Treat that
        // as a fail-closed condition.
        var conn = _state.Connection;
        if (conn is null) return;

        // Wait for the bootstrap-grace window after first contact. The
        // plugin's bootstrap thread populates the heartbeat fields
        // shortly after the handshake; first heartbeat or two can race.
        var sinceConnect = DateTimeOffset.UtcNow - conn.ConnectedAt;
        if (sinceConnect < AuthGraceWindow) return;

        // Plugin reporting Configured=0 is the expected steady state.
        // Nothing to do.
        if (_state.LastServerPasswordConfigured == 0) return;

        // Plugin says "I see ServerPassword in plugin-config.json." Under
        // v0.3.55+ this should be impossible — EmitPluginConfig hardcodes
        // ServerPassword="". A non-zero value here means a stale config
        // from a previous deploy, a manually-edited plugin-config.json,
        // or a Beacon update that regressed EmitPluginConfig. Stop SN2
        // before native + Lua enforcement layers race into the crash.
        var now = DateTimeOffset.UtcNow;
        var sinceWarn = now - _lastFailClosedWarnAt;
        if (sinceWarn > TimeSpan.FromSeconds(15))
        {
            _log.LogCritical(
                "FAIL-CLOSED: native plugin reported ServerPasswordConfigured={Configured} " +
                "hookReady={Ready}, but v0.3.55+ Beacon enforces password in BeaconAuth.lua only " +
                "and emits an empty ServerPassword in plugin-config.json. This usually means a " +
                "stale plugin-config.json from a pre-v0.3.55 deploy or a manually-edited config. " +
                "Stopping Subnautica 2 to prevent the native gate from racing with the Lua gate " +
                "(which reproduces the SN2 fatal crash loop). Fix plugin-config.json " +
                "ServerPassword field to empty and restart.",
                _state.LastServerPasswordConfigured, _state.LastServerPasswordHookReady);
            _lastFailClosedWarnAt = now;
        }

        // Kill SN2. Path-scoped KillSn2 covers standalone deploys (where
        // SnInstallRoot is configured); PluginPid-based kill covers
        // panel-managed deploys (where SnInstallRoot is intentionally
        // empty and KillSn2 logs "no anchor to scope by"). The IPC
        // handshake gives us the exact SN2 PID — Beacon.dll calls
        // GetCurrentProcessId() inside the SN2 process when sending
        // handshake.
        try
        {
            _coordinator.KillSn2(TimeSpan.FromSeconds(10));
            TryKillByPluginPid(conn.PluginPid);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FAIL-CLOSED: KillSn2 / KillByPluginPid threw");
        }
    }
}

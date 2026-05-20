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
/// Also enforces the server-password fail-closed contract: if
/// <see cref="BeaconServerOptions.ServerPassword"/> is non-empty AND the
/// plugin has reported on a heartbeat that its native ApproveLogin gate
/// failed to install, the watchdog kills SN2. This stops a passworded
/// server from silently running open when the native hook misses (AOB
/// drift on a Krafton update, FString ctor resolution failure, patch
/// failure, etc.).
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
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "HeartbeatWatchdog tick error");
            }
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
        // Only enforce when the operator actually set a password — an open
        // server has nothing to fail-closed about.
        if (string.IsNullOrEmpty(_opts.ServerPassword)) return;

        var conn = _state.Connection;
        if (conn is null) return;

        // Wait for the bootstrap-grace window after first contact. The
        // plugin's hook install runs on a background thread after the
        // handshake, so the first heartbeat or two can race.
        var sinceConnect = DateTimeOffset.UtcNow - conn.ConnectedAt;
        if (sinceConnect < AuthGraceWindow) return;

        // Legacy plugin (no auth-state fields, both stay 0): don't act.
        // We can't distinguish "no native gate installed" from "doesn't
        // report yet" on a pre-v0.2.17 plugin, and the Lua post-login
        // kick is still in place as a fallback.
        if (_state.LastServerPasswordConfigured == 0 && _state.LastServerPasswordHookReady == 0)
        {
            return;
        }

        // New plugin says "I'm enforcing your password and my native gate is up."
        if (_state.LastServerPasswordConfigured == 1 && _state.LastServerPasswordHookReady == 1)
        {
            return;
        }

        // New plugin says "Your appsettings.json has ServerPassword set,
        // but my native ApproveLogin gate is NOT installed."
        // The Lua post-login kick is a window of admit-then-kick — not an
        // acceptable wire-level boundary for a passworded server. Stop SN2.
        var now = DateTimeOffset.UtcNow;
        var sinceWarn = now - _lastFailClosedWarnAt;
        if (sinceWarn > TimeSpan.FromSeconds(15))
        {
            _log.LogCritical(
                "FAIL-CLOSED: ServerPassword is configured but plugin reported native ApproveLogin hook NOT ready " +
                "(configured={Configured} hookReady={Ready}). Stopping Subnautica 2 to prevent passworded server from running open. " +
                "Investigate Beacon.dll / Subnautica 2 update; once the native hook installs cleanly, BeaconServer's supervisor " +
                "will relaunch the game on the next loop.",
                _state.LastServerPasswordConfigured, _state.LastServerPasswordHookReady);
            _lastFailClosedWarnAt = now;
        }

        // Kill SN2. KillSn2 is scoped to SnInstallRoot, so on standalone
        // deploys the supervisor will respawn the game (and trip the same
        // gate again until the operator fixes the hook). On panel-managed
        // deploys SnInstallRoot is intentionally empty and KillSn2 logs
        // "no anchor to scope by" — the panel's PowerShell owns lifecycle.
        // In that case the loud CRITICAL log above is the recovery signal
        // (panel ops sees it in beacon-.log) and a follow-up panel-side
        // fix is the recovery path.
        try
        {
            _coordinator.KillSn2(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FAIL-CLOSED: KillSn2 threw");
        }
    }
}

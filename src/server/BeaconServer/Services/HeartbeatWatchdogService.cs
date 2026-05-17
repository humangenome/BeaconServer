using BeaconServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

/// <summary>
/// Watches plugin heartbeats. If we haven't heard from the plugin in
/// <see cref="BeaconServerOptions.PluginHeartbeatTimeoutSeconds"/>, log a warning;
/// future work hooks process supervision into this to restart the SN2 instance.
/// </summary>
public sealed class HeartbeatWatchdogService : BackgroundService
{
    private readonly ILogger<HeartbeatWatchdogService> _log;
    private readonly PipeServerState _state;
    private readonly TimeSpan _timeout;

    public HeartbeatWatchdogService(
        ILogger<HeartbeatWatchdogService> log,
        PipeServerState state,
        IOptions<BeaconServerOptions> options)
    {
        _log = log;
        _state = state;
        _timeout = TimeSpan.FromSeconds(options.Value.PluginHeartbeatTimeoutSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromSeconds(Math.Max(1, _timeout.TotalSeconds / 3));
        using var timer = new PeriodicTimer(period);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var conn = _state.Connection;
            if (conn is null) continue;
            var last = _state.LastHeartbeatAt;
            if (last is null) continue;
            var age = DateTimeOffset.UtcNow - last.Value;
            if (age > _timeout)
            {
                _log.LogWarning("Plugin heartbeat stale: instance={Instance} pid={Pid} age={Age}s",
                    conn.InstanceId, conn.PluginPid, (int)age.TotalSeconds);
            }
        }
    }
}

using Beacon.Protocol;
using BeaconServer.Configuration;
using BeaconServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeaconServer.Tests;

public class HeartbeatWatchdogServiceTests
{
    private sealed class CapturingLogger : ILogger<HeartbeatWatchdogService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static (HeartbeatWatchdogService Watchdog, PipeServerState State, CapturingLogger Log, Func<DateTimeOffset> Clock, Action<TimeSpan> Advance)
        Create(string password = "secret")
    {
        var now = DateTimeOffset.UtcNow;
        var log = new CapturingLogger();
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        var opts = Options.Create(new BeaconServerOptions { BeaconAuthPassword = password });
        var coordinator = new Sn2RestartCoordinator(NullLogger<Sn2RestartCoordinator>.Instance, opts);
        Func<DateTimeOffset> clock = () => now;
        var watchdog = new HeartbeatWatchdogService(log, state, coordinator, opts, clock);
        return (watchdog, state, log, clock, d => now += d);
    }

    private static PipeConnection Connection() =>
        new("default", 4242, "test", new FrameCodec(new byte[32]), (_, _) => Task.CompletedTask);

    // Only the no-connection fail-closed lines. The separate BeaconAuth status
    // check measures time since the pipe connected with the real clock, so it
    // is excluded here rather than coupled to the fake one.
    private static List<string> Critical(CapturingLogger log) =>
        log.Entries.Where(e => e.Level == LogLevel.Critical && e.Message.Contains("IPC pipe"))
            .Select(e => e.Message).ToList();

    [Fact]
    public void Lost_connection_is_reported_as_lost_not_as_never_connected()
    {
        var (watchdog, state, log, _, advance) = Create();
        state.SetConnection(Connection());
        watchdog.RunChecks();
        state.SetConnection(null);

        advance(TimeSpan.FromSeconds(179));
        watchdog.RunChecks();
        Critical(log).Should().BeEmpty("the cold-launch grace has not passed yet");

        advance(TimeSpan.FromSeconds(2));
        watchdog.RunChecks();
        var critical = Critical(log);
        critical.Should().ContainSingle();
        critical[0].Should().Contain("has not reconnected");
        critical[0].Should().NotContain("NEVER");
    }

    [Fact]
    public void Plugin_that_never_connected_keeps_the_never_connected_wording()
    {
        var (watchdog, _, log, _, advance) = Create();
        advance(TimeSpan.FromSeconds(181));
        watchdog.RunChecks();
        Critical(log).Should().ContainSingle().Which.Should().Contain("NEVER connected");
    }

    [Fact]
    public void A_long_outage_logs_on_the_repeat_interval_not_every_tick()
    {
        var (watchdog, state, log, _, advance) = Create();
        state.SetConnection(Connection());
        watchdog.RunChecks();
        state.SetConnection(null);
        advance(TimeSpan.FromSeconds(181));

        // One hour of ticks at the production cadence (every 10 s).
        for (var i = 0; i < 360; i++)
        {
            watchdog.RunChecks();
            advance(TimeSpan.FromSeconds(10));
        }

        var expected = (int)(TimeSpan.FromHours(1) / HeartbeatWatchdogService.NoConnectionRepeatInterval);
        Critical(log).Count.Should().BeInRange(expected, expected + 1);
    }

    [Fact]
    public void A_reconnect_resets_the_grace_and_the_repeat_window()
    {
        var (watchdog, state, log, _, advance) = Create();
        state.SetConnection(Connection());
        watchdog.RunChecks();
        state.SetConnection(null);
        advance(TimeSpan.FromSeconds(181));
        watchdog.RunChecks();
        Critical(log).Should().HaveCount(1);

        state.SetConnection(Connection());
        watchdog.RunChecks();
        state.SetConnection(null);
        advance(TimeSpan.FromSeconds(100));
        watchdog.RunChecks();
        Critical(log).Should().HaveCount(1, "the relaunch gets a fresh cold-launch grace");

        advance(TimeSpan.FromSeconds(90));
        watchdog.RunChecks();
        Critical(log).Should().HaveCount(2, "a new outage is reported straight away, not after the old repeat window");
    }

    [Fact]
    public void No_password_means_no_fail_closed_logging()
    {
        var (watchdog, _, log, _, advance) = Create(password: "");
        advance(TimeSpan.FromHours(1));
        watchdog.RunChecks();
        Critical(log).Should().BeEmpty();
    }
}

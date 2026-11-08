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
        Create(string password = "secret", string? hookFailure = null, string? pidFile = null)
    {
        var now = DateTimeOffset.UtcNow;
        var log = new CapturingLogger();
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        var o = new BeaconServerOptions { BeaconAuthPassword = password, Sn2PidFile = pidFile ?? "" };
        if (hookFailure is not null) o.Identity.HookFailure = hookFailure;
        var opts = Options.Create(o);
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

    private static List<string> PasswordLines(CapturingLogger log) =>
        log.Entries.Where(e => e.Level == LogLevel.Critical && e.Message.Contains("Join password is set"))
            .Select(e => e.Message).ToList();

    [Fact]
    public void A_password_with_the_native_check_ready_is_quiet()
    {
        var (watchdog, state, log, _, advance) = Create("secret");
        state.SetConnection(Connection());
        state.LastServerPasswordConfigured = 1;
        state.LastServerPasswordHookReady = 1;
        advance(TimeSpan.FromSeconds(25));
        watchdog.RunChecks();
        PasswordLines(log).Should().BeEmpty();
        state.SetConnection(null);
    }

    [Fact]
    public void A_password_without_the_native_check_is_reported_on_its_interval()
    {
        var (watchdog, state, log, _, advance) = Create("secret");
        state.SetConnection(Connection());
        state.LastServerPasswordConfigured = 1;
        state.LastServerPasswordHookReady = 0;
        advance(TimeSpan.FromSeconds(25));
        watchdog.RunChecks();
        watchdog.RunChecks();
        PasswordLines(log).Should().ContainSingle().Which.Should().Contain("NOT being checked");

        advance(HeartbeatWatchdogService.PasswordInactiveWarnEvery + TimeSpan.FromSeconds(1));
        watchdog.RunChecks();
        PasswordLines(log).Should().HaveCount(2);
        state.SetConnection(null);
    }

    [Fact]
    public void A_password_the_plugin_never_loaded_is_reported()
    {
        var (watchdog, state, log, _, advance) = Create("secret");
        state.SetConnection(Connection());
        state.LastServerPasswordConfigured = 0;
        advance(TimeSpan.FromSeconds(25));
        watchdog.RunChecks();
        PasswordLines(log).Should().ContainSingle();
        state.SetConnection(null);
    }

    [Fact]
    public void An_open_server_says_nothing_about_passwords()
    {
        var (watchdog, state, log, _, advance) = Create("");
        state.SetConnection(Connection());
        advance(TimeSpan.FromSeconds(25));
        watchdog.RunChecks();
        PasswordLines(log).Should().BeEmpty();
        log.Entries.Should().NotContain(e => e.Message.Contains("FAIL-CLOSED: the plugin reports"));
        state.SetConnection(null);
    }

    // 2026-11-02: a game that logged a fatal error and then went silent is ended so the panel can start it again; fatal
    // text from a game that is still writing its roster is left alone (the lane's guard).
    private static List<string> DeadGame(CapturingLogger log) =>
        log.Entries.Where(e => e.Level == LogLevel.Critical && e.Message.StartsWith("DEAD GAME")).Select(e => e.Message).ToList();

    [Fact]
    public void A_game_that_logged_a_fatal_error_and_went_silent_is_ended_once()
    {
        var (watchdog, state, log, clock, advance) = Create(password: "");
        var conn = Connection();
        state.SetConnection(conn);
        state.NoteRosterWrite(clock());               // the game's mods are writing
        advance(TimeSpan.FromSeconds(5));
        state.NoteGameFatalError(clock());            // the fatal line, then silence
        advance(TimeSpan.FromSeconds(59));
        watchdog.RunChecks();
        DeadGame(log).Should().BeEmpty("the window has not passed");
        advance(TimeSpan.FromSeconds(2));
        watchdog.RunChecks();
        DeadGame(log).Should().ContainSingle().Which.Should().Contain("pid=4242");
        advance(TimeSpan.FromSeconds(30));
        watchdog.RunChecks();
        DeadGame(log).Should().ContainSingle("it fires once per game");
    }

    [Fact]
    public void Fatal_text_from_a_game_still_writing_its_roster_is_left_alone()
    {
        var (watchdog, state, log, clock, advance) = Create(password: "");
        state.SetConnection(Connection());
        state.NoteRosterWrite(clock());
        state.NoteGameFatalError(clock());
        for (var i = 0; i < 30; i++)                  // five minutes, the mods writing every 10 s
        {
            advance(TimeSpan.FromSeconds(10));
            state.NoteRosterWrite(clock());
            watchdog.RunChecks();
        }
        DeadGame(log).Should().BeEmpty();
    }

    [Fact]
    public void A_silent_game_with_no_fatal_line_is_left_alone()
    {
        var (watchdog, state, log, clock, advance) = Create(password: "");
        state.SetConnection(Connection());
        state.NoteRosterWrite(clock());
        advance(TimeSpan.FromMinutes(5));
        watchdog.RunChecks();
        DeadGame(log).Should().BeEmpty();
    }

    [Fact]
    public void A_game_that_never_wrote_a_roster_is_not_judged()
    {
        var (watchdog, state, log, clock, advance) = Create(password: "");
        state.SetConnection(Connection());
        state.NoteGameFatalError(clock());
        advance(TimeSpan.FromMinutes(5));
        watchdog.RunChecks();
        DeadGame(log).Should().BeEmpty();
    }

    [Fact]
    public void A_fatal_line_from_the_previous_game_does_not_end_the_new_one()
    {
        var (watchdog, state, log, clock, advance) = Create(password: "");
        state.NoteGameFatalError(clock().AddMinutes(-10));
        state.SetConnection(Connection());
        state.NoteRosterWrite(clock().AddMinutes(-9));
        advance(TimeSpan.FromMinutes(5));
        watchdog.RunChecks();
        DeadGame(log).Should().BeEmpty();
    }

    // ---------- Steam identity check not active (Security 12104, 2026-11-06: option 2) ----------

    private static List<string> IdentityStops(CapturingLogger log) =>
        log.Entries.Where(e => e.Level == LogLevel.Critical && e.Message.Contains("FAIL-CLOSED: the game-side Steam identity check")).Select(e => e.Message).ToList();

    private static List<string> IdentityWarnings(CapturingLogger log) =>
        log.Entries.Where(e => e.Level == LogLevel.Critical && e.Message.StartsWith("The game-side Steam identity check is not active")).Select(e => e.Message).ToList();

    private static string TempPidFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "beacon-idstop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "sn2.pid");
    }

    [Theory]
    [InlineData(null, "stop-open-servers")]
    [InlineData("", "stop-open-servers")]
    [InlineData("Stop-Open-Servers", "stop-open-servers")]
    [InlineData("warn", "warn")]
    [InlineData("stop-all", "stop-all")]
    [InlineData("typo", "stop-all")]
    public void Hook_failure_setting_defaults_to_stopping_open_servers_and_unknown_fails_closed(string? value, string expected)
    {
        HeartbeatWatchdogService.IdentityHookFailureMode(value).Should().Be(expected);
    }

    [Fact]
    public void An_open_server_whose_identity_check_is_not_active_is_stopped_and_left_a_marker()
    {
        var pid = TempPidFile();
        try
        {
            var (watchdog, state, log, _, advance) = Create("", pidFile: pid);
            state.SetConnection(Connection());
            advance(TimeSpan.FromSeconds(25));
            watchdog.RunChecks();
            IdentityStops(log).Should().ContainSingle().Which.Should().Contain("join password not set");
            var marker = Path.Combine(Path.GetDirectoryName(pid)!, HeartbeatWatchdogService.IdentityStopMarkerName);
            File.Exists(marker).Should().BeTrue("the panel reads it and does not restart the server in a loop");
            File.ReadAllText(marker).Should().Contain("identity check not active");
            state.SetConnection(null);
        }
        finally { Directory.Delete(Path.GetDirectoryName(pid)!, recursive: true); }
    }

    [Fact]
    public void A_passworded_server_keeps_running_and_warns_once_per_window()
    {
        var pid = TempPidFile();
        try
        {
            var (watchdog, state, log, _, advance) = Create("secret", pidFile: pid);
            state.SetConnection(Connection());
            state.LastServerPasswordConfigured = 1;
            state.LastServerPasswordHookReady = 1;
            advance(TimeSpan.FromSeconds(25));
            watchdog.RunChecks();
            advance(TimeSpan.FromSeconds(60));
            watchdog.RunChecks();
            IdentityStops(log).Should().BeEmpty();
            IdentityWarnings(log).Should().ContainSingle();
            File.Exists(Path.Combine(Path.GetDirectoryName(pid)!, HeartbeatWatchdogService.IdentityStopMarkerName)).Should().BeFalse();
            state.SetConnection(null);
        }
        finally { Directory.Delete(Path.GetDirectoryName(pid)!, recursive: true); }
    }

    [Fact]
    public void Stop_all_stops_a_passworded_server_too_and_warn_stops_nothing()
    {
        var (all, allState, allLog, _, allAdvance) = Create("secret", hookFailure: "stop-all");
        allState.SetConnection(Connection());
        allState.LastServerPasswordConfigured = 1;
        allState.LastServerPasswordHookReady = 1;
        allAdvance(TimeSpan.FromSeconds(25));
        all.RunChecks();
        IdentityStops(allLog).Should().ContainSingle();
        allState.SetConnection(null);

        var (warn, warnState, warnLog, _, warnAdvance) = Create("", hookFailure: "warn");
        warnState.SetConnection(Connection());
        warnAdvance(TimeSpan.FromSeconds(25));
        warn.RunChecks();
        IdentityStops(warnLog).Should().BeEmpty();
        IdentityWarnings(warnLog).Should().ContainSingle();
        warnState.SetConnection(null);
    }

    [Fact]
    public void An_active_identity_check_or_the_grace_window_stops_nothing()
    {
        var (watchdog, state, log, _, advance) = Create("");
        state.SetConnection(Connection());
        watchdog.RunChecks();                       // inside the grace window
        IdentityStops(log).Should().BeEmpty();
        state.LastIdentityCheckReady = 1;
        advance(TimeSpan.FromSeconds(25));
        watchdog.RunChecks();
        IdentityStops(log).Should().BeEmpty();
        IdentityWarnings(log).Should().BeEmpty();
        state.SetConnection(null);
    }
}

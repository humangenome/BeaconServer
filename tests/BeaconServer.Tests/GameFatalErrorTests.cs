using BeaconServer.Services;
using FluentAssertions;
using Xunit;

namespace BeaconServer.Tests;

// 2026-11-02 lab (candidate 3): after a refused join the game logged a fatal error and shut down, but its process stayed
// alive and the plugin kept beating, so /api/v1/health read 200 for a dead server. A logged fatal error now reads as down
// for the game connection it happened in.
public class GameFatalErrorTests
{
    private const string FatalLine =
        "[2026.11.02-16.26.35:187][ 62]LogWindows: Error: appError called: Fatal error: [File:C:\\BuildAgent\\work\\x\\UE\\Engine\\Source\\Runtime\\Core\\Private\\HAL\\MallocBinned2.cpp] [Line: 900] ";
    private const string ShutdownLine = "[2026.11.02-16.26.35:265][ 62]LogExit: Executing StaticShutdownAfterError";

    private static PipeConnection Conn() =>
        new("default", 4242, "test", new Beacon.Protocol.FrameCodec(new byte[32]), (_, _) => Task.CompletedTask);

    private static PipeServerState NewState() =>
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger<PipeServerState>.Instance);

    private static Sn2LogTailService NewTail(PipeServerState state) =>
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger<Sn2LogTailService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new BeaconServer.Configuration.BeaconServerOptions()),
            state);

    [Fact]
    public void A_fatal_error_counts_for_the_running_game()
    {
        var state = NewState();
        state.SetConnection(Conn());
        state.GameHitFatalError.Should().BeFalse();
        state.NoteGameFatalError();
        state.GameHitFatalError.Should().BeTrue();
    }

    [Fact]
    public void A_fatal_error_before_this_game_connected_does_not_count()
    {
        var state = NewState();
        state.SetConnection(Conn());
        state.NoteGameFatalError(DateTimeOffset.UtcNow.AddMinutes(-5));   // the previous game
        state.GameHitFatalError.Should().BeFalse();
    }

    [Fact]
    public void Without_a_game_connection_there_is_no_fatal_flag()
    {
        var state = NewState();
        state.NoteGameFatalError();
        state.GameHitFatalError.Should().BeFalse();
    }

    [Theory]
    [InlineData(FatalLine)]
    [InlineData(ShutdownLine)]
    public void The_game_log_tailer_marks_a_live_fatal_line(string line)
    {
        var state = NewState();
        state.SetConnection(Conn());
        NewTail(state).EmitLine(line, trackPlayerEvents: true);
        state.GameHitFatalError.Should().BeTrue();
    }

    [Fact]
    public void An_old_fatal_line_read_at_start_up_does_not_mark_the_new_game()
    {
        var state = NewState();
        state.SetConnection(Conn());
        NewTail(state).EmitLine(FatalLine, trackPlayerEvents: false);
        state.GameHitFatalError.Should().BeFalse();
    }

    [Fact]
    public void Ordinary_error_lines_do_not_mark_the_game()
    {
        var state = NewState();
        state.SetConnection(Conn());
        var tail = NewTail(state);
        tail.EmitLine("[2026.11.02-16.26.34:986][343]LogEOSSDK: Warning: LogEOS: Error response received from backend.", trackPlayerEvents: true);
        tail.EmitLine("[2026.11.02-16.26.35:086][509]LogOnlineTitleFile: Error: EOS: EOS_TitleStorage_ReadFile() failed", trackPlayerEvents: true);
        state.GameHitFatalError.Should().BeFalse();
    }

    [Fact]
    public void A_password_of_spaces_is_a_password_for_the_status_too()
    {
        var state = NewState();
        state.SetConnection(Conn());
        state.LastServerPasswordConfigured = 1;
        state.LastServerPasswordHookReady = 1;
        state.PasswordCheckState("  ").Should().Be("on");
        state.PasswordCheckState("").Should().Be("not_active", "the plugin has a password the server does not");
    }

    [Fact]
    public async Task Health_reads_down_after_a_game_fatal_error()
    {
        await using var h = new HttpHardeningTests.Harness();
        await h.StartAsync();
        h.State.SetConnection(Conn());
        h.State.LastHeartbeatAt = DateTimeOffset.UtcNow;
        using var http = new HttpClient();
        var url = $"http://127.0.0.1:{h.Port}/api/v1/health";

        var up = await http.GetAsync(url);
        ((int)up.StatusCode).Should().Be(200);

        h.State.NoteGameFatalError();
        h.State.LastHeartbeatAt = DateTimeOffset.UtcNow;   // the plugin keeps beating on a dead game
        var down = await http.GetAsync(url);
        ((int)down.StatusCode).Should().Be(503);
        using var doc = System.Text.Json.JsonDocument.Parse(await down.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("game_fatal_error").GetBoolean().Should().BeTrue();
    }
}

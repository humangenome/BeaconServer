using BeaconServer.Configuration;
using BeaconServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BeaconServer.Tests;

public class WorldSaveServiceTests : IDisposable
{
    private const string AutosaveLine = "[2026.11.13-12.00.00:000][  1]LogUWEAnalytics: Not logged in to GPP. Queuing up event 'autosave_completed'.";
    private readonly string _userDir = Path.Combine(Path.GetTempPath(), "beacon-worldsave-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_userDir, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeRunner : IHostCommandRunner
    {
        public readonly List<string> Sent = new();
        public Func<string, string[], string?> Answer = (_, _) => "ok";
        public Action<string, string[]>? OnSend;

        public Task<string?> RunAsync(string name, string[] args, CancellationToken ct)
        {
            Sent.Add((name + " " + string.Join(' ', args)).Trim());
            OnSend?.Invoke(name, args);
            return Task.FromResult(Answer(name, args));
        }
    }

    private (WorldSaveService svc, GameSaveSignals signals, FakeRunner runner, PipeServerState state) Make(int minutes = 0, bool running = true)
    {
        var opts = new BeaconServerOptions
        {
            SnUserDir = _userDir,
            SnInstallRoot = "",
            Saves = new SavesOptions { HostSaveEveryMinutes = minutes },
        };
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        var signals = new GameSaveSignals();
        var runner = new FakeRunner();
        var svc = new WorldSaveService(NullLogger<WorldSaveService>.Instance, Options.Create(opts), state, signals, runner)
        {
            GameRunning = () => running,
            WaitPoll = TimeSpan.FromMilliseconds(10),
        };
        return (svc, signals, runner, state);
    }

    [Fact]
    public async Task Save_world_is_skipped_when_the_game_is_not_running()
    {
        var (svc, _, runner, _) = Make(running: false);
        var outcome = await svc.SaveNowAsync("stop", TimeSpan.FromSeconds(5));
        outcome.Saved.Should().BeFalse();
        outcome.How.Should().Be("the game is not running");
        runner.Sent.Should().BeEmpty();
        RconHostedService.FormatWorldSave(outcome).Should().Be("world save skipped: the game is not running");
    }

    [Fact]
    public async Task Save_world_waits_for_the_game_s_save_line()
    {
        var (svc, signals, runner, _) = Make();
        runner.OnSend = (name, args) =>
        {
            if (name == WorldSaveService.SaveCommand)
                _ = Task.Run(async () => { await Task.Delay(50); signals.Observe(AutosaveLine, DateTimeOffset.UtcNow); });
        };
        var outcome = await svc.SaveNowAsync("stop", TimeSpan.FromSeconds(5));
        runner.Sent.Should().Equal("beacon_world_save");
        outcome.Saved.Should().BeTrue();
        outcome.How.Should().Be("the game's save line");
        RconHostedService.FormatWorldSave(outcome).Should().StartWith("world saved in ");
    }

    [Fact]
    public async Task Save_world_is_also_confirmed_by_the_world_file()
    {
        var (svc, _, runner, _) = Make();
        var saves = Path.Combine(_userDir, "Saved", "SaveGames");
        Directory.CreateDirectory(saves);
        var file = Path.Combine(saves, "savegame_0.sav");
        File.WriteAllText(file, "old");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-5));
        runner.OnSend = (name, args) =>
        {
            if (name == WorldSaveService.SaveCommand)
                _ = Task.Run(async () => { await Task.Delay(50); File.WriteAllText(file, "new"); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1)); });
        };
        var outcome = await svc.SaveNowAsync("stop", TimeSpan.FromSeconds(5));
        outcome.Saved.Should().BeTrue();
        outcome.How.Should().Be("the world file");
    }

    [Fact]
    public async Task Save_world_says_plainly_when_the_server_mod_is_missing()
    {
        var (svc, _, runner, _) = Make();
        runner.Answer = (_, _) => null;
        var outcome = await svc.SaveNowAsync("stop", TimeSpan.FromSeconds(5));
        outcome.Saved.Should().BeFalse();
        outcome.How.Should().Contain("BeaconWorldSave server mod is not loaded");
        RconHostedService.FormatWorldSave(outcome).Should().StartWith("world save not confirmed: ");
    }

    [Fact]
    public async Task Save_world_gives_up_at_its_limit_and_names_the_last_save()
    {
        var (svc, _, _, _) = Make();
        var outcome = await svc.SaveNowAsync("stop", TimeSpan.FromMilliseconds(200));
        outcome.Saved.Should().BeFalse();
        outcome.How.Should().Contain("not confirmed within").And.Contain("last saved");
    }

    [Fact]
    public async Task With_the_setting_on_the_service_proves_its_save_then_switches_the_game_s_autosave_off()
    {
        var (svc, signals, runner, _) = Make(minutes: 10);
        await svc.TickOnceAsync(CancellationToken.None);
        runner.Sent.Should().BeEmpty("the game has not autosaved by itself yet");

        signals.Observe(AutosaveLine, DateTimeOffset.UtcNow);
        await svc.TickOnceAsync(CancellationToken.None);
        runner.Sent.Should().Equal("beacon_world_save");

        signals.Observe(AutosaveLine, DateTimeOffset.UtcNow.AddMilliseconds(5));
        await svc.TickOnceAsync(CancellationToken.None);
        runner.Sent.Should().Equal("beacon_world_save", "beacon_autosave toggle");
        svc.Policy.TookOver.Should().BeTrue();
    }

    [Fact]
    public async Task A_refused_save_request_is_a_failed_save()
    {
        var (svc, signals, runner, _) = Make(minutes: 10);
        runner.Answer = (name, _) => name == WorldSaveService.SaveCommand ? "fail no host controller" : "ok";
        signals.Observe(AutosaveLine, DateTimeOffset.UtcNow);
        await svc.TickOnceAsync(CancellationToken.None);
        svc.Policy.SavePending.Should().BeFalse();
        svc.Policy.TookOver.Should().BeFalse();
        signals.Observe(AutosaveLine, DateTimeOffset.UtcNow.AddMinutes(3));
        await svc.TickOnceAsync(CancellationToken.None);
        runner.Sent.Should().Equal("beacon_world_save");
    }

    [Fact]
    public async Task Two_saves_asked_at_once_make_one_save_and_both_get_its_answer()
    {
        var (svc, signals, runner, _) = Make();
        runner.OnSend = (name, args) =>
        {
            if (name == WorldSaveService.SaveCommand)
                _ = Task.Run(async () => { await Task.Delay(150); signals.Observe(AutosaveLine, DateTimeOffset.UtcNow); });
        };
        var a = svc.SaveNowAsync("panel stop", TimeSpan.FromSeconds(5));
        var b = svc.SaveNowAsync("owner", TimeSpan.FromSeconds(5), ownerAsked: true);
        var both = await Task.WhenAll(a, b);
        runner.Sent.Count(c => c == WorldSaveService.SaveCommand).Should().Be(1);
        both.Should().OnlyContain(o => o.Saved);
    }

    [Fact]
    public async Task An_owner_gets_one_save_per_30_seconds_and_otherwise_when_the_world_was_saved()
    {
        var (svc, signals, runner, _) = Make();
        var now = DateTimeOffset.UtcNow;
        svc.Clock = () => now;
        runner.OnSend = (name, args) => { if (name == WorldSaveService.SaveCommand) signals.Observe(AutosaveLine, now.AddMilliseconds(400)); };
        (await svc.SaveNowAsync("owner", TimeSpan.FromSeconds(5), ownerAsked: true)).Saved.Should().BeTrue();

        now = now.AddSeconds(12);
        var again = await svc.SaveNowAsync("owner", TimeSpan.FromSeconds(5), ownerAsked: true);
        again.Kind.Should().Be(SaveOutcomeKind.Recent);
        RconHostedService.FormatWorldSave(again).Should().Be("world saved 12 s ago (one save on request per 30 s, so no new save)");
        runner.Sent.Count(c => c == WorldSaveService.SaveCommand).Should().Be(1, "no new save inside the gap");

        // A save before a stop from this machine is not held to the owner's gap.
        (await svc.SaveNowAsync("panel stop", TimeSpan.FromSeconds(5))).Saved.Should().BeTrue();
        runner.Sent.Count(c => c == WorldSaveService.SaveCommand).Should().Be(2);

        now = now.AddSeconds(31);
        (await svc.SaveNowAsync("owner", TimeSpan.FromSeconds(5), ownerAsked: true)).Kind.Should().Be(SaveOutcomeKind.Asked);
        runner.Sent.Count(c => c == WorldSaveService.SaveCommand).Should().Be(3);
    }

    [Fact]
    public async Task An_owner_asking_again_after_a_save_that_did_not_land_is_told_when_to_ask_again()
    {
        var (svc, _, runner, _) = Make();
        var now = DateTimeOffset.UtcNow;
        svc.Clock = () => now;
        runner.Answer = (name, args) => name == WorldSaveService.SaveCommand ? "fail no host controller" : "ok";
        (await svc.SaveNowAsync("owner", TimeSpan.FromSeconds(5), ownerAsked: true)).Saved.Should().BeFalse();
        now = now.AddSeconds(10);
        var again = await svc.SaveNowAsync("owner", TimeSpan.FromSeconds(5), ownerAsked: true);
        again.Kind.Should().Be(SaveOutcomeKind.TooSoon);
        RconHostedService.FormatWorldSave(again).Should().StartWith("world save skipped: one save on request per 30 s; ")
            .And.EndWith("ask again in 20 s");
        runner.Sent.Count(c => c == WorldSaveService.SaveCommand).Should().Be(1);
    }

    [Theory]
    [InlineData("", 20, "")]
    [InlineData("12", 12, "")]
    [InlineData("20", 20, "")]
    [InlineData("25", 20, " (asked 25 s, used 20 s: the wait is 5-20 s)")]
    [InlineData("3", 5, " (asked 3 s, used 5 s: the wait is 5-20 s)")]
    [InlineData("soon", 20, " (not a number of seconds, used 20 s)")]
    public void Save_world_waits_5_to_20_seconds_and_says_when_it_held_the_number(string arg, int used, string note)
    {
        RconHostedService.WorldSaveSeconds(arg, 20).Should().Be((used, note));
    }

    [Fact]
    public void A_configured_wait_outside_5_to_20_seconds_is_held_too()
    {
        RconHostedService.WorldSaveSeconds("", 60).Seconds.Should().Be(20);
        RconHostedService.WorldSaveSeconds("", 1).Seconds.Should().Be(5);
    }

    [Fact]
    public void The_save_signals_count_only_the_game_s_save_lines()
    {
        var signals = new GameSaveSignals();
        signals.Observe("[2026.11.13-12.00.00:000][  1]LogSN2: Not sending player status event", DateTimeOffset.UtcNow).Should().BeFalse();
        signals.Observe(AutosaveLine, DateTimeOffset.UtcNow).Should().BeTrue();
        signals.DrainAutosaves().Should().HaveCount(1);
        signals.DrainAutosaves().Should().BeEmpty();
        signals.RunGeneration.Should().Be(0);
        signals.Observe("[2026.11.13-12.00.00:000][  0]LogSaveSystem: Save System initializing: SlotName savegame_0, Thumbnails enabled: true, Autosave enabled: true",
            DateTimeOffset.UtcNow).Should().BeTrue();
        signals.RunGeneration.Should().Be(1);
        signals.AutosaveEnabledAtStart.Should().BeTrue();
    }
}

public class Sn2LogTailSaveSignalTests
{
    private static Sn2LogTailService Tail(GameSaveSignals signals) =>
        new(NullLogger<Sn2LogTailService>.Instance, Options.Create(new BeaconServerOptions()),
            new PipeServerState(NullLogger<PipeServerState>.Instance), signals);

    [Fact]
    public void A_live_save_line_reaches_the_world_save_service()
    {
        var signals = new GameSaveSignals();
        Tail(signals).EmitLine("[2026.10.22-22.13.46:771][ 61]LogUWEAnalytics: Not logged in to GPP. Queuing up event 'autosave_completed'.", trackPlayerEvents: true);
        signals.DrainAutosaves().Should().HaveCount(1);
    }

    [Fact]
    public void A_save_line_in_the_old_log_read_at_start_up_does_not()
    {
        var signals = new GameSaveSignals();
        Tail(signals).EmitLine("[2026.10.22-22.13.46:771][ 61]LogUWEAnalytics: Not logged in to GPP. Queuing up event 'autosave_completed'.", trackPlayerEvents: false);
        signals.DrainAutosaves().Should().BeEmpty();
    }
}

public class WorldSaveStatusTests
{
    [Fact]
    public async Task Save_status_names_who_keeps_the_world_saved_and_what_the_game_says()
    {
        var runner = new StatusRunner();
        var svc = new WorldSaveService(NullLogger<WorldSaveService>.Instance,
            Options.Create(new BeaconServerOptions { SnUserDir = "", SnInstallRoot = "" }),
            new PipeServerState(NullLogger<PipeServerState>.Instance), new GameSaveSignals(), runner)
        { GameRunning = () => true };
        var line = await svc.StatusAsync();
        line.Should().StartWith("Beacon saves before a stop; the game's own autosave keeps the world otherwise");
        line.Should().EndWith("game: ok autosave=on cheat_manager=no save_component=yes");
        runner.Sent.Should().Equal("beacon_autosave status");
    }

    private sealed class StatusRunner : IHostCommandRunner
    {
        public readonly List<string> Sent = new();
        public Task<string?> RunAsync(string name, string[] args, CancellationToken ct)
        {
            Sent.Add((name + " " + string.Join(' ', args)).Trim());
            return Task.FromResult<string?>("ok autosave=on cheat_manager=no save_component=yes");
        }
    }
}

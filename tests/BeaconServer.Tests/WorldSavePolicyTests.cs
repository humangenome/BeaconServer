using BeaconServer.Services;
using FluentAssertions;

namespace BeaconServer.Tests;

// The world-save rules with a fake clock. A "game line" is the game's own finished-save line
// (autosave_completed) arriving when Beacon has not asked for a save; an "own line" is the same
// line arriving after Beacon's request.
public class WorldSavePolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private sealed class Rig
    {
        public readonly WorldSavePolicy Policy;
        public DateTimeOffset Now = T0;
        public int Players;
        public int Run;
        public DateTimeOffset? FileAt;
        public readonly List<WorldSaveNote> Notes = new();

        public Rig(int minutes = 10, string method = "console") => Policy = new WorldSavePolicy(minutes, method);

        public IReadOnlyList<WorldSaveAction> Tick(params DateTimeOffset[] lines)
        {
            var r = Policy.Tick(Now, new WorldSavePolicy.TickInput(Players, lines, Run, FileAt));
            Notes.AddRange(r.Notes);
            return r.Actions;
        }

        public IReadOnlyList<WorldSaveAction> At(double seconds, params double[] linesAtSeconds)
        {
            Now = T0.AddSeconds(seconds);
            return Tick(linesAtSeconds.Select(s => T0.AddSeconds(s)).ToArray());
        }

        // A game line at `seconds`, then Beacon's proving save confirmed 3 s later: Beacon has taken over.
        public void TakeOver(double seconds = 100)
        {
            At(seconds, seconds).Should().ContainSingle(a => a.Kind == WorldSaveActionKind.Save);
            At(seconds + 3, seconds + 3).Should().ContainSingle(a => a.Kind == WorldSaveActionKind.AutosaveOff);
            Policy.TookOver.Should().BeTrue();
        }
    }

    [Fact]
    public void Off_by_default_the_game_keeps_its_autosave_and_nothing_is_sent()
    {
        var rig = new Rig(minutes: 0);
        for (var s = 0; s <= 3600; s += 120) rig.At(s, s).Should().BeEmpty();
        rig.Policy.TookOver.Should().BeFalse();
    }

    [Fact]
    public void Off_by_default_a_save_before_stop_still_works()
    {
        var rig = new Rig(minutes: 0);
        rig.Policy.BeginSave(T0, "stop")!.Kind.Should().Be(WorldSaveActionKind.Save);
        rig.At(2, 2).Should().BeEmpty();
        rig.Policy.OutcomeFor(T0)!.Saved.Should().BeTrue();
    }

    [Fact]
    public void Nothing_is_switched_before_the_game_has_autosaved_by_itself()
    {
        var rig = new Rig { Players = 2 };
        for (var s = 0; s <= 900; s += 2) rig.At(s).Should().BeEmpty();
        rig.Policy.TookOver.Should().BeFalse();
    }

    [Fact]
    public void After_the_first_game_autosave_Beacon_saves_once_and_switches_off_only_when_that_save_lands()
    {
        var rig = new Rig { Players = 1 };
        var first = rig.At(90, 90);
        first.Should().ContainSingle().Which.Kind.Should().Be(WorldSaveActionKind.Save);
        rig.At(92).Should().BeEmpty("the proving save has not landed yet");
        var off = rig.At(94, 93);
        off.Should().ContainSingle().Which.Should().Be(new WorldSaveAction(WorldSaveActionKind.AutosaveOff, "Beacon takes over saving", "console"));
        rig.Policy.TookOver.Should().BeTrue();
        rig.Policy.OffRequested.Should().BeTrue();
    }

    [Fact]
    public void The_world_file_also_confirms_a_save()
    {
        var rig = new Rig();
        rig.At(90, 90);
        rig.FileAt = T0.AddSeconds(91.5);
        rig.At(92).Should().ContainSingle(a => a.Kind == WorldSaveActionKind.AutosaveOff);
    }

    [Fact]
    public void A_file_written_before_the_request_does_not_confirm_it()
    {
        var rig = new Rig();
        rig.FileAt = T0.AddSeconds(89);
        rig.At(90, 90);
        rig.At(92).Should().BeEmpty();
        rig.Policy.SavePending.Should().BeTrue();
    }

    [Fact]
    public void If_the_proving_save_never_lands_the_game_keeps_its_autosave_for_the_run()
    {
        var rig = new Rig { Players = 1 };
        rig.At(90, 90);
        rig.At(160).Should().BeEmpty("the save failed and nothing was ever switched");
        rig.Policy.TookOver.Should().BeFalse();
        // Later game autosaves do not start another attempt on this run.
        rig.At(210, 210).Should().BeEmpty();
        rig.At(330, 330).Should().BeEmpty();
        rig.Notes.Should().Contain(n => n.Text.Contains("stays on for this run"));
    }

    [Fact]
    public void The_game_module_refusing_the_proving_save_also_leaves_the_autosave_on()
    {
        var rig = new Rig();
        rig.At(90, 90);
        rig.Policy.SaveRequestFailed(rig.Now, "fail no host controller").Actions.Should().BeEmpty();
        rig.At(92, 92).Should().BeEmpty();
        rig.Policy.TookOver.Should().BeFalse();
    }

    [Fact]
    public void Lines_inside_the_window_after_Beacon_s_request_are_Beacon_s_own()
    {
        var rig = new Rig { Players = 1 };
        rig.TakeOver();
        // Two lines from one request (or a late one): neither counts as the game's autosave.
        rig.At(110, 110).Should().BeEmpty();
        rig.At(180, 180).Should().BeEmpty();
        rig.Policy.OffRequested.Should().BeTrue();
    }

    [Fact]
    public void Beacon_saves_every_N_minutes_while_players_have_been_on()
    {
        var rig = new Rig(minutes: 10) { Players = 2 };
        rig.TakeOver(100);
        rig.At(103 + 599).Should().BeEmpty();
        rig.At(103 + 600).Should().ContainSingle().Which.Reason.Should().Be("every 10 minutes");
        rig.At(103 + 602, 103 + 602).Should().BeEmpty();
        rig.At(103 + 1200).Should().ContainSingle(a => a.Kind == WorldSaveActionKind.Save);
    }

    [Fact]
    public void An_empty_world_that_was_saved_after_everyone_left_is_not_saved_again()
    {
        var rig = new Rig(minutes: 10) { Players = 0 };
        rig.TakeOver(100);
        for (var s = 110; s <= 3600; s += 10) rig.At(s).Should().BeEmpty();
    }

    [Fact]
    public void Players_leaving_get_one_save_15_seconds_after_the_last_of_the_batch()
    {
        var rig = new Rig { Players = 3 };
        rig.TakeOver(100);
        rig.Players = 2;
        rig.At(200).Should().BeEmpty();
        rig.Players = 0;
        rig.At(205).Should().BeEmpty();
        rig.At(219).Should().BeEmpty();
        var save = rig.At(220);
        save.Should().ContainSingle().Which.Reason.Should().Be("players left");
        rig.At(222, 222).Should().BeEmpty();
        rig.At(400).Should().BeEmpty("one save for the batch");
    }

    [Fact]
    public void A_join_is_not_a_save()
    {
        var rig = new Rig { Players = 1 };
        rig.TakeOver(100);
        rig.Players = 2;
        rig.At(200).Should().BeEmpty();
        rig.At(240).Should().BeEmpty();
    }

    [Fact]
    public void A_game_autosave_after_the_switch_is_answered_only_after_three_minutes_and_only_on_a_fresh_line()
    {
        var rig = new Rig { Players = 1 };
        rig.TakeOver(100);                          // switched off at 103
        rig.At(223, 223).Should().BeEmpty("120 s after the switch is too soon to send it again");
        rig.Policy.OffRequested.Should().BeFalse("the line proved the game is still autosaving");
        rig.At(300).Should().BeEmpty("no fresh line, so no toggle: its state is not proven");
        var again = rig.At(343, 343);
        again.Should().ContainSingle().Which.Should().Be(new WorldSaveAction(WorldSaveActionKind.AutosaveOff, "the game autosaved after the switch", "console"));
    }

    [Fact]
    public void After_three_switches_that_do_not_hold_Beacon_leaves_the_game_s_autosave_on()
    {
        var rig = new Rig { Players = 1 };
        rig.TakeOver(100);                                  // try 1 at 103
        rig.At(290, 290).Should().ContainSingle(a => a.Kind == WorldSaveActionKind.AutosaveOff);   // try 2
        rig.At(480, 480).Should().ContainSingle(a => a.Kind == WorldSaveActionKind.AutosaveOff);   // try 3
        rig.At(670, 670).Should().BeEmpty();
        rig.Policy.TookOver.Should().BeFalse();
        for (var s = 680; s < 3000; s += 120) rig.At(s, s).Should().BeEmpty();
        rig.Notes.Should().Contain(n => n.Text.Contains("stayed on after 3 tries"));
    }

    [Fact]
    public void A_scheduled_save_that_fails_switches_the_game_s_autosave_back_on_and_stops_the_schedule()
    {
        var rig = new Rig { Players = 1 };
        rig.TakeOver(100);
        var save = rig.At(703);
        save.Should().ContainSingle(a => a.Kind == WorldSaveActionKind.Save);
        var back = rig.At(764);
        back.Should().ContainSingle().Which.Should().Be(new WorldSaveAction(WorldSaveActionKind.AutosaveOn, "a world save failed", "console"));
        rig.Policy.Fallback.Should().BeTrue();
        rig.Policy.TookOver.Should().BeFalse();
        rig.Notes.Should().Contain(n => n.Level == WorldSaveNoteLevel.Error && n.Text.Contains("back on for the rest of this run"));
        for (var s = 770; s < 3000; s += 30) rig.At(s).Should().BeEmpty();
    }

    [Fact]
    public void The_game_s_own_line_after_switching_back_on_is_reported()
    {
        var rig = new Rig { Players = 1 };
        rig.TakeOver(100);
        rig.At(703);
        rig.At(764);
        rig.At(850, 850);
        rig.Notes.Should().Contain(n => n.Text.Contains("back on (it saved by itself)"));
        rig.Notes.Should().NotContain(n => n.Text.Contains("has not autosaved since"));
    }

    [Fact]
    public void No_autosave_after_switching_back_on_is_reported_as_an_error_once()
    {
        var rig = new Rig { Players = 1 };
        rig.TakeOver(100);
        rig.At(703);
        rig.At(764);
        for (var s = 770; s < 1500; s += 10) rig.At(s);
        rig.Notes.Count(n => n.Text.Contains("has not autosaved since")).Should().Be(1);
    }

    [Fact]
    public void A_toggle_is_never_sent_back_when_the_game_was_proven_to_be_autosaving()
    {
        // The switch did not hold (a game line came), so a toggle now would turn it OFF.
        var rig = new Rig { Players = 1 };
        rig.TakeOver(100);
        rig.At(223, 223);                       // the game autosaved: it is on
        var save = rig.At(703);
        save.Should().ContainSingle(a => a.Kind == WorldSaveActionKind.Save);
        rig.At(764).Should().BeEmpty("the game's autosave is already on; a toggle would switch it off");
        rig.Policy.Fallback.Should().BeTrue();
    }

    [Fact]
    public void The_setting_switch_is_set_back_on_after_a_failure_either_way()
    {
        var rig = new Rig(method: "setting") { Players = 1 };
        rig.At(100, 100);
        rig.At(103, 103).Should().ContainSingle().Which.Should().Be(new WorldSaveAction(WorldSaveActionKind.AutosaveOff, "Beacon takes over saving", "setting"));
        rig.At(223, 223);
        rig.At(703);
        rig.At(764).Should().ContainSingle().Which.Should().Be(new WorldSaveAction(WorldSaveActionKind.AutosaveOn, "a world save failed", "setting"));
    }

    [Fact]
    public void A_switch_the_game_refused_means_nothing_changed_and_Beacon_stops()
    {
        var rig = new Rig { Players = 1 };
        rig.TakeOver(100);
        rig.Policy.SwitchResult(rig.Now, WorldSaveActionKind.AutosaveOff, SwitchAnswer.NotApplied, "fail the host has no cheat manager");
        rig.Policy.TookOver.Should().BeFalse();
        for (var s = 110; s < 2000; s += 30) rig.At(s, s).Should().BeEmpty();
    }

    [Fact]
    public void A_switch_with_no_answer_keeps_Beacon_s_schedule_running()
    {
        var rig = new Rig { Players = 1 };
        rig.TakeOver(100);
        rig.Policy.SwitchResult(rig.Now, WorldSaveActionKind.AutosaveOff, SwitchAnswer.Unknown, "(no response from mod)");
        rig.Policy.TookOver.Should().BeTrue();
        rig.At(703).Should().ContainSingle(a => a.Kind == WorldSaveActionKind.Save);
    }

    [Fact]
    public void A_stop_save_joins_a_save_already_in_flight()
    {
        var rig = new Rig();
        rig.Policy.BeginSave(T0, "stop").Should().NotBeNull();
        rig.Policy.BeginSave(T0.AddSeconds(1), "stop again").Should().BeNull();
        rig.Policy.PendingSince.Should().Be(T0);
    }

    [Fact]
    public void A_failed_stop_save_before_Beacon_took_over_does_not_block_taking_over_later()
    {
        var rig = new Rig { Players = 1 };
        rig.Policy.BeginSave(T0, "save world (RCON)");
        rig.At(61).Should().BeEmpty();
        rig.Policy.OutcomeFor(T0)!.Saved.Should().BeFalse();
        rig.At(200, 200).Should().ContainSingle(a => a.Kind == WorldSaveActionKind.Save);
        rig.At(203, 203).Should().ContainSingle(a => a.Kind == WorldSaveActionKind.AutosaveOff);
    }

    [Fact]
    public void A_stop_save_after_taking_over_is_confirmed_like_any_other()
    {
        var rig = new Rig { Players = 1 };
        rig.TakeOver(100);
        var at = T0.AddSeconds(300);
        rig.Policy.BeginSave(at, "save world (RCON)")!.Kind.Should().Be(WorldSaveActionKind.Save);
        rig.At(302, 301.5);
        var outcome = rig.Policy.OutcomeFor(at)!;
        outcome.Saved.Should().BeTrue();
        outcome.Took.TotalSeconds.Should().BeApproximately(1.5, 0.01);
    }

    [Fact]
    public void A_new_game_run_starts_over_with_the_game_s_autosave_on()
    {
        var rig = new Rig { Players = 1 };
        rig.TakeOver(100);
        rig.Run = 1;
        rig.At(400).Should().BeEmpty();
        rig.Policy.TookOver.Should().BeFalse();
        rig.Policy.OffRequested.Should().BeFalse();
        rig.At(490, 490).Should().ContainSingle(a => a.Kind == WorldSaveActionKind.Save, "the new run proves Beacon's save again");
    }

    [Fact]
    public void A_new_run_after_a_failure_tries_again()
    {
        var rig = new Rig { Players = 1 };
        rig.At(90, 90);
        rig.At(160);                // proving save failed: no takeover this run
        rig.Run = 1;
        rig.At(300).Should().BeEmpty();
        rig.At(390, 390).Should().ContainSingle(a => a.Kind == WorldSaveActionKind.Save);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(10, 10)]
    [InlineData(500, 60)]
    public void The_interval_is_held_between_5_and_60_minutes(int asked, int used)
    {
        var rig = new Rig(minutes: asked) { Players = 1 };
        rig.TakeOver(100);
        rig.At(103 + used * 60 - 1).Should().BeEmpty();
        rig.At(103 + used * 60).Should().ContainSingle(a => a.Kind == WorldSaveActionKind.Save);
    }

    [Fact]
    public void An_unknown_switch_name_means_the_console_toggle()
    {
        new WorldSavePolicy(10, "whatever").Switch.Should().Be("console");
        new WorldSavePolicy(10, "Setting").Switch.Should().Be("setting");
    }
}

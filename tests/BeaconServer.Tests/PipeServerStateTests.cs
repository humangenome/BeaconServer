using Beacon.Protocol;
using BeaconServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeaconServer.Tests;

public class PipeServerStateTests
{
    [Fact]
    public void EffectivePlayerCount_uses_current_roster_not_stale_heartbeat_count()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance)
        {
            LastReportedPlayerCount = 2
        };

        state.SetPlayers(Array.Empty<PlayerSnapshot>());

        state.EffectivePlayerCount.Should().Be(0);
    }

    [Fact]
    public void EffectivePlayerCount_tracks_non_empty_roster()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance)
        {
            LastReportedPlayerCount = 4
        };

        state.SetPlayers(new[]
        {
            new PlayerSnapshot("steam:1", "One", 1000, 2000, 42)
        });

        state.EffectivePlayerCount.Should().Be(1);
    }

    [Fact]
    public void EffectivePlayerCount_uses_log_tracked_players_when_roster_file_is_empty()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);

        state.SetPlayers(Array.Empty<PlayerSnapshot>());
        state.UpsertLogPlayer("76561197979989479_60226C45E673E631", "Peatross");

        state.EffectivePlayerCount.Should().Be(1);
        state.Players.Single().DisplayName.Should().Be("Peatross");
    }

    [Fact]
    public void Players_deduplicate_log_player_against_roster_snapshot_by_name()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);

        state.SetPlayers(new[]
        {
            new PlayerSnapshot("roster:peatross", "Peatross", 1000, 2000, 0)
        });
        state.UpsertLogPlayer("76561197979989479_60226C45E673E631", "Peatross");

        state.EffectivePlayerCount.Should().Be(1);
    }

    [Fact]
    public void Two_players_with_one_name_both_count_in_the_host_list()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);

        state.SetPlayers(new[]
        {
            new PlayerSnapshot("steam:1", "Sam", 1000, 2000, 0),
            new PlayerSnapshot("steam:2", "Sam", 1000, 2000, 0),
        });

        state.EffectivePlayerCount.Should().Be(2);
        state.Players.Select(p => p.BeaconUserId).Should().BeEquivalentTo("steam:1", "steam:2");
    }

    [Fact]
    public void Two_players_with_one_name_both_count_from_the_log()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);

        state.UpsertLogPlayer("76561197979989479_60226C45E673E631", "Sam");
        state.UpsertLogPlayer("76561197960287930_11111111AAAAAAAA", "Sam");

        state.EffectivePlayerCount.Should().Be(2);
    }

    [Fact]
    public void A_log_entry_matches_one_host_entry_of_its_name_and_the_rest_still_count()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);

        state.SetPlayers(new[] { new PlayerSnapshot("roster:sam", "Sam", 1000, 2000, 0) });
        state.UpsertLogPlayer("76561197979989479_60226C45E673E631", "Sam");
        state.EffectivePlayerCount.Should().Be(1, "the same player in both lists");

        state.UpsertLogPlayer("76561197960287930_11111111AAAAAAAA", "Sam");
        state.EffectivePlayerCount.Should().Be(2, "the second Sam is in the log before the next host list");
    }

    [Fact]
    public void One_account_reconnecting_with_another_character_counts_once()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);

        state.UpsertLogPlayer("76561197979989479_60226C45E673E631", "Sam");
        state.UpsertLogPlayer("76561197979989479_0000000000000002", "Sam");

        state.EffectivePlayerCount.Should().Be(1);
    }

    [Fact]
    public void A_log_entry_with_no_real_id_does_not_add_a_player()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);

        state.UpsertLogPlayer("", "Sam");
        state.UpsertLogPlayer("76561197979989479_60226C45E673E631", "Sam");
        state.EffectivePlayerCount.Should().Be(1, "the name-only entry and the real one are one player");

        state.SetPlayers(new[] { new PlayerSnapshot("roster:sam", "Sam", 1000, 2000, 0) });
        state.EffectivePlayerCount.Should().Be(1);
    }

    [Fact]
    public void A_leave_by_address_with_two_same_named_players_does_not_clear_the_other()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);

        state.UpsertLogPlayer("76561197979989479_60226C45E673E631", "Sam");
        state.UpsertLogPlayer("76561197960287930_11111111AAAAAAAA", "Sam");

        state.ClearLogPlayersIfOnlyOne();

        state.EffectivePlayerCount.Should().Be(2);
    }

    private static PipeConnection Conn() =>
        new("default", 4242, "test", new Beacon.Protocol.FrameCodec(new byte[32]), (_, _) => Task.CompletedTask);

    [Fact]
    public void The_roster_is_the_list_while_it_is_fresh()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        state.SetConnection(Conn());
        var t0 = DateTimeOffset.UtcNow;

        state.SetPlayersFromRosterFile(new[] { new PlayerSnapshot("roster:sam", "Sam", 1000, 2000, 0, 1, 2, 3) }, t0, t0);
        state.SetPlayersFromPlugin(new[] { new PlayerSnapshot("512", "Sam", 0, 2000, 0) }, t0 + TimeSpan.FromSeconds(5));

        state.Players.Single().BeaconUserId.Should().Be("roster:sam", "the id does not flip between polls");
        state.Players.Single().ConnectedAtUnixMs.Should().Be(1000);
        state.Players.Single().PosX.Should().Be(1);
    }

    [Fact]
    public void The_plugin_list_counts_once_the_roster_goes_quiet()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        state.SetConnection(Conn());
        var t0 = DateTimeOffset.UtcNow;

        state.SetPlayersFromRosterFile(new[] { new PlayerSnapshot("roster:sam", "Sam", 1000, 2000, 0) }, t0, t0);
        state.SetPlayersFromPlugin(Array.Empty<PlayerSnapshot>(), t0 + PipeServerState.RosterFreshFor + TimeSpan.FromSeconds(1));

        state.EffectivePlayerCount.Should().Be(0);
    }

    [Fact]
    public void A_game_that_ended_leaves_no_players_or_addresses()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        state.SetConnection(Conn());
        state.SetPlayersFromRosterFile(new[] { new PlayerSnapshot("roster:sam", "Sam", 1000, 2000, 0) }, DateTimeOffset.UtcNow);
        state.UpsertLogPlayer("76561197979989479_60226C45E673E631", "Sam");
        state.NotePeer("203.0.113.5:7777", "76561197979989479_60226C45E673E631", "Sam");

        state.GameConnectionEnded();

        state.EffectivePlayerCount.Should().Be(0);
        state.PeersAt(System.Net.IPAddress.Parse("203.0.113.5")).Should().BeEmpty();
        // and the next game's plugin list is not held back by the old roster's time
        state.SetPlayersFromPlugin(new[] { new PlayerSnapshot("512", "Lee", 0, 2000, 0) });
        state.EffectivePlayerCount.Should().Be(1);
    }

    [Fact]
    public void The_last_roster_of_a_game_that_ended_does_not_bring_its_players_back()
    {
        // Lab 10/31: the game wrote roster.json, died, the pipe closed and cleared the list, then the watcher read that
        // last file and put the player back for minutes.
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        state.SetConnection(Conn());
        var lastWrite = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2);
        state.SetPlayersFromRosterFile(new[] { new PlayerSnapshot("roster:sam", "Sam", 1000, 2000, 0) }, lastWrite).Should().BeTrue();

        state.SetConnection(null);
        state.GameConnectionEnded();

        state.SetPlayersFromRosterFile(new[] { new PlayerSnapshot("roster:sam", "Sam", 1000, 2000, 0) }, lastWrite).Should().BeFalse();
        state.EffectivePlayerCount.Should().Be(0);
    }

    [Theory]
    [InlineData("", 0, 0, "off")]
    [InlineData("secret", 1, 1, "on")]
    [InlineData("secret", 1, 0, "not_active")]
    [InlineData("secret", 0, 0, "not_active")]
    [InlineData("", 1, 0, "not_active")]
    public void The_password_check_state_says_whether_joins_are_checked(string password, int configured, int ready, string expected)
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        state.SetConnection(Conn());
        state.LastServerPasswordConfigured = configured;
        state.LastServerPasswordHookReady = ready;
        state.PasswordCheckState(password).Should().Be(expected);
    }

    [Fact]
    public void The_password_check_state_is_unknown_with_no_game()
    {
        new PipeServerState(NullLogger<PipeServerState>.Instance).PasswordCheckState("secret").Should().Be("unknown");
    }

    [Fact]
    public void A_roster_file_counts_only_while_the_game_is_connected()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        state.SetPlayersFromRosterFile(new[] { new PlayerSnapshot("roster:sam", "Sam", 1000, 2000, 0) }, DateTimeOffset.UtcNow).Should().BeFalse();
        state.EffectivePlayerCount.Should().Be(0);
    }

    [Fact]
    public void An_old_roster_file_is_not_listed_when_the_next_game_connects()
    {
        // Lab 10/31: a BeaconServer started at the next boot listed the last player from a 5-minute-old roster.json.
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        state.SetConnection(Conn());
        var old = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
        state.SetPlayersFromRosterFile(new[] { new PlayerSnapshot("roster:sam", "Sam", 1000, 2000, 0) }, old).Should().BeFalse();
        state.EffectivePlayerCount.Should().Be(0);

        // the new game's own first write counts
        state.SetPlayersFromRosterFile(Array.Empty<PlayerSnapshot>(), DateTimeOffset.UtcNow).Should().BeTrue();
    }

    [Fact]
    public void A_BeaconServer_restart_with_the_game_up_takes_the_games_last_write()
    {
        // The game keeps running and reconnects within seconds; its last write just before the reconnect is current.
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        state.SetConnection(Conn());
        var justBefore = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(4);
        state.SetPlayersFromRosterFile(new[] { new PlayerSnapshot("roster:sam", "Sam", 1000, 2000, 0) }, justBefore).Should().BeTrue();
        state.EffectivePlayerCount.Should().Be(1);
    }

    [Fact]
    public void One_app_reporting_a_position_does_not_hide_the_other_players()
    {
        var positions = new[] { new PipeServerState.ClientPosition("76561197979989479_6", "Sam", 10, 20, 30, 1) };
        var roster = new[]
        {
            new PlayerSnapshot("roster:sam", "Sam", 1000, 2000, 0, 1, 2, 3),
            new PlayerSnapshot("roster:lee", "Lee", 1000, 2000, 0, 4, 5, 6),
        };

        var rows = BeaconHttpService.MergeMapPlayers(positions, roster);

        rows.Should().HaveCount(2);
        var json = System.Text.Json.JsonSerializer.Serialize(rows);
        json.Should().Contain("\"Lee\"").And.Contain("\"x\":10").And.Contain("\"x\":4");
        json.Should().NotContain("\"x\":1,", "Sam's reported position wins over the roster's");
    }

    [Fact]
    public void RemoveLogPlayer_drops_log_tracked_player()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);

        state.UpsertLogPlayer("player:1", "One");
        state.RemoveLogPlayer("player:1");

        state.EffectivePlayerCount.Should().Be(0);
    }

    [Fact]
    public void RemoveLogPlayer_drops_matching_roster_snapshot()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);

        state.SetPlayers(new[]
        {
            new PlayerSnapshot("player:1", "One", 1000, 2000, 0)
        });
        state.UpsertLogPlayer("player:1", "One");
        state.RemoveLogPlayer("player:1");

        state.EffectivePlayerCount.Should().Be(0);
    }

    [Fact]
    public void RemoveLogPlayerByDisplayName_drops_stale_roster_snapshot_with_different_id()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);

        state.SetPlayers(new[]
        {
            new PlayerSnapshot("roster-object-path", "HumanGenome", 1000, 2000, 0)
        });
        state.UpsertLogPlayer("sonar-id", "HumanGenome");
        state.RemoveLogPlayerByDisplayName("HumanGenome");

        state.EffectivePlayerCount.Should().Be(0);
    }

    [Fact]
    public void ClearLogPlayersIfOnlyOne_drops_single_stale_roster_snapshot()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);

        state.SetPlayers(new[]
        {
            new PlayerSnapshot("roster-object-path", "HumanGenome", 1000, 2000, 0)
        });
        state.ClearLogPlayersIfOnlyOne();

        state.EffectivePlayerCount.Should().Be(0);
    }

    [Fact]
    public void Players_keeps_log_only_player_until_leave_event_removes_it()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        var old = DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeMilliseconds();

        state.SetPlayers(Array.Empty<PlayerSnapshot>());
        state.SetLogPlayerForTest(new PlayerSnapshot("player:1", "One", old, old, 0));

        state.EffectivePlayerCount.Should().Be(1);

        state.RemoveLogPlayer("player:1");

        state.EffectivePlayerCount.Should().Be(0);
    }
}

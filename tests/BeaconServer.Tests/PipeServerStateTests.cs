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

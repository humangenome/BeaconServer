using Beacon.Protocol;
using BeaconServer.Services;

namespace BeaconServer.Tests;

public class RconReplyTextTests
{
    private const long Now = 1_792_600_000_000;

    private static PlayerSnapshot Player(string name, long minutesAgo) =>
        new($"ws:{name}", name, Now - minutesAgo * 60_000, Now, 0);

    [Fact]
    public void Players_ListsEachNameWithTimeOnTheServer()
    {
        var text = RconHostedService.FormatPlayers(
            new[] { Player("Mirabel", 65), Player("Corvina", 25), Player("Thistlewick", 0) }, 8, Now);

        Assert.Equal("3 of 8 players online\nMirabel  1h 05m\nCorvina  25m\nThistlewick  just joined", text);
        Assert.DoesNotContain("Phase", text);
    }

    [Fact]
    public void Players_None()
    {
        Assert.Equal("no players online", RconHostedService.FormatPlayers(Array.Empty<PlayerSnapshot>(), 8, Now));
    }

    [Fact]
    public void Players_FallsBackToTheIdWhenANameIsMissing()
    {
        var text = RconHostedService.FormatPlayers(new[] { new PlayerSnapshot("ws:cp_7", "", Now - 120_000, Now, 0) }, 4, Now);

        Assert.Equal("1 of 4 players online\nws:cp_7  2m", text);
    }

}

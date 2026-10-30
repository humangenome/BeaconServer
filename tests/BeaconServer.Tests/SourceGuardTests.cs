using Beacon.Rcon;
using FluentAssertions;
using Xunit;

namespace BeaconServer.Tests;

[Collection("SourceGuard")]
public sealed class SourceGuardTests : IDisposable
{
    private const string Ip = "203.0.113.77";

    public SourceGuardTests() => SourceGuard.ResetForTests();
    public void Dispose() => SourceGuard.ResetForTests();

    [Fact]
    public void Five_failures_turn_an_address_away_for_a_minute()
    {
        const long t = 1_000_000;
        for (var i = 0; i < SourceGuard.FailuresBeforeBlock - 1; i++)
            SourceGuard.Failure(Ip, t).Should().BeFalse();
        SourceGuard.BlockedSeconds(Ip, t).Should().Be(0);

        SourceGuard.Failure(Ip, t).Should().BeTrue();
        SourceGuard.BlockedSeconds(Ip, t).Should().Be(60);
        SourceGuard.BlockedSeconds(Ip, t + SourceGuard.FirstBlockMs).Should().Be(0);
    }

    [Fact]
    public void Blocks_double_up_to_fifteen_minutes()
    {
        var t = 1_000_000L;
        long last = 0;
        for (var round = 0; round < 8; round++)
        {
            for (var i = 0; i < SourceGuard.FailuresBeforeBlock; i++) SourceGuard.Failure(Ip, t);
            last = SourceGuard.BlockedSeconds(Ip, t);
            t += last * 1000 + 1;
        }
        last.Should().Be(SourceGuard.MaxBlockMs / 1000);
    }

    [Fact]
    public void A_success_clears_the_count_and_a_quiet_window_restarts_it()
    {
        const long t = 1_000_000;
        for (var i = 0; i < 4; i++) SourceGuard.Failure(Ip, t);
        SourceGuard.Success(Ip);
        SourceGuard.Failure(Ip, t).Should().BeFalse();
        for (var i = 0; i < 3; i++) SourceGuard.Failure(Ip, t);
        SourceGuard.Failure(Ip, t + SourceGuard.FailureWindowMs + 1).Should().BeFalse("the window has passed");
    }

    [Fact]
    public void Loopback_is_never_limited()
    {
        for (var i = 0; i < 50; i++) SourceGuard.Failure("127.0.0.1", 1_000_000).Should().BeFalse();
        SourceGuard.BlockedSeconds("127.0.0.1", 1_000_000).Should().Be(0);
        var guard = new SourceGuard(1);
        guard.TryOpen("127.0.0.1").Should().BeTrue();
        guard.TryOpen("127.0.0.1").Should().BeTrue();
    }

    [Fact]
    public void One_address_may_hold_only_its_share_of_connections()
    {
        var guard = new SourceGuard(2);
        guard.TryOpen(Ip).Should().BeTrue();
        guard.TryOpen(Ip).Should().BeTrue();
        guard.TryOpen(Ip).Should().BeFalse();
        guard.TryOpen("203.0.113.78").Should().BeTrue("another address has its own share");
        guard.Close(Ip);
        guard.TryOpen(Ip).Should().BeTrue();
    }
}

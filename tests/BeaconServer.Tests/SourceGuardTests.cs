using System.Collections;
using System.Reflection;
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

    // The failure table holds 4096 addresses before it makes room and 65536 at most.
    private const int TableLimit = 4096;
    private const int TableCeiling = 65536;

    private static string Addr(int i) => $"10.{(i >> 16) & 255}.{(i >> 8) & 255}.{i & 255}";

    private static void BlockNow(string source, long t)
    {
        for (var i = 0; i < SourceGuard.FailuresBeforeBlock; i++) SourceGuard.Failure(source, t);
    }

    private static int TrackedSources()
    {
        var field = typeof(SourceGuard).GetField("Fails", BindingFlags.NonPublic | BindingFlags.Static)!;
        return ((ICollection)field.GetValue(null)!).Count;
    }

    [Fact]
    public void A_flood_of_new_addresses_never_lifts_an_active_block()
    {
        const long t = 1_000_000;
        BlockNow(Ip, t);
        for (var i = 0; i < TableLimit * 3; i++) SourceGuard.Failure(Addr(i), t + 1);

        SourceGuard.BlockedSeconds(Ip, t + 2).Should().Be(60, "wrong tries from many new addresses must not lift a block");
        TrackedSources().Should().BeLessThanOrEqualTo(TableLimit, "addresses that are not blocked make room for new ones");
    }

    [Fact]
    public void The_longest_quiet_addresses_that_are_not_blocked_make_room_first()
    {
        const long t = 1_000_000;
        for (var i = 0; i < TableLimit - 1; i++) SourceGuard.Failure(Addr(i), t);
        for (var i = 0; i < SourceGuard.FailuresBeforeBlock - 1; i++) SourceGuard.Failure(Ip, t + 5_000);

        SourceGuard.Failure(Addr(TableLimit), t + 5_001);
        SourceGuard.Failure(Ip, t + 5_002).Should().BeTrue("the newest guesser keeps its count; the oldest rows go");
    }

    [Fact]
    public void A_table_full_of_blocks_keeps_every_block_until_the_ceiling_then_drops_the_one_nearest_to_running_out()
    {
        const long t = 1_000_000;
        BlockNow(Ip, t);
        for (var i = 0; i < TableCeiling - 1; i++) BlockNow(Addr(i), t + 1_000);

        TrackedSources().Should().Be(TableCeiling, "every block is kept while the table grows");
        SourceGuard.BlockedSeconds(Ip, t + 2_000).Should().BeGreaterThan(0);
        SourceGuard.BlockedSeconds(Addr(0), t + 2_000).Should().BeGreaterThan(0);

        const string newcomer = "198.51.100.9";
        BlockNow(newcomer, t + 2_000);
        TrackedSources().Should().Be(TableCeiling, "the table never grows past its ceiling");
        SourceGuard.BlockedSeconds(newcomer, t + 2_000).Should().BeGreaterThan(0);
        SourceGuard.BlockedSeconds(Ip, t + 2_000).Should().Be(0, "at the ceiling the block nearest to running out makes way");
        SourceGuard.BlockedSeconds(Addr(0), t + 2_000).Should().BeGreaterThan(0);
        SourceGuard.BlockedSeconds(Addr(TableCeiling - 2), t + 2_000).Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_table_full_of_blocks_logs_one_line_with_no_address_in_it()
    {
        const long t = 1_000_000;
        for (var i = 0; i < TableLimit; i++) BlockNow(Addr(i), t);
        SourceGuard.TakeNotice().Should().BeNull("the table is full but has not had to grow yet");

        for (var i = TableLimit; i < TableLimit + 600; i++) BlockNow(Addr(i), t);
        SourceGuard.TakeNotice().Should().Be(
            "auth table full: all 4096 tracked addresses are blocked; every block is kept (up to 65536)");
        SourceGuard.TakeNotice().Should().BeNull("one line, not one per address");
    }
}

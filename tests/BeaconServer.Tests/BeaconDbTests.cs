using Beacon.Persistence;
using FluentAssertions;
using Xunit;

namespace BeaconServer.Tests;

public class BeaconDbTests : IDisposable
{
    private readonly string _file;
    private readonly BeaconDb _db;

    public BeaconDbTests()
    {
        _file = Path.Combine(Path.GetTempPath(), $"beacon-test-{Guid.NewGuid():N}.db");
        _db = new BeaconDb(_file);
    }

    public void Dispose()
    {
        try { File.Delete(_file); } catch { }
        try { File.Delete(_file + "-wal"); } catch { }
        try { File.Delete(_file + "-shm"); } catch { }
    }

    [Fact]
    public void Player_upsert_first_then_update_keeps_first_seen()
    {
        _db.UpsertPlayerSeen("user-1", "alice", 1_000);
        _db.UpsertPlayerSeen("user-1", "ALICE2", 2_000);

        var p = _db.GetPlayer("user-1");
        p.Should().NotBeNull();
        p!.FirstSeenUnix.Should().Be(1_000);
        p.LastSeenUnix.Should().Be(2_000);
        p.DisplayName.Should().Be("ALICE2");
    }

    [Fact]
    public void Active_ban_returns_only_when_unexpired()
    {
        _db.AddBan(new BanRecord("u1", "cheating", ExpiresUnix: 2_000, AddedBy: "Drew", AddedUnix: 100));
        _db.AddBan(new BanRecord("u2", "spam", ExpiresUnix: null, AddedBy: "Drew", AddedUnix: 200));

        _db.GetActiveBan("u1", 1_500).Should().NotBeNull();
        _db.GetActiveBan("u1", 5_000).Should().BeNull("ban expired");
        _db.GetActiveBan("u2", 5_000).Should().NotBeNull("permanent ban");
        _db.GetActiveBan("nobody", 1_500).Should().BeNull();
    }

    [Fact]
    public void Ticket_consume_once_only()
    {
        var t = new JoinTicketRecord("tk-1", "u1", "alice", 1_000, 2_000, ConsumedUnix: null);
        _db.IssueTicket(t);

        var first = _db.ConsumeTicket("tk-1", 1_500);
        first.Should().NotBeNull();

        var second = _db.ConsumeTicket("tk-1", 1_600);
        second.Should().BeNull("ticket already consumed");

        var expired = new JoinTicketRecord("tk-2", "u2", "bob", 1_000, 1_400, ConsumedUnix: null);
        _db.IssueTicket(expired);
        _db.ConsumeTicket("tk-2", 1_500).Should().BeNull("ticket expired before consumption");
    }

    [Fact]
    public void Concurrent_ticket_consume_only_succeeds_once()
    {
        // Race 64 threads trying to consume the same ticket. The atomic
        // UPDATE ... RETURNING in BeaconDb must let exactly one win.
        var t = new JoinTicketRecord("race-tk", "u1", "alice", 1_000, 9_999_999, ConsumedUnix: null);
        _db.IssueTicket(t);

        var hits = 0;
        var threads = new Thread[64];
        for (int i = 0; i < threads.Length; i++)
        {
            threads[i] = new Thread(() =>
            {
                var got = _db.ConsumeTicket("race-tk", 5_000);
                if (got is not null) Interlocked.Increment(ref hits);
            });
        }
        foreach (var th in threads) th.Start();
        foreach (var th in threads) th.Join();
        hits.Should().Be(1, "exactly one thread should win the consume race");
    }

    [Fact]
    public void Audit_log_round_trip()
    {
        _db.Audit("AdminTest", "rcon.exec", target: null, detailJson: "{\"cmd\":\"status\"}", unixSeconds: 1_000);
        _db.Audit("Drew", "ban.add", target: "u1", detailJson: null, unixSeconds: 2_000);

        var entries = _db.RecentAudit(10);
        entries.Should().HaveCount(2);
        entries[0].Action.Should().Be("ban.add");
        entries[1].Action.Should().Be("rcon.exec");
    }

    [Fact]
    public void Snapshot_history_orders_newest_first()
    {
        _db.RecordSnapshot(new SnapshotRecord("s1", 1_000, "/saves/s1.zip", 1024, "abc", 30));
        _db.RecordSnapshot(new SnapshotRecord("s2", 2_000, "/saves/s2.zip", 2048, "def", 30));
        var list = _db.ListSnapshots();
        list.Should().HaveCount(2);
        list[0].SnapshotId.Should().Be("s2");
        list[1].SnapshotId.Should().Be("s1");
    }
}

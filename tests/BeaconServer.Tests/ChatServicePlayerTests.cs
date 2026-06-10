using BeaconServer.Configuration;
using BeaconServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeaconServer.Tests;

/// <summary>
/// Covers the v0.3.0 player-chat ingest path (BroadcastFromPlayer + per-IP
/// rate limiter). Server-broadcast and ring buffer behaviour is exercised
/// implicitly via the same code path.
/// </summary>
public class ChatServicePlayerTests : IDisposable
{
    private readonly string _baseDir;
    private readonly ChatService _svc;

    public ChatServicePlayerTests()
    {
        // ChatService writes outbound/inbound/history/motd files relative to
        // AppContext.BaseDirectory. The test runner runs in its own temp
        // working dir; the files land alongside the test DLL. We don't
        // need to redirect — just clean up after.
        _baseDir = AppContext.BaseDirectory;
        Cleanup();

        var opts = Options.Create(new BeaconServerOptions
        {
            Chat = new ChatOptions { RingBufferSize = 200 },
        });
        var monitor = new TestOptionsMonitor<BeaconServerOptions>(opts.Value);
        var dispatch = new CommandDispatchService(NullLogger<CommandDispatchService>.Instance, monitor);
        _svc = new ChatService(NullLogger<ChatService>.Instance, monitor, dispatch);
    }

    public void Dispose() => Cleanup();

    private void Cleanup()
    {
        foreach (var f in new[] { "chat-outbound.json", "chat-inbound.json",
                                  "chat-history.json", "motd.txt" })
        {
            try { File.Delete(Path.Combine(_baseDir, f)); } catch { }
        }
    }

    [Fact]
    public void BroadcastFromPlayer_appends_to_ring_with_player_channel()
    {
        var entry = _svc.BroadcastFromPlayer("10.0.0.1", "Drew", "hello server");

        entry.Should().NotBeNull();
        entry!.Channel.Should().Be("player");
        entry.Sender.Should().Be("Drew");
        entry.Target.Should().Be("all");
        entry.Msg.Should().Be("hello server");

        var ring = _svc.GetRecent(0, 100);
        ring.Should().ContainSingle().Which.Msg.Should().Be("hello server");
    }

    [Fact]
    public void BroadcastFromPlayer_falls_back_to_Player_when_sender_blank()
    {
        var entry = _svc.BroadcastFromPlayer("10.0.0.2", "", "anon msg");
        entry!.Sender.Should().Be("Player");
    }

    [Fact]
    public void BroadcastFromPlayer_rejects_empty_body()
    {
        _svc.BroadcastFromPlayer("10.0.0.3", "Drew", "").Should().BeNull();
        _svc.BroadcastFromPlayer("10.0.0.3", "Drew", "   ").Should().BeNull();
    }

    [Fact]
    public void BroadcastFromPlayer_normalises_long_body_and_strips_control_chars()
    {
        var dirty = "helloworld" + new string('x', 600);
        var entry = _svc.BroadcastFromPlayer("10.0.0.4", "Drew", dirty);
        entry.Should().NotBeNull();
        entry!.Msg.Should().NotContain("");
        // 512-byte UTF-8 cap leaves the start intact
        entry.Msg.Should().StartWith("helloworld");
        entry.Msg.Length.Should().BeLessThanOrEqualTo(512);
    }

    [Fact]
    public void BroadcastFromPlayer_burst_within_2s_returns_null()
    {
        var first = _svc.BroadcastFromPlayer("10.0.0.5", "Drew", "first");
        first.Should().NotBeNull();

        // Same IP, immediate retry → rejected by 2s burst cap
        var second = _svc.BroadcastFromPlayer("10.0.0.5", "Drew", "second");
        second.Should().BeNull();

        // Different IP same instant → not rate limited
        var other = _svc.BroadcastFromPlayer("10.0.0.6", "Other", "from other ip");
        other.Should().NotBeNull();
    }

    [Fact]
    public void BroadcastFromPlayer_independent_IPs_each_get_their_own_bucket()
    {
        for (int i = 0; i < 5; i++)
        {
            var ip = $"10.0.1.{i + 10}";
            _svc.BroadcastFromPlayer(ip, "Drew", $"hi from {ip}").Should().NotBeNull(
                because: $"each IP gets its own bucket — {ip} hasn't sent before");
        }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => new NullDisposable();
        private sealed class NullDisposable : IDisposable { public void Dispose() { } }
    }
}

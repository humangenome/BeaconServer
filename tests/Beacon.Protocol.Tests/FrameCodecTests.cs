using System.Security.Cryptography;
using Beacon.Protocol;
using FluentAssertions;
using Xunit;

namespace Beacon.Protocol.Tests;

public class FrameCodecTests
{
    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Encode_then_decode_recovers_payload()
    {
        var codec = new FrameCodec(NewKey());
        var hs = new HandshakeMessage(0, 1, 0, "AdminTest", "0.0.1", 12345);
        var bytes = codec.Encode(FrameType.Handshake, FrameFlags.None, 42, hs);

        var ok = codec.TryDecode(bytes, out var consumed, out var type, out var flags, out var seq, out var payload);
        ok.Should().BeTrue();
        consumed.Should().Be(bytes.Length);
        type.Should().Be(FrameType.Handshake);
        flags.Should().Be(FrameFlags.None);
        seq.Should().Be(42u);

        var round = codec.DeserializePayload<HandshakeMessage>(payload);
        round.Should().Be(hs);
    }

    [Fact]
    public void Tampered_payload_fails_hmac()
    {
        var codec = new FrameCodec(NewKey());
        var hb = new HeartbeatMessage(1, 0, 60);
        var bytes = codec.Encode(FrameType.Heartbeat, FrameFlags.None, 1, hb);
        bytes[bytes.Length / 2] ^= 0xFF;

        var act = () => codec.TryDecode(bytes, out _, out _, out _, out _, out _);
        act.Should().Throw<InvalidDataException>().WithMessage("*HMAC*");
    }

    [Fact]
    public void Wrong_key_rejects_frame()
    {
        var a = new FrameCodec(NewKey());
        var b = new FrameCodec(NewKey());
        var hb = new HeartbeatMessage(1, 0, 60);
        var bytes = a.Encode(FrameType.Heartbeat, FrameFlags.None, 1, hb);

        var act = () => b.TryDecode(bytes, out _, out _, out _, out _, out _);
        act.Should().Throw<InvalidDataException>().WithMessage("*HMAC*");
    }

    [Fact]
    public void Partial_buffer_returns_false_without_consuming()
    {
        var codec = new FrameCodec(NewKey());
        var hb = new HeartbeatMessage(1, 0, 60);
        var bytes = codec.Encode(FrameType.Heartbeat, FrameFlags.None, 1, hb);
        var partial = bytes.AsSpan(0, bytes.Length - 5).ToArray();

        var ok = codec.TryDecode(partial, out var consumed, out _, out _, out _, out _);
        ok.Should().BeFalse();
        consumed.Should().Be(0);
    }

    [Fact]
    public void Heartbeat_legacy_3_field_decodes_with_default_auth_state()
    {
        // Simulate a v0.2.16-or-earlier plugin sending a 3-field heartbeat
        // (the auth state fields didn't exist yet). The codec must decode
        // it with ServerPasswordConfigured=0 / ServerPasswordHookReady=0
        // so the watchdog treats it as "unknown" and doesn't fail-close.
        var codec = new FrameCodec(NewKey());
        // Hand-build an msgpack array of 3 ints to mimic the legacy plugin.
        var payload = new byte[] {
            0x93,                                    // fixarray, len=3
            0x01,                                    // unix_ms = 1 (positive fixint)
            0x00,                                    // player_count = 0
            0x3c,                                    // world_tick_rate = 60
        };
        var hb = codec.DeserializePayload<HeartbeatMessage>(payload);
        hb.UnixMillis.Should().Be(1);
        hb.InGamePlayerCount.Should().Be(0);
        hb.WorldTickRate.Should().Be(60);
        hb.ServerPasswordConfigured.Should().Be(0);
        hb.ServerPasswordHookReady.Should().Be(0);
    }

    [Fact]
    public void Heartbeat_full_5_field_round_trips()
    {
        var codec = new FrameCodec(NewKey());
        var hb = new HeartbeatMessage(7, 2, 30, 1, 1);
        var bytes = codec.Encode(FrameType.Heartbeat, FrameFlags.None, 1, hb);
        var ok = codec.TryDecode(bytes, out _, out _, out _, out _, out var payload);
        ok.Should().BeTrue();
        var round = codec.DeserializePayload<HeartbeatMessage>(payload);
        round.Should().Be(hb);
    }

    [Fact]
    public void Stream_of_two_frames_decodes_both()
    {
        var codec = new FrameCodec(NewKey());
        var a = codec.Encode(FrameType.Heartbeat, FrameFlags.None, 1, new HeartbeatMessage(1, 0, 60));
        var b = codec.Encode(FrameType.LogForward, FrameFlags.None, 2, new LogForwardMessage(1, "info", "test", "hello"));
        var stream = a.Concat(b).ToArray();

        var ok1 = codec.TryDecode(stream, out var c1, out var t1, out _, out var s1, out _);
        ok1.Should().BeTrue();
        c1.Should().Be(a.Length);
        t1.Should().Be(FrameType.Heartbeat);
        s1.Should().Be(1u);

        var ok2 = codec.TryDecode(stream.AsSpan(c1), out var c2, out var t2, out _, out var s2, out var pay2);
        ok2.Should().BeTrue();
        c2.Should().Be(b.Length);
        t2.Should().Be(FrameType.LogForward);
        s2.Should().Be(2u);
        codec.DeserializePayload<LogForwardMessage>(pay2).Message.Should().Be("hello");
    }
}

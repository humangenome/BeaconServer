using System.Collections.Generic;
using System.Security.Cryptography;
using Beacon.Protocol;
using FluentAssertions;
using MessagePack;
using Xunit;

namespace Beacon.Protocol.Tests;

// Covers the PlayerSnapshot position fields added for the native roster/web-map
// push (idle-tick fix). The two things that can silently break: the new double
// fields must survive the wire roundtrip, and a pre-position 5-field producer
// (the roster.json file-watcher path, or an older plugin) must still decode with
// the positions defaulted instead of throwing or misaligning.
public class PlayerSnapshotTests
{
    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void PlayerListSnapshot_roundtrips_pawn_positions()
    {
        var codec = new FrameCodec(NewKey());
        var snap = new PlayerListSnapshotMessage(new List<PlayerSnapshot>
        {
            new("steam_76561198000000000", "Diver", 1000L, 2000L, 42,
                PosX: 1.5, PosY: -2.5, PosZ: 234.0, PlayerId: 7),
        });
        var bytes = codec.Encode(FrameType.PlayerListSnapshot, FrameFlags.None, 1, snap);

        codec.TryDecode(bytes, out _, out var type, out _, out _, out var p).Should().BeTrue();
        type.Should().Be(FrameType.PlayerListSnapshot);
        var round = codec.DeserializePayload<PlayerListSnapshotMessage>(p);
        round.Players.Should().HaveCount(1);
        var pl = round.Players[0];
        pl.DisplayName.Should().Be("Diver");
        pl.PingMs.Should().Be(42);
        pl.PosX.Should().Be(1.5);
        pl.PosY.Should().Be(-2.5);
        pl.PosZ.Should().Be(234.0);
        pl.PlayerId.Should().Be(7);
    }

    // A producer from before positions existed emits a 5-element array.
    [MessagePackObject]
    public record LegacyPlayerSnapshot(
        [property: Key(0)] string BeaconUserId,
        [property: Key(1)] string DisplayName,
        [property: Key(2)] long ConnectedAtUnixMs,
        [property: Key(3)] long LastPacketUnixMs,
        [property: Key(4)] int PingMs);

    [Fact]
    public void Legacy_five_field_snapshot_defaults_positions_to_zero()
    {
        var legacyBytes = MessagePackSerializer.Serialize(
            new LegacyPlayerSnapshot("steam_1", "OldDiver", 1000L, 2000L, 11));
        var modern = MessagePackSerializer.Deserialize<PlayerSnapshot>(legacyBytes);
        modern.DisplayName.Should().Be("OldDiver");
        modern.PingMs.Should().Be(11);
        modern.PosX.Should().Be(0);
        modern.PosY.Should().Be(0);
        modern.PosZ.Should().Be(0);
        modern.PlayerId.Should().Be(0);
    }
}

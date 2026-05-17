using System.Security.Cryptography;
using Beacon.Protocol;
using FluentAssertions;
using Xunit;

namespace Beacon.Protocol.Tests;

public class ModFrameTests
{
    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void ModLoad_frame_roundtrips()
    {
        var codec = new FrameCodec(NewKey());
        var desc = new ModDescriptor(
            ModId: "humangenome.spawnermenu",
            Version: "1.2.3",
            Runtime: ModRuntime.ServerLua,
            Sha256: "aabbccddeeff",
            ArtifactUrl: "https://example/x.lua",
            RequiredOnClient: false);
        var payload = new ModLoadMessage(desc);
        var bytes = codec.Encode(FrameType.ModLoad, FrameFlags.None, 1, payload);

        codec.TryDecode(bytes, out _, out var type, out _, out _, out var p).Should().BeTrue();
        type.Should().Be(FrameType.ModLoad);
        var round = codec.DeserializePayload<ModLoadMessage>(p);
        round.Should().Be(payload);
        round.Mod.Runtime.Should().Be(ModRuntime.ServerLua);
    }

    [Fact]
    public void ModUnload_frame_roundtrips()
    {
        var codec = new FrameCodec(NewKey());
        var msg = new ModUnloadMessage("humangenome.spawnermenu");
        var bytes = codec.Encode(FrameType.ModUnload, FrameFlags.None, 2, msg);
        codec.TryDecode(bytes, out _, out var type, out _, out _, out var p).Should().BeTrue();
        type.Should().Be(FrameType.ModUnload);
        codec.DeserializePayload<ModUnloadMessage>(p).Should().Be(msg);
    }

    [Fact]
    public void ModEvent_carries_json_payload()
    {
        var codec = new FrameCodec(NewKey());
        var evt = new ModEventMessage(
            ModId: "core",
            EventName: "player.chat",
            PayloadJson: """{"who":"alice","text":"hello"}""");
        var bytes = codec.Encode(FrameType.ModEvent, FrameFlags.None, 3, evt);
        codec.TryDecode(bytes, out _, out var type, out _, out _, out var p).Should().BeTrue();
        type.Should().Be(FrameType.ModEvent);
        var round = codec.DeserializePayload<ModEventMessage>(p);
        round.Should().Be(evt);
    }

    [Fact]
    public void ModManifestResponse_with_multiple_required_mods()
    {
        var codec = new FrameCodec(NewKey());
        var msg = new ModManifestResponseMessage(
            RequiredClientMods: new()
            {
                new ModDescriptor("a.ui",   "1.0", ModRuntime.Client, "11", "u1", true),
                new ModDescriptor("a.hud",  "2.1", ModRuntime.Client, "22", "u2", true),
            },
            ServerName: "Beacon — AdminTest",
            Map: "Awake",
            Players: 0,
            MaxPlayers: 8);
        var bytes = codec.Encode(FrameType.ModManifestResponse, FrameFlags.None, 4, msg);
        codec.TryDecode(bytes, out _, out _, out _, out _, out var p).Should().BeTrue();
        var round = codec.DeserializePayload<ModManifestResponseMessage>(p);
        round.RequiredClientMods.Should().HaveCount(2);
        round.RequiredClientMods[0].ModId.Should().Be("a.ui");
        round.RequiredClientMods[1].RequiredOnClient.Should().BeTrue();
        round.ServerName.Should().Be("Beacon — AdminTest");
    }

    [Fact]
    public void ModRuntime_enum_values_are_stable()
    {
        // These integer values are part of the wire contract. Bumping them
        // breaks all existing manifests. Lock them in via this test.
        ((byte)ModRuntime.ServerNative).Should().Be(1);
        ((byte)ModRuntime.ServerLua).Should().Be(2);
        ((byte)ModRuntime.Client).Should().Be(3);
    }

    [Fact]
    public void Mod_frame_type_numbers_are_stable()
    {
        // Wire stability — these numeric type ids are persisted in client
        // manifests and BeaconServer audit logs. Don't renumber.
        ((byte)FrameType.ModLoad).Should().Be(60);
        ((byte)FrameType.ModUnload).Should().Be(61);
        ((byte)FrameType.ModEvent).Should().Be(62);
        ((byte)FrameType.ModManifestPublish).Should().Be(63);
        ((byte)FrameType.ModManifestQuery).Should().Be(64);
        ((byte)FrameType.ModManifestResponse).Should().Be(65);
    }
}

using System.Buffers.Binary;
using System.Text;
using BeaconServer.Services;
using FluentAssertions;
using Xunit;

namespace BeaconServer.Tests;

public class SaveCollectionUpgraderTests
{
    private static byte[] FakeSave(int version, string magic = "GSWU", byte sizeByte = 4)
    {
        var head = new List<byte>();
        head.AddRange(Encoding.ASCII.GetBytes(magic));
        head.AddRange(new byte[] { 7, 0, 0, 0 });
        head.AddRange(Enumerable.Repeat((byte)0xff, 8));
        head.AddRange(Encoding.ASCII.GetBytes("GVAS"));
        head.AddRange(Enumerable.Repeat((byte)1, 200));
        head.AddRange(Encoding.ASCII.GetBytes("\u0008\0\0\0Version\0\u000c\0\0\0IntProperty\0"));
        head.AddRange(new byte[] { 0, 0, 0, 0, sizeByte, 0, 0, 0, 0 });
        var v = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(v, version); head.AddRange(v);
        head.AddRange(Enumerable.Repeat((byte)2, 4000));
        return head.ToArray();
    }

    private static string FreshDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "sn2save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "SaveGames"));
        return root;
    }

    [Fact]
    public void Reads_the_changelist_from_the_version_string()
    {
        var bytes = Encoding.Unicode.GetBytes("xxSubnautica2-CL-131439\0yy");
        SaveCollectionUpgrader.ChangelistFromBytes(bytes).Should().Be(131439);
        SaveCollectionUpgrader.ChangelistFromBytes(Encoding.Unicode.GetBytes("nothing here")).Should().Be(0);
    }

    [Fact]
    public void Older_game_build_changes_nothing()
    {
        var root = FreshDir(); var sg = Path.Combine(root, "SaveGames");
        File.WriteAllBytes(Path.Combine(sg, "savegame_0.sav"), FakeSave(28));
        var r = SaveCollectionUpgrader.Upgrade(sg, 123362);
        r.Changed.Should().BeEmpty();
        SaveCollectionUpgrader.ReadVersion(File.ReadAllBytes(Path.Combine(sg, "savegame_0.sav"))).Should().Be(28);
        Directory.Exists(Path.Combine(root, "SaveGames-before-v29")).Should().BeFalse();
    }

    [Fact]
    public void Upgrades_27_and_28_keeps_others_and_backs_up_once()
    {
        var root = FreshDir(); var sg = Path.Combine(root, "SaveGames");
        var original = FakeSave(28);
        File.WriteAllBytes(Path.Combine(sg, "savegame_0.sav"), original);
        File.WriteAllBytes(Path.Combine(sg, "savegame_0_0.bak"), FakeSave(27));
        File.WriteAllBytes(Path.Combine(sg, "savegame_1.sav"), FakeSave(29));
        File.WriteAllBytes(Path.Combine(sg, "savegame_2.sav"), FakeSave(26));
        File.WriteAllText(Path.Combine(sg, "FrontendSavedState.sav"), "not a collection");

        var r = SaveCollectionUpgrader.Upgrade(sg, 131439);

        r.Changed.Should().BeEquivalentTo(new[] { "savegame_0.sav", "savegame_0_0.bak" });
        r.Errors.Should().BeEmpty();
        var upgraded = File.ReadAllBytes(Path.Combine(sg, "savegame_0.sav"));
        SaveCollectionUpgrader.ReadVersion(upgraded).Should().Be(29);
        upgraded.Length.Should().Be(original.Length);
        upgraded.Zip(original).Count(p => p.First != p.Second).Should().Be(1);
        SaveCollectionUpgrader.ReadVersion(File.ReadAllBytes(Path.Combine(sg, "savegame_1.sav"))).Should().Be(29);
        SaveCollectionUpgrader.ReadVersion(File.ReadAllBytes(Path.Combine(sg, "savegame_2.sav"))).Should().Be(26);
        File.ReadAllBytes(Path.Combine(root, "SaveGames-before-v29", "savegame_0.sav")).Should().Equal(original);
        File.Exists(Path.Combine(root, "SaveGames-before-v29", "FrontendSavedState.sav")).Should().BeTrue();
        Directory.EnumerateFiles(sg, "*.v29tmp").Should().BeEmpty();

        File.WriteAllText(Path.Combine(root, "SaveGames-before-v29", "marker.txt"), "keep");
        var again = SaveCollectionUpgrader.Upgrade(sg, 131439);
        again.Changed.Should().BeEmpty();
        again.Reason.Should().Be("nothing to upgrade");
        File.ReadAllText(Path.Combine(root, "SaveGames-before-v29", "marker.txt")).Should().Be("keep");
    }

    [Fact]
    public void Unexpected_tag_shape_is_left_alone()
    {
        var root = FreshDir(); var sg = Path.Combine(root, "SaveGames");
        var odd = FakeSave(28, sizeByte: 8);
        File.WriteAllBytes(Path.Combine(sg, "savegame_0.sav"), odd);
        SaveCollectionUpgrader.Upgrade(sg, 131439).Changed.Should().BeEmpty();
        File.ReadAllBytes(Path.Combine(sg, "savegame_0.sav")).Should().Equal(odd);
    }

    [Fact]
    public void Not_a_world_file_has_no_version()
    {
        SaveCollectionUpgrader.ReadVersion(FakeSave(28, magic: "XXXX")).Should().BeNull();
    }
}

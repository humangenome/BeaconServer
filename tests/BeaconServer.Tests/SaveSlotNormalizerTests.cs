using BeaconServer.Services;
using FluentAssertions;

namespace BeaconServer.Tests;

public sealed class SaveSlotNormalizerTests
{
    [Fact]
    public void Normalize_PromotesNewestSlotToZero_AndArchivesOtherSlots()
    {
        using var temp = new TempDir();
        var saveDir = temp.Path;

        WriteSave(saveDir, "savegame_0.sav", "zero", new DateTime(2026, 5, 15, 1, 0, 0, DateTimeKind.Utc));
        WriteSave(saveDir, "savegame_1.sav", "one", new DateTime(2026, 5, 16, 1, 0, 0, DateTimeKind.Utc));
        WriteSave(saveDir, "savegame_4.sav", "four", new DateTime(2026, 5, 17, 1, 0, 0, DateTimeKind.Utc));

        var result = SaveSlotNormalizer.Normalize(saveDir, timestamp: new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero));

        result.Changed.Should().BeTrue();
        result.ActiveSourceName.Should().Be("savegame_4.sav");
        result.ArchivedCount.Should().Be(2);
        result.ArchiveDir.Should().NotBeNull();
        File.ReadAllText(Path.Combine(saveDir, "savegame_0.sav")).Should().Be("four");
        File.Exists(Path.Combine(saveDir, "savegame_1.sav")).Should().BeFalse();
        File.Exists(Path.Combine(saveDir, "savegame_4.sav")).Should().BeFalse();
        File.ReadAllText(Path.Combine(result.ArchiveDir!, "savegame_0.sav")).Should().Be("zero");
        File.ReadAllText(Path.Combine(result.ArchiveDir!, "savegame_1.sav")).Should().Be("one");
        AssertNoFutureBlockers(saveDir);
    }

    [Fact]
    public void Normalize_LeavesSingleCanonicalSlotActive_AndNoBlockersCreated()
    {
        using var temp = new TempDir();
        var saveDir = temp.Path;

        WriteSave(saveDir, "savegame_0.sav", "zero", new DateTime(2026, 5, 17, 1, 0, 0, DateTimeKind.Utc));

        var result = SaveSlotNormalizer.Normalize(saveDir);

        result.Changed.Should().BeFalse();
        result.ActiveSourceName.Should().Be("savegame_0.sav");
        result.ArchivedCount.Should().Be(0);
        result.ArchiveDir.Should().BeNull();
        File.ReadAllText(Path.Combine(saveDir, "savegame_0.sav")).Should().Be("zero");
        AssertNoFutureBlockers(saveDir);
    }

    [Fact]
    public void Normalize_EmptyDir_LeavesUntouched()
    {
        using var temp = new TempDir();
        var result = SaveSlotNormalizer.Normalize(temp.Path);

        result.Changed.Should().BeFalse();
        result.ActiveSourceName.Should().BeNull();
        result.ArchivedCount.Should().Be(0);
        result.ArchiveDir.Should().BeNull();
        File.Exists(Path.Combine(temp.Path, "savegame_0.sav")).Should().BeFalse();
        AssertNoFutureBlockers(temp.Path);
    }

    [Fact]
    public void Normalize_NoOpsForBlankPath()
    {
        var result = SaveSlotNormalizer.Normalize("");
        result.Changed.Should().BeFalse();
    }

    [Fact]
    public void Normalize_PurgesStaleZeroByteBlockersFromPriorVersions()
    {
        using var temp = new TempDir();
        var saveDir = temp.Path;

        WriteSave(saveDir, "savegame_0.sav", "zero", new DateTime(2026, 5, 17, 1, 0, 0, DateTimeKind.Utc));
        // v0.3.33 left these zero-byte sentinels behind; v0.3.34 must remove them
        // so SN2's slot picker doesn't end up in CreateNewGameFailedNoSlots.
        foreach (var i in new[] { 1, 5, 17, 100, 255 })
        {
            using (File.Create(Path.Combine(saveDir, $"savegame_{i}.sav"))) { }
        }

        var result = SaveSlotNormalizer.Normalize(saveDir);

        result.Changed.Should().BeFalse();
        result.ActiveSourceName.Should().Be("savegame_0.sav");
        AssertNoFutureBlockers(saveDir);
        File.ReadAllText(Path.Combine(saveDir, "savegame_0.sav")).Should().Be("zero");
    }

    [Fact]
    public void Normalize_IgnoresZeroByteBlockersWhenChoosingActiveSave()
    {
        using var temp = new TempDir();
        var saveDir = temp.Path;

        WriteSave(saveDir, "savegame_0.sav", "zero", new DateTime(2026, 5, 17, 1, 0, 0, DateTimeKind.Utc));
        var blocker = Path.Combine(saveDir, "savegame_8.sav");
        using (File.Create(blocker))
        {
        }
        File.SetLastWriteTimeUtc(blocker, new DateTime(2026, 5, 18, 1, 0, 0, DateTimeKind.Utc));

        var result = SaveSlotNormalizer.Normalize(saveDir);

        result.ActiveSourceName.Should().Be("savegame_0.sav");
        result.ArchivedCount.Should().Be(0);
        result.ArchiveDir.Should().BeNull();
        File.ReadAllText(Path.Combine(saveDir, "savegame_0.sav")).Should().Be("zero");
        File.Exists(blocker).Should().BeFalse();
    }

    private static string WriteSave(string dir, string name, string content, DateTime mtimeUtc)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        File.SetCreationTimeUtc(path, mtimeUtc);
        File.SetLastWriteTimeUtc(path, mtimeUtc);
        return path;
    }

    private static void AssertNoFutureBlockers(string dir)
    {
        for (var i = 1; i <= 255; i++)
        {
            var path = Path.Combine(dir, $"savegame_{i}.sav");
            File.Exists(path).Should().BeFalse($"savegame_{i}.sav should not exist post-normalize");
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "beacon-save-slot-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}

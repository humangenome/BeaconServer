using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BeaconServer.Services;

public sealed record SaveSlotNormalizationResult(
    bool Changed,
    string? ActiveSourceName,
    int ArchivedCount,
    string? ArchiveDir);

public static partial class SaveSlotNormalizer
{
    private const string CanonicalSaveName = "savegame_0.sav";
    private const int MaxBlockedSaveSlotIndex = 255;

    public static SaveSlotNormalizationResult Normalize(
        string saveGamesDir,
        ILogger? log = null,
        DateTimeOffset? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(saveGamesDir))
        {
            return new SaveSlotNormalizationResult(false, null, 0, null);
        }

        Directory.CreateDirectory(saveGamesDir);

        var saves = Directory.EnumerateFiles(saveGamesDir, "savegame_*.sav", SearchOption.TopDirectoryOnly)
            .Select(TryReadSaveSlot)
            .Where(static s => s is not null)
            .Select(static s => s!)
            .OrderByDescending(static s => s.LastWriteUtc)
            .ThenByDescending(static s => s.CreationUtc)
            .ThenByDescending(static s => s.Index)
            .ToList();

        PurgeStaleBlockers(saveGamesDir, log);

        if (saves.Count == 0)
        {
            return new SaveSlotNormalizationResult(false, null, 0, null);
        }

        var active = saves[0];
        var activeTarget = Path.Combine(saveGamesDir, CanonicalSaveName);
        var needsRename = !Path.GetFileName(active.Path).Equals(CanonicalSaveName, StringComparison.OrdinalIgnoreCase);
        var savesToArchive = saves.Skip(1).ToList();

        if (!needsRename && savesToArchive.Count == 0)
        {
            return new SaveSlotNormalizationResult(false, active.Name, 0, null);
        }

        string? archiveDir = null;
        if (savesToArchive.Count > 0)
        {
            archiveDir = CreateArchiveDir(saveGamesDir, timestamp ?? DateTimeOffset.UtcNow);
            foreach (var save in savesToArchive)
            {
                File.Move(save.Path, UniquePath(Path.Combine(archiveDir, save.Name)));
            }
        }

        if (needsRename)
        {
            if (File.Exists(activeTarget))
            {
                archiveDir ??= CreateArchiveDir(saveGamesDir, timestamp ?? DateTimeOffset.UtcNow);
                File.Move(activeTarget, UniquePath(Path.Combine(archiveDir, CanonicalSaveName)));
            }

            File.Move(active.Path, activeTarget);
        }

        log?.LogInformation(
            "Subnautica 2 save slots normalized: active {ActiveSource} -> {Canonical}; archived {ArchivedCount} older slot(s){ArchiveSuffix}",
            active.Name,
            CanonicalSaveName,
            savesToArchive.Count,
            archiveDir is null ? "" : $" to {archiveDir}");

        return new SaveSlotNormalizationResult(true, active.Name, savesToArchive.Count, archiveDir);
    }

    private static SaveSlot? TryReadSaveSlot(string path)
    {
        var name = Path.GetFileName(path);
        var match = SaveSlotRegex().Match(name);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var index))
        {
            return null;
        }

        var info = new FileInfo(path);
        if (info.Length == 0)
        {
            return null;
        }

        return new SaveSlot(index, name, path, info.LastWriteTimeUtc, info.CreationTimeUtc);
    }

    // v0.3.33 seeded zero-byte savegame_1..255.sav files to push SN2's slot
    // picker down to slot 0. SN2's UWESaveSystem treated those as occupied-but-corrupt
    // entries and refused to allocate any new slot, ending in BlockedSaveSlot +
    // CreateNewGameFailedNoSlots. Purge any leftover blockers from earlier versions
    // so Subnautica 2 can boot cleanly on upgrade.
    private static void PurgeStaleBlockers(string saveGamesDir, ILogger? log)
    {
        var removed = 0;
        for (var i = 1; i <= MaxBlockedSaveSlotIndex; i++)
        {
            var path = Path.Combine(saveGamesDir, $"savegame_{i}.sav");
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length == 0)
                {
                    File.Delete(path);
                    removed++;
                }
            }
            catch
            {
                // Best-effort cleanup; a failure here must not block startup.
            }
        }

        if (removed > 0)
        {
            log?.LogInformation(
                "Subnautica 2 save slot legacy blockers purged: {Count} zero-byte sentinel slot(s) removed",
                removed);
        }
    }

    private static string CreateArchiveDir(string saveGamesDir, DateTimeOffset timestamp)
    {
        var archiveRoot = Path.Combine(saveGamesDir, "_archive");
        Directory.CreateDirectory(archiveRoot);

        var stamp = timestamp.UtcDateTime.ToString("yyyyMMddTHHmmssZ");
        var archiveDir = Path.Combine(archiveRoot, stamp);
        var candidate = archiveDir;
        var suffix = 1;
        while (Directory.Exists(candidate))
        {
            candidate = archiveDir + "-" + suffix++;
        }
        Directory.CreateDirectory(candidate);
        return candidate;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var i = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name}.{i}{ext}");
            i++;
        } while (File.Exists(candidate) || Directory.Exists(candidate));

        return candidate;
    }

    [GeneratedRegex(@"^savegame_(\d+)\.sav$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SaveSlotRegex();

    private sealed record SaveSlot(
        int Index,
        string Name,
        string Path,
        DateTime LastWriteUtc,
        DateTime CreationUtc);
}

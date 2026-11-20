namespace BeaconServer.Services;

/// <summary>
/// How much room stored world backups (snapshots) may take.
/// <para>
/// Snapshots pile up from three places: a backup the owner asks for, the copy every restore takes of the world it
/// replaces, and every zip the owner uploads (up to the upload cap each). Until 2026-11-18 only age removed them
/// (30 days), and only after a new backup was taken, so uploads and pre-restore copies could fill the disk every
/// server on the machine shares. Now the newest <c>maxCount</c> are kept, as long as they fit in <c>maxBytes</c>
/// together, counted from the newest; the rest go, oldest first. Age still removes them too.
/// </para>
/// <para>
/// Two are never removed, whatever the limits say: the newest snapshot (the latest pre-restore copy or upload),
/// and any snapshot a restore is reading at that moment.
/// </para>
/// <para>The sibling servers carry the same file; keep them in step.</para>
/// </summary>
internal static class SnapshotBudget
{
    internal readonly record struct Item(string Id, long TakenUnix, long SizeBytes, long RetentionDays, string FilePath);

    /// <summary>
    /// The snapshots to remove from <paramref name="items"/> (any order). A limit of 0 or less is off.
    /// <paramref name="keepPaths"/> are the files a restore is reading now.
    /// </summary>
    internal static List<Item> ToRemove(IEnumerable<Item> items, long nowUnix, int maxCount, long maxBytes,
        IReadOnlyCollection<string> keepPaths)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var newestFirst = items
            .OrderByDescending(i => i.TakenUnix)
            .ThenByDescending(i => i.Id, StringComparer.Ordinal)
            .ToList();
        // The two that always stay take their room first, so the limits hold for everything else.
        var mustKeep = newestFirst
            .Select((s, n) => n == 0 || keepPaths.Any(p => string.Equals(SafeFullPath(p), SafeFullPath(s.FilePath), comparison)))
            .ToList();
        var kept = mustKeep.Count(k => k);
        var keptBytes = newestFirst.Where((s, n) => mustKeep[n]).Sum(s => Math.Max(0, s.SizeBytes));
        var remove = new List<Item>();
        for (var n = 0; n < newestFirst.Count; n++)
        {
            if (mustKeep[n]) continue;
            var s = newestFirst[n];
            var size = Math.Max(0, s.SizeBytes);
            var expired = nowUnix - s.TakenUnix > s.RetentionDays * 86_400;
            var overCount = maxCount > 0 && kept >= maxCount;
            var overBytes = maxBytes > 0 && keptBytes + size > maxBytes;
            if (expired || overCount || overBytes)
            {
                remove.Add(s);
                continue;
            }
            kept++;
            keptBytes += size;
        }
        return remove;
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception) { return path; }
    }
}

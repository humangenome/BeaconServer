namespace BeaconServer.Services;

/// <summary>
/// Which automatic backups to remove to fit their room. The caller hands it automatic backups and nothing else,
/// so no backup an owner made, and none of unknown kind, is ever removed or counted.
/// <para>
/// The newest is kept while the rest fit, counted from the newest; the others go, oldest first, and age removes
/// them too. Two are never removed, whatever the limits say: the newest, and any a restore is reading right now.
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

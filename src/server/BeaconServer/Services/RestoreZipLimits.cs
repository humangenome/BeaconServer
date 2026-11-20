using System.IO.Compression;

namespace BeaconServer.Services;

/// <summary>
/// Limits on what a world zip may unpack to. The upload is capped (<c>MaxUploadBytes</c>), but a
/// small zip can unpack to far more than its own size and fill the disk the game and every other
/// server on the machine share. So every entry is checked BEFORE the game is stopped and before
/// anything is written (count, names, paths, each entry's stated size, the total, the free space it
/// would leave, and that each entry's data really is the size and checksum it states), and the unpack itself
/// counts the bytes it really writes as a second line.
/// </summary>
internal static class RestoreZipLimits
{
    internal sealed record Caps(long MaxTotalBytes, int MaxEntries, long MaxEntryBytes, long FreeSpaceFloorBytes);

    /// <summary>
    /// Why the zip may not be unpacked under <paramref name="caps"/>, or null when it may. Reads only
    /// the zip's directory, so it is cheap; <see cref="CheckStatedSizesHold"/> reads the data.
    /// </summary>
    internal static string? CheckBeforeUnpack(ZipArchive archive, Caps caps, long? freeBytes)
    {
        var entries = archive.Entries;
        if (entries.Count > caps.MaxEntries)
            return $"has {entries.Count} entries (limit {caps.MaxEntries})";
        long total = 0;
        foreach (var e in entries)
        {
            if (HasNameWindowsCannotStore(e.FullName))
                return "has an entry whose name Windows cannot store as a plain file (a ':', '*', '?', '\"', '<', '>', '|' or control character)";
            if (HasNameWindowsTreatsSpecially(e.FullName))
                return "has an entry whose name Windows treats as something other than a plain file (a device name such as CON, NUL, COM1 or LPT1, with or without an extension, or a name ending in a dot or a space)";
            if (LeavesTheFolder(e.FullName))
                return "has an entry whose path leaves the folder it unpacks into";
            if (e.Length < 0 || e.Length > caps.MaxEntryBytes)
                return $"has an entry of {e.Length} bytes unpacked (limit {caps.MaxEntryBytes} per file)";
            total += e.Length;
            if (total > caps.MaxTotalBytes)
                return $"unpacks to more than {caps.MaxTotalBytes} bytes in all";
        }
        if (freeBytes is long free && total + caps.FreeSpaceFloorBytes > free)
            return $"unpacks to {total} bytes, which would leave less than {caps.FreeSpaceFloorBytes} bytes free on the disk ({free} free now)";
        return null;
    }

    /// <summary>
    /// Read every entry's data once, writing nothing, and say why when an entry's data is not the size it
    /// states or does not match its CRC-32 checksum. A zip can state small sizes and hold far more; the zip
    /// reader stops a compressed entry at the size it states, so such an entry would otherwise unpack cut
    /// short, and only its checksum shows that. Run after <see cref="CheckBeforeUnpack"/> passed, so the
    /// reading is bounded by the caps: an entry is read at most one buffer past what it states. A zip whose
    /// data cannot be read throws.
    /// </summary>
    internal static string? CheckStatedSizesHold(ZipArchive archive)
    {
        var buffer = new byte[81920];
        foreach (var e in archive.Entries)
        {
            long held = 0;
            var crc = 0xFFFFFFFFu;
            using var s = e.Open();
            int n;
            while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                held += n;
                if (held > e.Length)
                    return $"has an entry that holds more than the {e.Length} bytes it states";
                crc = Crc32Update(crc, buffer.AsSpan(0, n));
            }
            if (held != e.Length)
                return $"has an entry that holds {held} bytes but states {e.Length}";
            if ((crc ^ 0xFFFFFFFFu) != e.Crc32)
                return "has an entry whose data does not match its checksum (the zip is damaged or states false sizes)";
        }
        return null;
    }

    // CRC-32 as zip uses it (IEEE 802.3, reflected, polynomial 0xEDB88320).
    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    // ZipFile.ExtractToDirectory renames these characters to '_' on Windows; the unpack below writes the name as given,
    // so a ':' would become an alternate data stream of another file. A game's own save never uses them, so such a zip
    // is refused before anything is stopped.
    internal static bool HasNameWindowsCannotStore(string fullName)
    {
        foreach (var ch in fullName)
        {
            if (ch < 32 || ch is ':' or '*' or '?' or '"' or '<' or '>' or '|') return true;
        }
        return false;
    }

    // Names Windows does not store as the plain file or folder they spell: a device name (CON, NUL, COM1 ...) opens the
    // device, with or without an extension ("nul.sav", "CON .txt"), and Windows drops a trailing dot or space, so
    // "World.sav." lands on World.sav. Checked in every part of the path, both separators. A game's own save never uses them.
    private static readonly HashSet<string> DeviceNames = BuildDeviceNames();

    private static HashSet<string> BuildDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" };
        for (var i = 0; i <= 9; i++) { names.Add("COM" + i); names.Add("LPT" + i); }
        foreach (var sup in new[] { "\u00B9", "\u00B2", "\u00B3" }) { names.Add("COM" + sup); names.Add("LPT" + sup); }
        return names;
    }

    internal static bool HasNameWindowsTreatsSpecially(string fullName)
    {
        foreach (var part in fullName.Replace('\\', '/').Split('/'))
        {
            if (part.Length == 0 || part == "." || part == "..") continue;
            if (part[^1] is '.' or ' ') return true;
            var dot = part.IndexOf('.');
            var stem = (dot < 0 ? part : part[..dot]).TrimEnd(' ');
            if (DeviceNames.Contains(stem)) return true;
        }
        return false;
    }

    /// <summary>
    /// Does an entry's path leave the folder it unpacks into? Rooted paths ("/x", "\\server\x", "C:x") and
    /// any ".." that climbs above the top count, with '/' and '\' both read as separators whatever system
    /// the server runs on. The unpack checks the real resolved path again.
    /// </summary>
    internal static bool LeavesTheFolder(string fullName)
    {
        var n = fullName.Replace('\\', '/');
        if (n.StartsWith('/')) return true;
        if (n.Length >= 2 && n[1] == ':') return true;
        var depth = 0;
        foreach (var part in n.Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..")
            {
                if (--depth < 0) return true;
                continue;
            }
            depth++;
        }
        return false;
    }

    /// <summary>
    /// Unpack every entry into <paramref name="destDir"/> (an empty staging folder), refusing any
    /// path that leaves it, and stopping with <see cref="InvalidDataException"/> as soon as an entry
    /// writes more than it stated or the total passes the cap. Same layout as
    /// <c>ZipFile.ExtractToDirectory(..., overwriteFiles: true)</c>. Nothing is ever written through a
    /// link: the staging folder and every folder the unpack makes under it are checked to be real
    /// folders as they are made, and a file that is a link is never opened (<see cref="IOException"/>).
    /// </summary>
    internal static void ExtractWithinCaps(string zipPath, string destDir, Caps caps, long? freeBytes)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var why = CheckBeforeUnpack(archive, caps, freeBytes);
        if (why is not null) throw new InvalidDataException("The zip " + why);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.GetFullPath(destDir);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        var rootProblem = RestoreFolders.EntryProblem(root);
        if (rootProblem is not null || !Directory.Exists(root))
            throw new IOException("The unpack stopped: " + (rootProblem ?? $"the folder {root} does not exist"));
        var plainFolders = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            Path.TrimEndingDirectorySeparator(root),
        };
        var buffer = new byte[81920];
        long written = 0;
        foreach (var entry in archive.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, comparison))
                throw new IOException("A zip entry points outside the folder it unpacks into.");
            if (Path.GetFileName(target).Length == 0)
            {
                if (entry.Length != 0) throw new IOException("A zip folder entry carries data.");
                MakePlainFolders(root, target, plainFolders);
                continue;
            }
            MakePlainFolders(root, Path.GetDirectoryName(target)!, plainFolders);
            if (RestoreFolders.IsLink(target))
                throw new IOException("The unpack stopped: " + target + " is a link or junction.");
            long thisEntry = 0;
            using (var src = entry.Open())
            using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    thisEntry += n;
                    written += n;
                    if (thisEntry > entry.Length)
                        throw new InvalidDataException("A zip entry unpacks to more than the size it states.");
                    if (written > caps.MaxTotalBytes)
                        throw new InvalidDataException($"The zip unpacks to more than {caps.MaxTotalBytes} bytes in all.");
                    dst.Write(buffer, 0, n);
                }
            }
            try { File.SetLastWriteTime(target, entry.LastWriteTime.DateTime); } catch { /* as ExtractToFile: best effort */ }
        }
    }

    /// <summary>
    /// Make <paramref name="folder"/> under the unpack root one level at a time, checking each level is a real
    /// folder (not a link, not a file) before anything goes into it. <paramref name="checkedFolders"/> holds the
    /// levels already checked in this unpack, so each is looked at once.
    /// </summary>
    private static void MakePlainFolders(string root, string folder, HashSet<string> checkedFolders)
    {
        var top = Path.TrimEndingDirectorySeparator(root);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (checkedFolders.Contains(full)) return;
        var current = top;
        foreach (var part in Path.GetRelativePath(top, full).Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            current = Path.Combine(current, part);
            if (checkedFolders.Contains(current)) continue;
            if (RestoreFolders.EntryProblem(current) is string before)
                throw new IOException("The unpack stopped: " + before);
            Directory.CreateDirectory(current);
            if (RestoreFolders.EntryProblem(current) is string after)
                throw new IOException("The unpack stopped: " + after);
            checkedFolders.Add(current);
        }
    }

    /// <summary>
    /// Free bytes on the disk that really holds <paramref name="path"/>, or null when it cannot be read.
    /// The path is resolved through every link in it first (a server folder can be a link from C: to D:),
    /// then measured on the drive or mount that holds the resolved path.
    /// </summary>
    internal static long? FreeBytesOf(string path)
    {
        try
        {
            var mounts = DriveInfo.GetDrives().Select(d => d.Name).ToList();
            return FreeBytesOf(path, mounts, m => new DriveInfo(m).AvailableFreeSpace);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The measuring step of <see cref="FreeBytesOf(string)"/>, with the mount list and the measure passed in (tests).</summary>
    internal static long? FreeBytesOf(string path, IEnumerable<string> mounts, Func<string, long?> freeOfMount)
    {
        try
        {
            var mount = LongestMountOf(RealPath(path), mounts);
            return mount is null ? null : freeOfMount(mount);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="path"/> with every link in it followed to its final target (symbolic links, and
    /// junctions on Windows), from the top folder down. Parts that do not exist yet are kept as written.
    /// </summary>
    internal static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        for (var hop = 0; hop < 40; hop++)
        {
            var root = Path.GetPathRoot(full) ?? "";
            var parts = full[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            string? moved = null;
            for (var i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (!info.Exists && info.LinkTarget is null) break;
                if (info.LinkTarget is null) continue;
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null) continue;
                moved = Path.GetFullPath(Path.Combine(new[] { StripDevicePrefix(target.FullName) }.Concat(parts.Skip(i + 1)).ToArray()));
                break;
            }
            if (moved is null || string.Equals(moved, full, StringComparison.Ordinal)) return full;
            full = moved;
        }
        return full;
    }

    // Windows can hand a link target back in its device form: \??\D:\x or \\?\D:\x, and \\?\UNC\server\share for a share.
    private static string StripDevicePrefix(string p)
    {
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + p[8..];
        if (p.StartsWith(@"\??\", StringComparison.Ordinal) || p.StartsWith(@"\\?\", StringComparison.Ordinal)) return p[4..];
        return p;
    }

    /// <summary>The mount (drive root on Windows) that holds <paramref name="realPath"/>: the longest one it sits under.</summary>
    internal static string? LongestMountOf(string realPath, IEnumerable<string> mounts)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var p = WithSeparator(realPath);
        string? best = null;
        foreach (var m in mounts)
        {
            if (string.IsNullOrEmpty(m)) continue;
            var mm = WithSeparator(m);
            if (p.StartsWith(mm, comparison) && (best is null || mm.Length > WithSeparator(best).Length)) best = m;
        }
        return best;
    }

    private static string WithSeparator(string p) =>
        p.EndsWith(Path.DirectorySeparatorChar) || p.EndsWith(Path.AltDirectorySeparatorChar) ? p : p + Path.DirectorySeparatorChar;
}

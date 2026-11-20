namespace BeaconServer.Services;

/// <summary>
/// The folders a world restore writes in, checked so that nothing goes through a link.
/// <para>
/// A restore unpacks the zip into a new folder beside the live save folder, then swaps the two by renaming
/// them, all inside the game's <c>Saved</c> folder in the server's own folder. The server runs with more rights
/// than the server's owner, so a link anywhere on that path (a junction, a symbolic link or any other reparse
/// point) could send the unpack, the renames or the clean-up somewhere else. So the <c>Saved</c> folder and every
/// folder above it must be real folders, with the one exception the snapshot walk also accepts: the server's own
/// folder kept on a second drive (<see cref="SecondDriveRule"/>), which is followed once to its real folder. The
/// folders the restore renames, makes or deletes inside <c>Saved</c> must be real folders too.
/// </para>
/// <para>
/// Checked before the game is stopped (a refusal leaves the game running and the world untouched), and again right
/// before every rename and delete, so a link that appears in between stops the restore there.
/// </para>
/// <para>The sibling servers carry the same file; keep them in step.</para>
/// </summary>
internal static class RestoreFolders
{
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Why a restore may not write under <paramref name="savedFolder"/>, or null when it may; then
    /// <paramref name="realSaved"/> is the real folder to work in. Every level from the drive down that exists must
    /// be a real folder; the server's own folder may be the second-drive link, which is followed once. Levels that do
    /// not exist yet are fine (there is nothing there to follow) and are kept as written.
    /// </summary>
    internal static string? Problem(string savedFolder, SecondDriveRule rule, out string realSaved)
    {
        realSaved = "";
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(savedFolder)); }
        catch (Exception) { return "the save folder path is not valid"; }
        var drive = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(drive)) return $"the save folder {full} has no drive";

        var real = drive;
        var followed = false;
        var parts = full[drive.Length..].Split(SnapshotWalk.Separators, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            real = Path.Combine(real, parts[i]);
            var info = new DirectoryInfo(real);
            if (!Exists(info))
            {
                real = Path.Combine(new[] { real }.Concat(parts.Skip(i + 1)).ToArray());
                break;
            }
            if (IsLink(info))
            {
                if (!followed && rule.Accepts(real, info, out var target))
                {
                    real = target;
                    followed = true;
                    continue;
                }
                return $"{real} is a link or junction at or above the save folder";
            }
            if ((info.Attributes & FileAttributes.Directory) == 0) return $"{real} is a file, not a folder";
        }
        realSaved = real;
        return null;
    }

    /// <summary>
    /// Null when <paramref name="path"/> does not exist or is a real folder; otherwise why a restore may not rename,
    /// make or delete it (a link or junction of any kind, a file, or a path that cannot be read).
    /// </summary>
    internal static string? EntryProblem(string path)
    {
        try
        {
            var info = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            if (!Exists(info)) return null;
            if (IsLink(info)) return $"{info.FullName} is a link or junction";
            if ((info.Attributes & FileAttributes.Directory) == 0) return $"{info.FullName} is a file, not a folder";
            return null;
        }
        catch (Exception)
        {
            return $"{path} could not be read";
        }
    }

    /// <summary>
    /// Throws <see cref="IOException"/> unless <paramref name="savedFolder"/> still passes <see cref="Problem"/>, still
    /// resolves to <paramref name="realSaved"/>, and none of <paramref name="entries"/> is a link or a file. Called
    /// right before every rename and delete a restore makes.
    /// </summary>
    internal static void CheckBeforeMove(string savedFolder, SecondDriveRule rule, string realSaved, params string[] entries)
    {
        var why = Problem(savedFolder, rule, out var now);
        if (why is null && !string.Equals(now, realSaved, PathComparison))
            why = $"the save folder now leads to {now}, not {realSaved}";
        if (why is null)
        {
            foreach (var entry in entries)
            {
                why = EntryProblem(entry);
                if (why is not null) break;
            }
        }
        if (why is not null) throw new IOException("The restore stopped before changing anything more: " + why);
    }

    /// <summary>
    /// Is there anything at <paramref name="path"/> that is a link (a symbolic link to a file or folder, a junction,
    /// or any other reparse point)? A path that cannot be read counts as one.
    /// </summary>
    internal static bool IsLink(string path)
    {
        try
        {
            var info = new FileInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            return Exists(info) && (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Delete a folder a restore made or moved aside, never going through a link inside it: every link in it is
    /// removed first, by itself (the link only, never what it points to), then the rest. Windows' own recursive
    /// delete stops with an error at a junction, which would leave the folder behind. A folder that is itself a
    /// link is not deleted (<see cref="IOException"/>).
    /// </summary>
    internal static void DeleteTree(string folder)
    {
        var top = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)));
        if (!Exists(top)) return;
        if (IsLink(top)) throw new IOException($"{top.FullName} is a link or junction; it is not deleted");
        RemoveLinksUnder(top);
        top.Delete(recursive: true);
    }

    private static readonly EnumerationOptions OneFolder = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    private static void RemoveLinksUnder(DirectoryInfo folder)
    {
        foreach (var entry in folder.EnumerateFileSystemInfos("*", OneFolder))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // A junction or folder link on Windows goes with RemoveDirectory; anything else, and every link
                // elsewhere, with unlink. Neither follows the link.
                if (OperatingSystem.IsWindows() && (entry.Attributes & FileAttributes.Directory) != 0)
                    Directory.Delete(entry.FullName, recursive: false);
                else
                    File.Delete(entry.FullName);
                continue;
            }
            if (entry is DirectoryInfo sub) RemoveLinksUnder(sub);
        }
    }

    // A folder that is any reparse point counts as a link, the same rule as the snapshot walk.
    private static bool IsLink(DirectoryInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    // Something is there, even a link whose target is gone: its own attributes are readable.
    private static bool Exists(FileSystemInfo info)
    {
        info.Refresh();
        return (int)info.Attributes != -1;
    }
}

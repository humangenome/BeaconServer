using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BeaconServer.Services;

/// <summary>
/// The files a world snapshot zips, walked without ever going through a link.
/// <para>
/// A snapshot is a zip the owner can download, so it holds only what is really in the save
/// folder. The walk never goes into a folder that is a junction, a symbolic link or any other
/// reparse point, and leaves out a file that is a symbolic link, so whatever such a link points
/// at stays out of the zip. A file with a second name (a hard link, which looks like a plain file and
/// can be the same bytes as a file outside the save folder) is left out the same way. Every plain
/// file is kept, hidden and system files included, as before.
/// </para>
/// <para>
/// The save folder and every folder above it must be real folders, with one exception: the server's
/// own folder kept on a second drive, a link to the very same path on another fixed drive
/// (<see cref="SecondDriveRule"/>). That one link is judged and resolved once, before the walk, and the
/// walk runs on the real folder. Any other link at or above the save folder refuses the snapshot
/// before any zip is made.
/// </para>
/// <para>The sibling servers carry the same file; keep them in step.</para>
/// </summary>
internal static class SnapshotWalk
{
    // One folder at a time, so the walk itself decides about every sub-folder. Nothing is
    // skipped by attribute here: hidden and system files stay in the snapshot, as before.
    private static readonly EnumerationOptions OneFolder = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Every plain file under the save folder <paramref name="root"/>, as full paths under
    /// <paramref name="walkedRoot"/>: the real save folder, which differs from <paramref name="root"/>
    /// only when the server's own folder is kept on a second drive. Zip names are made relative to
    /// <paramref name="walkedRoot"/>. The links left out are added to <paramref name="linksLeftOut"/> as
    /// paths relative to it (forward slashes). <paramref name="leaveOutFolder"/> gets a plain folder's
    /// relative path and returns true to leave that folder out without walking it.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">The save folder does not exist.</exception>
    /// <exception cref="IOException">The save folder, or a folder above it, is a link other than the server's own folder on a second drive.</exception>
    internal static List<string> PlainFiles(string root, ICollection<string> linksLeftOut, out string walkedRoot,
        Func<string, bool>? leaveOutFolder = null, SecondDriveRule? rule = null)
    {
        walkedRoot = ResolveSaveFolder(root, rule ?? SecondDriveRule.None);
        var top = new DirectoryInfo(walkedRoot);

        var files = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(top);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var entry in folder.EnumerateFileSystemInfos("*", OneFolder))
            {
                if (IsLink(entry))
                {
                    linksLeftOut.Add(Relative(top.FullName, entry.FullName));
                    continue;
                }
                if (entry is DirectoryInfo sub)
                {
                    if (leaveOutFolder is not null && leaveOutFolder(Relative(top.FullName, sub.FullName))) continue;
                    pending.Push(sub);
                }
                else if (HardLinkCount(entry.FullName) > 1)
                {
                    linksLeftOut.Add(Relative(top.FullName, entry.FullName));
                }
                else
                {
                    files.Add(entry.FullName);
                }
            }
        }
        return files;
    }

    /// <summary>
    /// The real save folder: every folder from the drive down to <paramref name="saveFolder"/> is checked
    /// once, and the only link allowed on the way is the server's own folder kept on a second drive,
    /// which is then followed (once) to its real folder.
    /// </summary>
    internal static string ResolveSaveFolder(string saveFolder, SecondDriveRule rule)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(saveFolder));
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"the save folder {full} does not exist");
        var drive = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(drive))
            throw new IOException($"the save folder {full} has no drive");
        var real = drive;
        var resolved = false;
        foreach (var part in full[drive.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            real = Path.Combine(real, part);
            var info = new DirectoryInfo(real);
            if (!info.Exists)
                throw new DirectoryNotFoundException($"the folder {real} does not exist");
            if (!IsLink(info)) continue;
            if (!resolved && rule.Accepts(real, info, out var target))
            {
                real = target;
                resolved = true;
                continue;
            }
            throw new IOException($"{real} is a link or junction at or above the save folder; no snapshot is taken through it");
        }
        return real;
    }

    internal static readonly char[] Separators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

    /// <summary>
    /// A folder that is any reparse point (junction, symbolic link, mount point) counts as a link.
    /// A file counts as one when it is a symbolic link; another kind of file reparse point (a
    /// deduplicated or cloud-backed file) is an ordinary file with its own bytes.
    /// </summary>
    internal static bool IsLink(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0) return false;
        if (info is DirectoryInfo) return true;
        try { return info.LinkTarget is not null; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    /// <summary>
    /// How many names the file has, read on Windows without opening its contents (attributes access only,
    /// never through a link). Elsewhere, or when the count cannot be read, 1: the zip then opens the file
    /// as it always did, and a file it cannot read still fails the snapshot loudly.
    /// </summary>
    internal static uint HardLinkCount(string path)
    {
        if (!OperatingSystem.IsWindows()) return 1;
        using var handle = CreateFileW(path, FileReadAttributes, FileShareReadWriteDelete, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) return 1;
        // BY_HANDLE_FILE_INFORMATION is 13 DWORDs; nNumberOfLinks is the 11th.
        var information = new uint[13];
        return GetFileInformationByHandle(handle, information) ? information[10] : 1;
    }

    private const uint FileReadAttributes = 0x80;
    private const uint FileShareReadWriteDelete = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 13)] uint[] lpFileInformation);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}

/// <summary>
/// The one link a save folder may sit under: the server's own folder (the folder that holds its user
/// folder) kept on a second drive, that is, a link to the very same path on another fixed local drive,
/// where the folder it points to and every folder above that are real folders. Nothing else is accepted.
/// </summary>
internal sealed class SecondDriveRule
{
    /// <summary>The one folder that may be such a link (a full path); null accepts no link at all.</summary>
    public string? ServerFolder { get; init; }

    /// <summary>The drive part of a full path, ending in a separator (C:\ on a machine); null when there is none.</summary>
    public required Func<string, string?> DriveOf { get; init; }

    /// <summary>Whether a drive (as <see cref="DriveOf"/> gives it) is a fixed local drive.</summary>
    public required Func<string, bool> IsFixedDrive { get; init; }

    /// <summary>No link accepted anywhere at or above the save folder.</summary>
    internal static readonly SecondDriveRule None = new() { DriveOf = MachineDriveOf, IsFixedDrive = MachineIsFixedDrive };

    /// <summary>The rule on a machine for a server whose user folder is <paramref name="userFolder"/>: its parent may be the link.</summary>
    internal static SecondDriveRule ForUserFolder(string? userFolder) => new()
    {
        ServerFolder = string.IsNullOrWhiteSpace(userFolder)
            ? null
            : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(userFolder))),
        DriveOf = MachineDriveOf,
        IsFixedDrive = MachineIsFixedDrive,
    };

    private static string? MachineDriveOf(string path) =>
        Path.GetPathRoot(path) is { Length: 3 } root && root[1] == ':' && char.IsAsciiLetter(root[0]) ? root : null;

    private static bool MachineIsFixedDrive(string drive)
    {
        try { return new DriveInfo(drive).DriveType == DriveType.Fixed; }
        catch (ArgumentException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>True, with the real folder in <paramref name="target"/>, when <paramref name="linkPath"/> is the server's
    /// own folder and links to the very same path on another fixed drive.</summary>
    internal bool Accepts(string linkPath, DirectoryInfo link, out string target)
    {
        target = "";
        if (ServerFolder is null) return false;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.TrimEndingDirectorySeparator(linkPath), Path.TrimEndingDirectorySeparator(ServerFolder), comparison)) return false;
        var drive = DriveOf(linkPath);
        if (drive is null) return false;
        string? stored;
        try { stored = link.LinkTarget; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        if (string.IsNullOrEmpty(stored)) return false;
        if (stored.StartsWith(@"\\?\", StringComparison.Ordinal)) stored = stored[4..];
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stored, Path.GetDirectoryName(linkPath)!));
        var targetDrive = DriveOf(full);
        if (targetDrive is null || string.Equals(targetDrive, drive, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(Plain(full[targetDrive.Length..]), Plain(linkPath[drive.Length..]), StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsFixedDrive(targetDrive)) return false;
        var cur = targetDrive;
        foreach (var part in full[targetDrive.Length..].Split(SnapshotWalk.Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            cur = Path.Combine(cur, part);
            var info = new DirectoryInfo(cur);
            if (!info.Exists || SnapshotWalk.IsLink(info)) return false;
        }
        target = full;
        return true;
    }

    private static string Plain(string tail) => tail.Replace('/', '\\');
}

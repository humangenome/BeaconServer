using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging;

namespace BeaconServer.Services;

public sealed record SaveCollectionUpgradeResult(
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Errors,
    string Reason,
    string? BackupDir);

/// <summary>
/// Upgrades old Subnautica 2 world files before a headless boot.
///
/// From build 25400633 (CL-131439) every world file carries a save collection
/// version and the game expects 29. Its own rule: below 27 obsolete, 27-28
/// "needs upgrade", 29 valid, above 29 too new. The retail game upgrades old
/// worlds from its save menu; a headless listen host loads savegame_0 directly,
/// the load refuses "needs upgrade" without a log line, and the server comes up
/// without its world and with saving blocked. The game's own collection upgrade
/// only sets the version to 29 and writes the file back, so this does the same:
/// one int32 at its exact property-tag position, originals copied once to
/// SaveGames-before-v29, and only when the installed game is CL-131439 or newer.
/// </summary>
public static class SaveCollectionUpgrader
{
    public const int CurrentVersion = 29;
    public const int FirstChangelistWithVersion29 = 131439;

    private static readonly byte[] VersionTag =
        Encoding.ASCII.GetBytes("\u0008\0\0\0Version\0\u000c\0\0\0IntProperty\0");
    private static readonly byte[] IntTagTail = { 0, 0, 0, 0, 4, 0, 0, 0, 0 };

    /// <summary>Changelist from the exe's version resource ("Subnautica2-CL-131439"), 0 when unknown.</summary>
    public static int ReadChangelist(string exePath)
    {
        try
        {
            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var len = (int)Math.Min(fs.Length, 8L * 1024 * 1024);
            fs.Seek(fs.Length - len, SeekOrigin.Begin);
            var tail = new byte[len];
            var read = 0;
            while (read < len)
            {
                var n = fs.Read(tail, read, len - read);
                if (n <= 0) break;
                read += n;
            }
            return ChangelistFromBytes(tail.AsSpan(0, read));
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    internal static int ChangelistFromBytes(ReadOnlySpan<byte> bytes)
    {
        var needle = Encoding.Unicode.GetBytes("Subnautica2-CL-");
        var pos = bytes.IndexOf(needle);
        if (pos < 0) return 0;
        var value = 0;
        var digits = 0;
        for (var i = pos + needle.Length; i + 1 < bytes.Length && digits < 8; i += 2)
        {
            var c = bytes[i];
            if (bytes[i + 1] != 0 || c < (byte)'0' || c > (byte)'9') break;
            value = value * 10 + (c - '0');
            digits++;
        }
        return digits == 0 ? 0 : value;
    }

    /// <summary>Offset of the collection Version int32, or -1 when the file is not a world collection.</summary>
    internal static int VersionOffset(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 64 || bytes[0] != (byte)'G' || bytes[1] != (byte)'S' || bytes[2] != (byte)'W' || bytes[3] != (byte)'U')
            return -1;
        var window = bytes[..Math.Min(bytes.Length, 8192 + VersionTag.Length)];
        var pos = window.IndexOf(VersionTag);
        if (pos < 0) return -1;
        var after = pos + VersionTag.Length;
        if (bytes.Length < after + IntTagTail.Length + 4) return -1;
        if (!bytes.Slice(after, IntTagTail.Length).SequenceEqual(IntTagTail)) return -1;
        return after + IntTagTail.Length;
    }

    internal static int? ReadVersion(ReadOnlySpan<byte> bytes)
    {
        var off = VersionOffset(bytes);
        return off < 0 ? null : BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(off, 4));
    }

    public static SaveCollectionUpgradeResult Upgrade(string saveGamesDir, int installedChangelist, ILogger? log = null)
    {
        var changed = new List<string>();
        var skipped = new List<string>();
        var errors = new List<string>();
        if (installedChangelist < FirstChangelistWithVersion29)
            return new(changed, skipped, errors, $"game build {installedChangelist} predates save collection version 29", null);
        if (string.IsNullOrWhiteSpace(saveGamesDir) || !Directory.Exists(saveGamesDir))
            return new(changed, skipped, errors, "no save folder", null);

        var todo = new List<(string Name, string Path, byte[] Bytes)>();
        foreach (var path in Directory.EnumerateFiles(saveGamesDir).OrderBy(static p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = System.IO.Path.GetFileName(path);
            if (!name.StartsWith("savegame_", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith(".sav", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{name}: unreadable"); continue; }
            var version = ReadVersion(bytes);
            if (version is null) { skipped.Add($"{name}: not a collection file"); continue; }
            if (version is 27 or 28) todo.Add((name, path, bytes));
            else skipped.Add($"{name}: version {version}");
        }
        if (todo.Count == 0) return new(changed, skipped, errors, "nothing to upgrade", null);

        var backupDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(saveGamesDir.TrimEnd('\\', '/'))!, "SaveGames-before-v29");
        if (!Directory.Exists(backupDir))
        {
            try
            {
                Directory.CreateDirectory(backupDir);
                foreach (var file in Directory.EnumerateFiles(saveGamesDir))
                    File.Copy(file, System.IO.Path.Combine(backupDir, System.IO.Path.GetFileName(file)), overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"backup failed: {ex.Message}");
                return new(changed, skipped, errors, "backup failed, nothing changed", backupDir);
            }
        }

        foreach (var (name, path, bytes) in todo)
        {
            var off = VersionOffset(bytes);
            var updated = (byte[])bytes.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(off, 4), CurrentVersion);
            if (ReadVersion(updated) != CurrentVersion) { errors.Add($"{name}: rewrite check failed"); continue; }
            var tmp = path + ".v29tmp";
            try
            {
                File.WriteAllBytes(tmp, updated);
                File.Move(tmp, path, overwrite: true);
                changed.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{name}: write failed ({ex.Message})");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
            }
        }
        var reason = $"{changed.Count} file(s) upgraded to version {CurrentVersion}";
        log?.LogInformation("World files for game build CL-{Changelist}: {Reason}; originals in {Backup}", installedChangelist, reason, backupDir);
        return new(changed, skipped, errors, reason, backupDir);
    }
}

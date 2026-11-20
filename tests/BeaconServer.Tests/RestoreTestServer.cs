using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using BeaconServer.Configuration;
using BeaconServer.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BeaconServer.Tests;

/// <summary>
/// A server in a temp folder with a live world, for the restore and snapshot-room tests. The caps are small so
/// the tests stay fast. Every link a test makes is removed first, by itself, never what it points at.
/// </summary>
internal sealed class RestoreTestServer : IDisposable
{
    internal const long KiB = 1024;

    /// <summary>A zip entry that makes a valid world for this game.</summary>
    internal const string WorldEntry = "savegame_0.sav";

    /// <summary>A file of the live world, relative to the save tree.</summary>
    internal const string LiveFile = "savegame_0.sav";

    /// <summary>A folder inside the save tree for the extra entries a test adds.</summary>
    internal const string Sub = "extra/";

    internal readonly string Root = Path.Combine(Path.GetTempPath(), "beacon-restore-" + Guid.NewGuid().ToString("N"));
    internal readonly BeaconServerOptions Options;
    internal readonly Sn2RestartCoordinator Coordinator;
    internal readonly SaveOrchestratorService Service;
    private readonly List<string> _links = new();

    internal string UserDir { get; }
    internal string SavedDir => Path.Combine(UserDir, "Saved");
    internal string Tree => Path.Combine(SavedDir, "SaveGames");
    internal string Outside => Path.Combine(Root, "outside");
    internal string SaveDir => Path.Combine(Root, "saves");

    /// <param name="userDir">The game's user folder; under <see cref="Root"/> by default.</param>
    /// <param name="liveWorld">Write a live world into the save tree first.</param>
    internal RestoreTestServer(Func<string, string>? userDir = null, bool liveWorld = true)
    {
        UserDir = userDir is null ? Path.Combine(Root, "userdir") : userDir(Root);
        Directory.CreateDirectory(Path.Combine(Root, "data"));
        Directory.CreateDirectory(Outside);
        File.WriteAllText(Path.Combine(Outside, "outside.txt"), "not part of any save");
        if (liveWorld)
        {
            var live = Path.Combine(Tree, LiveFile);
            Directory.CreateDirectory(Path.GetDirectoryName(live)!);
            File.WriteAllText(live, "live world");
        }
        Options = new BeaconServerOptions
        {
            SnUserDir = UserDir,
            SaveDir = SaveDir,
            HmacKeyPath = Path.Combine(Root, "data", "hmac.key"),
            SnapshotsEnabled = false,
            MaxRestoreUnpackedBytes = 1024 * KiB,
            MaxRestoreEntries = 20,
            MaxRestoreEntryBytes = 512 * KiB,
            RestoreFreeSpaceFloorBytes = 0,
        };
        var opts = Microsoft.Extensions.Options.Options.Create(Options);
        Coordinator = new Sn2RestartCoordinator(NullLogger<Sn2RestartCoordinator>.Instance, opts);
        Service = new SaveOrchestratorService(NullLogger<SaveOrchestratorService>.Instance, opts,
            new PipeServerState(NullLogger<PipeServerState>.Instance), Coordinator);
    }

    public void Dispose()
    {
        Service.Dispose();
        foreach (var link in _links.AsEnumerable().Reverse()) RemoveLink(link);
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal Task<bool> Restore(string zip) => Service.RestoreFromZipPathAsync(zip, "test", "snapshot.import_restore");

    /// <summary>A zip in <see cref="Root"/>: the world entry (when asked) plus entries of zeros.</summary>
    internal string Zip(string name, bool withWorld, params (string Entry, long ZeroBytes)[] extra)
    {
        var zip = Path.Combine(Root, name);
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        if (withWorld)
        {
            using var w = new StreamWriter(archive.CreateEntry(WorldEntry).Open());
            w.Write("restored world");
        }
        var zeros = new byte[64 * KiB];
        foreach (var (entry, bytes) in extra)
        {
            using var s = archive.CreateEntry(entry, CompressionLevel.SmallestSize).Open();
            for (long left = bytes; left > 0; left -= zeros.Length) s.Write(zeros, 0, (int)Math.Min(zeros.Length, left));
        }
        return zip;
    }

    internal string LiveText() => File.ReadAllText(Path.Combine(Tree, LiveFile));

    internal string RestoredText() => File.ReadAllText(Path.Combine(Tree, WorldEntry));

    /// <summary>The live world is as it was, no restore folder is left beside it, and no copy was taken.</summary>
    internal void LiveWorldUntouched()
    {
        Assert.Equal("live world", LiveText());
        Assert.Empty(Directory.GetDirectories(SavedDir, "SaveGames.incoming-*"));
        Assert.Empty(Directory.GetDirectories(SavedDir, "SaveGames.old-*"));
        Assert.Empty(Service.ListSnapshots());
    }

    /// <summary>The folder outside the save holds exactly what it held at the start.</summary>
    internal void OutsideUntouched()
    {
        var names = Directory.GetFileSystemEntries(Outside, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(Outside, p).Replace('\\', '/')).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "outside.txt" }, names);
        Assert.Equal("not part of any save", File.ReadAllText(Path.Combine(Outside, "outside.txt")));
    }

    /// <summary>A folder link: a symbolic link, or a junction on Windows (no special right needed).</summary>
    internal void MakeFolderLink(string link, string target)
    {
        _links.Add(link);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "/d", "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(30_000);
        Assert.True(Directory.Exists(link), "could not make a folder link for the test");
    }

    internal static void RemoveLink(string link)
    {
        try
        {
            var info = new FileInfo(link);
            if ((int)info.Attributes == -1 || !info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            if (OperatingSystem.IsWindows() && info.Attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(link, recursive: false);
            else File.Delete(link);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Rewrites the uncompressed size the zip states for one entry, in its local header and its central directory
    /// record, leaving the compressed data as it is.
    /// </summary>
    internal static void StateSize(string zip, string entry, uint size)
    {
        var bytes = File.ReadAllBytes(zip);
        var name = Encoding.UTF8.GetBytes(entry);
        var patched = 0;
        for (var i = 0; i + 4 <= bytes.Length; i++)
        {
            var sig = BitConverter.ToUInt32(bytes, i);
            int nameAt, sizeAt;
            if (sig == 0x04034b50) { nameAt = i + 30; sizeAt = i + 22; }
            else if (sig == 0x02014b50) { nameAt = i + 46; sizeAt = i + 24; }
            else continue;
            if (nameAt + name.Length > bytes.Length) continue;
            if (!bytes.AsSpan(nameAt, name.Length).SequenceEqual(name)) continue;
            BitConverter.GetBytes(size).CopyTo(bytes, sizeAt);
            patched++;
        }
        Assert.Equal(2, patched);
        File.WriteAllBytes(zip, bytes);
    }
}

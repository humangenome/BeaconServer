using System.IO.Compression;
using System.Security.Cryptography;
using Beacon.Persistence;
using Beacon.Protocol;
using BeaconServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

/// <summary>
/// Save snapshot orchestrator. On a triggered snapshot, sends
/// <see cref="FrameType.SaveQuiesce"/> to the plugin, waits for the
/// game-side quiesce to complete, copies save files to the snapshot
/// directory under an atomic-rename pattern, hashes them, records a
/// row in the database, and rotates old snapshots past retention.
/// </summary>
public sealed class SaveOrchestratorService : IHostedService, IDisposable
{
    private readonly ILogger<SaveOrchestratorService> _log;
    private readonly BeaconServerOptions _opts;
    private readonly PipeServerState _state;
    private readonly Sn2RestartCoordinator _coordinator;
    private readonly BeaconDb _db;
    private readonly string _dbPath;
    private FileSystemWatcher? _watcher;
    private readonly System.Threading.SemaphoreSlim _autoLock = new(1, 1);
    private DateTime _lastAutoSnapshotUtc = DateTime.MinValue;
    private static readonly TimeSpan AutoSnapshotDebounce = TimeSpan.FromSeconds(45);

    public SaveOrchestratorService(
        ILogger<SaveOrchestratorService> log,
        IOptions<BeaconServerOptions> opts,
        PipeServerState state,
        Sn2RestartCoordinator coordinator)
    {
        _log = log;
        _opts = opts.Value;
        _state = state;
        _coordinator = coordinator;
        _dbPath = Path.Combine(Path.GetDirectoryName(_opts.HmacKeyPath) ?? "data", "beacon.db");
        _db = new BeaconDb(_dbPath);
    }

    public BeaconDb Database => _db;

    /// <summary>Free bytes on the disk under a path (tests replace it).</summary>
    internal Func<string, long?> FreeSpaceProbe { get; set; } = RestoreZipLimits.FreeBytesOf;

    /// <summary>
    /// Tests only: runs right before each folder a restore makes, renames or deletes, with the step's name, so a
    /// test can put a link in place at exactly that moment. Null otherwise.
    /// </summary>
    internal Action<string>? TestBeforeMove { get; set; }

    /// <summary>
    /// The one link a save folder may sit under (<see cref="SecondDriveRule"/>), for a user folder. Tests give it
    /// stand-in drives; on a machine it is the server's own folder kept on a second drive.
    /// </summary>
    internal Func<string?, SecondDriveRule> FolderRule { get; set; } = SecondDriveRule.ForUserFolder;

    // The zip a restore is reading right now: snapshot rotation never removes it.
    private readonly object _restoreSourceGate = new();
    private string? _restoreSource;

    private string SavedDir => Path.Combine(_opts.SnUserDir, "Saved");

    private RestoreZipLimits.Caps RestoreCaps => new(
        _opts.MaxRestoreUnpackedBytes, _opts.MaxRestoreEntries, _opts.MaxRestoreEntryBytes, _opts.RestoreFreeSpaceFloorBytes);

    private long? RestoreFreeBytes()
    {
        if (_opts.RestoreFreeSpaceFloorBytes <= 0) return null;
        return FreeSpaceProbe(SavedDir);
    }

    public Task StartAsync(CancellationToken _)
    {
        _log.LogInformation("Save orchestrator ready; db={Path}, save_dir={Dir}, snapshots_enabled={Enabled}",
            _dbPath, _opts.SaveDir, _opts.SnapshotsEnabled);
        _foundBackupsListing = Task.Run(() => ListBackupsFoundOnDiskAsync(DateTime.UtcNow));
        TryNormalizeSaveSlots();
        if (_opts.SnapshotsEnabled)
        {
            TryStartFileWatcher();
        }
        else
        {
            _log.LogInformation("Auto-snapshot FileSystemWatcher disabled by config (SnapshotsEnabled=false)");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken _)
    {
        try { _watcher?.Dispose(); } catch { }
        _watcher = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _watcher?.Dispose(); } catch { }
        try { _autoLock.Dispose(); } catch { }
    }

    /// <summary>
    /// Watch SN2's SaveGames dir for auto-save writes. SN2's host process
    /// auto-saves savegame_0.sav every ~1 minute. When the file mtime
    /// changes, debounce and trigger a Beacon snapshot. This is the "Beacon
    /// snapshots, Subnautica 2 owns the trigger" model — no save RPC needed.
    /// </summary>
    private void TryStartFileWatcher()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_opts.SnUserDir))
            {
                _log.LogInformation("FileSystemWatcher skipped: SnUserDir not configured");
                return;
            }
            var sourceDir = Path.Combine(_opts.SnUserDir, "Saved", "SaveGames");
            Directory.CreateDirectory(sourceDir);
            _watcher = new FileSystemWatcher(sourceDir, "savegame_*.sav")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, e) => _ = OnSaveFileChangedAsync(e.FullPath);
            _watcher.Created += (_, e) => _ = OnSaveFileChangedAsync(e.FullPath);
            _watcher.Renamed += (_, e) => _ = OnSaveFileChangedAsync(e.FullPath);
            _log.LogInformation("FileSystemWatcher armed on {Dir} (savegame_*.sav)", sourceDir);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FileSystemWatcher failed to start; auto-snapshots disabled");
        }
    }

    /// <summary>
    /// Subnautica 2 listen-host startup creates a fresh savegame_N.sav when multiple
    /// slots are present and no explicit "continue slot" is supplied. Beacon
    /// hosts one world per instance, so keep the newest slot as savegame_0.sav
    /// and archive the rest before the game process starts.
    /// </summary>
    private void TryNormalizeSaveSlots()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_opts.SnUserDir))
            {
                _log.LogInformation("Save slot normalization skipped: SnUserDir not configured");
                return;
            }

            var sourceDir = Path.Combine(_opts.SnUserDir, "Saved", "SaveGames");
            if (_coordinator.IsOwnedSn2Running())
            {
                _log.LogInformation("Save slot normalization skipped: owned Subnautica 2 process is already running");
                return;
            }

            SaveSlotNormalizer.Normalize(sourceDir, _log);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Save slot normalization failed; Subnautica 2 may create a new save slot on this launch");
        }
    }

    private async Task OnSaveFileChangedAsync(string path)
    {
        // Debounce: Subnautica 2 may emit multiple write events per save (size, mtime,
        // last access). Only one snapshot per AutoSnapshotDebounce window.
        if (!await _autoLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var now = DateTime.UtcNow;
            if (now - _lastAutoSnapshotUtc < AutoSnapshotDebounce) return;
            // Wait briefly for Subnautica 2 to finish its write
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var rec = await SnapshotAsync(requestedBy: AutomaticRequester, CancellationToken.None).ConfigureAwait(false);
            if (rec is not null)
            {
                _lastAutoSnapshotUtc = now;
                _log.LogInformation("Auto-snapshot {Id} fired from {Path}", rec.SnapshotId, path);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Auto-snapshot from FileSystemWatcher failed for {Path}", path);
        }
        finally
        {
            _autoLock.Release();
        }
    }

    /// <summary>
    /// Trigger a snapshot. Returns the snapshot record on success, null on failure.
    /// Invoked from RCON ("save snapshot"), the FileSystemWatcher when Subnautica 2 auto-saves,
    /// or a scheduled timer.
    /// </summary>
    public async Task<SnapshotRecord?> SnapshotAsync(string requestedBy, CancellationToken ct = default, Action? noRoom = null)
    {
        // The stamp is the id's first part, set here only: no request can choose it.
        var kind = requestedBy == AutomaticRequester ? AutomaticIdPrefix : "snap-";
        var snapshotId = $"{kind}{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}".Substring(0, 36);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // The file this call wrote that is not in the list yet: removed if the call fails, because rotation only
        // knows listed snapshots and would never remove it.
        string? unlisted = null;

        try
        {
            // No request to the game first: Subnautica 2 writes its own savegame_0.sav atomically (temp +
            // rename), so a snapshot taken at any moment is self-consistent. There used to be a SaveQuiesce
            // message here, but the plugin never reads its end of the pipe: once the pipe's buffer filled
            // (about 10-13 hours of auto-snapshots) the send hung with the snapshot lock held, and
            // auto-snapshots stopped with nothing in the log. After a kill it could also abort a restore.

            Directory.CreateDirectory(_opts.SaveDir);
            var sourceDir = Path.Combine(_opts.SnUserDir, "Saved", "SaveGames");
            if (!Directory.Exists(sourceDir))
            {
                _log.LogWarning("Save source dir not found: {Dir}", sourceDir);
                return null;
            }

            if (!HasRoomForLiveSaves(sourceDir))
            {
                _log.LogWarning("Snapshot {Id} failed", snapshotId);
                noRoom?.Invoke();
                return null;
            }

            var snapshotPath = Path.Combine(_opts.SaveDir, $"{snapshotId}.zip");
            var tmpPath = snapshotPath + ".tmp";
            unlisted = tmpPath;
            var linksLeftOut = CreateSaveGamesZip(sourceDir, tmpPath, FolderRule(_opts.SnUserDir));
            if (linksLeftOut.Count > 0)
                _log.LogWarning("Snapshot {Id}: {Count} link(s) in the save folder were left out, not followed: {Links}",
                    snapshotId, linksLeftOut.Count, string.Join(", ", linksLeftOut.Take(10)));
            File.Move(tmpPath, snapshotPath);
            unlisted = snapshotPath;

            var size = new FileInfo(snapshotPath).Length;
            var hash = await Sha256OfAsync(snapshotPath, ct).ConfigureAwait(false);

            var record = new SnapshotRecord(
                SnapshotId: snapshotId,
                TakenUnix: now,
                FilePath: snapshotPath,
                SizeBytes: size,
                Sha256Hex: hash,
                RetentionDays: 30);
            _db.RecordSnapshot(record);
            unlisted = null;
            _db.Audit(requestedBy, "save.snapshot", target: snapshotId,
                detailJson: $"{{\"path\":\"{snapshotPath}\",\"bytes\":{size}}}", unixSeconds: now);

            _log.LogInformation("Snapshot {Id} written: {Bytes} bytes sha={Hash}", snapshotId, size, hash[..16]);
            RotateOldSnapshots();
            return record;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Snapshot {Id} failed", snapshotId);
            if (unlisted is not null) TryRemoveSnapshotFile(unlisted);
            return null;
        }
    }

    /// <summary>What an owner is told, word for word, when a backup is refused for room.</summary>
    internal const string BackupNoRoomWords = "Your other backups are safe. This one wasn't saved because the server's drive is almost full. Free up space, or ask your host.";

    /// <summary>What an owner is told, word for word, when a restore, upload or world copy is refused for room.</summary>
    internal const string RestoreNoRoomWords = "Your world is safe. It wasn't replaced because the server's drive is too full to back it up first. Free up space, or ask your host.";

    /// <summary>
    /// False when writing this many bytes would leave less than <c>RestoreFreeSpaceFloorBytes</c> free on the
    /// drive backups are kept on, the floor a restore's unpack keeps too. Nothing is removed to make room. An
    /// unreadable drive and a floor of 0 refuse nothing, as in that check.
    /// </summary>
    internal bool HasRoomForBackup(long bytes)
    {
        var floor = _opts.RestoreFreeSpaceFloorBytes;
        return floor <= 0 || FreeSpaceProbe(_opts.SaveDir) is not long free || free - bytes >= floor;
    }

    // The most a backup of the live saves can take: its files' own sizes, before packing.
    private bool HasRoomForLiveSaves(string sourceDir)
    {
        if (_opts.RestoreFreeSpaceFloorBytes <= 0) return true;
        var files = SnapshotWalk.PlainFiles(sourceDir, new List<string>(), out _, rule: FolderRule(_opts.SnUserDir));
        return HasRoomForBackup(files.Sum(file => new FileInfo(file).Length));
    }

    private bool NoRoomForPreRestoreCopy()
    {
        var liveSaves = Path.Combine(_opts.SnUserDir, "Saved", "SaveGames");
        try
        {
            return Directory.Exists(liveSaves) && !HasRoomForLiveSaves(liveSaves);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A save folder the walk refuses or can't read is answered by the copy below, as before.
            return false;
        }
    }

    private Task? _foundBackupsListing;

    // At a start, a backup zip in SaveDir that the list doesn't have (a server moved to another machine, a list
    // that was lost, a zip put there by hand) is listed as one of unknown kind: its id never reads as automatic, so
    // it is never removed. Only plain files are taken, and only ones older than this start.
    private async Task ListBackupsFoundOnDiskAsync(DateTime startedUtc)
    {
        try
        {
            var folder = new DirectoryInfo(_opts.SaveDir);
            if (!folder.Exists) return;
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var rows = _db.ListSnapshots(int.MaxValue);
            var paths = rows.Select(r => Path.GetFullPath(r.FilePath)).ToHashSet(comparer);
            var ids = rows.Select(r => r.SnapshotId).ToHashSet(StringComparer.Ordinal);
            foreach (var file in folder.EnumerateFiles("*.zip"))
            {
                if (!string.Equals(file.Extension, ".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (file.LastWriteTimeUtc >= startedUtc || paths.Contains(file.FullName)) continue;
                var nameHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(file.FullName));
                var id = ("found-" + Convert.ToHexString(nameHash).ToLowerInvariant()).Substring(0, 36);
                if (!ids.Add(id)) continue;
                try
                {
                    var hash = await Sha256OfAsync(file.FullName, CancellationToken.None).ConfigureAwait(false);
                    // 100 years: an older build expires every row by this field, and would take the owner's file with it.
                    _db.RecordSnapshot(new SnapshotRecord(id, new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds(),
                        file.FullName, file.Length, hash, 36_500));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or unreadable: it stays where it is and is tried again at the next start.
                }
            }
        }
        catch (Exception)
        {
            // Listing only. It must never stop a start, and it changes no file.
        }
    }

    private const string AutomaticRequester = "auto:filewatcher";
    private const string AutomaticIdPrefix = "auto-";

    // Only a backup stamped as automatic when it was taken may be removed. One asked for, an upload, a copy taken
    // before a restore, and any whose kind cannot be told (every one stored before the stamp) is never removed and
    // takes none of the automatic ones' room: it may be the only copy of that world, and a removal has no undo.
    private static bool IsAutomatic(SnapshotRecord s) =>
        s.SnapshotId.StartsWith(AutomaticIdPrefix, StringComparison.Ordinal) &&
        string.Equals(Path.GetFileName(s.FilePath), s.SnapshotId + ".zip", StringComparison.Ordinal);

    // MaxSnapshotBytes, or a tenth of the drive's free space when that is less. 0 turns the room off.
    private long AutomaticRoomBytes()
    {
        var room = _opts.MaxSnapshotBytes;
        if (room <= 0) return 0;
        return FreeSpaceProbe(_opts.SaveDir) is long free ? Math.Max(1, Math.Min(room, free / 10)) : room;
    }

    /// <summary>
    /// Remove automatic backups past their room, oldest first (<see cref="SnapshotBudget"/>), and those past 30
    /// days. Runs after every snapshot and upload. No other backup is removed or counted. The newest automatic one
    /// and one a restore is reading stay. A zip is removed only when it sits directly in <c>SaveDir</c>, and its
    /// row goes with it.
    /// </summary>
    internal void RotateOldSnapshots()
    {
        try
        {
            string? reading;
            lock (_restoreSourceGate) reading = _restoreSource;
            var items = _db.ListSnapshots(int.MaxValue)
                .Where(IsAutomatic)
                .Select(s => new SnapshotBudget.Item(s.SnapshotId, s.TakenUnix, s.SizeBytes, s.RetentionDays, s.FilePath));
            var keep = reading is null ? Array.Empty<string>() : new[] { reading };
            foreach (var s in SnapshotBudget.ToRemove(items, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                         _opts.MaxSnapshotsKept, AutomaticRoomBytes(), keep))
            {
                // The row stays while its zip does; the next pass tries again.
                if (!TryRemoveSnapshotFile(s.FilePath)) continue;
                _db.DeleteSnapshot(s.Id);
                _log.LogInformation("Snapshot {Id} removed by rotation ({Bytes} bytes, taken {Taken:u})",
                    s.Id, s.SizeBytes, DateTimeOffset.FromUnixTimeSeconds(s.TakenUnix));
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Snapshot rotation pass failed");
        }
    }

    // A snapshot zip is removed only when it sits directly in SaveDir, so a row can never send the removal anywhere
    // else. File.Delete removes a link itself, never what it points to. A zip that is already gone counts as removed.
    private bool TryRemoveSnapshotFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_opts.SaveDir));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.GetDirectoryName(full), dir, comparison) || Directory.Exists(full))
            {
                _log.LogWarning("Snapshot rotation left {Path} alone: it is not a file in the snapshot folder {Dir}", full, dir);
                return false;
            }
            File.Delete(full);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Snapshot rotation could not remove {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Zips the save tree to <paramref name="destinationPath"/> and returns the links in it that were
    /// left out. The walk never follows a link (<see cref="SnapshotWalk"/>), and the file list is made
    /// before the zip exists, so a save folder under any link but the server's own folder kept on a
    /// second drive is refused with nothing written. Zip names are relative to the real save folder.
    /// </summary>
    internal static List<string> CreateSaveGamesZip(string sourceDir, string destinationPath, SecondDriveRule? rule = null)
    {
        var linksLeftOut = new List<string>();
        var files = SnapshotWalk.PlainFiles(sourceDir, linksLeftOut, out var walkedRoot, rule: rule);
        using var archive = System.IO.Compression.ZipFile.Open(
            destinationPath,
            System.IO.Compression.ZipArchiveMode.Create);

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(walkedRoot, file).Replace('\\', '/');
            var info = new FileInfo(file);
            if (info.Length == 0 && IsRootSaveSlotPath(relativePath))
            {
                continue;
            }

            archive.CreateEntryFromFile(file, relativePath, System.IO.Compression.CompressionLevel.Optimal);
        }

        return linksLeftOut;
    }

    private static bool IsRootSaveSlotPath(string relativePath) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            relativePath,
            @"^savegame_\d+\.sav$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static async Task<string> Sha256OfAsync(string path, CancellationToken ct)
    {
        await using var f = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(f, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public IReadOnlyList<SnapshotRecord> ListSnapshots(int limit = 50) =>
        _db.ListSnapshots(limit);

    /// <summary>
    /// Restore a previously-taken snapshot by ID. Takes a pre-restore
    /// snapshot first (so the operation is reversible), kills SN2, wipes
    /// SaveGames, extracts the target snapshot zip, then releases the gate
    /// so the supervisor relaunches Subnautica 2 with the restored world.
    /// </summary>
    public async Task<bool> RestoreSnapshotAsync(string snapshotId, string requestedBy, CancellationToken ct = default,
        Action? noRoom = null)
    {
        var snap = _db.ListSnapshots(int.MaxValue).FirstOrDefault(s => s.SnapshotId == snapshotId);
        if (snap is null)
        {
            _log.LogWarning("Restore failed: snapshot {Id} not found", snapshotId);
            return false;
        }
        if (!File.Exists(snap.FilePath))
        {
            _log.LogWarning("Restore failed: snapshot file missing {Path}", snap.FilePath);
            return false;
        }
        return await RestoreFromZipPathAsync(snap.FilePath, requestedBy, $"restore:{snapshotId}", ct, noRoom).ConfigureAwait(false);
    }

    /// <summary>
    /// Restore from a freshly-uploaded zip on disk (e.g. an imported vanilla
    /// save or a cross-server transfer payload). The zip must contain the
    /// contents of a SaveGames directory at its root (savegame_0.sav etc.).
    /// Checks the zip and the save folders, then stops SN2, takes a pre-restore snapshot, swaps in the new files,
    /// and releases the supervisor gate.
    /// </summary>
    public async Task<bool> RestoreFromZipPathAsync(
        string zipPath, string requestedBy, string auditAction, CancellationToken ct = default, Action? noRoom = null)
    {
        if (!File.Exists(zipPath))
        {
            _log.LogWarning("Restore failed: source zip missing {Path}", zipPath);
            return false;
        }

        using var _ = await _coordinator.BeginRestoreAsync(ct).ConfigureAwait(false);

        // 0. Check the zip and the folders BEFORE stopping anything. A zip with no savegame_*.sav at the root
        // (almost certainly the wrong folder zipped), one that unpacks to more than the caps allow, one with an
        // entry whose path leaves the folder or whose name Windows would not store as a plain file, one whose data
        // is not the size it states, and a save folder that sits under a link are all refused with the game still
        // running and the world untouched. Until 2026-11-18 the zip was checked only after the stop and the
        // pre-restore copy, it unpacked with no limit, and nothing checked the folders for links.
        try
        {
            using var probe = ZipFile.OpenRead(zipPath);
            if (!probe.Entries.Any(e => e.Length > 0 && IsRootSaveSlotPath(e.FullName)))
            {
                _log.LogWarning("Restore refused before stopping the game: zip {Path} contains no savegame_*.sav at the root", zipPath);
                return false;
            }
            // The directory first (cheap), then one read of the data, writing nothing; the read is bounded by the
            // caps the first check just held the stated sizes to.
            var why = RestoreZipLimits.CheckBeforeUnpack(probe, RestoreCaps, RestoreFreeBytes());
            // On a drive that is nearly full the unpack's check answers first. When only room stopped it and the copy
            // of the live world has no room either, that is the reason the owner is given.
            if (why is not null && RestoreZipLimits.CheckBeforeUnpack(probe, RestoreCaps, null) is null && NoRoomForPreRestoreCopy())
                noRoom?.Invoke();
            why ??= RestoreZipLimits.CheckStatedSizesHold(probe);
            if (why is not null)
            {
                _log.LogWarning("Restore refused before stopping the game: zip {Path} {Why}", zipPath, why);
                return false;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Restore refused before stopping the game: zip {Path} could not be opened or read", zipPath);
            return false;
        }

        var rule = FolderRule(_opts.SnUserDir);
        var folderWhy = RestoreFolders.Problem(SavedDir, rule, out var realSaved);
        var saveGamesDir = folderWhy is null ? Path.Combine(realSaved, "SaveGames") : "";
        folderWhy ??= RestoreFolders.EntryProblem(saveGamesDir);
        if (folderWhy is not null)
        {
            _log.LogWarning("Restore refused before stopping the game: {Why}; a restore never writes through a link", folderWhy);
            return false;
        }

        lock (_restoreSourceGate) _restoreSource = Path.GetFullPath(zipPath);
        try
        {
            return await StopAndSwapAsync(zipPath, requestedBy, auditAction, rule, realSaved, saveGamesDir, ct, noRoom)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_restoreSourceGate) _restoreSource = null;
        }
        // gate released by `using` — supervisor will relaunch Subnautica 2 on its
        // next loop iteration.
    }

    private async Task<bool> StopAndSwapAsync(string zipPath, string requestedBy, string auditAction,
        SecondDriveRule rule, string realSaved, string saveGamesDir, CancellationToken ct, Action? noRoom)
    {
        // No room for the copy taken before the swap: refused while the game still runs.
        if (NoRoomForPreRestoreCopy())
        {
            _log.LogWarning("Restore aborted: pre-restore snapshot failed");
            noRoom?.Invoke();
            return false;
        }

        // 1. Stop Subnautica 2 first. Taking a snapshot BEFORE the kill would
        // capture whatever state Subnautica 2 was mid-writing — which on a save
        // tick is half a savegame_0.sav. Kill, wait for exit, then snap.
        _coordinator.KillSn2(TimeSpan.FromSeconds(20));
        if (_coordinator.IsOwnedSn2Running())
        {
            // A game still running would write over the world this restore puts in place.
            _log.LogError("Restore aborted: Subnautica 2 is still running after the stop");
            return false;
        }

        // 2. Snapshot the now-stable state so a bad restore is reversible.
        var pre = await SnapshotAsync($"pre-restore:{requestedBy}", ct, noRoom).ConfigureAwait(false);
        if (pre is null)
        {
            _log.LogWarning("Restore aborted: pre-restore snapshot failed");
            return false;
        }

        // 3. Atomic SaveGames swap, in the real Saved folder checked in step 0. Extract the incoming zip into a
        // temp directory first; on success, rename the old SaveGames out of the way and rename the temp dir into
        // place. If anything fails before the rename, the live SaveGames is untouched and the operation is a
        // no-op rather than a corruption. Right before each folder is made, renamed or deleted, the Saved folder
        // and the folders involved are checked again: a link that appeared stops the restore there.
        try
        {
            var savedDir = SavedDir;
            void CheckFolders(string step, params string[] folders)
            {
                TestBeforeMove?.Invoke(step);
                RestoreFolders.CheckBeforeMove(savedDir, rule, realSaved, folders);
            }

            var stagingDir = saveGamesDir + ".incoming-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var prevDir = saveGamesDir + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            bool savesMovedAside = false;
            try
            {
                CheckFolders("make-staging", saveGamesDir, stagingDir, prevDir);
                Directory.CreateDirectory(realSaved);
                Directory.CreateDirectory(stagingDir);
                CheckFolders("unpack", stagingDir);
                // Entries may not escape the staging dir, nothing is written through a link, and the unpack stops
                // the moment it writes more than the caps allow, whatever sizes the zip states; the staging dir is
                // the sandbox and is removed on any failure below.
                RestoreZipLimits.ExtractWithinCaps(zipPath, stagingDir, RestoreCaps, RestoreFreeBytes());

                if (Directory.Exists(saveGamesDir))
                {
                    CheckFolders("move-live-aside", saveGamesDir, prevDir);
                    Directory.Move(saveGamesDir, prevDir);
                    savesMovedAside = true;
                }
                CheckFolders("move-incoming-in", stagingDir, saveGamesDir);
                Directory.Move(stagingDir, saveGamesDir);
                if (Directory.Exists(prevDir))
                {
                    try
                    {
                        CheckFolders("delete-old", prevDir);
                        RestoreFolders.DeleteTree(prevDir);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "The replaced saves were left at {Prev}", prevDir);
                    }
                }
            }
            catch (Exception ex)
            {
                // Rollback. If we already renamed the live SaveGames out to
                // prevDir but couldn't get staging into place, restore the
                // original so the customer doesn't end up with NO save dir.
                if (savesMovedAside && Directory.Exists(prevDir) && !Directory.Exists(saveGamesDir))
                {
                    try
                    {
                        CheckFolders("rollback", saveGamesDir, prevDir);
                        Directory.Move(prevDir, saveGamesDir);
                    }
                    catch (Exception rollbackEx)
                    {
                        _log.LogError(rollbackEx,
                            "CRITICAL: rollback of SaveGames from {Prev} to {Live} failed after restore error; " +
                            "manual recovery may be required",
                            prevDir, saveGamesDir);
                    }
                }
                if (Directory.Exists(stagingDir))
                {
                    try
                    {
                        CheckFolders("delete-staging", stagingDir);
                        RestoreFolders.DeleteTree(stagingDir);
                    }
                    catch { }
                }
                _log.LogError(ex, "Restore failed; SaveGames rolled back from {Prev}", prevDir);
                throw;
            }

            // The game loads savegame_0 only. An imported SaveGames folder can carry its
            // world in another slot (savegame_2.sav from a player's own PC), so make the
            // newest slot savegame_0 now, the same way a BeaconServer start does.
            try { SaveSlotNormalizer.Normalize(saveGamesDir, _log); }
            catch (Exception ex) { _log.LogWarning(ex, "Save slot normalization after restore failed"); }

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _db.Audit(requestedBy, auditAction, target: pre.SnapshotId,
                detailJson: $"{{\"pre_snapshot\":\"{pre.SnapshotId}\",\"source\":\"{Path.GetFileName(zipPath)}\"}}",
                unixSeconds: nowUnix);

            // The FileSystemWatcher's directory handle was opened against
            // the ORIGINAL SaveGames dir; Directory.Move above replaced
            // that inode. Tear it down and re-arm against the new dir or
            // future auto-snapshots stop firing until BeaconServer restart.
            // Only when auto-snapshots are on: re-arming unconditionally turned
            // them on after every restore on a host configured with
            // SnapshotsEnabled=false, until BeaconServer restarted.
            try { _watcher?.Dispose(); } catch { }
            _watcher = null;
            if (_opts.SnapshotsEnabled) TryStartFileWatcher();

            _log.LogInformation("Restore complete from {Zip}; pre-snapshot {Id}", zipPath, pre.SnapshotId);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Restore failed while swapping SaveGames");
            return false;
        }
    }
}

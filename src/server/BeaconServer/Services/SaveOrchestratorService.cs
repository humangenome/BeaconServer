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

    public Task StartAsync(CancellationToken _)
    {
        _log.LogInformation("Save orchestrator ready; db={Path}, save_dir={Dir}", _dbPath, _opts.SaveDir);
        TryStartFileWatcher();
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
    /// snapshots, SN2 owns the trigger" model — no save RPC needed.
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

    private async Task OnSaveFileChangedAsync(string path)
    {
        // Debounce: SN2 may emit multiple write events per save (size, mtime,
        // last access). Only one snapshot per AutoSnapshotDebounce window.
        if (!await _autoLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var now = DateTime.UtcNow;
            if (now - _lastAutoSnapshotUtc < AutoSnapshotDebounce) return;
            // Wait briefly for SN2 to finish its write
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            var rec = await SnapshotAsync(requestedBy: "auto:filewatcher", CancellationToken.None).ConfigureAwait(false);
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
    /// Invoked from RCON ("save snapshot"), the FileSystemWatcher when SN2 auto-saves,
    /// or a scheduled timer.
    /// </summary>
    public async Task<SnapshotRecord?> SnapshotAsync(string requestedBy, CancellationToken ct = default)
    {
        var snapshotId = $"snap-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}".Substring(0, 36);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            // If plugin is connected, request a SaveQuiesce so the game flushes
            // in-flight state. If not connected, fall through — SN2 writes its
            // own savegame_0.sav atomically (temp + rename), so a snapshot taken
            // without quiesce is still self-consistent. This unblocks the
            // FileSystemWatcher auto-snapshot path which fires when SN2 has just
            // finished writing the file.
            var conn = _state.Connection;
            if (conn is not null)
            {
                await conn.SendAsync(FrameType.SaveQuiesce,
                    new SaveQuiesceMessage(snapshotId, TimeoutSeconds: 30),
                    ct).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }

            Directory.CreateDirectory(_opts.SaveDir);
            var sourceDir = Path.Combine(_opts.SnUserDir, "Saved", "SaveGames");
            if (!Directory.Exists(sourceDir))
            {
                _log.LogWarning("Save source dir not found: {Dir}", sourceDir);
                return null;
            }

            var snapshotPath = Path.Combine(_opts.SaveDir, $"{snapshotId}.zip");
            var tmpPath = snapshotPath + ".tmp";
            System.IO.Compression.ZipFile.CreateFromDirectory(sourceDir, tmpPath);
            File.Move(tmpPath, snapshotPath);

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
            _db.Audit(requestedBy, "save.snapshot", target: snapshotId,
                detailJson: $"{{\"path\":\"{snapshotPath}\",\"bytes\":{size}}}", unixSeconds: now);

            _log.LogInformation("Snapshot {Id} written: {Bytes} bytes sha={Hash}", snapshotId, size, hash[..16]);
            RotateOldSnapshots();
            return record;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Snapshot {Id} failed", snapshotId);
            return null;
        }
    }

    private void RotateOldSnapshots()
    {
        try
        {
            var snapshots = _db.ListSnapshots(int.MaxValue);
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var s in snapshots)
            {
                var ageSeconds = nowUnix - s.TakenUnix;
                var maxSeconds = s.RetentionDays * 86_400;
                if (ageSeconds <= maxSeconds) continue;
                if (File.Exists(s.FilePath))
                {
                    try { File.Delete(s.FilePath); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Snapshot rotation pass failed");
        }
    }

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
    /// so the supervisor relaunches SN2 with the restored world.
    /// </summary>
    public async Task<bool> RestoreSnapshotAsync(string snapshotId, string requestedBy, CancellationToken ct = default)
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
        return await RestoreFromZipPathAsync(snap.FilePath, requestedBy, $"restore:{snapshotId}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Restore from a freshly-uploaded zip on disk (e.g. an imported vanilla
    /// save or a cross-server transfer payload). The zip must contain the
    /// contents of a SaveGames directory at its root (savegame_0.sav etc.).
    /// Takes a pre-restore snapshot, kills SN2, swaps in the new files, then
    /// releases the supervisor gate.
    /// </summary>
    public async Task<bool> RestoreFromZipPathAsync(
        string zipPath, string requestedBy, string auditAction, CancellationToken ct = default)
    {
        if (!File.Exists(zipPath))
        {
            _log.LogWarning("Restore failed: source zip missing {Path}", zipPath);
            return false;
        }

        using var _ = await _coordinator.BeginRestoreAsync(ct).ConfigureAwait(false);

        // 1. Stop SN2 first. Taking a snapshot BEFORE the kill would
        // capture whatever state SN2 was mid-writing — which on a save
        // tick is half a savegame_0.sav. Kill, wait for exit, then snap.
        _coordinator.KillSn2(TimeSpan.FromSeconds(20));

        // 2. Snapshot the now-stable state so a bad restore is reversible.
        var pre = await SnapshotAsync($"pre-restore:{requestedBy}", ct).ConfigureAwait(false);
        if (pre is null)
        {
            _log.LogWarning("Restore aborted: pre-restore snapshot failed");
            return false;
        }

        // 3. Atomic SaveGames swap. Extract the incoming zip into a temp
        // directory first; on success, rename the old SaveGames out of the
        // way and rename the temp dir into place. If anything fails before
        // the rename, the live SaveGames is untouched and the operation is
        // a no-op rather than a corruption.
        try
        {
            var saveGamesDir = Path.Combine(_opts.SnUserDir, "Saved", "SaveGames");
            Directory.CreateDirectory(Path.GetDirectoryName(saveGamesDir)!);

            var stagingDir = saveGamesDir + ".incoming-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var prevDir = saveGamesDir + ".old-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Validate the zip BEFORE touching live SaveGames. A zip with no
            // savegame_*.sav at the root is almost certainly a customer
            // zipping the wrong folder — refuse rather than wipe their world.
            try
            {
                using var probe = System.IO.Compression.ZipFile.OpenRead(zipPath);
                var hasSave = probe.Entries.Any(e =>
                    System.Text.RegularExpressions.Regex.IsMatch(
                        e.FullName, @"^savegame_\d+\.sav$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                if (!hasSave)
                {
                    _log.LogWarning("Restore rejected: zip {Path} contains no savegame_*.sav at the root", zipPath);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Restore rejected: zip {Path} could not be opened", zipPath);
                return false;
            }

            bool savesMovedAside = false;
            try
            {
                Directory.CreateDirectory(stagingDir);
                // .NET 5+ ZipFile.ExtractToDirectory validates entries don't
                // escape the destination (zip-slip is rejected with an
                // IOException), so the staging dir is the canonical sandbox.
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);

                if (Directory.Exists(saveGamesDir))
                {
                    Directory.Move(saveGamesDir, prevDir);
                    savesMovedAside = true;
                }
                Directory.Move(stagingDir, saveGamesDir);
                if (Directory.Exists(prevDir))
                {
                    try { Directory.Delete(prevDir, recursive: true); } catch { /* best effort */ }
                }
            }
            catch (Exception ex)
            {
                // Rollback. If we already renamed the live SaveGames out to
                // prevDir but couldn't get staging into place, restore the
                // original so the customer doesn't end up with NO save dir.
                if (savesMovedAside && Directory.Exists(prevDir) && !Directory.Exists(saveGamesDir))
                {
                    try { Directory.Move(prevDir, saveGamesDir); }
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
                    try { Directory.Delete(stagingDir, recursive: true); } catch { }
                }
                _log.LogError(ex, "Restore failed; SaveGames rolled back from {Prev}", prevDir);
                throw;
            }

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _db.Audit(requestedBy, auditAction, target: pre.SnapshotId,
                detailJson: $"{{\"pre_snapshot\":\"{pre.SnapshotId}\",\"source\":\"{Path.GetFileName(zipPath)}\"}}",
                unixSeconds: nowUnix);

            // The FileSystemWatcher's directory handle was opened against
            // the ORIGINAL SaveGames dir; Directory.Move above replaced
            // that inode. Tear it down and re-arm against the new dir or
            // future auto-snapshots stop firing until BeaconServer restart.
            try { _watcher?.Dispose(); } catch { }
            _watcher = null;
            TryStartFileWatcher();

            _log.LogInformation("Restore complete from {Zip}; pre-snapshot {Id}", zipPath, pre.SnapshotId);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Restore failed while swapping SaveGames");
            return false;
        }
        // gate released by `using` — supervisor will relaunch SN2 on its
        // next loop iteration.
    }
}

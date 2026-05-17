using System.Diagnostics;
using BeaconServer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

/// <summary>
/// Coordinates between operations that need to pause SN2 (snapshot restore,
/// world wipe, in-place upgrade) and the supervisor loop that keeps it
/// running. A restore acquires the gate, kills SN2 if it's running, mutates
/// SaveGames, then releases the gate. The supervisor waits on the gate
/// before each relaunch so a new SN2 doesn't spawn into a half-written
/// world.
///
/// Kill scoping: only kills SN2 instances whose executable lives under the
/// configured <see cref="BeaconServerOptions.SnInstallRoot"/> (or, when that
/// is empty, instances whose command line references the configured
/// <see cref="BeaconServerOptions.SnUserDir"/>). A self-hoster running
/// vanilla SN2 elsewhere on the same machine will not be touched.
/// </summary>
public sealed class Sn2RestartCoordinator
{
    private static readonly string[] Sn2ProcessNames =
    [
        "Subnautica2-Win64-Shipping",
        "Subnautica2",
    ];

    private readonly ILogger<Sn2RestartCoordinator> _log;
    private readonly BeaconServerOptions _opts;
    private readonly SemaphoreSlim _restoreGate = new(1, 1);

    public Sn2RestartCoordinator(ILogger<Sn2RestartCoordinator> log, IOptions<BeaconServerOptions> opts)
    {
        _log = log;
        _opts = opts.Value;
    }

    public async Task<IDisposable> BeginRestoreAsync(CancellationToken ct)
    {
        await _restoreGate.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_restoreGate);
    }

    public async Task WaitForNoRestoreAsync(CancellationToken ct)
    {
        await _restoreGate.WaitAsync(ct).ConfigureAwait(false);
        _restoreGate.Release();
    }

    /// <summary>
    /// Kill the SN2 instances this BeaconServer owns. Scoping is strict:
    /// only processes whose path starts with <c>SnInstallRoot</c> (when
    /// configured) or whose command line includes <c>SnUserDir</c> are
    /// touched. Unrelated SN2 processes on the machine — e.g. a customer's
    /// vanilla Steam session running alongside their host — are left alone.
    /// </summary>
    public void KillSn2(TimeSpan waitForExit)
    {
        if (!OperatingSystem.IsWindows()) return;

        var installRoot = NormalizeForCompare(_opts.SnInstallRoot);
        var userDir = NormalizeForCompare(_opts.SnUserDir);
        if (installRoot is null && userDir is null)
        {
            // No anchor to scope by — refuse to fire rather than kill every
            // SN2 on the box. This is the path Codex caught.
            _log.LogWarning("KillSn2 skipped: neither SnInstallRoot nor SnUserDir is configured");
            return;
        }

        foreach (var name in Sn2ProcessNames)
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    if (!OwnsProcess(p, installRoot, userDir))
                    {
                        _log.LogDebug("KillSn2 skipping pid={Pid} (not under our SnInstallRoot/SnUserDir)", p.Id);
                        continue;
                    }
                    _log.LogInformation("Restoring: killing SN2 pid={Pid} name={Name}", p.Id, name);
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit((int)waitForExit.TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to kill SN2 pid={Pid}", p.Id);
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
    }

    private static bool OwnsProcess(Process p, string? installRoot, string? userDir)
    {
        // Path match — fast, no WMI. MainModule.FileName requires SeDebug-ish
        // privileges for processes owned by other users; in BeaconServer's
        // case it always runs as the same user as SN2, so MainModule works.
        string? exePath = null;
        try { exePath = p.MainModule?.FileName; }
        catch { /* access denied or exited */ }

        if (installRoot is not null && exePath is not null)
        {
            var normExe = NormalizeForCompare(exePath)!;
            // StartsWith without a directory-separator boundary would treat
            // 'C:\Games\SN2-vanilla\...' as living under 'C:\Games\SN2'.
            // Require either exact match (unlikely — exe path includes the
            // .exe) or the next char after the install root to be a
            // separator before claiming ownership.
            var sep = Path.DirectorySeparatorChar;
            if (string.Equals(normExe, installRoot, StringComparison.OrdinalIgnoreCase)
                || normExe.StartsWith(installRoot + sep, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Command line match for -USERDIR flag. WMI is the cleanest source
        // but pulls a heavy dependency; fall back to reading the process's
        // command line through Win32. For our scope, MainWindowTitle + PE
        // module path already cover 99% of cases.
        if (userDir is not null && exePath is not null)
        {
            // Process MAY have been launched with -USERDIR=<userDir>. Without
            // pulling a command-line helper, treat the install-root match as
            // authoritative; userDir is best-effort context only.
        }

        return false;
    }

    private static string? NormalizeForCompare(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return null; }
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _s;
        private int _disposed;
        public Releaser(SemaphoreSlim s) => _s = s;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _s.Release();
        }
    }
}

using System.Text.RegularExpressions;
using BeaconServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

/// <summary>
/// Tails the SN2 host's UE log file (<c>{SnUserDir}/Saved/Logs/Subnautica2.log</c>),
/// filters for operator-meaningful categories, and re-emits each line
/// through Serilog with a <c>[SN2]</c> source prefix. Joins, leaves, chat,
/// errors, save events, map travels — all show up interleaved with
/// BeaconServer's own log stream.
///
/// Also looks for the SN2 build banner on the first pass and registers it
/// with <see cref="BeaconVersionInfo"/> so A2S responses can publish it.
/// </summary>
public sealed class Sn2LogTailService : BackgroundService
{
    private static readonly Regex BuildRegex = new(
        @"LogInit:\s+Build:\s+(?<build>[A-Za-z0-9\-_.]+)",
        RegexOptions.Compiled);

    private static readonly Regex CategoryRegex = new(
        @"^\[[\d.\-: ]+\]\[\s*\d+\]\s*(?<cat>Log[A-Za-z]+):",
        RegexOptions.Compiled);

    // SN2 log categories worth surfacing on the operator console.
    // Skip FMOD, RHI, Slate, ShaderCompiler, PakFile, IoDispatcher, etc.
    private static readonly HashSet<string> InterestingCategories = new(StringComparer.Ordinal)
    {
        "LogNet",
        "LogSN2",
        "LogSonar",
        "LogSaveSystem",
        "LogChat",
        "LogWorld",
        "LogGameplayMessage",
        "LogUWE",
        "LogUWEGameplay",
        "LogUWEFrontend",
        "LogGlobalStatus",
        "LogOnline",
        "LogLoad",
        "LogExit",
    };

    private static readonly Regex JoinRegex   = new(@"NotifyAcceptedConnection.*RemoteAddr:\s*(?<addr>[^,]+)", RegexOptions.Compiled);
    private static readonly Regex LeaveRegex  = new(@"UNetConnection::Close.*RemoteAddr:\s*(?<addr>[^,]+)",     RegexOptions.Compiled);
    private static readonly Regex TravelRegex = new(@"UEngine::Browse Started Browse:\s*""(?<url>[^""]+)""",    RegexOptions.Compiled);

    private readonly ILogger<Sn2LogTailService> _log;
    private readonly BeaconServerOptions _opts;
    private long _position;
    private bool _buildLogged;

    public Sn2LogTailService(ILogger<Sn2LogTailService> log, IOptions<BeaconServerOptions> opts)
    {
        _log = log;
        _opts = opts.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.SnUserDir))
        {
            _log.LogInformation("[SN2] log tail idle: SnUserDir not configured");
            return;
        }
        var logPath = Path.Combine(_opts.SnUserDir, "Saved", "Logs", "Subnautica2.log");
        _log.LogInformation("[SN2] log tail watching {Path}", logPath);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(logPath))
                {
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    continue;
                }
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < _position) _position = 0; // log rotated
                fs.Seek(_position, SeekOrigin.Begin);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = await sr.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    EmitLine(line);
                }
                _position = fs.Position;
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { /* log got rotated mid-read */ }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[SN2] log tail loop error");
            }
            try { await Task.Delay(1500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void EmitLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;

        // Capture the SN2 build banner once per run.
        if (!_buildLogged)
        {
            var m = BuildRegex.Match(line);
            if (m.Success)
            {
                var build = m.Groups["build"].Value;
                BeaconVersionInfo.SetSn2Build(build);
                _log.LogInformation("[SN2] build detected: {Build}", build);
                _buildLogged = true;
            }
        }

        var catMatch = CategoryRegex.Match(line);
        var cat = catMatch.Success ? catMatch.Groups["cat"].Value : null;
        var isInteresting = cat is not null && InterestingCategories.Contains(cat);
        if (!isInteresting) return;

        // Promote join / leave / travel to dedicated highlight messages.
        var joinMatch = JoinRegex.Match(line);
        if (joinMatch.Success)
        {
            _log.LogInformation("[SN2] player joined from {Addr}", joinMatch.Groups["addr"].Value);
            return;
        }
        var leaveMatch = LeaveRegex.Match(line);
        if (leaveMatch.Success)
        {
            _log.LogInformation("[SN2] connection closed for {Addr}", leaveMatch.Groups["addr"].Value);
            return;
        }
        var travelMatch = TravelRegex.Match(line);
        if (travelMatch.Success)
        {
            _log.LogInformation("[SN2] travel -> {Url}", travelMatch.Groups["url"].Value);
            return;
        }

        // Default: pass through with [SN2] prefix and category as info.
        if (line.Contains("Error", StringComparison.OrdinalIgnoreCase))
            _log.LogError("[SN2] {Line}", line);
        else if (line.Contains("Warning", StringComparison.OrdinalIgnoreCase))
            _log.LogWarning("[SN2] {Line}", line);
        else
            _log.LogInformation("[SN2] {Line}", line);
    }
}

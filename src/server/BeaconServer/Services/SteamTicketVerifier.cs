using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using BeaconServer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

public enum SteamTicketOutcome
{
    Ok,
    Refused,      // Steam (or the ticket check) said no; Detail says why
    Unavailable,  // no Steam library, no anonymous logon, or no answer in time: fail closed
}

public sealed record SteamTicketResult(SteamTicketOutcome Outcome, string Detail);

public interface ISteamTicketVerifier
{
    Task<SteamTicketResult> VerifyAsync(string steam64, byte[] ticket, CancellationToken ct);

    /// <summary>True while the anonymous Steam game-server login is up and checks can be asked.</summary>
    bool Ready { get; }
}

/// <summary>Loads the Steam client and Steam API libraries. Tests stand in fake exports.</summary>
internal interface ISteamNativeLibraries
{
    void LoadClient(string path);

    /// <summary>Loads the Steam API library and returns its export lookup (IntPtr.Zero = not exported).</summary>
    Func<string, IntPtr> LoadApi(string path);
}

/// <summary>
/// Which Steam game-server functions a steam_api64.dll offers, and which of them the ticket check calls. The start call
/// changed in Steamworks SDK 1.58: older libraries export only SteamInternal_GameServer_Init, newer ones only
/// SteamInternal_GameServer_Init_V2. Subnautica 2 ships the SDK 1.53 library (in a folder named Steamv157). The
/// SteamGameServer accessor is versioned too (v014 up to SDK 1.55, v015 from 1.56); a library exports the one its own
/// SteamAPI_ISteamGameServer_* functions are built for, so the newest one found is used.
/// </summary>
internal sealed record SteamGameServerExports(string? Init, string? GameServerAccessor, IReadOnlyList<string> Missing)
{
    internal const string InitV2 = "SteamInternal_GameServer_Init_V2";
    internal const string InitLegacy = "SteamInternal_GameServer_Init";
    private const string AccessorPrefix = "SteamAPI_SteamGameServer_v";

    /// <summary>Every other function the check calls; each has the same name and arguments in SDK 1.53 to 1.61.</summary>
    internal static readonly string[] Required =
    [
        "SteamAPI_ManualDispatch_Init", "SteamGameServer_GetHSteamPipe", "SteamAPI_ManualDispatch_RunFrame",
        "SteamAPI_ManualDispatch_GetNextCallback", "SteamAPI_ManualDispatch_FreeLastCallback",
        "SteamAPI_ISteamGameServer_SetProduct", "SteamAPI_ISteamGameServer_SetModDir",
        "SteamAPI_ISteamGameServer_SetDedicatedServer", "SteamAPI_ISteamGameServer_LogOnAnonymous",
        "SteamAPI_ISteamGameServer_BLoggedOn", "SteamAPI_ISteamGameServer_BeginAuthSession",
        "SteamAPI_ISteamGameServer_EndAuthSession",
    ];

    public bool Usable => Missing.Count == 0;

    public bool InitIsV2 => Init == InitV2;

    /// <summary>The interface version behind the accessor, e.g. "SteamGameServer014".</summary>
    public string? GameServerInterface => GameServerAccessor is null ? null : "SteamGameServer" + GameServerAccessor[AccessorPrefix.Length..];

    public static SteamGameServerExports Resolve(Func<string, bool> has)
    {
        var missing = new List<string>();
        var init = has(InitV2) ? InitV2 : has(InitLegacy) ? InitLegacy : null;
        if (init is null) missing.Add(InitV2 + " or " + InitLegacy + " (no game-server start call)");
        string? accessor = null;
        for (var v = 30; v >= 10 && accessor is null; v--)
        {
            var name = AccessorPrefix + v.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            if (has(name)) accessor = name;
        }
        if (accessor is null) missing.Add(AccessorPrefix + "0NN (no SteamGameServer accessor)");
        missing.AddRange(Required.Where(n => !has(n)));
        return new SteamGameServerExports(init, accessor, missing);
    }
}

/// <summary>
/// Checks a joining player's Steam session ticket the way a Steam game server does, with no Steam account: an
/// anonymous game-server login for Subnautica 2 (the library's game-server start call + LogOnAnonymous), then
/// BeginAuthSession and Steam's ValidateAuthTicketResponse_t. Same approach as a Steam game server's own ticket check (proven on
/// real Steam, 2026-10-29). Anonymous game-server login for 1962700 was proven on 2026-11-06 (Steamworks 1.61): it
/// connects, and a made-up ticket is refused at BeginAuthSession. The game's own library is older (SDK 1.53, see
/// <see cref="SteamGameServerExports"/>), so the start call is picked from what the library exports. A result is only
/// Ok after Steam's ValidateAuthTicketResponse says OK for that Steam ID; no answer within the timeout fails closed.
/// One dedicated thread owns every Steam call and pumps callbacks.
/// </summary>
public sealed class SteamGameServerTicketVerifier : ISteamTicketVerifier, IDisposable
{
    private const int CbSteamServersConnected = 101;
    private const int CbSteamServerConnectFailure = 102;
    private const int CbValidateAuthTicketResponse = 143;
    private static readonly string[] AuthResponses =
    [
        "OK", "UserNotConnectedToSteam", "NoLicenseOrExpired", "VACBanned", "LoggedInElseWhere", "VACCheckTimedOut",
        "AuthTicketCanceled", "AuthTicketInvalidAlreadyUsed", "AuthTicketInvalid", "PublisherIssuedBan",
        "AuthTicketNetworkIdentityFailure",
    ];
    private static readonly string[] BeginResults = ["OK", "InvalidTicket", "DuplicateRequest", "InvalidVersion", "GameMismatch", "ExpiredTicket"];

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidFn();
    // SDK 1.58+: ESteamAPIInitResult SteamInternal_GameServer_Init_V2(uint32 unIP, uint16 usGamePort, uint16 usQueryPort,
    //   EServerMode eServerMode, const char *pchVersionString, const char *pszInternalCheckInterfaceVersions, SteamErrMsg *pOutErrMsg)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GsInitFn(uint ip, ushort gamePort, ushort queryPort, int mode, [MarshalAs(UnmanagedType.LPStr)] string version, byte[] interfaces, byte[] err);
    // Up to SDK 1.57 (steam_gameserver.h): bool SteamInternal_GameServer_Init(uint32 unIP, uint16 usLegacySteamPort,
    //   uint16 usGamePort, uint16 usQueryPort, EServerMode eServerMode, const char *pchVersionString)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool GsInitLegacyFn(uint ip, ushort legacySteamPort, ushort gamePort, ushort queryPort, int mode, [MarshalAs(UnmanagedType.LPStr)] string version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PipeFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RunFrameFn(int pipe);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool NextCallbackFn(int pipe, IntPtr msg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeCallbackFn(int pipe);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AccessorFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GsStrFn(IntPtr gs, [MarshalAs(UnmanagedType.LPStr)] string s);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GsBoolFn(IntPtr gs, [MarshalAs(UnmanagedType.I1)] bool b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GsFn(IntPtr gs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool GsBoolGetFn(IntPtr gs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BeginAuthFn(IntPtr gs, byte[] ticket, int cb, ulong steamId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void EndAuthFn(IntPtr gs, ulong steamId);

    private readonly ILogger<SteamGameServerTicketVerifier> _log;
    private readonly IdentityOptions _opts;
    private readonly BeaconServerOptions _root;
    private readonly BlockingCollection<Work> _queue = new();
    private readonly Thread _thread;
    private readonly ISteamNativeLibraries _native;
    private readonly TimeSpan _loginWarnAfter;
    private string? _unavailable;
    private volatile bool _loggedOn;

    public bool Ready => _unavailable is null && _loggedOn;

    private sealed record Work(ulong SteamId, byte[] Ticket, TaskCompletionSource<SteamTicketResult> Done, DateTime Deadline);

    public SteamGameServerTicketVerifier(IOptions<BeaconServerOptions> options, ILogger<SteamGameServerTicketVerifier> log)
        : this(options, log, new NativeLibraries(), TimeSpan.FromSeconds(60))
    {
    }

    internal SteamGameServerTicketVerifier(IOptions<BeaconServerOptions> options, ILogger<SteamGameServerTicketVerifier> log,
        ISteamNativeLibraries native, TimeSpan loginWarnAfter)
    {
        _log = log;
        _root = options.Value;
        _opts = options.Value.Identity;
        _native = native;
        _loginWarnAfter = loginWarnAfter;
        _thread = new Thread(Run) { IsBackground = true, Name = "steam-ticket-check" };
        _thread.Start();
    }

    private sealed class NativeLibraries : ISteamNativeLibraries
    {
        public void LoadClient(string path) => NativeLibrary.Load(path);

        public Func<string, IntPtr> LoadApi(string path)
        {
            var lib = NativeLibrary.Load(path);
            return name => NativeLibrary.TryGetExport(lib, name, out var p) ? p : IntPtr.Zero;
        }
    }

    public Task<SteamTicketResult> VerifyAsync(string steam64, byte[] ticket, CancellationToken ct)
    {
        if (!ulong.TryParse(steam64, out var id) || ticket.Length is < 16 or > 4096)
            return Task.FromResult(new SteamTicketResult(SteamTicketOutcome.Refused, "malformed request"));
        if (_unavailable is not null)
            return Task.FromResult(new SteamTicketResult(SteamTicketOutcome.Unavailable, _unavailable));
        var tcs = new TaskCompletionSource<SteamTicketResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(_opts.SteamCheckTimeoutSeconds, 5, 60));
        if (!_queue.TryAdd(new Work(id, ticket, tcs, DateTime.UtcNow + timeout)))
            return Task.FromResult(new SteamTicketResult(SteamTicketOutcome.Unavailable, "checker stopped"));
        ct.Register(() => tcs.TrySetCanceled(ct));
        // The checker may have given up between the first look and the add; nothing would ever answer this one.
        if (_unavailable is { } why) tcs.TrySetResult(new SteamTicketResult(SteamTicketOutcome.Unavailable, why));
        return tcs.Task;
    }

    /// <summary>
    /// The game's own 64-bit Steam API library: Engine\Binaries\ThirdParty\Steamworks\Steamv*\Win64\steam_api64.dll
    /// (highest Steamworks version) under the game folder. Game folders tried in order: SnInstallRoot when it exists,
    /// the folder above BeaconServer (the hosted layout: BeaconServer\ sits inside the game folder), and the folders
    /// above the configured game executable. Returns null when none has it.
    /// </summary>
    internal static string? FindSteamApi(BeaconServerOptions root, string baseDirectory)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(root.SnInstallRoot)) roots.Add(root.SnInstallRoot);
        roots.Add(Path.Combine(baseDirectory, ".."));
        if (!string.IsNullOrWhiteSpace(root.SnExecutablePath))
        {
            var d = Path.GetDirectoryName(root.SnExecutablePath);
            for (var i = 0; i < 5 && !string.IsNullOrEmpty(d); i++) { roots.Add(d); d = Path.GetDirectoryName(d); }
        }
        foreach (var r in roots)
        {
            string steamworks;
            try { steamworks = Path.GetFullPath(Path.Combine(r, "Engine", "Binaries", "ThirdParty", "Steamworks")); }
            catch (Exception) { continue; }
            if (!Directory.Exists(steamworks)) continue;
            var best = Directory.EnumerateDirectories(steamworks, "Steamv*")
                .Select(dir => (dir, ver: int.TryParse(Path.GetFileName(dir)["Steamv".Length..], out var v) ? v : -1))
                .Where(x => x.ver >= 0 && File.Exists(Path.Combine(x.dir, "Win64", "steam_api64.dll")))
                .OrderByDescending(x => x.ver)
                .Select(x => Path.Combine(x.dir, "Win64", "steam_api64.dll"))
                .FirstOrDefault();
            if (best is not null) return best;
        }
        return null;
    }

    private void Run()
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(_opts.SteamApiPath) ? FindSteamApi(_root, AppContext.BaseDirectory) : _opts.SteamApiPath;
            if (path is null || !File.Exists(path)) { Fail("no Steam API library found in the game folder"); return; }
            _log.LogInformation("Identity: Steam API library {Path}", path);
            // A Steam game server needs steamclient64.dll. the Steam API library looks for it by name, so loading it first by
            // full path (its folder supplies tier0_s64 / vstdlib_s64) makes a box without a Steam client work too.
            var client = FindSteamClient(_opts.SteamClientPath);
            // A host fetches the Steam client files (Valve's redistributable) in the background at its first start, so
            // they may land after this process started. With a configured place, wait for them instead of giving up
            // for the life of the process; every check asked meanwhile fails closed.
            if (client is null && !string.IsNullOrWhiteSpace(_opts.SteamClientPath))
            {
                _log.LogInformation("Identity: waiting for the Steam client files in {Path}", _opts.SteamClientPath);
                var nextLook = DateTime.UtcNow.AddSeconds(30);
                while (client is null)
                {
                    if (_queue.IsCompleted) return;
                    if (_queue.TryTake(out var waiting, 1000))
                        waiting.Done.TrySetResult(new SteamTicketResult(SteamTicketOutcome.Unavailable, "Steam client files not there yet"));
                    if (DateTime.UtcNow < nextLook) continue;
                    client = FindSteamClient(_opts.SteamClientPath);
                    nextLook = DateTime.UtcNow.AddSeconds(30);
                }
            }
            // Steam reads the app id from the environment through the C runtime's copy, taken when the Steam API library loads, so it
            // has to be set BEFORE the library loads (lab 2026-10-30: set after the load, init said "Can't determine AppID").
            // The old value goes back right after init so nothing BeaconServer starts later (the game) inherits it.
            var oldAppId = Environment.GetEnvironmentVariable("SteamAppId");
            Environment.SetEnvironmentVariable("SteamAppId", _opts.SteamAppId.ToString());
            Func<string, IntPtr> export;
            SteamGameServerExports api;
            string? initFailure = null;
            try
            {
                if (client is not null) { _native.LoadClient(client); _log.LogInformation("Identity: Steam client library {Path}", client); }
                else _log.LogInformation("Identity: no steamclient64.dll found; relying on the Steam API library's own search");
                export = _native.LoadApi(path);
                api = SteamGameServerExports.Resolve(name => export(name) != IntPtr.Zero);
                if (!api.Usable)
                {
                    // Nothing is started on a library that cannot finish the check; every ticket check answers Unavailable.
                    Fail($"the Steam API library lacks {string.Join(", ", api.Missing)} ({path})", LogLevel.Error);
                    return;
                }
                _log.LogInformation("Identity: Steam game-server start call {Init}, interface {Accessor}", api.Init, api.GameServerAccessor);
                // port 0 = no game or query socket of our own; authentication mode (2), no VAC.
                if (api.InitIsV2)
                {
                    var gsInit = Marshal.GetDelegateForFunctionPointer<GsInitFn>(export(SteamGameServerExports.InitV2));
                    var err = new byte[1024];
                    var ifaces = Encoding.ASCII.GetBytes("SteamUtils010\0" + api.GameServerInterface + "\0\0");
                    var rc = gsInit(0, 0, 0xFFFF, 2, "1.0.0.0", ifaces, err);
                    if (rc != 0) initFailure = $"Steam game-server init failed ({rc}: {Ascii(err)})";
                }
                else
                {
                    var gsInit = Marshal.GetDelegateForFunctionPointer<GsInitLegacyFn>(export(SteamGameServerExports.InitLegacy));
                    // usLegacySteamPort is always 0 (what the SDK's own SteamGameServer_Init passes).
                    if (!gsInit(0, 0, 0, 0xFFFF, 2, "1.0.0.0")) initFailure = "Steam game-server init failed (" + SteamGameServerExports.InitLegacy + " returned false)";
                }
            }
            finally { Environment.SetEnvironmentVariable("SteamAppId", oldAppId); }
            if (initFailure is not null) { Fail(initFailure); return; }

            T F<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(export(name));
            var dispatchInit = F<VoidFn>("SteamAPI_ManualDispatch_Init");
            var gsPipe = F<PipeFn>("SteamGameServer_GetHSteamPipe");
            var runFrame = F<RunFrameFn>("SteamAPI_ManualDispatch_RunFrame");
            var next = F<NextCallbackFn>("SteamAPI_ManualDispatch_GetNextCallback");
            var free = F<FreeCallbackFn>("SteamAPI_ManualDispatch_FreeLastCallback");
            var gsAccessor = F<AccessorFn>(api.GameServerAccessor!);
            var setProduct = F<GsStrFn>("SteamAPI_ISteamGameServer_SetProduct");
            var setModDir = F<GsStrFn>("SteamAPI_ISteamGameServer_SetModDir");
            var setDedicated = F<GsBoolFn>("SteamAPI_ISteamGameServer_SetDedicatedServer");
            var logOn = F<GsFn>("SteamAPI_ISteamGameServer_LogOnAnonymous");
            var loggedOn = F<GsBoolGetFn>("SteamAPI_ISteamGameServer_BLoggedOn");
            var beginAuth = F<BeginAuthFn>("SteamAPI_ISteamGameServer_BeginAuthSession");
            var endAuth = F<EndAuthFn>("SteamAPI_ISteamGameServer_EndAuthSession");

            // Manual dispatch is switched on AFTER init (steam_api.h); before init no callback ever arrives (lab 2026-10-30).
            dispatchInit();
            var gs = gsAccessor();
            setProduct(gs, _opts.SteamAppId.ToString());
            setModDir(gs, "subnautica2");
            setDedicated(gs, true);
            logOn(gs);
            var pipe = gsPipe();
            _log.LogInformation("Identity: anonymous Steam game-server login started for app {App}", _opts.SteamAppId);

            var pending = new Dictionary<ulong, Work>();
            var msg = Marshal.AllocHGlobal(32);
            var connectedLogged = false;
            var loginStarted = DateTime.UtcNow;
            var loginWarned = false;
            try
            {
                while (true)
                {
                    // callbacks
                    runFrame(pipe);
                    _loggedOn = loggedOn(gs);
                    // Without this line a login that never comes up would leave the check off with nothing in the log.
                    if (!_loggedOn && !loginWarned && DateTime.UtcNow - loginStarted >= _loginWarnAfter)
                    {
                        loginWarned = true;
                        _log.LogWarning("Identity: Steam game-server login still not up after {Seconds} s; no join pass can be issued until it is",
                            _loginWarnAfter.TotalSeconds);
                    }
                    while (next(pipe, msg))
                    {
                        var id = Marshal.ReadInt32(msg, 4);
                        var param = Marshal.ReadIntPtr(msg, 8);
                        if (id == CbSteamServersConnected && !connectedLogged) { connectedLogged = true; _log.LogInformation("Identity: Steam game-server login OK"); }
                        else if (id == CbSteamServerConnectFailure) _log.LogWarning("Identity: Steam game-server login failed (EResult {R})", Marshal.ReadInt32(param, 0));
                        else if (id == CbValidateAuthTicketResponse)
                        {
                            var who = (ulong)Marshal.ReadInt64(param, 0);
                            var resp = Marshal.ReadInt32(param, 8);
                            if (pending.Remove(who, out var w))
                            {
                                endAuth(gs, who);
                                var name = resp >= 0 && resp < AuthResponses.Length ? AuthResponses[resp] : resp.ToString();
                                // Steam's OK proves who the player is; a family-shared licence is still that player.
                                w.Done.TrySetResult(resp == 0
                                    ? new SteamTicketResult(SteamTicketOutcome.Ok, "Steam: OK")
                                    : new SteamTicketResult(SteamTicketOutcome.Refused, "Steam: " + name));
                            }
                        }
                        free(pipe);
                    }
                    // timeouts
                    var now = DateTime.UtcNow;
                    foreach (var kv in pending.Where(kv => kv.Value.Deadline < now).ToList())
                    {
                        pending.Remove(kv.Key);
                        endAuth(gs, kv.Key);
                        kv.Value.Done.TrySetResult(new SteamTicketResult(SteamTicketOutcome.Unavailable, "no answer from Steam in time"));
                    }
                    // new work
                    while (_queue.TryTake(out var work, pending.Count == 0 ? 100 : 0))
                    {
                        if (work.Done.Task.IsCompleted) continue;
                        if (!loggedOn(gs)) { work.Done.TrySetResult(new SteamTicketResult(SteamTicketOutcome.Unavailable, "Steam game-server login not up")); continue; }
                        if (pending.ContainsKey(work.SteamId)) { work.Done.TrySetResult(new SteamTicketResult(SteamTicketOutcome.Refused, "a check for this Steam ID is already running")); continue; }
                        var begin = beginAuth(gs, work.Ticket, work.Ticket.Length, work.SteamId);
                        if (begin != 0)
                        {
                            work.Done.TrySetResult(new SteamTicketResult(SteamTicketOutcome.Refused,
                                "ticket: " + (begin < BeginResults.Length ? BeginResults[begin] : begin.ToString())));
                            continue;
                        }
                        pending[work.SteamId] = work;
                    }
                    if (pending.Count > 0) Thread.Sleep(50);
                    if (_queue.IsAddingCompleted && _queue.Count == 0 && pending.Count == 0) break;
                }
            }
            finally { Marshal.FreeHGlobal(msg); }
        }
        catch (Exception ex)
        {
            Fail("Steam check failed: " + ex.Message);
        }
    }

    internal static string? FindSteamClient(string configured)
    {
        var candidates = new List<string>();
        // Identity:SteamClientPath may name the file or the folder that holds it (the self-host notes say "set their folder").
        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(Directory.Exists(configured) ? Path.Combine(configured, "steamclient64.dll") : configured);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "steamclient64.dll"));
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in new[] { @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" })
            {
                try
                {
                    using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                    if (k?.GetValue("InstallPath") is string dir) candidates.Add(Path.Combine(dir, "steamclient64.dll"));
                }
                catch (Exception) { }
            }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private void Fail(string why, LogLevel level = LogLevel.Warning)
    {
        _unavailable = why;
        _log.Log(level, "Identity: Steam ticket checks unavailable: {Why}", why);
        while (_queue.TryTake(out var w)) w.Done.TrySetResult(new SteamTicketResult(SteamTicketOutcome.Unavailable, why));
    }

    private static string Ascii(byte[] b)
    {
        var n = Array.IndexOf(b, (byte)0);
        return Encoding.ASCII.GetString(b, 0, n < 0 ? b.Length : n);
    }

    public void Dispose() => _queue.CompleteAdding();
}

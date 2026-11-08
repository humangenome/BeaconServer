using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using BeaconServer.Configuration;
using BeaconServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeaconServer.Tests;

/// <summary>
/// The Steam identity check must start on the Steam API library the game ships. Subnautica 2's library (in a folder
/// named Steamv157) is the Steamworks SDK 1.53 build: it has the old game-server start call and the v014 accessor,
/// and no SteamInternal_GameServer_Init_V2. A Steamworks 1.61 library has only V2 and v015. The lists below are every
/// export with the prefixes the check can ask for, read from the two real files.
/// </summary>
public sealed class SteamGameServerExportsTests
{
    private static readonly string[] Common =
    [
        "SteamAPI_ISteamGameServer_AssociateWithClan", "SteamAPI_ISteamGameServer_BeginAuthSession",
        "SteamAPI_ISteamGameServer_BLoggedOn", "SteamAPI_ISteamGameServer_BSecure",
        "SteamAPI_ISteamGameServer_BUpdateUserData", "SteamAPI_ISteamGameServer_CancelAuthTicket",
        "SteamAPI_ISteamGameServer_ClearAllKeyValues", "SteamAPI_ISteamGameServer_ComputeNewPlayerCompatibility",
        "SteamAPI_ISteamGameServer_CreateUnauthenticatedUserConnection", "SteamAPI_ISteamGameServer_EndAuthSession",
        "SteamAPI_ISteamGameServer_GetAuthSessionTicket", "SteamAPI_ISteamGameServer_GetGameplayStats",
        "SteamAPI_ISteamGameServer_GetNextOutgoingPacket", "SteamAPI_ISteamGameServer_GetPublicIP",
        "SteamAPI_ISteamGameServer_GetServerReputation", "SteamAPI_ISteamGameServer_GetSteamID",
        "SteamAPI_ISteamGameServer_HandleIncomingPacket", "SteamAPI_ISteamGameServer_LogOff",
        "SteamAPI_ISteamGameServer_LogOn", "SteamAPI_ISteamGameServer_LogOnAnonymous",
        "SteamAPI_ISteamGameServer_RequestUserGroupStatus", "SteamAPI_ISteamGameServer_SendUserConnectAndAuthenticate_DEPRECATED",
        "SteamAPI_ISteamGameServer_SendUserDisconnect_DEPRECATED", "SteamAPI_ISteamGameServer_SetAdvertiseServerActive",
        "SteamAPI_ISteamGameServer_SetBotPlayerCount", "SteamAPI_ISteamGameServer_SetDedicatedServer",
        "SteamAPI_ISteamGameServer_SetGameData", "SteamAPI_ISteamGameServer_SetGameDescription",
        "SteamAPI_ISteamGameServer_SetGameTags", "SteamAPI_ISteamGameServer_SetKeyValue",
        "SteamAPI_ISteamGameServer_SetMapName", "SteamAPI_ISteamGameServer_SetMaxPlayerCount",
        "SteamAPI_ISteamGameServer_SetModDir", "SteamAPI_ISteamGameServer_SetPasswordProtected",
        "SteamAPI_ISteamGameServer_SetProduct", "SteamAPI_ISteamGameServer_SetRegion",
        "SteamAPI_ISteamGameServer_SetServerName", "SteamAPI_ISteamGameServer_SetSpectatorPort",
        "SteamAPI_ISteamGameServer_SetSpectatorServerName", "SteamAPI_ISteamGameServer_UserHasLicenseForApp",
        "SteamAPI_ISteamGameServer_WasRestartRequested", "SteamAPI_ManualDispatch_FreeLastCallback",
        "SteamAPI_ManualDispatch_GetAPICallResult", "SteamAPI_ManualDispatch_GetNextCallback",
        "SteamAPI_ManualDispatch_Init", "SteamAPI_ManualDispatch_RunFrame",
        "SteamAPI_SteamGameServerHTTP_v003", "SteamAPI_SteamGameServerInventory_v003",
        "SteamAPI_SteamGameServerNetworkingMessages_SteamAPI_v002", "SteamAPI_SteamGameServerNetworkingSockets_SteamAPI_v012",
        "SteamAPI_SteamGameServerNetworking_v006", "SteamAPI_SteamGameServerStats_v001",
        "SteamAPI_SteamGameServerUtils_v010", "SteamGameServer_BSecure",
        "SteamGameServer_GetHSteamPipe", "SteamGameServer_GetHSteamUser",
        "SteamGameServer_GetIPCCallCount", "SteamGameServer_GetSteamID",
        "SteamGameServer_InitSafe", "SteamGameServer_RunCallbacks",
        "SteamGameServer_Shutdown", "SteamInternal_ContextInit",
        "SteamInternal_CreateInterface", "SteamInternal_FindOrCreateGameServerInterface",
        "SteamInternal_FindOrCreateUserInterface",
    ];

    /// <summary>
    /// Subnautica 2's Engine/Binaries/ThirdParty/Steamworks/Steamv157/Win64/steam_api64.dll (295,336 bytes, sha256
    /// 4df999c0c8cb12589f0864d52be5d4c775577aeb27fee28b49b188f9ba083eea, signed by Valve; byte-identical to the
    /// Steamworks SDK 1.53 redistributable).
    /// </summary>
    internal static readonly string[] Sdk153 =
        [.. Common, "SteamAPI_SteamGameServerUGC_v016", "SteamAPI_SteamGameServer_v014", "SteamInternal_GameServer_Init"];

    /// <summary>A Steamworks SDK 1.61 steam_api64.dll (319,584 bytes, sha256 670d654aa3255c5061cf0236a1cdbb2f5076cbd5ae44f613b6f4921558e83e2d).</summary>
    internal static readonly string[] Sdk161 =
        [.. Common, "SteamAPI_SteamGameServerUGC_v020", "SteamAPI_SteamGameServer_v015", "SteamInternal_GameServer_Init_V2", "SteamInternal_SteamAPI_Init"];

    /// <summary>SDK 1.56/1.57 shape: the old start call with the v015 accessor.</summary>
    internal static readonly string[] Sdk157 =
        [.. Common, "SteamAPI_SteamGameServerUGC_v017", "SteamAPI_SteamGameServer_v015", "SteamInternal_GameServer_Init"];

    private static SteamGameServerExports Resolve(IEnumerable<string> exports)
    {
        var set = exports.ToHashSet(StringComparer.Ordinal);
        return SteamGameServerExports.Resolve(set.Contains);
    }

    [Fact]
    public void Subnautica2_library_without_V2_uses_the_old_start_call_and_v014()
    {
        var api = Resolve(Sdk153);
        api.Missing.Should().BeEmpty();
        api.Usable.Should().BeTrue();
        api.Init.Should().Be("SteamInternal_GameServer_Init");
        api.InitIsV2.Should().BeFalse();
        api.GameServerAccessor.Should().Be("SteamAPI_SteamGameServer_v014");
        api.GameServerInterface.Should().Be("SteamGameServer014");
    }

    [Fact]
    public void Steamworks_161_library_uses_V2_and_v015()
    {
        var api = Resolve(Sdk161);
        api.Missing.Should().BeEmpty();
        api.Init.Should().Be("SteamInternal_GameServer_Init_V2");
        api.InitIsV2.Should().BeTrue();
        api.GameServerAccessor.Should().Be("SteamAPI_SteamGameServer_v015");
        api.GameServerInterface.Should().Be("SteamGameServer015");
    }

    [Fact]
    public void Steamworks_157_shape_uses_the_old_start_call_and_v015()
    {
        var api = Resolve(Sdk157);
        api.Missing.Should().BeEmpty();
        api.Init.Should().Be("SteamInternal_GameServer_Init");
        api.GameServerAccessor.Should().Be("SteamAPI_SteamGameServer_v015");
    }

    [Fact]
    public void V2_wins_when_a_library_has_both_start_calls()
    {
        Resolve([.. Sdk161, "SteamInternal_GameServer_Init"]).Init.Should().Be("SteamInternal_GameServer_Init_V2");
    }

    [Fact]
    public void Newest_accessor_wins_when_a_library_has_several()
    {
        Resolve([.. Sdk153, "SteamAPI_SteamGameServer_v013", "SteamAPI_SteamGameServer_v016"])
            .GameServerAccessor.Should().Be("SteamAPI_SteamGameServer_v016");
    }

    [Fact]
    public void A_missing_function_is_named()
    {
        var api = Resolve(Sdk153.Where(n => n != "SteamAPI_ISteamGameServer_BeginAuthSession"));
        api.Usable.Should().BeFalse();
        api.Missing.Should().Equal("SteamAPI_ISteamGameServer_BeginAuthSession");
    }

    [Fact]
    public void No_start_call_and_no_accessor_are_named()
    {
        var api = Resolve(Common);
        api.Usable.Should().BeFalse();
        api.Init.Should().BeNull();
        api.GameServerAccessor.Should().BeNull();
        api.Missing.Should().Contain(m => m.Contains("SteamInternal_GameServer_Init_V2") && m.Contains("SteamInternal_GameServer_Init "));
        api.Missing.Should().Contain(m => m.StartsWith("SteamAPI_SteamGameServer_v"));
    }

    // ---- the verifier's own start-up, on fake exports (no Steam needed) ----

    [Fact]
    public async Task Verifier_on_the_Subnautica2_library_starts_with_the_old_call_and_checks_a_ticket()
    {
        using var fake = new FakeSteamApi(Sdk153);
        var (verifier, log, file) = Make(fake);
        try
        {
            (await WaitAsync(() => verifier.Ready)).Should().BeTrue(string.Join(" | ", log.Lines));
            fake.LegacyInitArgs.Should().Equal(0u, 0u, 0u, 0xFFFFu, 2u);
            fake.InitVersion.Should().Be("1.0.0.0");
            fake.V2InitCalls.Should().Be(0);
            fake.AccessorCalled.Should().Be("SteamAPI_SteamGameServer_v014");
            fake.Product.Should().Be("1962700");
            fake.Dedicated.Should().BeTrue();
            fake.LogOnAnonymousCalls.Should().Be(1);
            log.Lines.Should().Contain(l => l.Contains("SteamInternal_GameServer_Init") && l.Contains("SteamAPI_SteamGameServer_v014"));

            var ok = await verifier.VerifyAsync("76561198000000001", new byte[64], CancellationToken.None);
            ok.Outcome.Should().Be(SteamTicketOutcome.Ok);
            fake.EndAuthFor.Should().Contain(76561198000000001UL);

            fake.Answer = 8; // AuthTicketInvalid
            var no = await verifier.VerifyAsync("76561198000000002", new byte[64], CancellationToken.None);
            no.Outcome.Should().Be(SteamTicketOutcome.Refused);
            no.Detail.Should().Be("Steam: AuthTicketInvalid");
        }
        finally { verifier.Dispose(); File.Delete(file); }
    }

    [Fact]
    public async Task Verifier_on_a_161_library_starts_with_V2_and_names_SteamGameServer015()
    {
        using var fake = new FakeSteamApi(Sdk161);
        var (verifier, log, file) = Make(fake);
        try
        {
            (await WaitAsync(() => verifier.Ready)).Should().BeTrue(string.Join(" | ", log.Lines));
            fake.V2InitCalls.Should().Be(1);
            fake.LegacyInitArgs.Should().BeEmpty();
            fake.V2Interfaces.Should().Equal("SteamUtils010", "SteamGameServer015");
            fake.AccessorCalled.Should().Be("SteamAPI_SteamGameServer_v015");
        }
        finally { verifier.Dispose(); File.Delete(file); }
    }

    [Fact]
    public async Task Verifier_on_a_library_missing_a_function_fails_loudly_and_at_once()
    {
        using var fake = new FakeSteamApi(Sdk153.Where(n => n != "SteamAPI_ISteamGameServer_BeginAuthSession"));
        var (verifier, log, file) = Make(fake);
        try
        {
            (await WaitAsync(() => log.Errors.Count > 0)).Should().BeTrue(string.Join(" | ", log.Lines));
            log.Errors.Should().ContainSingle(l => l.Contains("SteamAPI_ISteamGameServer_BeginAuthSession"));
            verifier.Ready.Should().BeFalse();
            fake.LegacyInitArgs.Should().BeEmpty("nothing is started on a library that cannot finish the check");

            var answer = verifier.VerifyAsync("76561198000000001", new byte[64], CancellationToken.None);
            (await Task.WhenAny(answer, Task.Delay(TimeSpan.FromSeconds(5)))).Should().BeSameAs(answer);
            (await answer).Outcome.Should().Be(SteamTicketOutcome.Unavailable);
            (await answer).Detail.Should().Contain("SteamAPI_ISteamGameServer_BeginAuthSession");
        }
        finally { verifier.Dispose(); File.Delete(file); }
    }

    [Fact]
    public async Task Verifier_says_so_when_the_Steam_login_never_comes_up()
    {
        using var fake = new FakeSteamApi(Sdk153) { LoggedOn = false };
        var (verifier, log, file) = Make(fake, loginWarnAfter: TimeSpan.FromMilliseconds(300));
        try
        {
            (await WaitAsync(() => log.Warnings.Any(l => l.Contains("login still not up")))).Should().BeTrue(string.Join(" | ", log.Lines));
            await Task.Delay(400);
            log.Warnings.Count(l => l.Contains("login still not up")).Should().Be(1);
            verifier.Ready.Should().BeFalse();
        }
        finally { verifier.Dispose(); File.Delete(file); }
    }

    private static (SteamGameServerTicketVerifier Verifier, CapturingLogger Log, string File) Make(FakeSteamApi fake, TimeSpan? loginWarnAfter = null)
    {
        var file = Path.Combine(Path.GetTempPath(), "beacon-fake-steam_api64-" + Guid.NewGuid().ToString("N") + ".dll");
        File.WriteAllText(file, "fake");
        var opts = Options.Create(new BeaconServerOptions
        {
            InstanceId = "lab-1",
            Identity = new IdentityOptions { SteamApiPath = file, SteamCheckTimeoutSeconds = 5 },
        });
        var log = new CapturingLogger();
        return (new SteamGameServerTicketVerifier(opts, log, fake, loginWarnAfter ?? TimeSpan.FromSeconds(60)), log, file);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private sealed class CapturingLogger : ILogger<SteamGameServerTicketVerifier>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Line)> _all = new();
        public List<string> Lines => _all.Select(x => x.Line).ToList();
        public List<string> Errors => _all.Where(x => x.Level >= LogLevel.Error).Select(x => x.Line).ToList();
        public List<string> Warnings => _all.Where(x => x.Level == LogLevel.Warning).Select(x => x.Line).ToList();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _all.Enqueue((logLevel, formatter(state, exception)));
    }

    /// <summary>
    /// Stands in for steam_api64.dll: static native entry points (UnmanagedCallersOnly), exported under the names in the
    /// given list only, so the verifier's own delegates and marshaling run as they do on the real library. Calls are
    /// routed to the fake whose LoadApi ran on the calling thread (each verifier owns one thread), so a verifier left
    /// over from another test never touches this one.
    /// </summary>
    private sealed unsafe class FakeSteamApi : ISteamNativeLibraries, IDisposable
    {
        [ThreadStatic] private static FakeSteamApi? t_current;
        private static FakeSteamApi Me => t_current ?? throw new InvalidOperationException("no fake on this thread");

        private static readonly IntPtr GameServer = new(0x5157);
        private readonly HashSet<string> _exports;
        private readonly ConcurrentQueue<ulong> _answers = new();
        private IntPtr _param;

        public volatile bool LoggedOn = true;
        public volatile int Answer; // EAuthSessionResponse sent back for each ticket
        public List<uint> LegacyInitArgs { get; } = new();
        public string? InitVersion;
        public int V2InitCalls;
        public List<string> V2Interfaces { get; } = new();
        public string? AccessorCalled;
        public string? Product;
        public bool Dedicated;
        public int LogOnAnonymousCalls;
        public ConcurrentBag<ulong> EndAuthFor { get; } = new();

        public FakeSteamApi(IEnumerable<string> exports) => _exports = exports.ToHashSet(StringComparer.Ordinal);

        private static readonly Dictionary<string, IntPtr> Functions = new(StringComparer.Ordinal)
        {
            ["SteamInternal_GameServer_Init"] = (IntPtr)(delegate* unmanaged[Cdecl]<uint, ushort, ushort, ushort, int, IntPtr, byte>)&InitLegacy,
            ["SteamInternal_GameServer_Init_V2"] = (IntPtr)(delegate* unmanaged[Cdecl]<uint, ushort, ushort, int, IntPtr, IntPtr, IntPtr, int>)&InitV2,
            ["SteamAPI_ManualDispatch_Init"] = (IntPtr)(delegate* unmanaged[Cdecl]<void>)&Nothing,
            ["SteamGameServer_GetHSteamPipe"] = (IntPtr)(delegate* unmanaged[Cdecl]<int>)&Pipe,
            ["SteamAPI_ManualDispatch_RunFrame"] = (IntPtr)(delegate* unmanaged[Cdecl]<int, void>)&RunFrame,
            ["SteamAPI_ManualDispatch_GetNextCallback"] = (IntPtr)(delegate* unmanaged[Cdecl]<int, IntPtr, byte>)&NextCallback,
            ["SteamAPI_ManualDispatch_FreeLastCallback"] = (IntPtr)(delegate* unmanaged[Cdecl]<int, void>)&FreeLastCallback,
            ["SteamAPI_SteamGameServer_v013"] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr>)&Accessor013,
            ["SteamAPI_SteamGameServer_v014"] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr>)&Accessor014,
            ["SteamAPI_SteamGameServer_v015"] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr>)&Accessor015,
            ["SteamAPI_SteamGameServer_v016"] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr>)&Accessor016,
            ["SteamAPI_ISteamGameServer_SetProduct"] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&SetProduct,
            ["SteamAPI_ISteamGameServer_SetModDir"] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&SetModDir,
            ["SteamAPI_ISteamGameServer_SetDedicatedServer"] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte, void>)&SetDedicated,
            ["SteamAPI_ISteamGameServer_LogOnAnonymous"] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&LogOnAnonymous,
            ["SteamAPI_ISteamGameServer_BLoggedOn"] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte>)&BLoggedOn,
            ["SteamAPI_ISteamGameServer_BeginAuthSession"] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, ulong, int>)&BeginAuthSession,
            ["SteamAPI_ISteamGameServer_EndAuthSession"] = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, ulong, void>)&EndAuthSession,
        };

        public void LoadClient(string path) { }

        public Func<string, IntPtr> LoadApi(string path)
        {
            t_current = this;
            return name => _exports.Contains(name) && Functions.TryGetValue(name, out var fn) ? fn : IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_param != IntPtr.Zero) { Marshal.FreeHGlobal(_param); _param = IntPtr.Zero; }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static byte InitLegacy(uint ip, ushort legacySteamPort, ushort gamePort, ushort queryPort, int mode, IntPtr version)
        {
            Me.LegacyInitArgs.AddRange([ip, legacySteamPort, gamePort, queryPort, (uint)mode]);
            Me.InitVersion = Marshal.PtrToStringAnsi(version);
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static int InitV2(uint ip, ushort gamePort, ushort queryPort, int mode, IntPtr version, IntPtr interfaces, IntPtr err)
        {
            Me.V2InitCalls++;
            Me.InitVersion = Marshal.PtrToStringAnsi(version);
            for (var p = interfaces; ;)
            {
                var s = Marshal.PtrToStringAnsi(p) ?? "";
                if (s.Length == 0) break;
                Me.V2Interfaces.Add(s);
                p += Encoding.ASCII.GetByteCount(s) + 1;
            }
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void Nothing() { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static int Pipe() => 7;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void RunFrame(int pipe) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static byte NextCallback(int pipe, IntPtr msg)
        {
            var me = Me;
            if (!me._answers.TryDequeue(out var who)) return 0;
            me._param = Marshal.AllocHGlobal(24);
            Marshal.WriteInt64(me._param, 0, (long)who);
            Marshal.WriteInt32(me._param, 8, me.Answer);
            Marshal.WriteInt64(me._param, 16, (long)who);
            Marshal.WriteInt32(msg, 0, 1);
            Marshal.WriteInt32(msg, 4, 143); // ValidateAuthTicketResponse_t
            Marshal.WriteIntPtr(msg, 8, me._param);
            Marshal.WriteInt32(msg, 16, 24);
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void FreeLastCallback(int pipe)
        {
            var me = Me;
            if (me._param != IntPtr.Zero) { Marshal.FreeHGlobal(me._param); me._param = IntPtr.Zero; }
        }

        private static IntPtr Accessor(string version) { Me.AccessorCalled = "SteamAPI_SteamGameServer_v" + version; return GameServer; }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static IntPtr Accessor013() => Accessor("013");

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static IntPtr Accessor014() => Accessor("014");

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static IntPtr Accessor015() => Accessor("015");

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static IntPtr Accessor016() => Accessor("016");

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void SetProduct(IntPtr gs, IntPtr s) => Me.Product = Marshal.PtrToStringAnsi(s);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void SetModDir(IntPtr gs, IntPtr s) { }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void SetDedicated(IntPtr gs, byte b) => Me.Dedicated = b != 0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void LogOnAnonymous(IntPtr gs) => Me.LogOnAnonymousCalls++;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static byte BLoggedOn(IntPtr gs) => Me.LoggedOn ? (byte)1 : (byte)0;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static int BeginAuthSession(IntPtr gs, IntPtr ticket, int cb, ulong steamId) { Me._answers.Enqueue(steamId); return 0; }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
        private static void EndAuthSession(IntPtr gs, ulong steamId) => Me.EndAuthFor.Add(steamId);
    }
}

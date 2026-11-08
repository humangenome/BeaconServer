using Beacon.Protocol;
using BeaconServer.Configuration;
using BeaconServer.Services;
using FluentAssertions;
using MessagePack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeaconServer.Tests;

/// <summary>Steam identity check at join (0.3.140): the join pass BeaconServer issues after Steam's OK.</summary>
public sealed class JoinPassServiceTests
{
    private const string SteamId = "76561198000000001";
    private const string OtherSteamId = "76561198000000002";
    private static readonly string TicketHex = new string('a', 200) + "0123456789abcdef";

    private sealed class FakeSteam : ISteamTicketVerifier
    {
        public SteamTicketResult Answer = new(SteamTicketOutcome.Ok, "Steam: OK");
        public bool Ready { get; set; } = true;
        public int Calls;
        public string? LastSteamId;
        public Task<SteamTicketResult> VerifyAsync(string steam64, byte[] ticket, CancellationToken ct)
        {
            Calls++;
            LastSteamId = steam64;
            return Task.FromResult(Answer);
        }
    }

    private sealed class CapturingLogger : ILogger<JoinPassService>
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    private static (JoinPassService Svc, PipeServerState State, FakeSteam Steam, CapturingLogger Log, Action<TimeSpan> Advance) Make(
        IdentityOptions? identity = null, bool withKey = true)
    {
        var opts = Options.Create(new BeaconServerOptions { InstanceId = "lab-1", Identity = identity ?? new IdentityOptions() });
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        if (withKey) state.SetIdentityKey(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        state.LastIdentityCheckReady = 1;
        var steam = new FakeSteam();
        var log = new CapturingLogger();
        var now = new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.Zero);
        var svc = new JoinPassService(opts, state, new InstanceIdentityProvider(opts), steam, log, () => now);
        return (svc, state, steam, log, d => now += d);
    }

    [Fact]
    public void Mac_matches_the_shared_contract_vector()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        JoinPassService.Mac(key, SteamId, 2000000000, "00112233445566778899aabbccddeeff")
            .Should().Be("e95c3d57c98a61c1226075ee8252d1c08ac8cdccff99f10a7de24e7207ca8625");
        JoinPassService.Issue(key, SteamId, 2000000000, "00112233445566778899aabbccddeeff")
            .Should().Be("1.76561198000000001.2000000000.00112233445566778899aabbccddeeff.e95c3d57c98a61c1226075ee8252d1c08ac8cdccff99f10a7de24e7207ca8625");
    }

    [Fact]
    public async Task Steam_OK_issues_a_pass_for_that_Steam_ID_signed_with_the_games_key()
    {
        var (svc, state, steam, _, _) = Make();
        var r = await svc.RequestAsync("203.0.113.5", SteamId, TicketHex, CancellationToken.None);

        r.Outcome.Should().Be(JoinPassOutcome.Issued);
        r.ExpiresInSeconds.Should().Be(120);
        steam.LastSteamId.Should().Be(SteamId);
        var parts = r.Pass!.Split('.');
        parts.Should().HaveCount(5);
        parts[0].Should().Be("1");
        parts[1].Should().Be(SteamId);
        long.Parse(parts[2]).Should().Be(new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds() + 120);
        parts[3].Should().MatchRegex("^[0-9a-f]{32}$");
        parts[4].Should().Be(JoinPassService.Mac(state.IdentityKeyCopy()!, SteamId, long.Parse(parts[2]), parts[3]));
        r.Pass.Should().MatchRegex("^[0-9a-f.]+$").And.HaveLength(r.Pass.Length).And.Match(p => p.Length <= 160);
    }

    [Fact]
    public async Task Two_passes_never_share_a_nonce()
    {
        var (svc, _, _, _, _) = Make();
        var a = await svc.RequestAsync("203.0.113.5", SteamId, TicketHex, CancellationToken.None);
        var b = await svc.RequestAsync("203.0.113.5", SteamId, TicketHex, CancellationToken.None);
        a.Pass!.Split('.')[3].Should().NotBe(b.Pass!.Split('.')[3]);
    }

    [Theory]
    [InlineData(SteamTicketOutcome.Refused, JoinPassOutcome.Refused)]
    [InlineData(SteamTicketOutcome.Unavailable, JoinPassOutcome.Unavailable)]
    public async Task Steam_refusal_or_no_answer_issues_nothing(SteamTicketOutcome steamSays, JoinPassOutcome expected)
    {
        var (svc, _, steam, _, _) = Make();
        steam.Answer = new SteamTicketResult(steamSays, "Steam: AuthTicketInvalid");
        var r = await svc.RequestAsync("203.0.113.5", SteamId, TicketHex, CancellationToken.None);
        r.Outcome.Should().Be(expected);
        r.Pass.Should().BeNull();
    }

    [Fact]
    public async Task No_key_from_the_game_yet_fails_closed_without_asking_Steam()
    {
        var (svc, _, steam, _, _) = Make(withKey: false);
        var r = await svc.RequestAsync("203.0.113.5", SteamId, TicketHex, CancellationToken.None);
        r.Outcome.Should().Be(JoinPassOutcome.Unavailable);
        steam.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(null, "ok")]
    [InlineData("7656119800000000", "ok")]          // 16 digits
    [InlineData("765611980000000011", "ok")]        // 18 digits
    [InlineData("12345678901234567", "ok")]         // not in the individual-account range
    [InlineData("7656119800000000a", "ok")]
    [InlineData(SteamId, null)]
    [InlineData(SteamId, "abc")]                     // odd length
    [InlineData(SteamId, "00")]                      // too short
    [InlineData(SteamId, "zz00000000000000000000000000000000")]
    public async Task Malformed_requests_are_refused_without_asking_Steam(string? steamId, string? ticket)
    {
        var (svc, _, steam, _, _) = Make();
        var r = await svc.RequestAsync("203.0.113.5", steamId, ticket == "ok" ? TicketHex : ticket, CancellationToken.None);
        r.Outcome.Should().Be(JoinPassOutcome.BadRequest);
        steam.Calls.Should().Be(0);
    }

    [Fact]
    public async Task One_address_gets_six_requests_a_minute()
    {
        var (svc, _, _, _, advance) = Make();
        for (var i = 0; i < 6; i++)
            (await svc.RequestAsync("203.0.113.5", SteamId, TicketHex, CancellationToken.None)).Outcome.Should().Be(JoinPassOutcome.Issued);
        (await svc.RequestAsync("203.0.113.5", SteamId, TicketHex, CancellationToken.None)).Outcome.Should().Be(JoinPassOutcome.RateLimited);
        // a malformed request counts too
        (await svc.RequestAsync("203.0.113.5", null, null, CancellationToken.None)).Outcome.Should().Be(JoinPassOutcome.RateLimited);
        (await svc.RequestAsync("203.0.113.6", SteamId, TicketHex, CancellationToken.None)).Outcome.Should().Be(JoinPassOutcome.Issued);
        advance(TimeSpan.FromSeconds(61));
        (await svc.RequestAsync("203.0.113.5", SteamId, TicketHex, CancellationToken.None)).Outcome.Should().Be(JoinPassOutcome.Issued);
    }

    [Fact]
    public async Task Everyone_together_gets_the_overall_cap_a_minute()
    {
        var (svc, _, _, _, advance) = Make(new IdentityOptions { RequestsPerMinute = 10, RequestsPerAddressPerMinute = 6 });
        for (var i = 0; i < 10; i++)
            (await svc.RequestAsync($"203.0.113.{i}", SteamId, TicketHex, CancellationToken.None)).Outcome.Should().Be(JoinPassOutcome.Issued);
        (await svc.RequestAsync("198.51.100.1", SteamId, TicketHex, CancellationToken.None)).Outcome.Should().Be(JoinPassOutcome.RateLimited);
        advance(TimeSpan.FromSeconds(61));
        (await svc.RequestAsync("198.51.100.1", SteamId, TicketHex, CancellationToken.None)).Outcome.Should().Be(JoinPassOutcome.Issued);
    }

    [Fact]
    public async Task Logs_name_the_server_and_never_the_ticket_the_pass_or_the_Steam_ID()
    {
        var (svc, _, steam, log, _) = Make();
        var ok = await svc.RequestAsync("203.0.113.5", SteamId, TicketHex, CancellationToken.None);
        steam.Answer = new SteamTicketResult(SteamTicketOutcome.Refused, "Steam: AuthTicketInvalid");
        await svc.RequestAsync("203.0.113.5", OtherSteamId, TicketHex, CancellationToken.None);
        await svc.RequestAsync("203.0.113.5", "bad", TicketHex, CancellationToken.None);

        log.Lines.Should().NotBeEmpty();
        log.Lines.Should().OnlyContain(l => l.Contains("lab-1"));
        foreach (var line in log.Lines)
        {
            line.Should().NotContain(SteamId).And.NotContain(OtherSteamId).And.NotContain(TicketHex)
                .And.NotContain(ok.Pass!).And.NotContain(ok.Pass!.Split('.')[4]).And.NotContain("203.0.113.5");
        }
    }

    [Fact]
    public void Status_is_on_only_with_the_hook_the_key_and_Steam()
    {
        var (svc, state, steam, _, _) = Make();
        svc.Status.Should().Be("on");
        steam.Ready = false;
        svc.Status.Should().Be("not_active");
        steam.Ready = true;
        state.LastIdentityCheckReady = 0;
        svc.Status.Should().Be("not_active");
        state.LastIdentityCheckReady = 1;
        state.SetIdentityKey(null);
        svc.Status.Should().Be("not_active");
    }

    [Fact]
    public void A_new_or_lost_game_connection_drops_the_old_key()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        state.SetIdentityKey(new byte[32]);
        state.LastIdentityCheckReady = 1;
        state.SetConnection(new PipeConnection("default", 4242, "test", new FrameCodec(new byte[32]), (_, _) => Task.CompletedTask));
        state.IdentityKeyCopy().Should().BeNull();
        state.LastIdentityCheckReady.Should().Be(0);
        state.SetIdentityKey(new byte[32]);
        state.SetConnection(null);
        state.IdentityKeyCopy().Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000ff")]
    [InlineData("zz00000000000000000000000000000000000000000000000000000000000000")]
    public void Only_a_32_byte_hex_key_is_accepted(string? hex)
    {
        JoinPassService.TryParseKeyHex(hex).Should().BeNull();
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        state.SetIdentityKey(new byte[16]);
        state.IdentityKeyCopy().Should().BeNull();
    }

    [Fact]
    public void Heartbeat_carries_the_identity_field_and_an_older_plugin_reads_as_not_ready()
    {
        var six = MessagePackSerializer.Serialize(new object[] { 1L, 0, 60, 0, 0, 1 });
        MessagePackSerializer.Deserialize<HeartbeatMessage>(six).IdentityCheckReady.Should().Be(1);
        var five = MessagePackSerializer.Serialize(new object[] { 1L, 0, 60, 1, 1 });
        MessagePackSerializer.Deserialize<HeartbeatMessage>(five).IdentityCheckReady.Should().Be(0);
        var key = MessagePackSerializer.Serialize(new object[] { new string('a', 64) });
        MessagePackSerializer.Deserialize<IdentityKeyMessage>(key).KeyHex.Should().Be(new string('a', 64));
    }

    [Fact]
    public void The_game_Steam_library_is_found_in_the_highest_Steamworks_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-steamapi-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "BeaconServer");
        try
        {
            Directory.CreateDirectory(bin);
            foreach (var v in new[] { "Steamv153", "Steamv161", "Steamv99" })
            {
                var d = Path.Combine(root, "Engine", "Binaries", "ThirdParty", "Steamworks", v, "Win64");
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "steam_api64.dll"), "x");
            }
            var empty = Path.Combine(root, "Engine", "Binaries", "ThirdParty", "Steamworks", "Steamv170", "Win64");
            Directory.CreateDirectory(empty); // a folder without the library is skipped

            var found = SteamGameServerTicketVerifier.FindSteamApi(new BeaconServerOptions { SnInstallRoot = "" }, bin);
            found.Should().Be(Path.GetFullPath(Path.Combine(root, "Engine", "Binaries", "ThirdParty", "Steamworks", "Steamv161", "Win64", "steam_api64.dll")));

            SteamGameServerTicketVerifier.FindSteamApi(new BeaconServerOptions { SnInstallRoot = "" }, Path.Combine(root, "nowhere", "deeper"))
                .Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

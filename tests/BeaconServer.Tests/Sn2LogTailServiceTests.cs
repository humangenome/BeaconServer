using BeaconServer.Services;
using BeaconServer.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BeaconServer.Tests;

public class Sn2LogTailServiceTests
{
    [Theory]
    [InlineData("server-37FB85D64BD7D9149C078BB47E84A17E")]
    [InlineData("server-afe38f523f13079aa3f196e0e8053834")]
    [InlineData("ns123-0123456789ABCDEF")]
    [InlineData("WIN-SERVER-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("SERVER-0123456789ABCDEF0123456789ABCDEF")]
    public void IsGeneratedPlayerName_recognizes_listen_host_names(string name)
    {
        Sn2LogTailService.IsGeneratedPlayerName(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("PlayerOne")]
    [InlineData("server-admin")]
    [InlineData("server-1234")]
    [InlineData("nsplayer")]
    [InlineData("friendly-name-0123456789ABCDEF")]
    public void IsGeneratedPlayerName_keeps_normal_player_names(string name)
    {
        Sn2LogTailService.IsGeneratedPlayerName(name).Should().BeFalse();
    }

    [Theory]
    [InlineData("[2026.05.22-19.54.11:411][123]LogNet: NotifyAcceptingConnection accepted from: 1.2.3.4:5555", "1.2.3.4:5555")]
    [InlineData("[2026.05.22-19.54.11:411][123]LogNet: Server accepting post-challenge connection from: 1.2.3.4:5555", "1.2.3.4:5555")]
    public void TryExtractAcceptedAddress_handles_current_sn2_join_lines(string line, string expected)
    {
        Sn2LogTailService.TryExtractAcceptedAddress(line, out var address).Should().BeTrue();
        address.Should().Be(expected);
    }

    [Theory]
    [InlineData("[2026.05.22-20.00.12:003][321]LogNet: UChannel::Close: ChIndex == 0. Closing connection. RemoteAddr: 1.2.3.4:5555", "1.2.3.4:5555")]
    [InlineData("[2026.05.22-20.00.12:003][321]LogNet: UChannel::CleanUp: ChIndex == 0. Closing connection. RemoteAddr: 1.2.3.4:5555", "1.2.3.4:5555")]
    [InlineData("[2026.05.22-20.00.12:003][321]LogNet: UNetConnection::Tick: Connection TIMED OUT. RemoteAddr: 1.2.3.4:5555", "1.2.3.4:5555")]
    [InlineData("[2026.05.22-20.00.12:003][321]LogNet: UNetDriver::RemoveClientConnection - Removed address 1.2.3.4:5555", "1.2.3.4:5555")]
    public void TryExtractLeaveAddress_handles_current_sn2_disconnect_lines(string line, string expected)
    {
        Sn2LogTailService.TryExtractLeaveAddress(line, out var address).Should().BeTrue();
        address.Should().Be(expected);
    }

    [Fact]
    public async Task Tailer_ignores_existing_join_lines_then_counts_new_live_join()
    {
        var root = Path.Combine(Path.GetTempPath(), "beacon-sn2-tail-" + Guid.NewGuid().ToString("N"));
        var logDir = Path.Combine(root, "Saved", "Logs");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "Subnautica2.log");
        await File.WriteAllTextAsync(logPath, string.Join(Environment.NewLine, new[]
        {
            "[2026.05.22-20.35.23:038][679]LogNet: NotifyAcceptingConnection accepted from: 1.2.3.4:1111",
            "[2026.05.22-20.35.23:271][686]LogNet: Login request: ?Name=OldPlayer??SonarPlayerId=76561197966093888_OLD?PlatformUserId=76561197966093888?PlatformProvider=STEAM userId: NULL:RYZEN platform: NULL",
            "[2026.05.22-20.35.24:094][710]LogNet: Join succeeded: OldPlayer",
            ""
        }));

        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        var service = new Sn2LogTailService(
            NullLogger<Sn2LogTailService>.Instance,
            Options.Create(new BeaconServerOptions { SnUserDir = root }),
            state);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(250);
            state.EffectivePlayerCount.Should().Be(0);

            await File.AppendAllTextAsync(logPath, string.Join(Environment.NewLine, new[]
            {
                "[2026.05.22-21.00.35:371][265]LogNet: NotifyAcceptingConnection accepted from: 5.6.7.8:2222",
                "[2026.05.22-21.00.35:472][268]LogNet: Server accepting post-challenge connection from: 5.6.7.8:2222",
                "[2026.05.22-21.00.35:672][274]LogNet: Login request: ?Name=HumanGenome??SonarPlayerId=76561197966093888_A4E6F67EAD6BCF60?PlatformUserId=76561197966093888?PlatformProvider=STEAM userId: NULL:RYZEN platform: NULL",
                "[2026.05.22-21.00.36:662][303]LogNet: Join succeeded: HumanGenome",
                ""
            }));

            await WaitUntilAsync(() => state.EffectivePlayerCount == 1, TimeSpan.FromSeconds(5));
            state.Players.Single().DisplayName.Should().Be("HumanGenome");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("[2026.10.07-03.35.48:343][ 42]LogNet: Login request: ?Name=bert?Password=hunter2??SonarPlayerId=1_A?PlatformUserId=1",
                "[2026.10.07-03.35.48:343][ 42]LogNet: Login request: ?Name=bert?Password=<redacted>??SonarPlayerId=1_A?PlatformUserId=1")]
    [InlineData("LogNet: Join request: /Game/Maps/Main/L_Main?Name=a b?password=x y?Sonar=1",
                "LogNet: Join request: /Game/Maps/Main/L_Main?Name=a b?password=<redacted>?Sonar=1")]
    [InlineData("travel ?ServerPassword=abc&x=1", "travel ?ServerPassword=<redacted>&x=1")]
    [InlineData("LogNet: Login request: ?Name=bert??SonarPlayerId=1_A", "LogNet: Login request: ?Name=bert??SonarPlayerId=1_A")]
    public void RedactSecrets_masks_password_options(string line, string expected)
    {
        Sn2LogTailService.RedactSecrets(line).Should().Be(expected);
    }

    [Fact]
    public void TryParseLoginRequest_keeps_display_names_with_spaces()
    {
        const string line = "[2026.10.07-03.51.43:381][288]LogNet: Login request: ?Name=The God-Emperor of Mankind?Password=<redacted>??SonarPlayerId=76561198328204762_303327E1D3F41A8F?PlatformUserId=76561198328204762?PlatformProvider=STEAM userId: NULL";

        Sn2LogTailService.TryParseLoginRequest(line, out var name, out var sonar).Should().BeTrue();
        name.Should().Be("The God-Emperor of Mankind");
        sonar.Should().Be("76561198328204762_303327E1D3F41A8F");
    }

    [Fact]
    public void TryParseLoginRequest_still_reads_single_word_names()
    {
        const string line = "[2026.05.22-21.00.35:672][274]LogNet: Login request: ?Name=HumanGenome??SonarPlayerId=76561197966093888_A4E6F67EAD6BCF60?PlatformUserId=76561197966093888?PlatformProvider=STEAM userId: NULL:RYZEN platform: NULL";

        Sn2LogTailService.TryParseLoginRequest(line, out var name, out var sonar).Should().BeTrue();
        name.Should().Be("HumanGenome");
        sonar.Should().Be("76561197966093888_A4E6F67EAD6BCF60");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }

        condition().Should().BeTrue();
    }
}

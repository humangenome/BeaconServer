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

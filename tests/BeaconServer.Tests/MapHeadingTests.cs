using System.Net;
using System.Text;
using System.Text.Json;
using Beacon.Protocol;
using BeaconServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeaconServer.Tests;

/// <summary>
/// The live map's heading (which way a player looks): every player row of
/// GET /api/v1/map/state carries "heading", a whole degree from 0 to 359 or
/// null when it is not known (a player with no app position, so no body to
/// read; an app from before heading; a value that is no number). The app sends
/// it with its position; a body without it is still taken.
/// </summary>
[Collection("ChatFiles")]
public class MapHeadingTests
{
    private static JsonElement[] Rows(object[] rows) =>
        JsonDocument.Parse(JsonSerializer.Serialize(rows)).RootElement.EnumerateArray().ToArray();

    [Fact]
    public void Every_row_carries_heading_and_a_player_without_an_app_position_has_null()
    {
        var positions = new[]
        {
            new PipeServerState.ClientPosition("765_a", "Alice", 10, 20, 30, 1, 90),
            new PipeServerState.ClientPosition("765_b", "Bo", 11, 21, 31, 1),
        };
        var roster = new[]
        {
            new PlayerSnapshot("roster:alice", "Alice", 1000, 2000, 0),
            new PlayerSnapshot("roster:lee", "Lee", 1000, 2000, 0),
        };

        var rows = Rows(BeaconHttpService.MergeMapPlayers(positions, roster));

        rows.Should().HaveCount(3);
        rows.Select(r => r.TryGetProperty("heading", out var _)).Should().AllBeEquivalentTo(true, "the field is always present");
        var byName = rows.ToDictionary(r => r.GetProperty("name").GetString()!);
        byName["Alice"].GetProperty("heading").GetInt32().Should().Be(90);
        byName["Bo"].GetProperty("heading").ValueKind.Should().Be(JsonValueKind.Null, "an app that sent no heading");
        byName["Lee"].GetProperty("heading").ValueKind.Should().Be(JsonValueKind.Null, "the roster has no body to read");
        byName["Lee"].GetProperty("x").GetDouble().Should().Be(0);
        byName["Alice"].GetProperty("biome").GetString().Should().Be("", "the existing fields are unchanged");
    }

    [Theory]
    [InlineData(90.4, 90)]
    [InlineData(-90.0, 270)]
    [InlineData(359.6, 0)]
    [InlineData(450.6, 91)]
    public void A_stored_heading_is_a_whole_degree_from_0_to_359(double sent, int stored)
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        state.UpsertClientPosition("765_a", "Alice", 1, 2, 3, sent);
        state.RecentClientPositions(TimeSpan.FromMinutes(1)).Single().Heading.Should().Be(stored);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_heading_that_is_no_number_is_dropped_and_the_position_kept(double sent)
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        state.UpsertClientPosition("765_a", "Alice", 1, 2, 3, sent);
        var p = state.RecentClientPositions(TimeSpan.FromMinutes(1)).Single();
        p.Heading.Should().BeNull();
        p.X.Should().Be(1);
    }

    [Fact]
    public void A_position_without_heading_is_stored_with_null()
    {
        var state = new PipeServerState(NullLogger<PipeServerState>.Instance);
        state.UpsertClientPosition("765_a", "Alice", 1, 2, 3);
        state.RecentClientPositions(TimeSpan.FromMinutes(1)).Single().Heading.Should().BeNull();
    }

    [Fact]
    public async Task The_heading_an_app_posts_reaches_the_map_state()
    {
        await using var h = new HttpHardeningTests.Harness();
        await h.StartAsync();
        using var http = new HttpClient();
        var post = $"http://127.0.0.1:{h.Port}/api/v1/map/position";
        h.State.NotePeer("127.0.0.1:5555", "765_a", "Alice");

        async Task<JsonElement> Player()
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{h.Port}/api/v1/map/state");
            req.Headers.Add("X-Beacon-Map-Key", "k1");
            var res = await http.SendAsync(req);
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("players").EnumerateArray().Single().Clone();
        }
        async Task<HttpStatusCode> Post(string body) =>
            (await http.PostAsync(post, new StringContent(body, Encoding.UTF8, "application/json"))).StatusCode;

        (await Post("{\"id\":\"x\",\"name\":\"x\",\"x\":1,\"y\":2,\"z\":3,\"heading\":450.6}")).Should().Be(HttpStatusCode.OK);
        var p = await Player();
        p.GetProperty("heading").GetInt32().Should().Be(91);
        p.GetProperty("name").GetString().Should().Be("Alice");
        p.GetProperty("x").GetDouble().Should().Be(1);

        (await Post("{\"id\":\"x\",\"name\":\"x\",\"x\":4,\"y\":5,\"z\":6,\"heading\":-90}")).Should().Be(HttpStatusCode.OK);
        (await Player()).GetProperty("heading").GetInt32().Should().Be(270);

        (await Post("{\"id\":\"x\",\"name\":\"x\",\"x\":7,\"y\":8,\"z\":9,\"heading\":null}")).Should().Be(HttpStatusCode.OK);
        p = await Player();
        p.GetProperty("heading").ValueKind.Should().Be(JsonValueKind.Null);
        p.GetProperty("x").GetDouble().Should().Be(7);

        // An app from before heading: the same body as always is still taken, with heading null.
        (await Post("{\"id\":\"x\",\"name\":\"x\",\"x\":10,\"y\":11,\"z\":12}")).Should().Be(HttpStatusCode.OK);
        p = await Player();
        p.GetProperty("heading").ValueKind.Should().Be(JsonValueKind.Null);
        p.GetProperty("x").GetDouble().Should().Be(10);

        // A heading that is not a JSON number is refused like a bad x; the last position stays.
        (await Post("{\"id\":\"x\",\"name\":\"x\",\"x\":13,\"y\":14,\"z\":15,\"heading\":\"north\"}")).Should().Be(HttpStatusCode.BadRequest);
        (await Player()).GetProperty("x").GetDouble().Should().Be(10);
    }

    [Fact]
    public async Task With_no_app_position_the_map_state_lists_the_player_with_heading_null()
    {
        await using var h = new HttpHardeningTests.Harness();
        await h.StartAsync();
        h.State.SetPlayers(new[] { new PlayerSnapshot("roster:lee", "Lee", 1000, 2000, 0) });
        using var http = new HttpClient();
        var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{h.Port}/api/v1/map/state");
        req.Headers.Add("X-Beacon-Map-Key", "k1");
        var res = await http.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var p = doc.RootElement.GetProperty("players").EnumerateArray().Single();
        p.GetProperty("name").GetString().Should().Be("Lee");
        p.TryGetProperty("heading", out var heading).Should().BeTrue();
        heading.ValueKind.Should().Be(JsonValueKind.Null);
    }
}

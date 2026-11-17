using Beacon.Protocol;
using FluentAssertions;
using Xunit;

namespace Beacon.Protocol.Tests;

// The live map's heading: any yaw becomes a whole degree from 0 to 359, the
// nearest one; nothing usable becomes null. The client mod (Lua) rounds the
// same way and its spec checks the same edge values.
public class MapHeadingTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(360.0, 0)]
    [InlineData(720.0, 0)]
    [InlineData(-90.0, 270)]
    [InlineData(-180.0, 180)]
    [InlineData(180.0, 180)]
    [InlineData(180.5, 181)]
    [InlineData(359.49, 359)]
    [InlineData(359.5, 0)]
    [InlineData(359.99, 0)]
    [InlineData(-0.4, 0)]
    [InlineData(-0.5, 0)]
    [InlineData(-0.6, 359)]
    [InlineData(1e9, 280)]
    [InlineData(-1e9, 80)]
    [InlineData(45.0, 45)]
    [InlineData(-1e-20, 0)]
    public void A_yaw_becomes_the_nearest_whole_degree_from_0_to_359(double yaw, int expected)
    {
        MapHeading.Normalize(yaw).Should().Be(expected);
    }

    [Fact]
    public void Every_yaw_lands_in_range_within_half_a_degree()
    {
        for (var i = -2000; i <= 2000; i++)
        {
            var yaw = i * 0.73;
            var h = MapHeading.Normalize(yaw);
            h.Should().NotBeNull();
            h!.Value.Should().BeInRange(0, 359, "yaw {0}", yaw);
            var diff = Math.Abs(((yaw - h.Value) % 360 + 540) % 360 - 180);
            diff.Should().BeLessThanOrEqualTo(0.5 + 1e-9, "yaw {0} gave {1}", yaw, h.Value);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Nothing_usable_is_null(double yaw)
    {
        MapHeading.Normalize(yaw).Should().BeNull();
    }

    [Fact]
    public void No_value_is_null()
    {
        MapHeading.Normalize(null).Should().BeNull();
    }
}

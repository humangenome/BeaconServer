using FluentAssertions;
using Xunit;

namespace Beacon.Integration.Tests;

public class PlaceholderTests
{
    [Fact]
    public void Placeholder_until_phase_1() => true.Should().BeTrue();
}

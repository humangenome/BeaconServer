using Beacon.Protocol;
using FluentAssertions;
using Xunit;

namespace Beacon.Protocol.Tests;

public class ProtocolVersionTests
{
    [Fact]
    public void Version_constants_are_sane()
    {
        ProtocolVersion.Major.Should().BeGreaterOrEqualTo(0);
        ProtocolVersion.Minor.Should().BeGreaterOrEqualTo(0);
        ProtocolVersion.Patch.Should().BeGreaterOrEqualTo(0);
    }
}

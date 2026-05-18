using BeaconServer.Configuration;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

public sealed class InstanceIdentityProvider
{
    private readonly BeaconServerOptions _options;

    public InstanceIdentityProvider(IOptions<BeaconServerOptions> options)
    {
        _options = options.Value;
    }

    public string InstanceId => _options.InstanceId;

    // On Linux, .NET maps pipe names to Unix domain sockets at /tmp/CoreFxPipe_{name}.
    // A slash in the name becomes a path separator, making the socket path invalid.
    public string PipeName => OperatingSystem.IsWindows()
            ? _options.PipeName
            : _options.PipeName.Replace('/', '.');

    public int GameplayPort => _options.GameplayPort;
    public int BeaconControlPort => _options.BeaconControlPort;
    public int QueryPort => _options.QueryPort;
    public int RconPort => _options.RconPort;
}

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

    public string PipeName => _options.PipeName;

    public int GameplayPort => _options.GameplayPort;
    public int BeaconControlPort => _options.BeaconControlPort;
    public int QueryPort => _options.QueryPort;
    public int RconPort => _options.RconPort;
}

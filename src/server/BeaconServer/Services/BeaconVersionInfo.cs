using System.Reflection;

namespace BeaconServer.Services;

/// <summary>
/// Single source of truth for version strings surfaced to operators, A2S
/// queries, and the startup banner. Beacon's own version comes from the
/// assembly; SN2's build number is read at runtime from the host's UE log.
/// </summary>
public static class BeaconVersionInfo
{
    public static string BeaconVersion { get; } = ResolveBeaconVersion();

    public static string Sn2Build { get; private set; } = "unknown";

    /// <summary>
    /// Called by <see cref="Sn2VersionProbeService"/> once it parses the
    /// host log. Subsequent A2S query responses + banner refreshes include it.
    /// </summary>
    public static void SetSn2Build(string build)
    {
        if (!string.IsNullOrWhiteSpace(build)) Sn2Build = build.Trim();
    }

    private static string ResolveBeaconVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip "+commitsha" suffix MSBuild appends in default release builds.
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}

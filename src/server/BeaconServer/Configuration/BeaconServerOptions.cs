namespace BeaconServer.Configuration;

public sealed class BeaconServerOptions
{
    public string InstanceId { get; set; } = "default";

    public string PipeName { get; set; } = @"Beacon\default";

    /// <summary>Path to a 64-hex-character HMAC key file, generated per instance.</summary>
    public string HmacKeyPath { get; set; } = "data/hmac.key";

    public int GameplayPort { get; set; } = 27015;

    public int BeaconControlPort { get; set; } = 27016;

    public int QueryPort { get; set; } = 27017;

    public int RconPort { get; set; } = 27018;

    /// <summary>
    /// HTTP API port. Used by the launcher to list/upload/download/restore
    /// snapshots and to fetch instance metadata. Auth is per-instance HMAC,
    /// same key as the named-pipe IPC.
    /// </summary>
    public int HttpPort { get; set; } = 27019;

    /// <summary>
    /// Maximum upload size for snapshot import / restore. SN2 worlds at
    /// 100% exploration are typically &lt; 200 MB; cap at 2 GB by default to
    /// reject malformed or hostile uploads without exhausting RAM.
    /// </summary>
    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public string RconPassword { get; set; } = "";

    public string SnInstallRoot { get; set; } = @"C:\Beacon\game";

    public string SnUserDir { get; set; } = @"C:\Beacon\userdir";

    public string SaveDir { get; set; } = @"C:\Beacon\saves";

    /// <summary>Heartbeat window — if plugin doesn't ping in this many seconds, assume crashed.</summary>
    public int PluginHeartbeatTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Slot count reported via A2S. SN2's session UI labels lobbies as 1/4, so
    /// 4 is the natural default — anything higher and the in-game host UI
    /// reports a slot count that doesn't match what BeaconServer hands out.
    /// </summary>
    public int MaxPlayers { get; set; } = 4;

    /// <summary>
    /// Extra arguments appended to the SN2 launch command line.
    /// Use "-nullrhi" on headless/Wine hosts to disable GPU init.
    /// </summary>
    public string ExtraGameArgs { get; set; } = "";
}

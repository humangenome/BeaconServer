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

    /// <summary>
    /// LEGACY field — IGNORED as of v0.3.55. Was the password handed to
    /// SN2's native handshake + the native Beacon.dll ApproveLogin hook.
    /// SN2's stock GameMode fatal-crashes the dedicated process when a
    /// password is set on the native path (the ?Password= option in the
    /// launch URL is dropped by SN2's internal map travel, the native
    /// check sees a mismatch, and the process self-terminates with
    /// "appError: Couldn't spawn player"). v0.3.55 routes password
    /// enforcement through BeaconAuth.lua only (see
    /// <see cref="BeaconAuthPassword"/>). Lua, A2S, and native (BeaconPlugin.cpp)
    /// all ignore this field in v0.3.55+. Field is kept for binary
    /// appsettings.json compatibility only; do NOT set it.
    /// </summary>
    public string ServerPassword { get; set; } = "";

    /// <summary>
    /// Password enforced by BeaconAuth.lua's K2_PostLogin hook for
    /// incoming remote clients. Written by the panel into appsettings.json.
    /// Empty string means open server (no password gate). The listen-server
    /// host is positively detected and exempt from the gate. Used by
    /// SourceQueryHostedService for the A2S PasswordRequired flag so the
    /// Steam server browser correctly advertises passworded instances.
    /// </summary>
    public string BeaconAuthPassword { get; set; } = "";

    public string ServerName { get; set; } = "";

    /// <summary>
    /// Admin identities for mod command dispatch (ModKit Pillar 4). An entry
    /// matches a caller by SteamID64, BeaconUserId, or display name
    /// (case-insensitive). Admin status is NOT in the roster — it lives here
    /// and is surfaced to mods as <c>ctx.caller.is_admin</c> on dispatched
    /// commands (see protocol/modkit-v1.md). RCON callers are always admin
    /// (the RCON password is the gate); in-game chat callers are matched
    /// against this list by sender name. Empty list means no chat caller is
    /// admin.
    /// </summary>
    public List<string> Admins { get; set; } = new();

    public string SnInstallRoot { get; set; } = @"C:\Beacon\game";

    /// <summary>
    /// Optional direct path to the Subnautica 2 executable. Leave empty to
    /// auto-detect supported Steam/Epic Win64 and Xbox WinGDK layouts under
    /// <see cref="SnInstallRoot"/>.
    /// </summary>
    public string SnExecutablePath { get; set; } = "";

    public string SnUserDir { get; set; } = @"C:\Beacon\userdir";

    public string SaveDir { get; set; } = @"C:\Beacon\saves";

    /// <summary>
    /// Optional path to a single-line file containing the PID of the SN2
    /// process this BeaconServer instance owns. Used by KillSn2 as a
    /// fallback when neither <see cref="SnInstallRoot"/> nor a command-line
    /// scope exists (e.g. panel-managed deploys where the panel's
    /// PowerShell owns SN2's lifecycle and writes Logs\sn2.pid). The pid
    /// is always validated against the SN2 process-name whitelist before
    /// kill — a recycled OS PID owned by an unrelated process will not
    /// be touched. Empty disables the pid-file kill path.
    /// </summary>
    public string Sn2PidFile { get; set; } = "";

    /// <summary>Heartbeat window — if plugin doesn't ping in this many seconds, assume crashed.</summary>
    public int PluginHeartbeatTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Slot count reported via A2S. SN2's session UI labels lobbies as 1/4, so
    /// 4 is the natural default — anything higher and the in-game host UI
    /// reports a slot count that doesn't match what BeaconServer hands out.
    /// </summary>
    public int MaxPlayers { get; set; } = 4;

    /// <summary>
    /// Take a snapshot zip of the SaveGames dir into <see cref="SaveDir"/>
    /// on every auto-save (via FileSystemWatcher). Useful for self-hosters
    /// who want a rollback option. Hosting providers (e.g. SurvivalServers)
    /// run their own backup chain and should set this to false to avoid
    /// disk churn — a 1-per-minute snapshot rate produces ~1500 zips and
    /// ~1 GB per gameserver per day.
    /// </summary>
    public bool SnapshotsEnabled { get; set; } = true;

    /// <summary>Chat plane configuration. See protocol/chat-v1.md.</summary>
    public ChatOptions Chat { get; set; } = new();

    /// <summary>
    /// Mod manifest published at GET /api/v1/manifest. Hosters list the mods
    /// they require, recommend, or forbid; the launcher reads this on join
    /// and installs/offers/warns accordingly. Spec: protocol/manifest-v1.md.
    /// </summary>
    public ModsOptions Mods { get; set; } = new();

    /// <summary>Live web map. See protocol/map-v1.md.</summary>
    public MapOptions Map { get; set; } = new();
}

public sealed class ChatOptions
{
    /// <summary>
    /// Whether in-game chat is enabled for this server. When false, the chat
    /// overlay (beacon-hud) is dropped from the published manifest so joining
    /// clients never install or load it — chat is off server-wide. Read fresh
    /// per manifest request, so editing appsettings.json applies without a
    /// restart. Default true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>In-memory ring buffer cap for /api/v1/chat/recent. Min 20, default 200.</summary>
    public int RingBufferSize { get; set; } = 200;
}

public sealed class ModsOptions
{
    public List<ModEntry> Required { get; set; } = new();
    public List<ModEntry> Recommended { get; set; } = new();
    public List<BlockedModEntry> Blocked { get; set; } = new();
}

public sealed class ModEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Sha256 { get; set; }
    public long? SizeBytes { get; set; }
    public string InstallRoot { get; set; } = "";
    public string? Notes { get; set; }
}

public sealed class BlockedModEntry
{
    public string Id { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class MapOptions
{
    /// <summary>Master toggle. When false, /api/v1/map/state returns 404.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When true, /api/v1/map/state and the /map/ SPA are reachable without
    /// HMAC auth. Use for community-facing servers that want to publish a
    /// live map dashboard. Default false — server admin/launcher only.
    /// </summary>
    public bool Public { get; set; } = false;

    /// <summary>
    /// Cap on cached state age (ms). If roster.json is older than this,
    /// /api/v1/map/state still serves it but flags stale=true so the SPA
    /// can dim or warn. Default 10000 (10s).
    /// </summary>
    public int StaleAfterMs { get; set; } = 10000;
}

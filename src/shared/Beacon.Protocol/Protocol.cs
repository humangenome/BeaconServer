using MessagePack;

namespace Beacon.Protocol;

/// <summary>
/// Wire protocol between Beacon.dll (UE4SS plugin loaded into the Subnautica 2
/// process) and BeaconServer.exe (.NET 8 supervisor). Bumped on any
/// breaking schema change.
/// </summary>
public static class ProtocolVersion
{
    public const int Major = 0;
    public const int Minor = 1;
    public const int Patch = 0;

    public static string Display => $"{Major}.{Minor}.{Patch}";
}

/// <summary>
/// Frame envelope layout:
///   [u32 length][u8 type][u8 flags][u32 seq][payload...][32 bytes HMAC-SHA256]
/// length covers everything after itself.
/// payload is MessagePack — varies by type.
/// </summary>
public enum FrameType : byte
{
    Handshake = 1,
    HandshakeAck = 2,
    Heartbeat = 3,
    LogForward = 4,
    PlayerJoined = 10,
    PlayerLeft = 11,
    PlayerListSnapshot = 12,
    JoinTicketRequest = 20,
    JoinTicketResponse = 21,
    RconCommand = 30,
    RconResult = 31,
    BroadcastChat = 32,
    SaveQuiesce = 40,
    SaveSnapshotComplete = 41,
    ShutdownRequest = 50,
    Goodbye = 51,

    // Mod-host plugin API (Phase 3+). Beacon supports two mod surfaces:
    //   1. Reserved: server-side native mods loaded by Beacon.dll inside the game
    //      process. They observe game events (player join, chat, world tick)
    //      and can call back into UE5 via UE4SS reflection.
    //   2. Server-side scripted (Lua) mods, also loaded by Beacon.dll,
    //      sandboxed via sol2/LuaJIT. Limited surface: game events + a
    //      curated UE5 helper API.
    //   3. Client-side mods, loaded by Beacon into the customer's Subnautica 2
    //      via a Beacon-installed UE4SS dropper. The server tells the client
    //      via ModManifest which mods to load — the client refuses to join
    //      otherwise (hash-pinned manifest).
    ModLoad = 60,                  // server -> plugin: load a mod by id+sha256
    ModUnload = 61,                // server -> plugin: unload a mod by id
    ModEvent = 62,                 // plugin -> server: a mod fired an event
    ModManifestPublish = 63,       // server -> plugin: full server-side mod list (for replication)
    ModManifestQuery = 64,         // client launcher -> server: tell me what mods I need to install
    ModManifestResponse = 65,      // server -> client launcher: here is the required mod set + hashes
}

[Flags]
public enum FrameFlags : byte
{
    None = 0,
    RequiresAck = 1,
    IsAck = 2,
}

[MessagePackObject]
public record HandshakeMessage(
    [property: Key(0)] int ProtocolMajor,
    [property: Key(1)] int ProtocolMinor,
    [property: Key(2)] int ProtocolPatch,
    [property: Key(3)] string InstanceId,
    [property: Key(4)] string PluginVersion,
    [property: Key(5)] int Pid);

[MessagePackObject]
public record HandshakeAckMessage(
    [property: Key(0)] bool Accepted,
    [property: Key(1)] string? Reason,
    [property: Key(2)] int ServerProtocolMajor,
    [property: Key(3)] int ServerProtocolMinor);

[MessagePackObject]
public record HeartbeatMessage(
    [property: Key(0)] long UnixMillis,
    [property: Key(1)] int InGamePlayerCount,
    [property: Key(2)] int WorldTickRate,
    // 1 = plugin sees ServerPassword non-empty and is enforcing it,
    // 0 = either no password set (open server) OR legacy 3-field heartbeat
    //     from a plugin that doesn't report auth state. The watchdog only
    //     acts when this is 1 AND the next field is 0.
    [property: Key(3)] int ServerPasswordConfigured = 0,
    // 1 = native ApproveLogin hook installed and ready to gate joins,
    // 0 = native hook failed to install OR legacy 3-field heartbeat.
    // Server fails closed (stop Subnautica 2) when Configured=1 && HookReady=0.
    [property: Key(4)] int ServerPasswordHookReady = 0);

[MessagePackObject]
public record LogForwardMessage(
    [property: Key(0)] long UnixMillis,
    [property: Key(1)] string Level,
    [property: Key(2)] string Source,
    [property: Key(3)] string Message);

[MessagePackObject]
public record PlayerJoinedMessage(
    [property: Key(0)] string BeaconUserId,
    [property: Key(1)] string DisplayName,
    [property: Key(2)] string ClientAddress,
    [property: Key(3)] string JoinTicketId);

[MessagePackObject]
public record PlayerLeftMessage(
    [property: Key(0)] string BeaconUserId,
    [property: Key(1)] string Reason);

[MessagePackObject]
public record PlayerListSnapshotMessage(
    [property: Key(0)] List<PlayerSnapshot> Players);

[MessagePackObject]
public record PlayerSnapshot(
    [property: Key(0)] string BeaconUserId,
    [property: Key(1)] string DisplayName,
    [property: Key(2)] long ConnectedAtUnixMs,
    [property: Key(3)] long LastPacketUnixMs,
    [property: Key(4)] int PingMs);

[MessagePackObject]
public record JoinTicketRequestMessage(
    [property: Key(0)] string ClientAddress,
    [property: Key(1)] string OfferedTicketId);

[MessagePackObject]
public record JoinTicketResponseMessage(
    [property: Key(0)] bool Accepted,
    [property: Key(1)] string? BeaconUserId,
    [property: Key(2)] string? DisplayName,
    [property: Key(3)] string? RejectionReason);

[MessagePackObject]
public record RconCommandMessage(
    [property: Key(0)] string RequestId,
    [property: Key(1)] string Command);

[MessagePackObject]
public record RconResultMessage(
    [property: Key(0)] string RequestId,
    [property: Key(1)] bool Success,
    [property: Key(2)] string Output);

[MessagePackObject]
public record BroadcastChatMessage(
    [property: Key(0)] string From,
    [property: Key(1)] string Text);

[MessagePackObject]
public record SaveQuiesceMessage(
    [property: Key(0)] string SnapshotId,
    [property: Key(1)] int TimeoutSeconds);

[MessagePackObject]
public record SaveSnapshotCompleteMessage(
    [property: Key(0)] string SnapshotId,
    [property: Key(1)] bool Success,
    [property: Key(2)] string? Error);

[MessagePackObject]
public record ShutdownRequestMessage(
    [property: Key(0)] string Reason,
    [property: Key(1)] int GraceSeconds);

[MessagePackObject]
public record GoodbyeMessage(
    [property: Key(0)] string Reason);

// ── Mod plugin API ──────────────────────────────────────────────────────

/// <summary>Mod surface — what kind of mod runtime hosts this artifact.</summary>
public enum ModRuntime : byte
{
    /// <summary>Reserved server-side native mod surface.</summary>
    ServerNative = 1,
    /// <summary>Server-side Lua mod. .lua loaded into Beacon's sol2 sandbox.</summary>
    ServerLua = 2,
    /// <summary>Client-side mod installed by Beacon into the customer's Subnautica 2 install.</summary>
    Client = 3,
}

[MessagePackObject]
public record ModDescriptor(
    [property: Key(0)] string ModId,             // stable id, e.g. "humangenome.spawnermenu"
    [property: Key(1)] string Version,           // semver
    [property: Key(2)] ModRuntime Runtime,
    [property: Key(3)] string Sha256,            // artifact integrity hash
    [property: Key(4)] string ArtifactUrl,       // server-side path or HTTPS url
    [property: Key(5)] bool RequiredOnClient);   // join enforced — clients without it are rejected

[MessagePackObject]
public record ModLoadMessage(
    [property: Key(0)] ModDescriptor Mod);

[MessagePackObject]
public record ModUnloadMessage(
    [property: Key(0)] string ModId);

[MessagePackObject]
public record ModEventMessage(
    [property: Key(0)] string ModId,
    [property: Key(1)] string EventName,         // e.g. "player.chat", "world.tick", "player.died"
    [property: Key(2)] string PayloadJson);       // mod-defined event data

[MessagePackObject]
public record ModManifestPublishMessage(
    [property: Key(0)] List<ModDescriptor> Mods);

[MessagePackObject]
public record ModManifestQueryMessage(
    [property: Key(0)] string LauncherVersion);

[MessagePackObject]
public record ModManifestResponseMessage(
    [property: Key(0)] List<ModDescriptor> RequiredClientMods,
    [property: Key(1)] string ServerName,
    [property: Key(2)] string Map,
    [property: Key(3)] int Players,
    [property: Key(4)] int MaxPlayers);

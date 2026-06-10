using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeaconServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

/// <summary>
/// ModKit Pillar 4 — reverse command dispatch (see protocol/modkit-v1.md).
///
/// The Lua side (BeaconModKit) is the producer/dispatcher:
///   - WRITES commands.json (the registry of mod-registered slash commands).
///   - POLLS command-queue.json every 250ms, dispatches each entry, WRITES
///     the per-id reply to command-replies/&lt;id&gt;.json, then OVERWRITES
///     command-queue.json with an empty queue to mark it drained.
///
/// This service is the BeaconServer-side consumer:
///   - Polls commands.json into an in-memory registry of {name -> admin_only}
///     so callers can be routed (mod command vs built-in) without a round trip.
///   - <see cref="DispatchAsync"/> appends a queue entry (atomic read-modify-
///     write, serialized so concurrent BeaconServer writers don't lose updates),
///     then waits for the reply file and returns the mod's reply.
///
/// File-IPC only — same transport ChatService uses (chat-outbound/-inbound).
/// </summary>
public sealed class CommandDispatchService : BackgroundService
{
    private readonly ILogger<CommandDispatchService> _log;
    private readonly IOptionsMonitor<BeaconServerOptions> _opts;

    private readonly string _commandsPath;
    private readonly string _queuePath;
    private readonly string _repliesDir;

    // Registry of mod-registered commands: name (lowercase) -> admin_only.
    // Replaced wholesale on each commands.json refresh.
    private volatile IReadOnlyDictionary<string, bool> _registry =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    private long _lastCommandsSize = -1;
    private DateTimeOffset _lastCommandsRead = DateTimeOffset.MinValue;

    // Serializes the queue read-modify-write so two concurrent DispatchAsync
    // callers (e.g. an RCON command and an in-game slash at the same instant)
    // can't read-then-clobber each other's append.
    private readonly SemaphoreSlim _queueLock = new(1, 1);

    // How long to wait for ModKit to write the reply file. ModKit polls the
    // queue every 250ms, so a reply normally lands within ~250-500ms; 2s gives
    // generous headroom for a busy game thread.
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReplyPollInterval = TimeSpan.FromMilliseconds(50);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public CommandDispatchService(ILogger<CommandDispatchService> log, IOptionsMonitor<BeaconServerOptions> opts)
    {
        _log = log;
        _opts = opts;
        var dir = AppContext.BaseDirectory;
        _commandsPath = Path.Combine(dir, "commands.json");
        _queuePath    = Path.Combine(dir, "command-queue.json");
        _repliesDir   = Path.Combine(dir, "command-replies");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Command dispatch service started: commands={Commands} queue={Queue}",
            _commandsPath, _queuePath);

        // ModKit creates command-replies\, but make sure it exists so a reply
        // poll never trips on a missing directory if dispatch runs first.
        try { Directory.CreateDirectory(_repliesDir); } catch (Exception ex) { _log.LogDebug(ex, "replies dir"); }

        while (!ct.IsCancellationRequested)
        {
            try { RefreshRegistryIfChanged(); } catch (Exception ex) { _log.LogDebug(ex, "registry refresh"); }

            try { await Task.Delay(TimeSpan.FromMilliseconds(1000), ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { break; }
        }

        _log.LogInformation("Command dispatch service stopping");
    }

    // ---------- Public surface (consumed by RCON + ChatService) ----------

    /// <summary>
    /// True if <paramref name="name"/> is a registered mod command. Lets a
    /// caller decide whether to route to the mod or fall through to a built-in
    /// without paying for a queue round trip.
    /// </summary>
    public bool IsModCommand(string name) =>
        !string.IsNullOrEmpty(name) && _registry.ContainsKey(name);

    /// <summary>
    /// True if <paramref name="identity"/> (SteamID64, BeaconUserId, or display
    /// name) is configured as an admin in Beacon:Admins[]. Case-insensitive.
    /// </summary>
    public bool IsAdmin(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return false;
        var admins = _opts.CurrentValue.Admins;
        if (admins is null || admins.Count == 0) return false;
        foreach (var a in admins)
            if (!string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), identity.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Dispatch a slash command to the mod that registered it.
    /// Returns:
    ///   - <c>null</c> if <paramref name="name"/> is NOT a registered mod
    ///     command — the caller should fall through to built-in handling.
    ///   - a "not authorized" string if the command is admin-only and the
    ///     caller is not an admin.
    ///   - the mod's reply (or its error string) on success.
    ///   - "(no response from mod)" if the mod never wrote a reply.
    /// </summary>
    public async Task<string?> DispatchAsync(string name, string[] args, string raw, CallerInfo caller, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (!_registry.TryGetValue(name, out var adminOnly)) return null;  // not a mod command

        if (adminOnly && !caller.IsAdmin)
            return $"not authorized: /{name} is admin-only";

        var id = Guid.NewGuid().ToString("N");
        var entry = new QueueEntry
        {
            Id     = id,
            Name   = name,
            Args   = args ?? Array.Empty<string>(),
            Raw    = raw ?? "",
            Caller = new CallerDto
            {
                Kind    = caller.Kind,
                Name    = caller.Name,
                SteamId = caller.SteamId,
                IsAdmin = caller.IsAdmin,
            },
        };

        // First attempt: append + wait.
        await AppendToQueueAsync(entry, ct).ConfigureAwait(false);
        var reply = await AwaitReplyAsync(id, ReplyTimeout, ct).ConfigureAwait(false);
        if (reply is not null) return reply;

        // Re-queue once: ModKit clears the queue after each drain, so if our
        // entry raced the clear (got read while the file was momentarily empty,
        // or our write landed right as ModKit overwrote with []), a single
        // re-queue recovers it for v1. A fresh id avoids colliding with a
        // possibly-late reply from the first attempt.
        var retryId = Guid.NewGuid().ToString("N");
        entry.Id = retryId;
        await AppendToQueueAsync(entry, ct).ConfigureAwait(false);
        reply = await AwaitReplyAsync(retryId, ReplyTimeout, ct).ConfigureAwait(false);
        if (reply is not null) return reply;

        _log.LogDebug("No reply for /{Name} after re-queue (ids {Id1},{Id2})", name, id, retryId);
        return "(no response from mod)";
    }

    // ---------- test seams (InternalsVisibleTo BeaconServer.Tests) ----------

    internal void RefreshRegistryForTest() => RefreshRegistryIfChanged();
    internal string CommandsPathForTest => _commandsPath;
    internal string QueuePathForTest => _queuePath;
    internal string RepliesDirForTest => _repliesDir;
    internal IReadOnlyDictionary<string, bool> RegistryForTest => _registry;

    // ---------- commands.json refresh ----------

    private void RefreshRegistryIfChanged()
    {
        if (!File.Exists(_commandsPath))
        {
            if (_registry.Count > 0)
                _registry = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            _lastCommandsSize = -1;
            return;
        }

        var info = new FileInfo(_commandsPath);
        if (info.Length == _lastCommandsSize && info.LastWriteTimeUtc <= _lastCommandsRead) return;
        _lastCommandsSize = info.Length;
        _lastCommandsRead = info.LastWriteTimeUtc;

        string body;
        try { body = File.ReadAllText(_commandsPath); }
        catch (IOException) { _lastCommandsSize = -1; return; }  // race with Lua's atomic write — retry next tick

        CommandsFile? parsed;
        try { parsed = JsonSerializer.Deserialize<CommandsFile>(body, JsonOpts); }
        catch (JsonException ex) { _log.LogDebug(ex, "commands.json parse"); return; }

        var next = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (parsed?.Commands is not null)
        {
            foreach (var c in parsed.Commands)
            {
                if (string.IsNullOrWhiteSpace(c.Name)) continue;
                next[c.Name.Trim()] = c.AdminOnly;
            }
        }

        var prevCount = _registry.Count;
        _registry = next;
        if (next.Count != prevCount)
            _log.LogDebug("Mod command registry refreshed: {Count} command(s)", next.Count);
    }

    // ---------- command-queue.json append (atomic RMW) ----------

    private async Task AppendToQueueAsync(QueueEntry entry, CancellationToken ct)
    {
        await _queueLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = ReadQueue();
            file.Queue ??= new List<QueueEntry>();
            file.Queue.Add(entry);
            file.Version = 1;
            var json = JsonSerializer.Serialize(file, JsonOpts);
            WriteAtomic(_queuePath, json);
        }
        finally
        {
            _queueLock.Release();
        }
    }

    private QueueFile ReadQueue()
    {
        if (!File.Exists(_queuePath)) return new QueueFile { Version = 1, Queue = new() };
        string body;
        try { body = File.ReadAllText(_queuePath); }
        catch (IOException) { return new QueueFile { Version = 1, Queue = new() }; }  // mid-write; start clean
        if (string.IsNullOrWhiteSpace(body)) return new QueueFile { Version = 1, Queue = new() };
        try
        {
            var parsed = JsonSerializer.Deserialize<QueueFile>(body, JsonOpts);
            if (parsed is null) return new QueueFile { Version = 1, Queue = new() };
            parsed.Queue ??= new List<QueueEntry>();
            return parsed;
        }
        catch (JsonException)
        {
            // Corrupt/partial queue file — don't propagate ModKit's pending
            // entries (we can't parse them anyway); start a fresh queue with
            // just our append. ModKit drains whatever it last read.
            return new QueueFile { Version = 1, Queue = new() };
        }
    }

    // ---------- command-replies/<id>.json wait ----------

    private async Task<string?> AwaitReplyAsync(string id, TimeSpan timeout, CancellationToken ct)
    {
        var path = Path.Combine(_repliesDir, id + ".json");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return null;
            if (File.Exists(path))
            {
                var result = TryReadReply(path);
                if (result is not null)
                {
                    TryDelete(path);
                    return result;
                }
                // File exists but isn't readable/parseable yet (ModKit's
                // atomic write is delete-then-rename, so a brief window can
                // show a zero-byte or absent file). Keep polling.
            }
            try { await Task.Delay(ReplyPollInterval, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { return null; }
        }
        return null;
    }

    private string? TryReadReply(string path)
    {
        string body;
        try { body = File.ReadAllText(path); }
        catch (IOException) { return null; }  // mid-write race
        if (string.IsNullOrWhiteSpace(body)) return null;

        ReplyFile? parsed;
        try { parsed = JsonSerializer.Deserialize<ReplyFile>(body, JsonOpts); }
        catch (JsonException) { return null; }
        if (parsed is null) return null;

        // ModKit sets error when the dispatch failed (unknown_command,
        // admin_required, handler_error). Surface it; otherwise the reply.
        if (!string.IsNullOrEmpty(parsed.Error))
            return string.IsNullOrEmpty(parsed.Reply) ? $"error: {parsed.Error}" : parsed.Reply;
        return parsed.Reply ?? "";
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) { _log.LogDebug(ex, "reply delete {Path}", path); }
    }

    // ---------- helpers ----------

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    // ---------- DTOs ----------

    private sealed class CommandsFile
    {
        [JsonPropertyName("version")]  public int                  Version  { get; set; } = 1;
        [JsonPropertyName("updated")]  public long                 Updated  { get; set; }
        [JsonPropertyName("commands")] public List<CommandEntry>?  Commands { get; set; }
    }

    private sealed class CommandEntry
    {
        [JsonPropertyName("name")]       public string Name      { get; set; } = "";
        [JsonPropertyName("help")]       public string Help      { get; set; } = "";
        [JsonPropertyName("usage")]      public string Usage     { get; set; } = "";
        [JsonPropertyName("admin_only")] public bool   AdminOnly { get; set; }
    }

    private sealed class QueueFile
    {
        [JsonPropertyName("version")] public int               Version { get; set; } = 1;
        [JsonPropertyName("queue")]   public List<QueueEntry>? Queue   { get; set; }
    }

    private sealed class QueueEntry
    {
        [JsonPropertyName("id")]     public string    Id     { get; set; } = "";
        [JsonPropertyName("name")]   public string    Name   { get; set; } = "";
        [JsonPropertyName("args")]   public string[]  Args   { get; set; } = Array.Empty<string>();
        [JsonPropertyName("raw")]    public string    Raw    { get; set; } = "";
        [JsonPropertyName("caller")] public CallerDto Caller { get; set; } = new();
    }

    private sealed class CallerDto
    {
        [JsonPropertyName("kind")]     public string  Kind    { get; set; } = "unknown";
        [JsonPropertyName("name")]     public string? Name    { get; set; }
        [JsonPropertyName("steam_id")] public string? SteamId { get; set; }
        [JsonPropertyName("is_admin")] public bool    IsAdmin { get; set; }
    }

    private sealed class ReplyFile
    {
        [JsonPropertyName("id")]    public string? Id    { get; set; }
        [JsonPropertyName("reply")] public string? Reply { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("ts")]    public long    Ts    { get; set; }
    }
}

/// <summary>
/// Parses slash-command text into name + args. Shared by the RCON and in-game
/// chat dispatch paths so they agree on tokenization.
/// </summary>
public static class SlashCommand
{
    /// <summary>
    /// Parse "<c>/name arg1 arg2</c>" (or "<c>name arg1 arg2</c>") into its
    /// lowercase name, the arg tokens, and the verbatim remainder after the
    /// name (<c>raw</c>). Returns false for empty/whitespace input or a bare
    /// "/" with no name. The leading slash is optional and stripped.
    /// </summary>
    public static bool TryParse(string? text, out string name, out string[] args, out string raw)
    {
        name = "";
        args = Array.Empty<string>();
        raw = "";

        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        raw = t;   // full slash line as received, e.g. "/wave alice" (protocol/modkit-v1.md:99)
        if (t[0] == '/') t = t.Substring(1).TrimStart();
        if (t.Length == 0) return false;

        var sp = t.IndexOf(' ');
        if (sp < 0)
        {
            name = t.ToLowerInvariant();
            args = Array.Empty<string>();
        }
        else
        {
            name = t.Substring(0, sp).ToLowerInvariant();
            var tail = t.Substring(sp + 1).Trim();
            args = tail.Length == 0
                ? Array.Empty<string>()
                : tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }
        return name.Length > 0;
    }
}

/// <summary>
/// Identifies the origin of a dispatched slash command. RCON callers are
/// always admin (the RCON password is the gate); in-game chat callers are
/// matched against Beacon:Admins[] by sender name.
/// </summary>
public sealed class CallerInfo
{
    public string Kind { get; init; } = "unknown";   // "rcon" | "chat" | "http" | "console"
    public string? Name { get; init; }
    public string? SteamId { get; init; }
    public bool IsAdmin { get; init; }

    public static CallerInfo Rcon(string? name = "admin") =>
        new() { Kind = "rcon", Name = name, IsAdmin = true };

    public static CallerInfo Chat(string sender, string? steamId, bool isAdmin) =>
        new() { Kind = "chat", Name = sender, SteamId = steamId, IsAdmin = isAdmin };
}

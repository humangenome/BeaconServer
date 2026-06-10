using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeaconServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

/// <summary>
/// Two-file chat plane (see protocol/chat-v1.md):
///   - chat-outbound.json: Lua writes, ChatService drains (in-game origin)
///   - chat-inbound.json:  ChatService writes, Lua drains (server origin)
/// Maintains an in-memory ring buffer of the last N messages, serves HTTP
/// readers, and persists chat-history.json (debounced) for diagnostics.
/// </summary>
public sealed class ChatService : BackgroundService
{
    private readonly ILogger<ChatService> _log;
    private readonly IOptionsMonitor<BeaconServerOptions> _opts;
    private readonly CommandDispatchService _commands;

    private readonly string _outboundPath;
    private readonly string _inboundPath;
    private readonly string _historyPath;
    private readonly string _motdPath;

    private long _lastOutboundSize = -1;
    private DateTimeOffset _lastOutboundRead = DateTimeOffset.MinValue;

    private readonly object _ringLock = new();
    private readonly LinkedList<ChatMessage> _ring = new();
    private long _historyDirtyAtUnixMs = 0;
    private long _historyLastWriteUnixMs = 0;

    public ChatService(ILogger<ChatService> log, IOptionsMonitor<BeaconServerOptions> opts, CommandDispatchService commands)
    {
        _log = log;
        _opts = opts;
        _commands = commands;
        var dir = AppContext.BaseDirectory;
        _outboundPath = Path.Combine(dir, "chat-outbound.json");
        _inboundPath  = Path.Combine(dir, "chat-inbound.json");
        _historyPath  = Path.Combine(dir, "chat-history.json");
        _motdPath     = Path.Combine(dir, "motd.txt");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _log.LogInformation("Chat service started: outbound={Out} inbound={In}",
            _outboundPath, _inboundPath);

        // Load history on boot so /chat/recent survives restarts within a session.
        TryLoadHistory();

        while (!ct.IsCancellationRequested)
        {
            try { DrainOutboundIfChanged(); } catch (Exception ex) { _log.LogDebug(ex, "outbound drain"); }
            try { FlushHistoryIfDirty(); }   catch (Exception ex) { _log.LogDebug(ex, "history flush"); }

            try { await Task.Delay(TimeSpan.FromMilliseconds(1000), ct); }
            catch (TaskCanceledException) { break; }
        }

        _log.LogInformation("Chat service stopping");
    }

    // ---------- Public surface (consumed by HTTP, RCON, scheduler) ----------

    public ChatMessage BroadcastFromServer(string msg, string? channel = null, string? color = null, string? sender = null, string? target = null)
    {
        var entry = new ChatMessage
        {
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Sender  = string.IsNullOrWhiteSpace(sender) ? "Server" : Truncate(sender, 64),
            Target  = string.IsNullOrWhiteSpace(target) ? "all"    : Truncate(target, 128),
            Channel = NormalizeChannel(channel),
            Msg     = NormalizeBody(msg),
            Color   = NormalizeColor(color),
        };

        AppendToRing(entry);
        AppendToInbound(entry);
        return entry;
    }

    /// <summary>
    /// Public ingest path for messages typed by a player in their BeaconHud
    /// overlay. Forced channel "player", target "all". Rate-limited per client
    /// IP (see <see cref="CheckPlayerRate"/>). Returns null if the rate cap
    /// fires; caller maps that to HTTP 429.
    /// </summary>
    public ChatMessage? BroadcastFromPlayer(string clientIp, string sender, string msg)
    {
        if (!CheckPlayerRate(clientIp)) return null;
        var clean = NormalizeBody(msg);
        if (clean.Length == 0) return null;
        var senderName = string.IsNullOrWhiteSpace(sender) ? "Player" : Truncate(sender, 64);

        // A message that starts with "/" is a slash command, not chat. Route it
        // to the mod dispatch path and deliver the reply privately back to the
        // sender. Only intercept when the name is actually a registered mod
        // command — otherwise a normal "/me ..." style line falls through and
        // broadcasts as ordinary chat. The dispatch round trip (queue write +
        // up to ~2s reply wait) runs off-thread so the player's HTTP relay
        // returns immediately; the reply lands as an inbound "system" message
        // targeted to the sender.
        if (clean.StartsWith("/") && SlashCommand.TryParse(clean, out var cmdName, out var cmdArgs, out var cmdRaw)
            && _commands.IsModCommand(cmdName))
        {
            // SECURITY: this path is the PUBLIC, unauthenticated /chat/player
            // endpoint and `senderName` is a client-supplied string — anyone can
            // POST sender="<an admin's name>". Granting admin off that name let
            // any remote client run admin-only mod commands. The public chat
            // surface can never confer admin; admin commands must come through
            // the authenticated RCON/console path (which sets caller.IsAdmin).
            const bool isAdmin = false;
            _ = DispatchPlayerCommandAsync(senderName, cmdName, cmdArgs, cmdRaw, isAdmin);
            // Return a non-broadcast acknowledgement entry: the HTTP layer only
            // reads Ts (to report 200 ok). It is intentionally NOT appended to
            // the ring or inbound so the raw command text never shows in chat.
            return new ChatMessage
            {
                Ts      = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Sender  = senderName,
                Target  = senderName,
                Channel = "system",
                Msg     = "",
            };
        }

        var entry = new ChatMessage
        {
            Ts      = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Sender  = senderName,
            Target  = "all",
            Channel = "player",
            Msg     = clean,
            Color   = null,
        };
        AppendToRing(entry);
        AppendToInbound(entry);
        return entry;
    }

    // Runs the mod dispatch off the HTTP request thread and delivers the reply
    // privately to the sender via a "system"-channel inbound message targeted
    // to their name. BeaconHud renders target-matched inbound lines to that
    // player only. Swallows + logs faults — a mod command failure must never
    // bubble into the chat ingest path.
    private async Task DispatchPlayerCommandAsync(string sender, string name, string[] args, string raw, bool isAdmin)
    {
        try
        {
            var reply = await _commands.DispatchAsync(name, args, raw, CallerInfo.Chat(sender, null, isAdmin))
                .ConfigureAwait(false);
            // null => not a mod command (registry changed between the check and
            // dispatch); nothing to deliver. Empty string => command ran but
            // had no reply text; also nothing to deliver.
            if (!string.IsNullOrEmpty(reply))
                BroadcastFromServer(reply, channel: "system", sender: "Server", target: sender);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "player command dispatch /{Name}", name);
        }
    }

    // ---- per-IP rate limiter for /chat/player ----
    //
    // The launcher relays exactly one message per Enter-press, so the
    // expected ceiling is ~1 msg/sec/player even when someone is mashing.
    // Cap at 1 msg/2s burst + 10 msgs/30s sustained per remote IP. NAT'd
    // players (same household) share a bucket; that's fine — they're not
    // throwing volume.
    private readonly object _rateLock = new();
    private readonly Dictionary<string, Queue<long>> _rateBuckets = new();
    private const int RateBurstWindowSec = 2;
    private const int RateSustainedWindowSec = 30;
    private const int RateSustainedMax = 10;

    private bool CheckPlayerRate(string clientIp)
    {
        if (string.IsNullOrEmpty(clientIp)) clientIp = "unknown";
        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_rateLock)
        {
            if (!_rateBuckets.TryGetValue(clientIp, out var q))
            {
                q = new Queue<long>(RateSustainedMax + 1);
                _rateBuckets[clientIp] = q;
            }
            // drop entries outside the sustained window
            while (q.Count > 0 && nowSec - q.Peek() > RateSustainedWindowSec) q.Dequeue();
            if (q.Count >= RateSustainedMax) return false;
            // burst check: last message within the burst window?
            if (q.Count > 0 && nowSec - LastOf(q) < RateBurstWindowSec) return false;
            q.Enqueue(nowSec);

            // opportunistic garbage collection so the dictionary doesn't grow
            // unbounded for transient IPs that visit once
            if (_rateBuckets.Count > 1024) PruneRateBuckets(nowSec);
            return true;
        }
    }

    private static long LastOf(Queue<long> q)
    {
        long last = 0;
        foreach (var t in q) last = t;
        return last;
    }

    private void PruneRateBuckets(long nowSec)
    {
        var stale = new List<string>();
        foreach (var kv in _rateBuckets)
        {
            var lastSeen = LastOf(kv.Value);
            if (nowSec - lastSeen > RateSustainedWindowSec * 2) stale.Add(kv.Key);
        }
        foreach (var k in stale) _rateBuckets.Remove(k);
    }

    public IReadOnlyList<ChatMessage> GetRecent(long sinceUnixMs, int limit)
    {
        if (limit <= 0) limit = 100;
        if (limit > 500) limit = 500;
        var sinceSec = sinceUnixMs / 1000;
        lock (_ringLock)
        {
            // ring is oldest -> newest; collect newest entries strictly after sinceSec
            var picked = new List<ChatMessage>();
            foreach (var m in _ring)
            {
                if (m.Ts > sinceSec) picked.Add(m);
            }
            if (picked.Count > limit) picked = picked.GetRange(picked.Count - limit, limit);
            return picked;
        }
    }

    public string GetMotd()
    {
        try { return File.Exists(_motdPath) ? File.ReadAllText(_motdPath).Trim() : ""; }
        catch { return ""; }
    }

    public bool SetMotd(string msg)
    {
        try
        {
            var clean = NormalizeBody(msg);
            File.WriteAllText(_motdPath + ".tmp", clean, new UTF8Encoding(false));
            File.Move(_motdPath + ".tmp", _motdPath, overwrite: true);
            _log.LogInformation("MOTD updated: {Msg}", clean.Length > 80 ? clean[..80] + "..." : clean);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SetMotd failed");
            return false;
        }
    }

    // ---------- Outbound drain (Lua -> BeaconServer) ----------

    private void DrainOutboundIfChanged()
    {
        if (!File.Exists(_outboundPath)) return;
        var info = new FileInfo(_outboundPath);
        if (info.Length == _lastOutboundSize && info.LastWriteTimeUtc <= _lastOutboundRead) return;
        _lastOutboundSize = info.Length;
        _lastOutboundRead = info.LastWriteTimeUtc;

        string body;
        try { body = File.ReadAllText(_outboundPath); }
        catch (IOException) { return; }  // race with Lua's atomic write — retry next tick

        ChatFile? parsed;
        try { parsed = JsonSerializer.Deserialize<ChatFile>(body, JsonOpts); }
        catch (JsonException ex) { _log.LogDebug(ex, "outbound parse"); return; }

        if (parsed?.Messages is null || parsed.Messages.Count == 0) return;

        foreach (var m in parsed.Messages)
        {
            // Lua side already normalised these but defend in depth.
            m.Msg     = NormalizeBody(m.Msg);
            m.Channel = NormalizeChannel(m.Channel);
            m.Sender  = string.IsNullOrWhiteSpace(m.Sender) ? "Player" : Truncate(m.Sender, 64);
            m.Target  = string.IsNullOrWhiteSpace(m.Target) ? "all" : Truncate(m.Target, 128);
            m.Color   = NormalizeColor(m.Color);
            AppendToRing(m);
        }

        // Drain the outbound file (single consumer = us).
        WriteAtomic(_outboundPath, """{"version":1,"messages":[]}""");
        // size reset → ensure next change is detected even if Lua writes nothing new
        _lastOutboundSize = -1;

        _log.LogDebug("Drained {N} outbound chat messages", parsed.Messages.Count);
    }

    // ---------- Inbound append (BeaconServer -> Lua) ----------

    private readonly object _inboundLock = new();
    private void AppendToInbound(ChatMessage entry)
    {
        lock (_inboundLock)
        {
            ChatFile file;
            if (File.Exists(_inboundPath))
            {
                try
                {
                    var body = File.ReadAllText(_inboundPath);
                    file = JsonSerializer.Deserialize<ChatFile>(body, JsonOpts)
                        ?? new ChatFile { Version = 1, Messages = new() };
                    file.Messages ??= new();
                }
                catch { file = new ChatFile { Version = 1, Messages = new() }; }
            }
            else
            {
                file = new ChatFile { Version = 1, Messages = new() };
            }
            file.Messages!.Add(entry);
            // cap unread inbound at 200 to bound disk if Lua isn't draining
            if (file.Messages.Count > 200)
                file.Messages.RemoveRange(0, file.Messages.Count - 200);
            var json = JsonSerializer.Serialize(file, JsonOpts);
            WriteAtomic(_inboundPath, json);
        }
    }

    // ---------- Ring buffer + history ----------

    private void AppendToRing(ChatMessage entry)
    {
        var cap = Math.Max(20, _opts.CurrentValue.Chat?.RingBufferSize ?? 200);
        lock (_ringLock)
        {
            _ring.AddLast(entry);
            while (_ring.Count > cap) _ring.RemoveFirst();
            _historyDirtyAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    private void FlushHistoryIfDirty()
    {
        long dirty;
        long lastWrite;
        lock (_ringLock) { dirty = _historyDirtyAtUnixMs; lastWrite = _historyLastWriteUnixMs; }
        if (dirty == 0) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now - lastWrite < 1000) return;  // 1s debounce

        List<ChatMessage> snapshot;
        lock (_ringLock)
        {
            snapshot = new List<ChatMessage>(_ring);
            _historyDirtyAtUnixMs = 0;
            _historyLastWriteUnixMs = now;
        }
        var file = new ChatFile { Version = 1, Messages = snapshot };
        var json = JsonSerializer.Serialize(file, JsonOpts);
        try { WriteAtomic(_historyPath, json); }
        catch (Exception ex) { _log.LogDebug(ex, "history write"); }
    }

    private void TryLoadHistory()
    {
        if (!File.Exists(_historyPath)) return;
        try
        {
            var body = File.ReadAllText(_historyPath);
            var parsed = JsonSerializer.Deserialize<ChatFile>(body, JsonOpts);
            if (parsed?.Messages is null) return;
            lock (_ringLock)
            {
                foreach (var m in parsed.Messages) _ring.AddLast(m);
                var cap = Math.Max(20, _opts.CurrentValue.Chat?.RingBufferSize ?? 200);
                while (_ring.Count > cap) _ring.RemoveFirst();
            }
            _log.LogInformation("Loaded {N} historical chat messages from {Path}", parsed.Messages.Count, _historyPath);
        }
        catch (Exception ex) { _log.LogDebug(ex, "history load"); }
    }

    // ---------- helpers ----------

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string NormalizeBody(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // strip control chars (keep tab + newline out — chat is single line)
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (c >= 0x20 && c != 0x7F) sb.Append(c);
        var clean = sb.ToString();
        var bytes = Encoding.UTF8.GetByteCount(clean);
        if (bytes <= 512) return clean;
        // truncate by bytes — safe-cut UTF-8
        var arr = Encoding.UTF8.GetBytes(clean);
        var cap = 512;
        while (cap > 0 && (arr[cap] & 0xC0) == 0x80) cap--;
        return Encoding.UTF8.GetString(arr, 0, cap);
    }

    private static string NormalizeChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return "system";
        var c = channel.Trim().ToLowerInvariant();
        return c is "system" or "admin" or "player" or "motd" ? c : "system";
    }

    private static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        var c = color.Trim();
        if (c.Length != 7 || c[0] != '#') return null;
        for (int i = 1; i < 7; i++)
            if (!Uri.IsHexDigit(c[i])) return null;
        return c;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];

    // ---------- DTOs ----------

    public sealed class ChatMessage
    {
        [JsonPropertyName("ts")]      public long    Ts      { get; set; }
        [JsonPropertyName("sender")]  public string  Sender  { get; set; } = "";
        [JsonPropertyName("target")]  public string  Target  { get; set; } = "all";
        [JsonPropertyName("channel")] public string  Channel { get; set; } = "system";
        [JsonPropertyName("msg")]     public string  Msg     { get; set; } = "";
        [JsonPropertyName("color")]   public string? Color   { get; set; }
    }

    private sealed class ChatFile
    {
        [JsonPropertyName("version")]  public int                Version  { get; set; } = 1;
        [JsonPropertyName("messages")] public List<ChatMessage>? Messages { get; set; }
    }
}

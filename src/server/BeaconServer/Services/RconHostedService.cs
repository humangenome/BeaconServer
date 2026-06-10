using Beacon.Protocol;
using Beacon.Rcon;
using BeaconServer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeaconServer.Services;

/// <summary>
/// Source RCON server. Translates RCON commands into <see cref="FrameType.RconCommand"/>
/// frames sent to the plugin and awaits the matching response, with a fallback
/// for purely server-side commands (status, players, save).
/// </summary>
public sealed class RconHostedService : IHostedService
{
    private readonly ILogger<RconHostedService> _log;
    private readonly BeaconServerOptions _opts;
    private readonly PipeServerState _state;
    private readonly SaveOrchestratorService _saves;
    private readonly ChatService _chat;
    private readonly CommandDispatchService _commands;
    private RconServer? _server;

    public RconHostedService(
        ILogger<RconHostedService> log,
        IOptions<BeaconServerOptions> opts,
        PipeServerState state,
        SaveOrchestratorService saves,
        ChatService chat,
        CommandDispatchService commands)
    {
        _log = log;
        _opts = opts.Value;
        _state = state;
        _saves = saves;
        _chat = chat;
        _commands = commands;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_opts.RconPassword))
        {
            _log.LogWarning("RCON disabled (no password set in Beacon:RconPassword)");
            return Task.CompletedTask;
        }
        _server = new RconServer(_opts.RconPort, _opts.RconPassword, ExecuteAsync, _log);
        _server.Start(ct);
        _log.LogInformation("RCON listening on TCP {Port}", _server.BoundPort);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken _)
    {
        if (_server is not null) await _server.StopAsync().ConfigureAwait(false);
    }

    private async Task<string> ExecuteAsync(string command)
    {
        var trimmed = command.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "";

        var parts = trimmed.Split(' ', 2);
        var head = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1] : "";

        // A "say"/"announce" whose payload starts with "/" is an admin issuing
        // a mod slash command via chat — route it to the mod, not the chat log.
        // If the name isn't a registered mod command, fall back to broadcasting
        // the literal text (the "/" was just part of the message).
        if ((head == "say" || head == "announce") && rest.TrimStart().StartsWith("/"))
        {
            var modReply = await TryDispatchSlashAsync(rest).ConfigureAwait(false);
            if (modReply is not null) return modReply;
            return HandleChat(rest, head == "announce" ? "admin" : "system");
        }

        switch (head)
        {
            case "help":     return "commands: status, players, ping, save snapshot, save list, say <msg>, announce <msg>, motd [msg], /<modcmd> [args]";
            case "status":   return BuildStatus();
            case "players":  return BuildPlayers();
            case "ping":     return "pong";
            case "save":     return await HandleSaveAsync(rest).ConfigureAwait(false);
            case "snapshot": return await HandleSaveAsync("snapshot").ConfigureAwait(false);
            case "say":      return HandleChat(rest, "system");
            case "announce": return HandleChat(rest, "admin");
            case "motd":     return HandleMotd(rest);
        }

        // Not a built-in. Try it as a mod-registered command — both bare
        // ("wave alice") and slash-prefixed ("/wave alice") forms. RCON is
        // always admin (the RCON password is the gate).
        var dispatched = await TryDispatchSlashAsync(trimmed).ConfigureAwait(false);
        if (dispatched is not null) return dispatched;

        return $"unknown rcon command: {head} (try: help)";
    }

    /// <summary>
    /// Parse "&lt;name&gt; arg1 arg2 ..." (optionally "/name ...") and dispatch
    /// to a mod command. Returns null when the name isn't a registered mod
    /// command so the caller can fall through to built-in behavior.
    /// </summary>
    private async Task<string?> TryDispatchSlashAsync(string text)
    {
        if (!SlashCommand.TryParse(text, out var name, out var args, out var raw))
            return null;
        return await _commands.DispatchAsync(name, args, raw, CallerInfo.Rcon()).ConfigureAwait(false);
    }

    private string HandleChat(string msg, string channel)
    {
        var clean = (msg ?? "").Trim();
        if (string.IsNullOrEmpty(clean)) return $"usage: {(channel == "admin" ? "announce" : "say")} <message>";
        var entry = _chat.BroadcastFromServer(clean, channel: channel, sender: "Server");
        return $"chat ok ({channel}): {entry.Msg}";
    }

    private string HandleMotd(string sub)
    {
        var trimmed = (sub ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            var cur = _chat.GetMotd();
            return string.IsNullOrEmpty(cur) ? "motd is empty" : $"motd: {cur}";
        }
        return _chat.SetMotd(trimmed) ? "motd updated" : "motd update failed (check log)";
    }

    private async Task<string> HandleSaveAsync(string sub)
    {
        var arg = sub.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(arg) || arg == "snapshot")
        {
            // SN2 host auto-saves to <userdir>/Saved/SaveGames/savegame_0.sav
            // every ~60s. We snapshot the on-disk save file directly; the
            // plugin SaveQuiesce ack is not required because SN2's own save
            // writer is atomic (writes to temp then renames). The
            // FileSystemWatcher in SaveOrchestratorService handles
            // auto-snapshots; this RCON path is for admin-triggered.
            var rec = await _saves.SnapshotAsync("rcon").ConfigureAwait(false);
            return rec is null
                ? "snapshot failed (check beacon log; save dir likely missing)"
                : $"snapshot ok: {rec.SnapshotId} ({rec.SizeBytes} bytes, sha={rec.Sha256Hex[..16]})";
        }
        if (arg == "list")
        {
            var snaps = _saves.Database.ListSnapshots(20);
            if (snaps.Count == 0) return "no snapshots yet";
            var sb = new System.Text.StringBuilder();
            foreach (var s in snaps)
                sb.AppendLine($"{s.SnapshotId}  {s.SizeBytes}B  age={(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - s.TakenUnix)}s  sha={s.Sha256Hex[..16]}");
            return sb.ToString().TrimEnd();
        }
        return "usage: save snapshot | save list";
    }

    private string BuildStatus()
    {
        var conn = _state.Connection;
        return conn is null
            ? $"instance={_opts.InstanceId} plugin=disconnected"
            : $"instance={_opts.InstanceId} plugin=connected pid={conn.PluginPid} version={conn.PluginVersion} players={_state.EffectivePlayerCount}";
    }

    private string BuildPlayers()
    {
        var n = _state.EffectivePlayerCount;
        return n == 0 ? "no players online" : $"{n} player(s) online (per-player names land in Phase 2)";
    }
}

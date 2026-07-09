using System.Text.Json;
using BeaconServer.Configuration;
using BeaconServer.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BeaconServer.Tests;

/// <summary>
/// Covers ModKit Pillar 4 reverse command dispatch (CommandDispatchService).
/// The service reads/writes files in AppContext.BaseDirectory; each test cleans
/// the shared files and drives the registry refresh + queue drain manually so
/// we don't depend on the 1s background loop or a real Lua mod. The "ModKit"
/// half is simulated by a helper that reproduces the Lua drain semantics:
/// read command-queue.json, write command-replies/&lt;id&gt;.json per entry, then
/// overwrite the queue with the empty sentinel.
/// </summary>
public class CommandDispatchServiceTests : IDisposable
{
    private readonly string _baseDir;
    private readonly CommandDispatchService _svc;
    private readonly BeaconServerOptions _opts;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public CommandDispatchServiceTests()
    {
        _baseDir = AppContext.BaseDirectory;
        Cleanup();
        _opts = new BeaconServerOptions { Admins = new List<string> { "Drew", "76561198000000000" } };
        var monitor = new TestOptionsMonitor<BeaconServerOptions>(_opts);
        _svc = new CommandDispatchService(NullLogger<CommandDispatchService>.Instance, monitor);
        Directory.CreateDirectory(_svc.RepliesDirForTest);
    }

    public void Dispose() => Cleanup();

    private void Cleanup()
    {
        var dir = AppContext.BaseDirectory;
        foreach (var f in new[] { "commands.json", "command-queue.json" })
            try { File.Delete(Path.Combine(dir, f)); } catch { }
        var replies = Path.Combine(dir, "command-replies");
        try { if (Directory.Exists(replies)) Directory.Delete(replies, recursive: true); } catch { }
    }

    // ---- helpers ----

    private void WriteCommandsFile(params (string name, bool adminOnly)[] cmds)
    {
        var commands = cmds.Select(c => new
        {
            name = c.name, help = "", usage = "/" + c.name, admin_only = c.adminOnly,
        });
        var body = JsonSerializer.Serialize(new { version = 1, updated = 1, commands });
        File.WriteAllText(_svc.CommandsPathForTest, body);
        _svc.RefreshRegistryForTest();
    }

    // Reproduces the BeaconModKit drain_command_queue() Lua semantics so a
    // dispatch can complete in-test without a live UE4SS state. Runs once.
    private void SimulateModKitDrain(Func<string, string[], string> handler)
    {
        var queuePath = _svc.QueuePathForTest;
        if (!File.Exists(queuePath)) return;
        var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(queuePath));
        if (!doc.TryGetProperty("queue", out var queue) || queue.ValueKind != JsonValueKind.Array) return;
        if (queue.GetArrayLength() == 0) return;
        foreach (var entry in queue.EnumerateArray())
        {
            var id = entry.GetProperty("id").GetString() ?? "";
            var name = entry.GetProperty("name").GetString() ?? "";
            var args = entry.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : Array.Empty<string>();
            var reply = handler(name, args);
            var replyBody = JsonSerializer.Serialize(new
            {
                id, reply, error = (string?)null, ts = 1L,
            });
            File.WriteAllText(Path.Combine(_svc.RepliesDirForTest, id + ".json"), replyBody);
        }
        // ModKit overwrites the queue with the empty sentinel after draining.
        File.WriteAllText(queuePath, "{\"version\":1,\"queue\":[]}");
    }

    // Background "ModKit" that polls the queue until it sees an entry, replies,
    // and clears — mirrors the 250ms Lua loop so DispatchAsync's await resolves.
    private PollerHandle StartModKitPoller(Func<string, string[], string> handler)
    {
        var cts = new CancellationTokenSource();
        var task = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { SimulateModKitDrain(handler); } catch { }
                try { await Task.Delay(20, cts.Token); } catch { return; }
            }
        });
        return new PollerHandle(cts, task);
    }

    // ---- registry ----

    [Fact]
    public void RefreshRegistry_loads_commands_json_into_registry()
    {
        WriteCommandsFile(("wave", false), ("kick", true));
        _svc.IsModCommand("wave").Should().BeTrue();
        _svc.IsModCommand("kick").Should().BeTrue();
        _svc.IsModCommand("WAVE").Should().BeTrue("registry lookup is case-insensitive");
        _svc.IsModCommand("nope").Should().BeFalse();
    }

    [Fact]
    public void RefreshRegistry_tolerates_missing_commands_file()
    {
        // No commands.json written.
        _svc.RefreshRegistryForTest();
        _svc.IsModCommand("anything").Should().BeFalse();
        _svc.RegistryForTest.Should().BeEmpty();
    }

    // ---- admin lookup ----

    [Fact]
    public void IsAdmin_matches_configured_identities_case_insensitively()
    {
        _svc.IsAdmin("Drew").Should().BeTrue();
        _svc.IsAdmin("drew").Should().BeTrue();
        _svc.IsAdmin("76561198000000000").Should().BeTrue();
        _svc.IsAdmin("Random").Should().BeFalse();
        _svc.IsAdmin("").Should().BeFalse();
        _svc.IsAdmin(null).Should().BeFalse();
    }

    // ---- dispatch ----

    [Fact]
    public async Task DispatchAsync_returns_null_for_unregistered_command()
    {
        WriteCommandsFile(("wave", false));
        var reply = await _svc.DispatchAsync("notacommand", Array.Empty<string>(), "", CallerInfo.Rcon());
        reply.Should().BeNull("an unknown name falls through to built-in handling");
    }

    [Fact]
    public async Task DispatchAsync_blocks_admin_only_for_non_admin()
    {
        WriteCommandsFile(("kick", true));
        var caller = CallerInfo.Chat("Random", null, isAdmin: false);
        var reply = await _svc.DispatchAsync("kick", Array.Empty<string>(), "", caller);
        reply.Should().NotBeNull();
        reply.Should().Contain("not authorized");
        // No queue entry should have been written for a blocked command.
        File.Exists(_svc.QueuePathForTest).Should().BeFalse();
    }

    [Fact]
    public async Task DispatchAsync_roundtrips_reply_through_queue_and_reply_file()
    {
        WriteCommandsFile(("wave", false));
        var cts = StartModKitPoller((name, args) => $"waved at {(args.Length > 0 ? args[0] : "nobody")}");
        try
        {
            var reply = await _svc.DispatchAsync("wave", new[] { "alice" }, "alice", CallerInfo.Rcon());
            reply.Should().Be("waved at alice");
        }
        finally { cts.Cancel(); }
    }

    [Fact]
    public async Task DispatchAsync_admin_only_runs_for_admin_caller()
    {
        WriteCommandsFile(("kick", true));
        var cts = StartModKitPoller((name, args) => "kicked");
        try
        {
            var caller = CallerInfo.Chat("Drew", null, isAdmin: true);
            var reply = await _svc.DispatchAsync("kick", new[] { "griefer" }, "griefer", caller);
            reply.Should().Be("kicked");
        }
        finally { cts.Cancel(); }
    }

    [Fact]
    public async Task DispatchAsync_writes_exact_wire_shape_to_queue()
    {
        WriteCommandsFile(("wave", false));
        // Don't reply — just inspect what landed in the queue before timeout.
        var dispatch = _svc.DispatchAsync("wave", new[] { "alice", "bob" }, "/wave alice bob",
            CallerInfo.Chat("Drew", "76561198000000000", isAdmin: true));

        // Give the append a moment, then read the queue file.
        JsonElement entry = default;
        for (int i = 0; i < 50 && entry.ValueKind != JsonValueKind.Object; i++)
        {
            if (File.Exists(_svc.QueuePathForTest))
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(_svc.QueuePathForTest));
                if (doc.TryGetProperty("queue", out var q) && q.GetArrayLength() > 0)
                    entry = q[0];
            }
            if (entry.ValueKind != JsonValueKind.Object) await Task.Delay(20);
        }

        entry.ValueKind.Should().Be(JsonValueKind.Object, "queue entry must be written before the reply window");
        entry.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        entry.GetProperty("name").GetString().Should().Be("wave");
        entry.GetProperty("raw").GetString().Should().Be("/wave alice bob");
        entry.GetProperty("args").EnumerateArray().Select(x => x.GetString())
            .Should().Equal("alice", "bob");
        var caller = entry.GetProperty("caller");
        caller.GetProperty("kind").GetString().Should().Be("chat");
        caller.GetProperty("name").GetString().Should().Be("Drew");
        caller.GetProperty("steam_id").GetString().Should().Be("76561198000000000");
        caller.GetProperty("is_admin").GetBoolean().Should().BeTrue();

        await dispatch;  // let it time out cleanly
    }

    [Fact]
    public async Task DispatchAsync_returns_no_response_on_timeout()
    {
        WriteCommandsFile(("wave", false));
        // No ModKit poller running — nothing writes a reply.
        var reply = await _svc.DispatchAsync("wave", Array.Empty<string>(), "", CallerInfo.Rcon());
        reply.Should().Be("(no response from mod)");
    }

    [Fact]
    public async Task DispatchAsync_concurrent_callers_both_get_queued()
    {
        WriteCommandsFile(("wave", false), ("ping", false));
        // Capture how many distinct entries ModKit observes across drains.
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        var cts = StartModKitPoller((name, args) => { seen.Add(name); return $"ok:{name}"; });
        try
        {
            var t1 = _svc.DispatchAsync("wave", Array.Empty<string>(), "", CallerInfo.Rcon());
            var t2 = _svc.DispatchAsync("ping", Array.Empty<string>(), "", CallerInfo.Rcon());
            var replies = await Task.WhenAll(t1, t2);
            replies.Should().Contain("ok:wave");
            replies.Should().Contain("ok:ping");
        }
        finally { cts.Cancel(); }
    }

    // ---- slash parser ----

    [Theory]
    // raw is the full slash line as received (protocol/modkit-v1.md:99); args is the tail.
    [InlineData("/wave alice", "wave", "alice", "/wave alice")]
    [InlineData("wave alice bob", "wave", "alice|bob", "wave alice bob")]
    [InlineData("/Kick Griefer", "kick", "Griefer", "/Kick Griefer")]
    [InlineData("/ping", "ping", "", "/ping")]
    [InlineData("  /wave   alice  ", "wave", "alice", "/wave   alice")]
    public void SlashCommand_parses_name_args_and_raw(string input, string expectName, string expectArgs, string expectRaw)
    {
        SlashCommand.TryParse(input, out var name, out var args, out var raw).Should().BeTrue();
        name.Should().Be(expectName);
        raw.Should().Be(expectRaw);
        var wantArgs = expectArgs.Length == 0 ? Array.Empty<string>() : expectArgs.Split('|');
        args.Should().Equal(wantArgs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("/   ")]
    public void SlashCommand_rejects_empty_or_bare_slash(string input)
    {
        SlashCommand.TryParse(input, out _, out _, out _).Should().BeFalse();
    }

    private sealed class TestOptionsMonitor<T> : Microsoft.Extensions.Options.IOptionsMonitor<T>
    {
        public TestOptionsMonitor(T value) { CurrentValue = value; }
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => new NullDisposable();
        private sealed class NullDisposable : IDisposable { public void Dispose() { } }
    }

    private sealed class PollerHandle : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _task;
        private int _cancelled;

        public PollerHandle(CancellationTokenSource cts, Task task)
        {
            _cts = cts;
            _task = task;
        }

        public void Cancel() => Dispose();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _cancelled, 1) == 1) return;
            _cts.Cancel();
            try { _task.Wait(TimeSpan.FromSeconds(1)); } catch { }
            _cts.Dispose();
        }
    }
}

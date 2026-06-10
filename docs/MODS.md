# Modding Beacon

Beacon supports Lua mods through [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS). Mods run on the same UE4SS install as Beacon itself, so anything UE4SS can do you can do — hook UE5 game functions, replace assets, drive UI, talk to the server.

There's no walled API. A Beacon "mod" is just a UE4SS mod that happens to load alongside Beacon's own runtime. On top of raw UE4SS, the server ships **BeaconModKit** (preview), which publishes `Beacon.*` Lua namespaces for logging, commands, players, events, chat, HTTP, and game-thread work.

## Where mods live

| Folder | Loads on |
|---|---|
| `<install>\ue4ss\Mods\<your-mod>\` | Both the **client** and the **server** |

On a launcher install, `<install>` is `%LOCALAPPDATA%\Beacon\`. On a server, it's the install root next to the `BeaconServer\` folder — BeaconServer stages the whole `ue4ss\` folder into the game's binary directory on every launch.

Mods a server declares in its manifest are installed by the launcher separately, under `%LOCALAPPDATA%\Beacon\mods\<instance>\`, one folder per server — see "Distributing your mod" below.

**Server note:** Beacon's own server mods load through a single-runtime loader. `ue4ss\Mods\mods.txt` enables only `BeaconServerRuntime`, which loads the other Beacon mods in a fixed order inside one Lua state (one background thread instead of one per mod). A third-party mod with its own `enabled.txt` still loads the normal UE4SS way; it just costs its own event loop.

## Lua mods

Quickest path to "something working":

1. Create `ue4ss\Mods\HelloBeacon\enabled.txt` (empty file — UE4SS uses this as the on/off switch).
2. Create `ue4ss\Mods\HelloBeacon\Scripts\main.lua`:

```lua
print("[HelloBeacon] loaded")

-- Log the player controller every 10 seconds
LoopAsync(10000, function()
    local PC = FindFirstOf("PlayerController")
    if PC and PC:IsValid() then
        print("[HelloBeacon] PlayerController: " .. PC:GetFullName())
    end
    return false -- continue looping
end)
```

3. Restart the server (or the client, if it's a client mod). UE4SS prints `[HelloBeacon] loaded` in its log.

The full Lua API surface is documented at the [UE4SS Lua API reference](https://docs.ue4ss.com/dev/lua-api.html). Anything in there works here.

### Bundled mods

The client install ships two Lua mods under `ue4ss\Mods\`: **BeaconConnect** (enabled), which polls for a PlayerController on the main menu and then fires the `open <host>:<port>` console command that moves the player into your server, and **BeaconPlayerVisibility** (disabled by default), a fix for invisible remote players. BeaconConnect is a good working reference for a client-side mod:

```
ue4ss\Mods\BeaconConnect\Scripts\main.lua
```

The server ships its own mod set (auth, chat, roster, world warm-up, lifepod and unlock fixes, ModKit), loaded by `BeaconServerRuntime` as described above.

## Slash commands (ModKit)

Server mods can register slash commands with `Beacon.Commands.Register(name, handler, { admin_only = ..., help = ... })`. Registered commands are written to `commands.json` next to `BeaconServer.exe`, and BeaconServer dispatches them from two callers:

- **RCON** — type the command with or without the leading `/`. RCON callers are always admin.
- **In-game chat** — players type `/command` in the chat overlay. Admin-only commands require the sender to be listed in `Beacon:Admins` in `appsettings.json`.

The bundled BeaconChat mod registers `/say`, `/announce` (admin-only), `/help`, `/players`, and `/motd` this way.

## C++ mods

UE4SS C++ mods are not a stable public BeaconServer extension surface yet — UE4SS's own C++ mod loader expects an exact vtable layout and crashes Subnautica 2 without it, which is why Beacon loads its native `Beacon.dll` from Lua (`package.loadlib`) instead. Use Lua mods unless you are already comfortable maintaining your own UE4SS C++ mod against Subnautica 2 updates.

## Distributing your mod

Beacon doesn't have a central mod registry. Distribute your mod the way the rest of the UE4SS ecosystem does: a GitHub release zip with the mod folder structure inside.

Servers can also push your mod to every joining player: declare it under `Beacon:Mods:Required` (or `Recommended`) in `appsettings.json` with a download URL and a sha256 pin, and the launcher installs it automatically on join. The manifest format is served at `GET /api/v1/manifest`.

If your mod has client-side state that has to match server-side state, ship two folders (or two builds) and document which goes where.

## Troubleshooting

**My Lua mod doesn't run.**
Check `<install>\ue4ss\UE4SS.log` for parse errors. If your mod folder is missing `enabled.txt`, UE4SS skips it.

**My C++ mod doesn't load.**
Check the logs for DLL-load failures — `BeaconServer\logs\beacon-*.log` on the server, `%APPDATA%\Beacon\Beacon.log` for the launcher. The most common cause is a missing dependency — `dumpbin /dependents my_mod.dll` reveals what Windows is looking for.

**Hooks fire but values are wrong.**
Subnautica 2 is UE 5.6. Make sure your UE4SS install is built for 5.6 and that `UE4SS-settings.ini` pins the engine version:

```ini
[EngineVersionOverride]
MajorVersion = 5
MinorVersion = 6
```

If UE4SS falls back to autodetect on Subnautica 2, the AOB scans time out and your hooks never wire up.

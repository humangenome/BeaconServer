# Modding Beacon

Beacon supports Lua and C++ mods, both loaded through [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS). Mods run on the same UE4SS install as Beacon itself, so anything UE4SS can do you can do — hook UE5 game functions, replace assets, drive UI, talk to the server.

There's no walled API. A Beacon "mod" is just a UE4SS mod that happens to load alongside Beacon's own plugin.

## Where mods live

| Folder | Loads on |
|---|---|
| `<install>\ue4ss\Mods\<your-mod>\` | Both the **client** (via the launcher) and the **server** (BeaconServer) |
| `<install>\Beacon\mods\<your-mod>.dll` | Server-side C++ plugins via Beacon's host loader |

On a launcher install, `<install>` is `%LOCALAPPDATA%\Beacon\`. On a server, it's the BeaconServer install root.

## Lua mods

Quickest path to "something working":

1. Create `ue4ss\Mods\HelloBeacon\enabled.txt` (empty file — UE4SS uses this as the on/off switch).
2. Create `ue4ss\Mods\HelloBeacon\Scripts\main.lua`:

```lua
print("[HelloBeacon] loaded")

-- Print player names to the server console every 10 seconds
local LoopAsync = require("LoopAsync")

LoopAsync(10000, function()
    local PlayerController = UEHelpers.GetPlayerController()
    if not PlayerController then return false end

    print("[HelloBeacon] PlayerController: " .. PlayerController:GetFullName())
    return false -- continue looping
end)
```

3. Restart the server (or the client, if it's a client mod). UE4SS prints `[HelloBeacon] loaded` in its console.

The full Lua API surface is documented at the [UE4SS Lua API reference](https://docs.ue4ss.com/dev/lua-api.html). Anything in there works here.

### Bundled BeaconConnect mod

Beacon ships one Lua mod by default: `BeaconConnect`, which fires the `open <host>:<port>` console command on the client after the world finishes loading. That's the mod that actually moves the player off the main menu into your server. You can look at it as a working reference for hooking world-load events:

```
ue4ss\Mods\BeaconConnect\Scripts\main.lua
```

## C++ mods

C++ mods are loaded by Beacon's host loader (not by UE4SS directly). They export a single entry point:

```cpp
// my_mod.cpp
#include "BeaconHost.h"

extern "C" __declspec(dllexport) void beacon_mod_init(const BeaconHostApi* api)
{
    api->log("[MyMod] loaded");
}
```

Build as a Windows x64 DLL, drop it at `<install>\Beacon\mods\my_mod.dll`. Beacon's loader will pick it up at start.

The `BeaconHostApi` struct exposes:

- `log(const char*)` — write to the Beacon log
- `instance_id` — unique id for this server instance (useful in multi-instance setups)
- Future-reserved fields for event hooks (player join/leave, command dispatch)

The reference loader is at `src/native/Beacon.Plugin/src/BeaconPlugin.cpp` in the repo. Read it if you need to know what the host calls and when.

## Running mods on a managed host

If your server is hosted by [SurvivalServers](https://www.survivalservers.com/games/subnautica_2/?utm_source=github&utm_medium=docs_mods&utm_campaign=beacon), you can upload mod folders through the panel's file manager — drop them under `ue4ss\Mods\` and restart the server.

## Distributing your mod

Beacon doesn't have a central mod registry. Distribute your mod the way the rest of the UE4SS ecosystem does: a GitHub release zip with the mod folder structure inside.

If your mod has client-side state that has to match server-side state, ship two folders (or two builds) and document which goes where.

## Troubleshooting

**My Lua mod doesn't run.**
Check `<install>\ue4ss\UE4SS.log` for parse errors. If your mod folder is missing `enabled.txt`, UE4SS skips it.

**My C++ mod doesn't load.**
Beacon's log (`<install>\Beacon\Logs\beaconserver.log` on the server, the launcher's own log on the client) shows DLL-load failures. Most common cause is a missing dependency — `dumpbin /dependents my_mod.dll` reveals what Windows is looking for.

**Hooks fire but values are wrong.**
SN2 is UE 5.6. Make sure your UE4SS install is built for 5.6 and that `UE4SS-settings.ini` has `EngineVersion=5.6` set — if it falls back to 5.x autodetect on SN2 the AOB scans time out and your hooks never wire up.

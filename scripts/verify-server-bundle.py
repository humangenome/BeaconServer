#!/usr/bin/env python3
"""Verify a Beacon server bundle zip before it is published.

The server bundle is the hoster artifact: the .NET supervisor PLUS the UE4SS
runtime and the Beacon server mods. A zip that carries only the supervisor
publish output is a valid, internally consistent, correctly checksummed zip --
and a completely broken server. It boots Subnautica 2 as a plain listen server
with no Beacon runtime, which players experience as connecting, seeing the
session, and bouncing straight back to the main menu.

That artifact has shipped by accident more than once, so every publish path
runs this check and fails closed on anything it cannot positively confirm.

    python scripts/verify-server-bundle.py <bundle.zip> [--min-bytes N]

Exit 0 only when the zip is a full bundle. Any other outcome exits 1.
"""

import argparse
import os
import sys
import zipfile

# Smallest full bundle ever published is ~105 MB; the broken supervisor-only
# artifacts were 47-54 MB. 90 MB sits well clear of both.
DEFAULT_MIN_BYTES = 90_000_000

# Minimum number of mod directories under ue4ss/Mods/. The bundle has carried
# at least ten since the server mod set stabilised.
MIN_MOD_DIRS = 10

# UE4SS.dll is ~16 MB. A truncated or placeholder file is not a runtime.
MIN_UE4SS_DLL_BYTES = 1_000_000

# Every one of these must be present and non-empty. The ue4ss/Mods entries are
# the mods without which the host misbehaves in ways that read to a player as
# "the server is broken": no auth gate, no roster, phantom host players in the
# lifepod gate, and a story-goal gate that never opens.
REQUIRED_ENTRIES = (
    "BeaconServer/BeaconServer.exe",
    "BeaconServer/BeaconServer.dll",
    "BeaconServer/appsettings.json",
    "Beacon.dll",
    "tools/beacon-injector.exe",
    "ue4ss/UE4SS.dll",
    "ue4ss/dwmapi.dll",
    "ue4ss/UE4SS-settings.ini",
    "ue4ss/Mods/mods.txt",
    "ue4ss/Mods/BeaconServerRuntime/Scripts/main.lua",
    "ue4ss/Mods/BeaconLoader/Scripts/main.lua",
    "ue4ss/Mods/BeaconAuth/Scripts/main.lua",
    "ue4ss/Mods/BeaconRoster/Scripts/main.lua",
    "ue4ss/Mods/BeaconNoPhantomHost/Scripts/main.lua",
    "ue4ss/Mods/BeaconStoryGoalUnlock/Scripts/main.lua",
)


def normalise(name):
    return name.replace("\\", "/").lstrip("./")


def enabled_mods(text):
    """Parse ue4ss Mods/mods.txt -> the names with a trailing ': 1'."""
    names = []
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith(";") or line.startswith("#"):
            continue
        if ":" not in line:
            continue
        name, _, state = line.partition(":")
        if state.strip() == "1":
            names.append(name.strip())
    return names


def verify(path, min_bytes):
    failures = []

    if not os.path.isfile(path):
        return ["not a file: {}".format(path)]

    size = os.path.getsize(path)
    if size < min_bytes:
        failures.append(
            "size {:,} bytes is below the {:,} byte floor -- this looks like a "
            "supervisor-only build, not the full bundle".format(size, min_bytes)
        )

    try:
        zf = zipfile.ZipFile(path)
    except Exception as exc:  # noqa: BLE001 - fail closed on anything
        return failures + ["cannot open as a zip: {}".format(exc)]

    with zf:
        bad = zf.testzip()
        if bad is not None:
            failures.append("corrupt entry: {}".format(bad))

        sizes = {}
        for info in zf.infolist():
            name = normalise(info.filename)
            if name.endswith("/"):
                continue
            sizes[name.lower()] = info.file_size

        for entry in REQUIRED_ENTRIES:
            key = entry.lower()
            if key not in sizes:
                failures.append("missing required entry: {}".format(entry))
            elif sizes[key] == 0:
                failures.append("required entry is empty: {}".format(entry))

        ue4ss_dll = sizes.get("ue4ss/ue4ss.dll")
        if ue4ss_dll is not None and ue4ss_dll < MIN_UE4SS_DLL_BYTES:
            failures.append(
                "ue4ss/UE4SS.dll is {:,} bytes, below the {:,} byte floor".format(
                    ue4ss_dll, MIN_UE4SS_DLL_BYTES
                )
            )

        mod_dirs = set()
        for name in sizes:
            if name.startswith("ue4ss/mods/"):
                rest = name[len("ue4ss/mods/"):]
                if "/" in rest:
                    mod_dirs.add(rest.split("/", 1)[0])
        if len(mod_dirs) < MIN_MOD_DIRS:
            failures.append(
                "only {} mod directories under ue4ss/Mods/ (expected at least "
                "{}): {}".format(
                    len(mod_dirs), MIN_MOD_DIRS, sorted(mod_dirs) or "none"
                )
            )

        # Anything mods.txt switches on has to actually be in the zip, or UE4SS
        # boots with a mod list that references nothing.
        try:
            manifest = zf.read("ue4ss/Mods/mods.txt").decode("utf-8-sig", "replace")
        except KeyError:
            manifest = None
        except Exception as exc:  # noqa: BLE001
            manifest = None
            failures.append("cannot read ue4ss/Mods/mods.txt: {}".format(exc))
        if manifest:
            for mod in enabled_mods(manifest):
                key = "ue4ss/mods/{}/scripts/main.lua".format(mod.lower())
                if key not in sizes:
                    failures.append(
                        "mods.txt enables '{}' but the zip has no "
                        "ue4ss/Mods/{}/Scripts/main.lua".format(mod, mod)
                    )

    return failures


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("zip", help="path to the server bundle zip")
    parser.add_argument(
        "--min-bytes",
        type=int,
        default=DEFAULT_MIN_BYTES,
        help="minimum acceptable zip size in bytes (default {})".format(
            DEFAULT_MIN_BYTES
        ),
    )
    args = parser.parse_args(argv)

    try:
        failures = verify(args.zip, args.min_bytes)
    except Exception as exc:  # noqa: BLE001 - never pass on an unexpected error
        print("FAIL {}: unexpected error: {}".format(args.zip, exc))
        return 1

    if failures:
        print("FAIL {} is not a valid Beacon server bundle:".format(args.zip))
        for line in failures:
            print("  - {}".format(line))
        print(
            "\nDo not publish this artifact. The full bundle carries the UE4SS "
            "runtime and the Beacon server mods alongside the supervisor; a "
            "supervisor-only zip produces a host that players connect to and "
            "immediately bounce off."
        )
        return 1

    print(
        "OK {} is a full server bundle ({:,} bytes)".format(
            args.zip, os.path.getsize(args.zip)
        )
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

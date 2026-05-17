# Contributing to BeaconServer

Short and to the point.

## Reporting bugs

Open an issue. Include:

- BeaconServer version (`status` over RCON, or `/api/v1/health`)
- Subnautica 2 build ID
- Steps to reproduce
- Server log excerpt (`logs/beaconserver-*.ndjson`)
- Whether anyone else can reproduce on a clean server

If your issue is about specific managed hosting (panel, billing, support), please contact your host directly. BeaconServer's GitHub issues are for the open-source server itself.

## Feature requests

Open an issue. Describe the use case, not the implementation. If you're proposing a wire-format or protocol change, point at the affected file(s) in `src/shared/Beacon.Protocol/`.

## Pull requests

- Branch from `main`, name `feat/<short-slug>` or `fix/<short-slug>`
- Keep commits short and focused — one logical change per commit
- Match existing code style
- For new dependencies, justify in the PR description and pin the version in `Directory.Packages.props`
- Run the test suite locally before opening the PR (`dotnet test BeaconServer.sln -c Release`)

## Code of conduct

Be civil. Be technical. Don't post game-piracy or anti-cheat-evasion material in issues or PRs.

# Security Policy

## Reporting a vulnerability

If you've found a security issue in Beacon (the server, client plugin, launcher, or admin API), please **do not** open a public GitHub issue.

Email security reports to: **security@humangenome.dev**, or open a private security advisory on GitHub at https://github.com/HumanGenome/Beacon/security/advisories/new — that's the preferred channel.

Include:
- A description of the vulnerability
- Steps to reproduce
- Affected component (server / plugin / launcher / API)
- Beacon version
- Whether the issue is currently being exploited

We aim to acknowledge reports within 72 hours and provide a triage update within 7 days.

## Scope

In scope:
- Remote code execution or unauthenticated takeover of `BeaconServer.exe`
- Authentication bypass on join handshake, RCON, or admin API
- IPC injection through the plugin / BeaconServer named pipe
- Save file corruption that lets a connected client write arbitrary host files
- Privilege escalation through plugin hooks

Out of scope:
- Hardware-host vulnerabilities (those belong to your hosting provider)
- Vulnerabilities in retail Subnautica 2 itself (report to Krafton)
- Vulnerabilities in third-party mods running on Beacon
- Anti-cheat / cheating concerns — Beacon does not provide anti-cheat

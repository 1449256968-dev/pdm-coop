# Pandemonium Classic — Unity Edition · Multiplayer Co-op Module

**English** | [中文](README.md)

Play *Pandemonium Classic* with **up to 8 people**: host-authoritative simulation, client-side prediction, direct / domain / virtual-LAN connections.

> Version **v0.7.1-p20** · protocol **20** · Unity **2021.3.15f1** · downloads in [Releases](https://github.com/1449256968-dev/pdm-coop/releases)

---

## What it does

- **Up to 8 players in one world** (1 host + 7 joiners) sharing NPCs and story progress.
- **Instant response for joiners**: local movement prediction, reconciled against the host, so movement never waits for the round trip.
- **Three ways to connect**: IP address, domain name (resolved automatically), or a virtual LAN / tunnel.
- **In-game connection UI**: address, port and nickname, live connection state and RTT; the original in-game panel is kept.
- **Host authority**: attacks, interactions, items, skills and story beats are decided by the host; joiners cannot rewrite the world.
- **Complete presentation**: other players, appearance and held items, attack arcs, nameplates and status bars; spectator view.
- **Independent character selection**: each player picks their own species.
- **Usable on weak links**: sync bandwidth adapts to the measured link and backs off under congestion instead of falling seconds behind.
- **Vanilla preserved**: launching without any flags is the original single-player game.

## Quick start

1. Download the module package `pdm-coop-module-*.zip` from [Releases](https://github.com/1449256968-dev/pdm-coop/releases).
2. Extract it over your project, keeping the `Assets/Scripts/Multiplayer/` layout.
3. Open the project in Unity and wait for the import to finish (`.meta` files are generated on first import).
4. Build the game the way your project already does.
5. Multiplayer: the host starts a game and plays; joiners enter the host IP or domain in the in-game panel. Or use the command line:

   ```powershell
   # host
   "Pandemonium Classic - Unity Edition.exe" -coopHost 7777 -coopStartGame
   # joiner
   "Pandemonium Classic - Unity Edition.exe" -coopJoin 26.72.60.203:7777
   ```

6. Allow **UDP 7777** through the host firewall; for remote play use a virtual LAN (Radmin VPN / Tailscale / ZeroTier) or port forwarding.

Full steps (virtual LAN, port forwarding, troubleshooting) are in **[INSTALL.en.md](INSTALL.en.md)**.

## Requirements

| Item | Requirement |
|---|---|
| Engine | Unity **2021.3.15f1** (Mono, Built-in render pipeline; other versions untested) |
| Networking | Mirror + kcp2k (the required sources ship in the module package) |
| Protocol | **20** — every player must run the **same build**; older versions cannot connect |
| Platform | verified on Windows x64 only |
| Players | 1 host + up to 7 joiners |

## Command-line flags

| Flag | Purpose |
|---|---|
| `-coopHost <port>` | host and listen (default 7777) |
| `-coopStartGame` | start a round automatically (with `-coopHost`) |
| `-coopJoin <address>:<port>` | join a room; the address may be an IP or a domain |
| `-coopUsername <name>` | set the nickname (percent-escape non-ASCII) |
| `-coopLowLatency` | low-latency mode (shorter interpolation buffer; LAN/direct links) |
| `-coopNoPrediction` | disable local movement prediction (diagnostics) |
| `-coopUploadKBps <KB/s>` | cap the host's total upstream budget (weak/limited links) |

## FAQ

- **A joiner cannot act / someone freezes** → use **v0.7.1-p20 or newer**: it fixes the connection form suppressing input and snapshot congestion kicking members out.
- **Cannot connect** → check the host is hosting, UDP 7777 is allowed, address/port are correct and both sides run the **same protocol**.
- **Remote play** → install Radmin VPN / Tailscale / ZeroTier and connect to the peer's virtual IP, or forward the port.
- **High latency / other players drifting** → check the panel RTT (a relay will be > 80 ms); try `-coopLowLatency`, and `-coopUploadKBps` on weak links.
- More troubleshooting in [INSTALL.en.md](INSTALL.en.md) section 5.

## Documentation

| File | Content |
|---|---|
| [INSTALL.md](INSTALL.md) / [INSTALL.en.md](INSTALL.en.md) | installation, multiplayer, ports and firewall, troubleshooting |
| [CHANGELOG.md](CHANGELOG.md) / [CHANGELOG.en.md](CHANGELOG.en.md) | version history and upgrade steps |
| [docs/IMPLEMENTATION.en.md](docs/IMPLEMENTATION.en.md) | architecture, protocol, replication model (for development) |
| [docs/PATCH_SITES.en.md](docs/PATCH_SITES.en.md) | list of edits made to the original scripts |
| [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) | third-party components and licences |

## License

The module and tooling are released under **MIT** (see [LICENSE](LICENSE)); the original game assets are
copyright their authors and are not included in this repository.

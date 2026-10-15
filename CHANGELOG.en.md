# Changelog

**English** | [中文](CHANGELOG.md)

> Version history of the multiplayer co-op module. Current release **v0.7.1-p17**
> (protocol 17, `GameBuild = Fork0.7.1`).

## v0.7.1-p17 — first public module package

The co-op module was split out of the full fork and published as a reusable module
(protocol **17**, `GameBuild = Fork0.7.1`).

**Included**

- `Assets/Scripts/Multiplayer/` — 55 files: host-authoritative networking, snapshot replication,
  pose/input streams, remote-actor context, authoritative room settings with tamper rollback,
  10 validation suites
- `Tools/` — 12 scripts: multi-player performance runs, regression suite, real-keyboard live tests,
  one-command sessions
- `docs/PATCH_SITES.md` — integration checklist: **99 files / 582 hooks**, categorised, with a 10-step order
- `README.md` / `README.en.md` / `INSTALL.md` / `INSTALL.en.md` / `LICENSE` (MIT) / third-party notices

**Not included**: the original game's code, scenes or asset payload. Integrate it into your own recovered project.

**Measured numbers** (four processes on one machine, 1280×720, all four players really moving and attacking)

| Scenario | Host | Joiners |
|---|---:|---:|
| Before optimisation | 52.82 | 55.83–56.49 |
| This build (static world) | 58.29 | 59.73–59.78 |
| This build (real activity, VSync off) | 50.8–55.3 | 58.1–59.4 |
| Ceiling (state replication frozen) | 59.72 | 59.95 |

**Main fixes and optimisations in this round**

- **Host stuck at 30 FPS (important)**: the project's Ultra/High quality levels use `vSyncCount: 1`;
  a host under real load spends ~18.1 ms per frame, misses every vblank and lands on **exactly 30 FPS**.
  The host now forces `vSyncCount = 0` + `targetFrameRate = 60` and re-asserts it every 0.5 s;
  **joiner display settings are untouched**.
- **World capture** 21.95 → 11–14 ms; **client snapshot apply** 19.44 → 6.48 ms; appearance apply
  5.97 → 0.21 ms; decode 23.95 → 7.95 ms.
- **Movement feel**: input 30 → **60 Hz**; poses 30 → **60 Hz (single-chunk) / 30 Hz (automatic fallback
  when the host is tight or several chunks are needed)**; pose interpolation held frames 112–242 →
  **4–14 per 60 s**.
- Clients skip unchanged field bodies and appearance bytes (128-bit hashes); the appearance cache now
  handles textures whose pixels change per frame and fully re-verifies about every 2 s.
- Host settings deep-compare 4 Hz → 1 Hz (the client's tamper rollback still happens within one 0.25 s poll).
- Encode/decode worker threads lowered to `BelowNormal` so they no longer compete with main threads when
  several instances share a machine.
- Compatibility flags `-coopCompatInput30` / `-coopCompatMotion30` / `-coopCompatFullApply` revert this
  round's optimisations one at a time for bisection.

**Upgrade notes**

- Every player must run the **same build** (protocol 17); cross-protocol handshakes are rejected.
- Re-integrate the module into your project using the checklist in `docs/PATCH_SITES.md`.

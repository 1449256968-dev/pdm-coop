# Pandemonium Classic — Unity Edition · Multiplayer Co-op Module (up to 8 players)

**English** | [中文](README.md)

> **In one sentence** — a host-authoritative multiplayer co-op module for the recovered Unity project of
> *Pandemonium Classic - Unity Edition* (up to 8 players), with self-contained snapshot replication,
> a 60 Hz input stream plus adaptive pose stream, a remote-actor context, authoritative room settings with
> tamper rollback, and one-command performance / regression / live-test tooling.

| | |
|---|---|
| Engine / network | Unity **2021.3.15f1** (Mono, Built-in RP) + **Mirror v96.11.3 / kcp2k** |
| Protocol | `ProtocolVersion = 17`, `GameBuild = Fork0.7.1` (every player must run the same build) |
| Players / port | up to **8** (host + 7 joiners); default UDP **7777** |
| Measured FPS | four processes on one machine (1 host + 3 joiners): host **58.29** / joiners **59.73–59.78** |
| Design goals | ① smooth non-host movement (Minecraft-like, no client-side prediction) ② 60 FPS for everyone |
| Repo contents | module source (55 files) + tooling (12 scripts) + full docs; **no original game code/scenes/assets** |

**中文 TL;DR** — 为《Pandemonium Classic - Unity Edition》恢复工程实现的房主权威联机合作模块（最多 8 人），
基于 Mirror/kcp2k（协议 17）。本仓库只有模块源码、工具与文档；需要接入到自己的恢复工程。
接入清单：[docs/PATCH_SITES.md](docs/PATCH_SITES.md)（[English](docs/PATCH_SITES.en.md)） · 安装与联机：[INSTALL.md](INSTALL.md) · [中文文档](README.md)

> **This repository ships the module only.** You supply the original game's code, scenes and assets —
> which keeps the co-op work publishable without hosting the original commercial game content.

Two design goals:

1. **Smooth non-host movement** — Minecraft-like feel, approaching it from "60 Hz input + adaptive pose
   stream + low interpolation delay" (there is no client-side prediction);
2. **60 FPS for everyone** — measured with 1 host + 3 joiners in four processes on one machine.

---

## Contents

- [1. What this repository is / is not](#1-what-this-repository-is--is-not)
- [2. Capabilities and measured numbers](#2-capabilities-and-measured-numbers)
- [3. Architecture](#3-architecture)
- [4. Protocol and messages](#4-protocol-and-messages)
- [5. Replication model](#5-replication-model)
- [6. Module file map](#6-module-file-map)
- [7. Integrating into your own project](#7-integrating-into-your-own-project)
- [8. Build and run](#8-build-and-run)
- [9. Tooling and validation suites](#9-tooling-and-validation-suites)
- [10. Command-line flags](#10-command-line-flags)
- [11. FAQ](#11-faq)
- [12. License and third parties](#12-license-and-third-parties)

---

## 1. What this repository is / is not

| | |
|---|---|
| ✅ **Is** | The complete source of a host-authoritative co-op module; one-command perf/regression/live test scripts; protocol and replication design docs; an integration checklist |
| ✅ **Is** | Reusable design ideas: self-contained snapshots, split reliable/unreliable channels, remote characters driven by the *original* gameplay code, authoritative room settings with tamper rollback |
| ❌ **Is not** | A runtime hot-plug plugin: it needs hooks inserted into the original project's source ([docs/PATCH_SITES.md](docs/PATCH_SITES.md): 99 files / 582 hooks) |
| ❌ **Is not** | A playable game out of the box: no original code, scenes or assets included |
| ❌ **Is not** | Engine-agnostic: Unity 2021.3 + Mirror/kcp2k, and it relies on the original game's "single local player" world view |

## 2. Capabilities and measured numbers

- **Players**: up to 8 (host + 7 joiners, `CoopNetworkManager.MaxPlayersIncludingHost = 8`)
- **Protocol version**: 17; `GameBuild = Fork0.7.1` (every player must run the same build)
- **Networking**: Mirror v96.11.3 + kcp2k (UDP, default port **7777**)

| Scenario (four processes on one machine, 1280×720, all four players really moving and attacking) | Host | Joiners |
|---|---:|---:|
| Before optimisation | 52.82 | 55.83–56.49 |
| Shipped build (static world) | **58.29** | **59.73–59.78** |
| Shipped build (real activity, VSync off) | 50.8–55.3 | 58.1–59.4 |
| Ceiling (state replication frozen) | 59.72 | 59.95 |

Stage timings (host): world capture 21.95 → **11–14 ms**; client apply 19.44 → **6.48 ms**;
client appearance apply 5.97 → **0.21 ms**; decode 23.95 → **7.95 ms**.
Movement metrics: pose interval 33.3 → **16.7 ms**; local "held frames" 112–242 → **4–14 per 60 s**; input 30 → **60 Hz**.

> ⚠️ **The VSync 60/30 cliff.** The project's Medium-and-above quality levels use `vSyncCount: 1`.
> A host under real load spends ~18.1 ms per frame; once that crosses the 16.67 ms vblank period it
> misses every refresh and lands on **exactly 30 FPS** (joiners stay near 59 at ~16.9 ms).
> This module therefore forces `vSyncCount = 0` + `targetFrameRate = 60` **on the host side** and
> re-asserts it every 0.5 s — **joiner display settings are never touched**.

## 3. Architecture

```
                 Joiner (client)                          Host (authority)
  ┌────────────────────────────────┐        ┌────────────────────────────────────┐
  │ view / mouse look: local, now  │        │ authoritative simulation of every   │
  │ no client-side prediction      │        │ player and every NPC                │
  │                                │        │ CoopActorContext makes the original │
  │                                │        │ code believe it drives that actor   │
  │ input samples 60 Hz ──unrel────▶│───────▶│ CoopPlayerInputFlow.Read(playerId) │
  │ action samples     ──reliable─▶│───────▶│ CoopActions.RunOriginal(actor)     │
  │                                │        │ world capture ~10 Hz (full tables) │
  │ pose stream        ◀──unrel────│────────│ CoopMotion (30/60 Hz adaptive)     │
  │ state snapshots    ◀──reliable─│────────│ CoopSnapshotCodec envelope + GZip  │
  │ recipes/textures   ◀──reliable─│────────│ CoopRecipeChunk / CoopStateChunk   │
  └────────────────────────────────┘        └────────────────────────────────────┘
```

Principles:

- **No gameplay rules are duplicated**: remote characters run the original code; the net layer only transports.
- **Host authority**: clients send intent only and never mutate world state.
- **Self-contained snapshots**: every snapshot carries the complete node/definition table, so packet loss,
  reordering and late joins cannot leave a client depending on an earlier packet.
- **Split channels**: state and actions are reliable; movement input and poses are unreliable (drop them, the next one arrives).

## 4. Protocol and messages

Messages live in `CoopMessages.cs` and the individual modules (protocol version **17**):

| Message | Channel | Direction | Rate | Purpose |
|---|---|---|---|---|
| `CoopHelloMessage` / `CoopWelcomeMessage` | reliable | C↔S | on connect | handshake, player id assignment, version check |
| `CoopPlayerNamesMessage` | reliable | S→C | on change | roster and usernames |
| `CoopPlayerInputMessage` | **unreliable** | C→S | **60 Hz** | movement input (strafe/forward/yaw/run + increasing sequence) |
| `CoopMotionFrame` | **unreliable** | S→C | **30/60 Hz adaptive** | character poses (id + position + rotation) |
| `CoopStateChunk` | reliable | S→C | ~10 Hz, chunked | world snapshot chunks (binary envelope + GZip) |
| `CoopRecipeChunk` | reliable | S→C | on demand | definition/texture resource chunks (with leases) |
| `CoopRoomSettingsMessage` | reliable | S→C | on change | authoritative room settings |
| `CoopWorldStartMessage` | reliable | S→C | world start | shared map seed/topology |
| `CoopDialogueMessage` / `CoopGameLogMessage` / `CoopSoundMessage` / `CoopMusicMessage` | reliable | S→C | on event | dialogue, log, audio sync |

**Adaptive pose rate**: while the whole world's poses fit in a single message (≤24 characters, KCP batch
threshold 1184 B) the stream runs at 60 Hz; when several chunks are needed, or the host's frame-time EMA
exceeds 20 ms, it falls back to 30 Hz (invisible to joiners apart from coarser sample spacing).

**Input expiry**: the host treats samples older than `MaxInputAge = 0.25 s` as neutral, so a stalling
client cannot make its character run wild.

## 5. Replication model

One snapshot (`CoopReplication` + `CoopCachedStateGraph` + `CoopAppearance`) contains:

1. **Complete node/definition table** — every row carries a version; unchanged rows are still re-serialised
   to detect change.
2. **Entity field bodies** — fields owned by physics/pose replication are skipped (position, velocity, kinematic, …).
3. **Appearance bytes** — renderer/material/line appearance. The host uses a **128-bit key cache**
   (instance id, sibling path, type, enabled/activeSelf, local TRS, material colour/texture/shader instanceID,
   LineRenderer data). Textures whose pixels can change per frame (RenderTextures, unnamed generated
   Texture2Ds) are marked dynamic and fully re-verified about every 2 s.
4. **Collection version fingerprints** — to tell whether collection-typed fields changed.
5. **Client-side skipping** — field bodies and appearance bytes are hash-compared; unchanged payloads are
   neither decoded nor written.

Encode/decode worker threads run at `BelowNormal` priority so they never compete with a main thread
(important when several instances share one machine).

## 6. Module file map

`Assets/Scripts/Multiplayer/` (55 files):

| Group | Files |
|---|---|
| Entry / transport | `CoopNetworkManager` `CoopNetworkBootstrap` `CoopVersionAuthenticator` `CoopConnectionPanel` `CoopTopology` `CoopMessages` |
| Session | `CoopSession` `CoopUsername` `CoopWorld` |
| Input | `CoopPlayerInput` `PlayerMovementInput` |
| Motion | `CoopMotion` `CoopFirstPersonVisibility` `CoopReplicaHands` |
| Actions / actor context | `CoopActions` `CoopActorContext` `CoopActorStatics` `CoopPlayerControl` `CoopInteractionTarget` |
| Replication | `CoopReplication` `CoopCachedStateGraph` `CoopStateGraph` `CoopFrozenGraph` `CoopFieldAccess` `CoopCollectionVersion` `CoopSnapshotCodec` `CoopAppearance` `CoopTextureReference` `CoopTextureRecipe` `CoopRemoteAvatarSpawner` `CoopPlayerRegistry` `CoopBillboards` `CoopNameplates` |
| UI / local features | `CoopUiBridge` `CoopDialogue` `CoopGameLog` `CoopInventory` `CoopAudio` `CoopRoomSettings` `CoopRoomSettingsGuard` `CoopSettingsComparison` `CoopCompat` |
| Perf / diagnostics | `CoopPerformanceProbe` `CoopScenarioProbe` `CoopAssimilationProbe` |
| Validation suites | `CoopCachedGraphValidation` `CoopAppearanceValidation` `CoopRecipeValidation` `CoopViewValidation` `CoopInputBurstValidation` `CoopIdentityValidation` `CoopMotionHandsValidation` `CoopInteractionValidation` `CoopMonsterControlValidation` `CoopLifecycleProbe` |

## 7. Integrating into your own project

Full checklist and ordering: **[docs/PATCH_SITES.md](docs/PATCH_SITES.md)**. Summary:

1. Copy `Assets/Scripts/Multiplayer/` (including `.meta`) into your `Assets/Scripts/`.
2. Insert the hooks into the original code following the 10-step checklist (99 files / 582 hooks).
3. Make sure `ProtocolVersion` (17) and `GameBuild` match, and that all players run the same build.
4. Verify with `-coopValidate` and `Tools/RunCoopRegression.ps1` (see §9).

> The scripts in `Tools/` assume the **full fork** layout (`RecoveredUnityProject/ExportedProject/`,
> `Builds/`). Adjust the path variables at the top of each script if your layout differs.

## 8. Build and run

Requirements: **Unity 2021.3.15f1** (Windows x64 / **Mono** / Built-in RP).

```powershell
# trigger a build (the project watches CoopBuild.request and writes back a result)
Set-Content .\RecoveredUnityProject\ExportedProject\CoopBuild.request -Value 'release'
Get-Content .\RecoveredUnityProject\ExportedProject\CoopBuild.result.json   # result=Succeeded when done

# run: host
.\Builds\ConnectionPrototype\Pandemonium Classic - Unity Edition.exe -coopHost 7777 -coopStartGame

# run: joiner
.\Builds\ConnectionPrototype\Pandemonium Classic - Unity Edition.exe -coopJoin 192.168.1.10:7777
```

To tell whether a build is current, check the timestamp of `..._Data\Managed\Assembly-CSharp.dll`
(**not** the .exe).

Player/host-facing instructions (GUI steps, firewall and port forwarding, success indicators,
troubleshooting) are in **[INSTALL.en.md](INSTALL.en.md)**.

## 9. Tooling and validation suites

| Script (`Tools/`) | Purpose |
|---|---|
| `StartPerformanceRun.ps1 -Tag t -PlayerCount 4 -Warmup 10 -Duration 60 [-SkipState]` | multi-player performance run |
| `Wait-PerformanceReport.ps1 -Tag t -Count 4` | wait until every process wrote its report |
| `SummarizePerformance.ps1 -Tags a,b -OutFile x.md` | summarise into a table |
| `RunCoopRegression.ps1 -Prefix ds-reg` | one-command regression suite |
| `TestLiveFourPlayer.ps1 -Players 4 -Validate` | real-keyboard multiplayer test |
| `TestHostUnderLoad.ps1 -Players 4 -Duration 60 [-Compat]` | **host** FPS under real activity |
| `TestLiveRenderedMovement.ps1` | does one client's own character actually move on screen (`ownerStep`) |
| `StartLiveSession.ps1 -Players 4 [-Compat]` | one-command session with probe + input trace, per-process logs |
| `StopCoopTestPlayers.ps1 -Tag t` | clean up test processes (**always run before a perf run**) |

Validation suites (the `-coopValidate` family) currently all **PASS**:
`CoopCachedGraphValidation` (cycles/aliases/2D arrays/late join), `CoopAppearanceValidation` (lossless
appearance schema), `CoopRecipeValidation` (128 cumulative frames, identical pixels), `CoopViewValidation`
(first-person hand visibility), `CoopInputBurstValidation` (two unreliable messages in one frame keep only
the newest), `CoopIdentityValidation` (names/nameplates/menu authority/wall occlusion),
`CoopMotionHandsValidation` (hand extent / camera yaw error / travel), `CoopInteractionValidation`
(11 interaction phases), `CoopMonsterControlValidation` (monster control takeover), `CoopLifecycleProbe`
(second world / reconnect / **client room-settings tamper rollback**).

## 10. Command-line flags

| Flag | Purpose |
|---|---|
| `-coopHost <port>` | host a room (default UDP 7777) |
| `-coopStartGame` | start the shared game immediately after hosting |
| `-coopJoin <ip:port>` | join directly |
| `-coopUsername <name>` | username; non-ASCII must be percent-escaped (e.g. `%E7%8E%A9%E5%AE%B6`) |
| `-coopPerf` (+`-coopPerfPlayers/-coopPerfWarmup/-coopPerfDuration/-coopPerfSkipState`) | performance probe emitting `[CoopPerf]` stage/frame/movement metrics |
| `-coopInputTrace` | input-path trace: `[CoopInput]`/`[CoopInputHost]`/`[CoopInputSim]`/`[CoopMove]` |
| `-coopValidate` | client state validation (hp/timers/items/AI) |
| `-coopUiTrace` | UI transition trace |
| `-coopCompatInput30` / `-coopCompatMotion30` / `-coopCompatFullApply` | revert optimisations one at a time (bisection) |
| `-logFile <path>` | write the log to a file (required when running several instances) |

## 11. FAQ

| Symptom | Cause / fix |
|---|---|
| **Host stuck at 30 FPS** | VSync 60/30 cliff (§2). The host side now disables VSync and caps at 60; changing the quality level re-asserts it within 0.5 s |
| FPS looks wrong / low | Frame-capture tools such as `PresentMon`, recorders or boosters steal a whole core — close them before measuring |
| Joiners kicked / cannot connect | Build mismatch (protocol 17); or the UDP port is not allowed through the firewall / not port-forwarded |
| Port in use, perf script errors | Run `Tools\StopCoopTestPlayers.ps1 -Tag <previous tag>` first |
| Garbled username | Percent-escape non-ASCII on the command line, or type it in the in-game panel |
| `ClipCursor failed: access denied` | Background windows cannot lock the cursor — harmless; the module avoids the modal error dialog |
| Joiner "cannot move but can look around" | Trace the path with `-coopInputTrace` (`[CoopInput]`→`[CoopInputHost]`→`[CoopInputSim] obey=True`→`[CoopMove]`). When scripting key presses, remember Windows does not deliver keyboard input to a non-foreground window |
| Where do I start porting? | Read the checklist in [docs/PATCH_SITES.md](docs/PATCH_SITES.md) and begin with the player update loop, the fixed step and coroutines |

## 12. License and third parties

- The **co-op module and tooling authored for this project** are released under **MIT** (see `LICENSE`).
- This repository **does not contain** the original game's code or assets; that content belongs to its
  rights holders. Use your own legally obtained copy, and **do not redistribute the original game files**.
- Third-party components: **Mirror** v96.11.3 (MIT), **kcp2k** (MIT) and others — see
  `THIRD_PARTY_NOTICES.md` and `ThirdPartyLicenses/`.

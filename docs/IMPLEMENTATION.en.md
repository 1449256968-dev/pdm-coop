# Implementation notes

**English** | [中文](IMPLEMENTATION.md)

Architecture, wire protocol and replication model notes for people extending or debugging the module.
For usage see [README](../README.en.md), for installation [INSTALL](../INSTALL.en.md), for changes
[CHANGELOG](../CHANGELOG.en.md).

---

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

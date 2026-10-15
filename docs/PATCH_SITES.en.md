# Original-project hook inventory (required for integration / porting)

**English** | [中文](PATCH_SITES.md)

This co-op module is **not a runtime plugin**: it needs hooks inserted into your own recovered
`Pandemonium Classic - Unity Edition` project. The table below lists the **99 files / 582 references**
that must be covered, sorted by reference count.

- `UI/authority` — permission gates on host-only settings entry points (typically
  `if (!CoopRoomSettings.CanEdit) return;`); miss one and a client can change a locked setting;
- `sim/actor` — character-simulation integration (`CoopActorContext` / `CoopActions` / `IsReplica` /
  coroutine wrapping), **the hardest and most important category**;
- `replication` — entity enumeration, appearance and object-graph capture entry points;
- `misc` — dialogue / log / merchant interaction entry points.

> The module source (`Assets/Scripts/Multiplayer/`) contains **no** original game code; the table is only
> file names and reference counts, so you can walk your own project and hook it up entry by entry.

## Reference counts

| Original file (under `Assets/Scripts/`) | References | Main category |
|---|---:|---|
|---|---:|---|
| `Assembly-CSharp/PlayerScript.cs` | 126 | sim/actor |
| `Assembly-CSharp/SettingsUI.cs` | 97 | UI/authority |
| `Assembly-CSharp/SpawnSettingsUI.cs` | 58 | UI/authority |
| `Assembly-CSharp/GeneralSettingsUI.cs` | 36 | UI/authority |
| `Assembly-CSharp/GameSystem.cs` | 26 | sim/actor |
| `Assembly-CSharp/Settings.cs` | 17 | UI/authority |
| `Assembly-CSharp/MainMenuUI.cs` | 16 | UI/authority |
| `Assembly-CSharp/MonsterSettingsSection.cs` | 15 | UI/authority |
| `Assembly-CSharp/AllMonsterSettingSection.cs` | 14 | UI/authority |
| `Assembly-CSharp/DiplomacySettingsUI.cs` | 10 | UI/authority |
| `Assembly-CSharp/RenderFunctions.cs` | 10 | replication |
| `Assembly-CSharp/MerchantUI.cs` | 10 | misc |
| `Assembly-CSharp/ItemSettingsUI.cs` | 10 | UI/authority |
| `Assembly-CSharp/CharacterStatus.cs` | 10 | sim/actor |
| `Assembly-CSharp/TradingUI.cs` | 8 | misc |
| `Assembly-CSharp/AlchemyUI.cs` | 6 | misc |
| `Assembly-CSharp/GunDeskUI.cs` | 6 | misc |
| `Assembly-CSharp/InventoryUI.cs` | 5 | sim/actor |
| `Assembly-CSharp/Compass.cs` | 4 | replication |
| `Assembly-CSharp/Minimap.cs` | 4 | replication |
| `Assembly-CSharp/RemoteProjectile3.cs` | 2 | sim/actor |
| `Assembly-CSharp/RemoteProjectile4.cs` | 2 | sim/actor |
| `Assembly-CSharp/RemoteProjectile7.cs` | 2 | sim/actor |
| `Assembly-CSharp/RemoteProjectile2.cs` | 2 | sim/actor |
| `Assembly-CSharp/QuestionUI.cs` | 2 | misc |
| `Assembly-CSharp/LobbedProjectileChild.cs` | 2 | sim/actor |
| `Assembly-CSharp/AllMonsterSettingCompactSection.cs` | 2 | UI/authority |
| `Assembly-CSharp/RemoteProjectile1.cs` | 2 | sim/actor |
| `Assembly-CSharp/RemoteProjectile.cs` | 2 | sim/actor |
| `Assembly-CSharp/CharacterColliderWatcher.cs` | 2 | sim/actor |
| `Assembly-CSharp/MonsterSettingsCompactSection.cs` | 2 | UI/authority |
| `Assembly-CSharp/NPCScript.cs` | 2 | replication |
| `Assembly-CSharp/RemoteModProjectile.cs` | 2 | sim/actor |
| `Assembly-CSharp/OverlayCamera.cs` | 2 | misc |
| `Assembly-CSharp/ThrowProjectile.cs` | 2 | sim/actor |
| `Assembly-CSharp/UFOMover.cs` | 1 | sim/actor |
| `Assembly-CSharp/NixieTrap.cs` | 1 | sim/actor |
| `Assembly-CSharp/UFOActualMover.cs` | 1 | sim/actor |
| `Assembly-CSharp/ParticlesDevice.cs` | 1 | sim/actor |
| `Assembly-CSharp/NPCType.cs` | 1 | misc |
| `Assembly-CSharp/WeremouseJobMarker.cs` | 1 | sim/actor |
| `Assembly-CSharp/NPCAI.cs` | 1 | misc |
| `Assembly-CSharp/OnEnterTrigger.cs` | 1 | sim/actor |
| `Assembly-CSharp/Pod.cs` | 1 | sim/actor |
| `Assembly-CSharp/TFStateText.cs` | 1 | misc |
| `Assembly-CSharp/Sarcophagus.cs` | 1 | sim/actor |
| `Assembly-CSharp/RilmaniMirror.cs` | 1 | sim/actor |
| `Assembly-CSharp/TextInputUI.cs` | 1 | misc |
| `Assembly-CSharp/SlotsMachine.cs` | 1 | sim/actor |
| `Assembly-CSharp/SkeletonPartyEffect.cs` | 1 | sim/actor |
| `Assembly-CSharp/ShotLine.cs` | 1 | sim/actor |
| `Assembly-CSharp/TrapSettingsSection.cs` | 1 | UI/authority |
| `Assembly-CSharp/PumpkinPatch.cs` | 1 | sim/actor |
| `Assembly-CSharp/Portal.cs` | 1 | sim/actor |
| `Assembly-CSharp/RabbitWarrenInteractable.cs` | 1 | sim/actor |
| `Assembly-CSharp/ReuserScroll.cs` | 1 | sim/actor |
| `Assembly-CSharp/TractorBeam.cs` | 1 | sim/actor |
| `Assembly-CSharp/StatueRoomSetupAndMonitor.cs` | 1 | sim/actor |
| `Assembly-CSharp/NewspaperStand.cs` | 1 | sim/actor |
| `Assembly-CSharp/Extractor.cs` | 1 | sim/actor |
| `Assembly-CSharp/FatherTree.cs` | 1 | sim/actor |
| `Assembly-CSharp/ExplosionEffect.cs` | 1 | sim/actor |
| `Assembly-CSharp/DreamerOrb.cs` | 1 | sim/actor |
| `Assembly-CSharp/ElevatorButton.cs` | 1 | sim/actor |
| `Assembly-CSharp/GremlinButton.cs` | 1 | sim/actor |
| `Assembly-CSharp/GremlinFactory.cs` | 1 | sim/actor |
| `Assembly-CSharp/Grave.cs` | 1 | sim/actor |
| `Assembly-CSharp/FloatingText.cs` | 1 | sim/actor |
| `Assembly-CSharp/FlowerBed.cs` | 1 | sim/actor |
| `Assembly-CSharp/CerberusSplitState.cs` | 1 | misc |
| `Assembly-CSharp/CheeseLocation.cs` | 1 | sim/actor |
| `Assembly-CSharp/Augmentor.cs` | 1 | sim/actor |
| `Assembly-CSharp/AlienTube.cs` | 1 | sim/actor |
| `Assembly-CSharp/AttackArc.cs` | 1 | sim/actor |
| `Assembly-CSharp/DirectorTerminal.cs` | 1 | sim/actor |
| `Assembly-CSharp/DraugrRelic.cs` | 1 | sim/actor |
| `Assembly-CSharp/DarkCloud.cs` | 1 | sim/actor |
| `Assembly-CSharp/CherubConverter.cs` | 1 | sim/actor |
| `Assembly-CSharp/CryptRoomEffect.cs` | 1 | sim/actor |
| `Assembly-CSharp/MarionetteTrap.cs` | 1 | sim/actor |
| `Assembly-CSharp/MermaidMusicBox.cs` | 1 | sim/actor |
| `Assembly-CSharp/LobbedProjectile.cs` | 1 | sim/actor |
| `Assembly-CSharp/Lamp.cs` | 1 | sim/actor |
| `Assembly-CSharp/LeshyBud.cs` | 1 | sim/actor |
| `Assembly-CSharp/MultiOptionUI.cs` | 1 | misc |
| `Assembly-CSharp/Mushroom.cs` | 1 | sim/actor |
| `Assembly-CSharp/Moon.cs` | 1 | sim/actor |
| `Assembly-CSharp/MimeWall.cs` | 1 | sim/actor |
| `Assembly-CSharp/MookConverter.cs` | 1 | sim/actor |
| `Assembly-CSharp/HolyShrine.cs` | 1 | sim/actor |
| `Assembly-CSharp/HolyswordBullet.cs` | 1 | sim/actor |
| `Assembly-CSharp/HiveVolunteerFloor.cs` | 1 | sim/actor |
| `Assembly-CSharp/HarpyNest.cs` | 1 | sim/actor |
| `Assembly-CSharp/HellhoundBrimstone.cs` | 1 | sim/actor |
| `Assembly-CSharp/ItemSpawnSlider.cs` | 1 | UI/authority |
| `Assembly-CSharp/LakeLotusSpawner.cs` | 1 | sim/actor |
| `Assembly-CSharp/ItemOrb.cs` | 1 | sim/actor |
| `Assembly-CSharp/InputScript.cs` | 1 | sim/actor |
| `Assembly-CSharp/ItemCrate.cs` | 1 | sim/actor |

**Total: 99 files / 582 references** (plus 20 `BindingFlags` private-field reflections inside the module).

## Integration checklist

Integrate in dependency order; each step can be verified on its own:

1. **Boot and transport** — create the carrier objects at game start: `KcpTransport` + `CoopNetworkManager`
   + `CoopConnectionPanel` (see `CoopNetworkBootstrap`). Check `ProtocolVersion` (currently 17) and `GameBuild`.
2. **Player update loop** — add the replica branch at the top of `PlayerScript.Update`:
   `if (CoopReplication.IsReplica) { CoopActions.ReplicaView(this); return; }`;
   remote characters go through `CoopActions.Instance.RunOriginal(this, CoopOriginalUpdate)`.
   Extract the original `Update` body into `CoopOriginalUpdate()` and add
   `if (IsRemotePlayer && CoopActorContext.Current != this) return;` so a remote character never reads local input.
3. **Fixed step** — in `CharacterStatus.FixedUpdate`, wrap remote characters in
   `using (new CoopActorContext(actor)) { ... }` so the original code believes it drives that actor.
4. **Coroutines** — wrap every original routine that spans frames and reads the "local player" with
   `CoopActorRoutine(actor, routine)` (including nested `IEnumerator`s).
5. **Movement input** — route movement capture through `IPlayerMovementInputSource`:
   local player → `PlayerMovementInput.CaptureLocal(yaw)`; remote character → `CoopRemoteMovementInputSource`.
6. **Action input** — `CoopActions` exposes `Key/Aim` etc.; replace the original direct keyboard/mouse reads with them.
7. **Room-settings gates** — add `if (!CoopRoomSettings.CanEdit) return;` to every host-only settings entry point
   (the vast majority of the 318 UI/authority hooks).
8. **Entity enumeration and appearance** — register the entity types to replicate and the appearance capture
   entry points in `CoopReplication`.
9. **Error dialogs** — ignore unrecoverable background-window log errors from the original project (for example
   Windows' `Failed to confine the cursor - ClipCursor failed`), otherwise they are treated as fatal, raise a modal
   dialog and pause actions.
10. **UI sessions** — when a player opens an original UI, mark the session with `CoopUiBridge` so the host freezes
    that player's movement and actions.

## Known fragility

- **`CoopActorContext` relies on the "only one local player" world view**: it swaps `GameSystem`'s
  `player / playerTransform / playerRotationTransform / activePrimaryCameraTransform`, `inputWatcher`,
  `messageLog / messageColours / playerClues`, and clones hidden player roots, the observer root and all `*UI`
  roots as isolation islands. If the original project changes its UI hierarchy or player structure, the isolation
  **fails silently** (original code then writes into the real local player's camera/UI).
- **Private-field reflection (20 sites)**: a renamed field does not raise an error; it silently corrupts
  replication semantics. Re-check after upgrading the original project.
- **Replication semantics are hand-curated**: which fields must be applied and which must be skipped
  (position belongs to the pose stream; velocity/kinematic belong to host physics) must be confirmed for your project.

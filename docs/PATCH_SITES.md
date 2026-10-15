# 原版工程钩子清单（集成/移植必读）

**中文** | [English](PATCH_SITES.en.md)

本联机模块**不是运行时插件**，它需要在你自己的 `Pandemonium Classic - Unity Edition`
恢复工程里插入若干钩子。下表是把本模块接进工程时必须覆盖的 **99 个文件 / 582 处**引用，
按引用数排序。

- `UI/authority` = 房主专属设置入口的权限门（典型写法 `if (!CoopRoomSettings.CanEdit) return;`），漏一个客户端就能改被锁定的设置；
- `sim/actor` = 角色模拟集成（`CoopActorContext` / `CoopActions` / `IsReplica` / 协程包装），**这是最难也最关键的一类**；
- `replication` = 实体枚举、外观与对象图捕获入口；
- `misc` = 对话/日志/商人等交互入口。

> 本模块源码（`Assets/Scripts/Multiplayer/`）**不含**原版游戏代码；下表只是文件名与引用计数，便于你对照自己的工程逐个接入。

## 引用统计

| 原版文件（`Assets/Scripts/` 下） | 引用数 | 主要类别 |
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

**合计 99 个文件 / 582 处引用**（另有模块内 20 处 `BindingFlags` 私有字段反射）。

## 集成检查清单

按依赖顺序接入，每一步都能单独验证：

1. **启动与传输** — 在游戏启动时创建承载对象：`KcpTransport` + `CoopNetworkManager` + `CoopConnectionPanel`
   （参考 `CoopNetworkBootstrap`）。校验 `ProtocolVersion`（当前 17）与 `GameBuild`。
2. **玩家主循环** — 在 `PlayerScript.Update` 顶部加：副本分支
   `if (CoopReplication.IsReplica) { CoopActions.ReplicaView(this); return; }`；
   远端角色走 `CoopActions.Instance.RunOriginal(this, CoopOriginalUpdate)`。
   把原 `Update` 方法体提取成 `CoopOriginalUpdate()`，并在其中加
   `if (IsRemotePlayer && CoopActorContext.Current != this) return;` 防止远端角色读本地输入。
3. **固定步** — `CharacterStatus.FixedUpdate`：远端角色包一层
   `using (new CoopActorContext(actor)) { ... }`，让原版逻辑以为在操作该角色。
4. **协程** — 所有会跨帧、且内部会读"本地玩家"的原版例程，用 `CoopActorRoutine(actor, routine)` 包装（含嵌套 `IEnumerator`）。
5. **移动输入** — 把移动输入采集改为经由 `IPlayerMovementInputSource`：
   本地玩家 → `PlayerMovementInput.CaptureLocal(yaw)`；远端角色 → `CoopRemoteMovementInputSource`。
6. **动作输入** — `CoopActions` 提供 `Key/Aim` 等查询，替换原版对键盘/鼠标的直接读取。
7. **房间设置权限门** — 所有房主专属设置入口加 `if (!CoopRoomSettings.CanEdit) return;`（318 处的绝大多数）。
8. **实体枚举与外观** — 在 `CoopReplication` 里登记需要同步的实体类型与外观捕获入口。
9. **错误弹窗** — 忽略后台窗口不可恢复的日志错误（例如 Windows 的
   `Failed to confine the cursor - ClipCursor failed`），否则会被当成致命错误弹模态框并暂停动作。
10. **UI 会话** — 玩家打开原版 UI 时用 `CoopUiBridge` 标记会话，宿主据此冻结该玩家的移动与动作。

## 已知脆弱点

- **`CoopActorContext` 依赖"全局只有一个本地玩家"的世界观**：它交换 `GameSystem` 的
  `player / playerTransform / playerRotationTransform / activePrimaryCameraTransform`、
  `inputWatcher`、`messageLog / messageColours / playerClues`，并克隆隐藏的玩家根、observer 根与所有 `*UI` 根作为隔离岛。
  原版一旦改动 UI 层级或玩家结构，隔离会**静默失效**（原版代码会写进真实本地玩家的相机/UI）。
- **私有字段反射（20 处）**：字段改名不会报错，而是让复制语义悄悄错位。升级原版后需重新核对。
- **复制语义是手工策展的**：哪些字段必须应用、哪些必须跳过（位置交给姿态流、velocity/kinematic 交给宿主物理）需要按你的工程逐一确认。

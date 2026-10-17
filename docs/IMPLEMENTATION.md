# 实现说明（IMPLEMENTATION）

**中文** | [English](IMPLEMENTATION.en.md)

本文记录模块的架构、网络协议与复制模型等实现细节，供二次开发与排查参考。
使用方式见 [README](../README.md)，安装联机见 [INSTALL](../INSTALL.md)，版本变更见 [CHANGELOG](../CHANGELOG.md)。

---

## 3. 架构

```
                 加入者（客户端）                        房主（权威）
  ┌────────────────────────────────┐        ┌────────────────────────────────────┐
  │ 视角/鼠标转向：本地即时生效     │        │ 所有玩家 + 所有 NPC 的权威模拟      │
  │ 没有客户端预测                  │        │ CoopActorContext：让原版逻辑       │
  │                                 │        │ "以为"正在操作某个远端角色          │
  │ 输入采样 60Hz ────不可靠───────▶│───────▶│ CoopPlayerInputFlow.Read(playerId) │
  │ 操作采样      ────可靠─────────▶│───────▶│ CoopActions.RunOriginal(actor)     │
  │                                 │        │ 世界捕获 ~10Hz（完整节点/定义表）   │
  │ 姿态流        ◀───不可靠────────│────────│ CoopMotion（30/60Hz 自适应）        │
  │ 状态快照      ◀───可靠分块──────│────────│ CoopSnapshotCodec 二进制信封 + GZip │
  │ 定义/贴图     ◀───可靠分块──────│────────│ CoopRecipeChunk / CoopStateChunk    │
  └────────────────────────────────┘        └────────────────────────────────────┘
```

设计原则：

- **不复制游戏规则**：远端角色直接跑原版代码，联机层只负责搬运与"伪造本地身份"。
- **房主权威**：客户端只发送输入与操作意图，从不修改世界状态。
- **快照自包含**：每个快照都带完整节点/定义表，丢包、乱序、迟到加入都不会依赖上一包而错位。
- **可靠/不可靠分道**：状态与操作走可靠通道；移动输入与姿态走不可靠通道（丢了就丢，下一帧补上）。

## 4. 协议与消息

消息定义见 `CoopMessages.cs` 与各模块（协议版本 **17**）：

| 消息 | 通道 | 方向 | 频率 | 用途 |
|---|---|---|---|---|
| `CoopHelloMessage` / `CoopWelcomeMessage` | 可靠 | C↔S | 连接时 | 握手、分配玩家 id、校验版本 |
| `CoopPlayerNamesMessage` | 可靠 | S→C | 变化时 | 名册与昵称 |
| `CoopPlayerInputMessage` | **不可靠** | C→S | **60Hz** | 移动输入（strafe/forward/yaw/run + 递增序号） |
| `CoopMotionFrame` | **不可靠** | S→C | **30/60Hz 自适应** | 角色姿态（id + position + rotation） |
| `CoopStateChunk` | 可靠 | S→C | ~10Hz 分块 | 世界快照分块（二进制信封 + GZip） |
| `CoopRecipeChunk` | 可靠 | S→C | 按需 | 定义/贴图资源分块（含租约） |
| `CoopRoomSettingsMessage` | 可靠 | S→C | 变化时 | 房间设置权威值 |
| `CoopWorldStartMessage` | 可靠 | S→C | 开局 | 世界开始信号（共享种子/拓扑） |
| `CoopDialogueMessage` / `CoopGameLogMessage` / `CoopSoundMessage` / `CoopMusicMessage` | 可靠 | S→C | 事件时 | 对话、日志、音频同步 |

**姿态流自适应**：整个世界姿态能装进单块消息（≤24 个角色，KCP 批阈值 1184B）时跑 60Hz；
需要多块、或房主帧耗时 EMA 超过 20ms 时自动回退 30Hz（加入者无感，只是采样间隔变粗）。

**输入过期保护**：宿主侧 `MaxInputAge = 0.25s`，超时按零输入处理，避免客户端卡顿导致角色乱跑。

## 5. 复制模型

一次快照（`CoopReplication` + `CoopCachedStateGraph` + `CoopAppearance`）包含：

1. **完整节点/定义表**：对象图每行带版本号；未变化的行仍重新序列化以检测变化。
2. **实体字段体**：跳过由物理/姿态流负责的成员（position、velocity、kinematic 等）。
3. **外观字节**：renderer/material/line 外观。房主侧用 **128-bit 键缓存**（实例 id、兄弟路径、类型、
   enabled/activeSelf、local TRS、material 颜色/贴图/shader instanceID、LineRenderer 数据）；
   "会逐帧变像素"的贴图（RenderTexture、未命名生成的 Texture2D）标记为动态，并每约 2s 完整复核一次。
4. **集合版本指纹**：判断集合类字段是否变化。
5. **客户端跳过未变化载荷**：字段体与外观字节做哈希比较，未变化则不解码、不写入。

解码/编码 worker 线程以 `BelowNormal` 优先级运行，避免与主线程抢 CPU（同机多开时尤其重要）。

## 6. 模块文件地图

`Assets/Scripts/Multiplayer/`（55 个文件）：

| 分组 | 文件 |
|---|---|
| 入口/传输 | `CoopNetworkManager` `CoopNetworkBootstrap` `CoopVersionAuthenticator` `CoopConnectionPanel` `CoopTopology` `CoopMessages` |
| 会话 | `CoopSession` `CoopUsername` `CoopWorld` |
| 输入 | `CoopPlayerInput` `PlayerMovementInput` |
| 移动 | `CoopMotion` `CoopFirstPersonVisibility` `CoopReplicaHands` |
| 动作/角色上下文 | `CoopActions` `CoopActorContext` `CoopActorStatics` `CoopPlayerControl` `CoopInteractionTarget` |
| 复制 | `CoopReplication` `CoopCachedStateGraph` `CoopStateGraph` `CoopFrozenGraph` `CoopFieldAccess` `CoopCollectionVersion` `CoopSnapshotCodec` `CoopAppearance` `CoopTextureReference` `CoopTextureRecipe` `CoopRemoteAvatarSpawner` `CoopPlayerRegistry` `CoopBillboards` `CoopNameplates` |
| UI/本地功能 | `CoopUiBridge` `CoopDialogue` `CoopGameLog` `CoopInventory` `CoopAudio` `CoopRoomSettings` `CoopRoomSettingsGuard` `CoopSettingsComparison` `CoopCompat` |
| 性能/诊断 | `CoopPerformanceProbe` `CoopScenarioProbe` `CoopAssimilationProbe` |
| 验证套件 | `CoopCachedGraphValidation` `CoopAppearanceValidation` `CoopRecipeValidation` `CoopViewValidation` `CoopInputBurstValidation` `CoopIdentityValidation` `CoopMotionHandsValidation` `CoopInteractionValidation` `CoopMonsterControlValidation` `CoopLifecycleProbe` |

# Pandemonium Classic — Unity Edition · 四人联机合作模块

**中文** | [English](README.en.md)

> **一句话说明**：为《Pandemonium Classic - Unity Edition》的 Unity 恢复工程实现的**房主权威多人联机合作模块**
> （最多 8 人），含自包含快照复制、60 Hz 输入流与自适应姿态流、远端角色上下文、房间设置权威与篡改回滚，
> 以及一键跑分 / 回归 / 实机测试工具链。

| 项目 | 内容 |
|---|---|
| 引擎 / 网络 | Unity **2021.3.15f1**（Mono、Built-in RP）+ **Mirror v96.11.3 / kcp2k** |
| 协议 | `ProtocolVersion = 20`、`GameBuild = Fork0.7.1`（所有玩家必须同一构建） |
| 人数 / 端口 | 最多 **8 人**（房主 + 7 名加入者）；UDP 默认 **7777** |
| 实测帧率 | 同机四进程（1 房主 + 3 加入者）：房主 **58.29** / 加入者 **59.73–59.78** |
| 设计目标 | ① 非房主移动平滑不卡顿（参考 Minecraft，无客户端预测）② 所有人平均 60 帧 |
| 仓库内容 | 联机模块源码（55 文件）+ 工具（12 脚本）+ 完整文档；**不含原版游戏代码/场景/资源** |

**English TL;DR** — Host-authoritative co-op multiplayer module (up to 8 players) for the recovered Unity
project of *Pandemonium Classic - Unity Edition*, on Mirror/kcp2k (protocol 20). Ships the module source,
tooling and docs only; integrate it into your own recovered project.
Integration checklist: [docs/PATCH_SITES.md](docs/PATCH_SITES.md) ([English](docs/PATCH_SITES.en.md)) · Install/host/join: [INSTALL.md](INSTALL.md) · [English docs](README.en.md)

> **本仓库只包含联机模块本体**。原版游戏的代码、场景与资源包需要你自备 —— 这样既能让更多人用上联机功能，
> 也避免把原版商业游戏内容公开托管。

---

## 目录

- [1. 这个仓库是什么 / 不是什么](#1-这个仓库是什么--不是什么)
- [2. 能力与实测数据](#2-能力与实测数据)
- [3. 架构](#3-架构)
- [4. 协议与消息](#4-协议与消息)
- [5. 复制模型](#5-复制模型)
- [6. 模块文件地图](#6-模块文件地图)
- [7. 接入你自己的工程](#7-接入你自己的工程)
- [8. 构建与运行](#8-构建与运行)
- [9. 工具与验证套件](#9-工具与验证套件)
- [10. 命令行参数](#10-命令行参数)
- [11. 常见问题](#11-常见问题)
- [12. 许可与第三方](#12-许可与第三方)

---

## 1. 这个仓库是什么 / 不是什么

| | 内容 |
|---|---|
| ✅ **是** | 房主权威联机模块的**完整源码**；一键跑分/回归/实机测试脚本；协议与复制模型文档；接入清单 |
| ✅ **是** | 可复用的设计：快照自包含、可靠/不可靠分道、远端角色跑原版逻辑、房间设置权威与篡改回滚 |
| ❌ **不是** | **运行时热插件**：它需要在原版工程源码里插入钩子（见 [docs/PATCH_SITES.md](docs/PATCH_SITES.md)：99 文件 / 582 处） |
| ❌ **不是** | 开箱即玩的完整游戏：不含原版代码、场景与资源包 |
| ❌ **不是** | 与引擎无关的方案：基于 Unity 2021.3 + Mirror/kcp2k，且依赖原版"单一本地玩家"的世界观 |

## 2. 能力与实测数据

- **人数**：最多 8 人（房主 + 7 名加入者，`CoopNetworkManager.MaxPlayersIncludingHost = 8`）
- **协议版本**：17；`GameBuild = Fork0.7.1`（所有玩家必须同一构建）
- **网络**：Mirror v96.11.3 + kcp2k（UDP，默认端口 **7777**）

| 场景（同机四进程，1280×720，四人真实按键移动+攻击） | 房主 | 加入者 |
|---|---:|---:|
| 优化前 | 52.82 | 55.83–56.49 |
| 交付（静态世界） | **58.29** | **59.73–59.78** |
| 交付（真实活动，vSync 关） | 50.8–55.3 | 58.1–59.4 |
| 天花板（冻结世界同步） | 59.72 | 59.95 |

分阶段耗时（房主）：世界捕获 21.95 → **11–14ms**；客户端应用 19.44 → **6.48ms**；
客户端外观应用 5.97 → **0.21ms**；解码 23.95 → **7.95ms**。
移动指标：姿态间隔 33.3 → **16.7ms**；本地角色"卡帧" 112–242 → **4–14 帧/60s**；输入 30 → **60Hz**。

> ⚠️ **垂直同步的 60/30 悬崖**：工程 Medium 及以上画质档为 `vSyncCount: 1`。房主真实活动下帧耗时约 18.1ms，
> 越过 16.67ms 周期后每帧都错过刷新 → **正好 30 FPS**（加入者 ~16.9ms 仍接近 59）。
> 本模块已在**房主侧**强制 `vSyncCount = 0` + `targetFrameRate = 60` 并每 0.5s 复查，**不改变加入者的画质设置**。

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

## 7. 接入你自己的工程

完整清单与检查顺序见 **[docs/PATCH_SITES.md](docs/PATCH_SITES.md)**。大致流程：

1. 把 `Assets/Scripts/Multiplayer/` 整个目录（含 `.meta`）拷进你的 `Assets/Scripts/`。
2. 按检查清单的 10 个步骤在原版代码里插入钩子（99 个文件 / 582 处）。
3. 确认 `ProtocolVersion`（17）与 `GameBuild`，并让所有玩家使用同一构建。
4. 用 `-coopValidate` 与 `Tools/RunCoopRegression.ps1` 验证（见 §9）。

> 工具脚本（`Tools/`）假设**完整 Fork** 的目录布局（`RecoveredUnityProject/ExportedProject/`、`Builds/`）。
> 若你的工程布局不同，改脚本顶部的路径变量即可。

## 8. 构建与运行

环境：**Unity 2021.3.15f1**（Windows x64 / **Mono** / Built-in RP）。

```powershell
# 触发构建（工程内监听 CoopBuild.request 并写回结果）
Set-Content .\RecoveredUnityProject\ExportedProject\CoopBuild.request -Value 'release'
Get-Content .\RecoveredUnityProject\ExportedProject\CoopBuild.result.json   # result=Succeeded 即完成

# 运行：房主
.\Builds\ConnectionPrototype\Pandemonium Classic - Unity Edition.exe -coopHost 7777 -coopStartGame

# 运行：加入者
.\Builds\ConnectionPrototype\Pandemonium Classic - Unity Edition.exe -coopJoin 192.168.1.10:7777
```

判定构建是否为最新：看 `..._Data\Managed\Assembly-CSharp.dll` 的时间戳，**不要看 exe 的时间戳**。

面向玩家/房主的完整安装与联机说明（含图形界面步骤、防火墙与端口转发、成功判定、排错）见 **[INSTALL.md](INSTALL.md)**。

## 9. 工具与验证套件

| 脚本（`Tools/`） | 用途 |
|---|---|
| `StartPerformanceRun.ps1 -Tag t -PlayerCount 4 -Warmup 10 -Duration 60 [-SkipState]` | 多人性能跑分 |
| `Wait-PerformanceReport.ps1 -Tag t -Count 4` | 等所有进程写出报告 |
| `SummarizePerformance.ps1 -Tags a,b -OutFile x.md` | 汇总为表格 |
| `RunCoopRegression.ps1 -Prefix ds-reg` | 一键回归套件 |
| `TestLiveFourPlayer.ps1 -Players 4 -Validate` | 真实按键驱动的多人联机测试 |
| `TestHostUnderLoad.ps1 -Players 4 -Duration 60 [-Compat]` | 真实活动下的**房主**帧率测量 |
| `TestLiveRenderedMovement.ps1` | 单客户端"自己角色是否在渲染层移动"（`ownerStep`） |
| `StartLiveSession.ps1 -Players 4 [-Compat]` | 一键开一局（自带探针与输入追踪，每进程独立日志） |
| `StopCoopTestPlayers.ps1 -Tag t` | 清理测试进程（**跑分前必用**，否则端口占用会直接抛错） |

验证套件（`-coopValidate` 系列）当前全部 **PASS**：
`CoopCachedGraphValidation`（循环引用/别名/2D 数组/迟到加入）、`CoopAppearanceValidation`（外观 schema 无损）、
`CoopRecipeValidation`（128 帧累积、像素一致）、`CoopViewValidation`（第一人称手部可见性）、
`CoopInputBurstValidation`（同帧两条不可靠消息只保留最新）、`CoopIdentityValidation`（昵称/名牌/菜单权威/墙体遮挡）、
`CoopMotionHandsValidation`（手部范围/相机偏航误差/位移）、`CoopInteractionValidation`（11 个交互阶段）、
`CoopMonsterControlValidation`（怪物控制接管）、`CoopLifecycleProbe`（第二世界/断线重连/**客户端房间设置篡改回滚**）。

## 10. 命令行参数

| 参数 | 作用 |
|---|---|
| `-coopHost <端口>` | 开房监听（默认 UDP 7777） |
| `-coopStartGame` | 开房后直接开始共享游戏 |
| `-coopJoin <IP:端口>` | 直接加入 |
| `-coopUsername <名字>` | 昵称；非 ASCII 需百分号转义（如 `%E7%8E%A9%E5%AE%B6`） |
| `-coopPerf`(+`-coopPerfPlayers/-coopPerfWarmup/-coopPerfDuration/-coopPerfSkipState`) | 性能探针，输出 `[CoopPerf]` 分阶段耗时/帧率/移动指标 |
| `-coopInputTrace` | 输入链路追踪：`[CoopInput]`/`[CoopInputHost]`/`[CoopInputSim]`/`[CoopMove]` |
| `-coopValidate` | 客户端状态校验（hp/计时器/物品/AI） |
| `-coopUiTrace` | UI 切换追踪 |
| `-coopCompatInput30` / `-coopCompatMotion30` / `-coopCompatFullApply` | 一键回退优化项（二分定位用） |
| `-logFile <路径>` | 输出日志到文件（多开/排错必用） |

## 11. 常见问题

| 现象 | 原因与处理 |
|---|---|
| **房主只有 30 帧** | 垂直同步 60/30 悬崖（§2）。房主侧已自动关闭 vSync 并锁 60；若你改过画质档，0.5s 内会被纠正 |
| 帧率数字异常 / 偏低 | 机器上有 `PresentMon`、录屏、加速器等抓帧工具会抢走整个核心；测量前关掉 |
| 加入者被踢 / 连不上 | 构建不一致（协议 20）；或防火墙未放行 UDP 端口 / 未做端口转发 |
| 端口占用、跑分脚本报错 | 先跑 `Tools\StopCoopTestPlayers.ps1 -Tag <上次Tag>` |
| 昵称乱码 | 命令行非 ASCII 用百分号转义，或在游戏界面直接输入 |
| `ClipCursor failed: 拒绝访问` | 后台窗口无法锁定光标，属正常；联机层已避免因此弹模态框 |
| 加入者"按键不动但能转视角" | 用 `-coopInputTrace` 看链路（`[CoopInput]`→`[CoopInputHost]`→`[CoopInputSim] obey=True`→`[CoopMove]`）。自动化脚本模拟按键时注意 Windows 不会把键盘发给非前台窗口 |
| 我该从哪里开始移植 | 先读 [docs/PATCH_SITES.md](docs/PATCH_SITES.md) 的集成检查清单，从"玩家主循环 + 固定步 + 协程"三处开始 |

## 12. 许可与第三方

- 本仓库中**由本项目作者编写的联机模块与工具代码**以 **MIT** 许可发布（见 `LICENSE`）。
- 本仓库**不包含**原版游戏的代码与资源；原版内容版权归其权利人所有，请使用你自己合法拥有的副本。
  若你在自己的工程里接入本模块，请自行确认原版内容的授权范围，**不要公开分发原版游戏文件**。
- 第三方组件：**Mirror** v96.11.3（MIT）、**kcp2k**（MIT）等，见 `THIRD_PARTY_NOTICES.md` 与 `ThirdPartyLicenses/`。

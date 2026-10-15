# 安装与联机指南（INSTALL）

**中文** | [English](INSTALL.en.md)

> **本文说明什么**：如何把《Pandemonium Classic - Unity Edition》四人（最多 8 人）联机合作模式**装起来并玩上** ——
> 玩家/房主路径（拿到游戏目录 → 开房/加入 → 端口与防火墙 → 排错），以及开发者路径（接入模块 → 构建 → 验证）。

本仓库是**联机模块源码**，不是可直接运行的游戏。要玩起来需要一份**已构建好的联机版游戏目录**。
按你的身份选一条路径：

| 你是 | 走哪条 |
|---|---|
| **玩家 / 房主**（不想编译） | [§1 拿到游戏目录](#1-拿到游戏目录) → [§2 联机操作](#2-联机操作) |
| **开发者 / 维护者**（想自己构建、改代码） | [§3 从源码构建](#3-从源码构建) → [§4 接入模块](#4-接入模块到你的工程) |

**English quick start**（assumes you already have a built game folder）:
host: `...exe -coopHost 7777 -coopStartGame` · join: `...exe -coopJoin <host-ip>:7777`
· allow UDP **7777** in the firewall · up to **8 players** (host + 7) · all players must run the **same build**.

---

## 1. 拿到游戏目录

三条现实路径（按省事程度排序）：

1. **让已经玩上的人把整个游戏目录拷给你**（最快）
   目录形如：
   ```
   Pandemonium Classic - Unity Edition.exe
   Pandemonium Classic - Unity Edition_Data\
   ```
   放到**无中文、无空格**的路径下，例如 `D:\PDM-Coop\`。
   ⚠️ 该目录含原版游戏资源，**仅限自用与熟人之间联机，请勿公开再分发**。

2. **下载本仓库 Releases 里的模块包**（`pdm-coop-module-*.zip`）
   内含**模块源码 + 工具 + 文档**，需要你有一份恢复工程才能构建 → 见 [§3](#3-从源码构建)。
   这是给你"自己接入并构建"用的，不是即玩包。

3. **自己接入并构建**（最完整，需要 Unity）→ [§3](#3-从源码构建) + [§4](#4-接入模块到你的工程)。

**首次运行**：Windows 会弹出防火墙提示（首次）→ **勾选专用网络与公用网络并点"允许访问"**。
房主必须允许，否则别人连不上。

---

## 2. 联机操作

前置：所有玩家使用**同一构建**（协议 17、`GameBuild = Fork0.7.1`）；人数上限 **8**（房主 + 7）。

### 2.1 房主

**图形界面**

1. 启动游戏，进入主菜单。
2. 点画面**右上角「联机」按钮**。
3. 填**用户名** → 点**「创建房间」**（端口默认 **7777**，被占用可改）。
4. 正常开一局新游戏；加入者会自动进入同一张地图、同一个种子。

**命令行（可直接开局）**

```powershell
"C:\path\to\Pandemonium Classic - Unity Edition.exe" -coopHost 7777 -coopStartGame -coopUsername %E6%88%BF%E4%B8%BB
```

把地址告诉朋友：局域网用你的**内网 IP**（`ipconfig` 查 IPv4）；公网用**公网 IP + 已转发端口**。

### 2.2 加入者

**图形界面**：右上角**「联机」** → 填**用户名** → **「房主 IP」**填房主地址（如 `192.168.1.10`）
→ **端口**填房主所用端口 → **「加入房间」** → 等房主开局，自动进入同一局。
状态栏显示 **「客户端已验证」** 即成功。

**命令行**

```powershell
"C:\path\to\Pandemonium Classic - Unity Edition.exe" -coopJoin 192.168.1.10:7777 -coopUsername Player1
```

### 2.3 网络与端口

| 场景 | 做法 |
|---|---|
| 同一局域网 | 直接填房主内网 IP；防火墙放行 UDP 端口 |
| 公网直连 | 路由器**端口转发**所选 UDP 端口 → 房主机；Windows 防火墙放行该端口 |
| 不想折腾路由器 | 用 **ZeroTier / Tailscale / Radmin VPN** 组虚拟局域网，加入者填虚拟 IP |

端口只占用 UDP；默认 **7777**（可自定义，但所有人要填同一个）。

### 2.4 验证是否真的连上

日志默认在 `%USERPROFILE%\AppData\LocalLow\tl\Pandemonium Classic - Unity Edition\Player.log`
（或用 `-logFile` 指定），关注：

| 关键字 | 含义 |
|---|---|
| `[Coop] Command line: hosting UDP 7777` | 房主已开房 |
| `[Coop] Command line: joining 192.168.x.x:7777` | 加入者正在连接 |
| `character registered; remote=True` | 房主的权威角色已为某个加入者创建 |
| `Input stream active for player N` | 收到该玩家输入流（移动生效的前提） |
| `Remote player N moved to x,y,z` | 该玩家角色确实在房主侧移动了 |

---

## 3. 从源码构建

**仅当你拥有完整的恢复工程时可用**（本仓库不含原版代码/场景/资源；缺失部分见
[§4.1](#41-把模块放进工程)）。

1. 安装 **Unity 2021.3.15f1**（含 Windows Build Support (Mono)）。本工程为 **Mono** + Built-in RP，不要切 IL2CPP。
2. 把你的恢复工程需要的原版资源目录放回 `Assets/` 对应位置（`StreamingAssets/`、`Texture2D/`、
   `Resources/`、`AudioClip/`、`Mesh/`、`GameObject/` 等）。
3. Unity Hub → **Open** → 选择工程目录（首次导入较慢）。
4. 触发构建并查看结果：

```powershell
Set-Content .\RecoveredUnityProject\ExportedProject\CoopBuild.request -Value 'release'
Get-Content .\RecoveredUnityProject\ExportedProject\CoopBuild.result.json   # result=Succeeded 即完成
```

5. 产物：`Builds\ConnectionPrototype\Pandemonium Classic - Unity Edition.exe`。
   判断新旧看 `..._Data\Managed\Assembly-CSharp.dll` 的时间戳（**不要看 exe 时间戳**）。

## 4. 接入模块到你的工程

### 4.1 把模块放进工程

把本仓库的 `Assets/Scripts/Multiplayer/`（含 `.meta`）整体拷进你的 `Assets/Scripts/`。

### 4.2 插入钩子

按 **[docs/PATCH_SITES.md](docs/PATCH_SITES.md)** 的检查清单，在原版代码里插入钩子
（99 个文件 / 582 处）。建议顺序：

1. 启动与传输（`KcpTransport` + `CoopNetworkManager` + `CoopConnectionPanel`）
2. `PlayerScript.Update` 的副本分支与 `CoopOriginalUpdate`
3. `CharacterStatus.FixedUpdate` 的 `CoopActorContext`
4. 协程包装 `CoopActorRoutine`
5. 移动输入改走 `IPlayerMovementInputSource`
6. `CoopActions` 替换键盘/鼠标直读
7. 房主专属设置入口加 `if (!CoopRoomSettings.CanEdit) return;`
8. 实体枚举与外观捕获入口
9. 忽略后台窗口不可恢复错误（`ClipCursor failed` 之类）
10. 玩家打开原版 UI 时用 `CoopUiBridge` 标记会话

### 4.3 验证

```powershell
.\Tools\StopCoopTestPlayers.ps1 -Tag clean    # 先清理，避免端口占用
.\Tools\StartLiveSession.ps1 -Players 2       # 一键开 2 人局（自带探针与输入追踪）
.\Tools\StopCoopTestPlayers.ps1 -Tag live
.\Tools\RunCoopRegression.ps1 -Prefix ds-reg  # 一键回归（外观/对象图/视图/身份/动作/交互/生命周期）
```

---

## 5. 常见问题

| 现象 | 原因与处理 |
|---|---|
| 别人连不上房主 | ① 防火墙首次弹窗没点"允许"；② 路由器未转发 UDP 端口；③ 填了内网 IP 但对方在外网（应填公网 IP 或用虚拟局域网） |
| 加入被拒/被踢 | 双方构建不一致（协议 17、`Fork0.7.1`） |
| `端口必须为 1–65535` / 创建房间失败 | 端口被占用（常见于上次没退干净）；换端口或结束残留进程 |
| 昵称乱码 | 命令行非 ASCII 用百分号转义（`-coopUsername %E7%8E%A9%E5%AE%B6`），或直接在界面输入 |
| **房主 30 帧** | 垂直同步 60/30 悬崖。房主侧已自动 `vSyncCount = 0` + 锁 60；改过画质档会在 0.5s 内被纠正 |
| 帧率明显偏低/抖动 | 关闭 `PresentMon`、录屏、加速器等抢 CPU 的工具再测 |
| `Failed to confine the cursor - ClipCursor failed: 拒绝访问。` | 后台窗口无法锁定光标，正常现象，可忽略 |
| 同机多开测试 | 每实例不同端口 + `-logFile`；先 `Tools\StopCoopTestPlayers.ps1` 清理 |
| 加入者"按键不动但能转视角" | 用 `-coopInputTrace` 查链路；自动化模拟按键时 Windows 不会把键盘发给非前台窗口，需先点击游戏窗口 |

---

## 6. 卸载

- 删除游戏目录即可（不写系统目录、不装服务、不改注册表；画质等 PlayerPrefs 记录在
  `%USERPROFILE%\AppData\LocalLow\tl\Pandemonium Classic - Unity Edition\`）。
- 从源码方式：删除克隆目录与 Unity 生成的 `Library/`、`Temp/` 即可。

---

## 7. 分发与合规

- 本仓库只发布**联机模块源码**（作者自有代码，MIT）。**不含原版游戏代码与资源。**
- 请使用你自己合法拥有的游戏副本；**不要公开分发原版游戏文件**。
- 把"含游戏本体的整包"给朋友属于灰色地带：仅限小范围自用，风险自负，**不建议公开挂 Release**。
- 想帮助更多人：把这个模块接到**你自己的**工程并贡献改进（见 [docs/PATCH_SITES.md](docs/PATCH_SITES.md)），
  而不是分发游戏本体。
- 第三方组件：Mirror（MIT）、kcp2k（MIT）等，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

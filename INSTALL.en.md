# Installation & Multiplayer Guide (INSTALL)

**English** | [中文](INSTALL.md)

> **What this document covers** — how to get the co-op mode of *Pandemonium Classic - Unity Edition*
> installed and played: the player/host path (obtain a game folder → host/join → ports and firewall →
> troubleshooting) and the developer path (integrate the module → build → verify).

This repository is the **co-op module source**, not a runnable game. To actually play you need an
**already-built co-op game folder**. Pick your path:

| You are | Go to |
|---|---|
| **Player / host** (no compiling) | [§1 Get a game folder](#1-get-a-game-folder) → [§2 Multiplayer](#2-multiplayer) |
| **Developer / maintainer** (build it yourself) | [§3 Build from source](#3-build-from-source) → [§4 Integrate the module](#4-integrate-the-module) |

**中文速览**：本仓库只有联机模块源码，需要一个**已构建好的游戏目录**才能玩。
房主：`...exe -coopHost 7777 -coopStartGame`；加入者：`...exe -coopJoin <房主IP>:7777`；
防火墙放行 UDP **7777**；最多 **8 人**；所有人必须是**同一构建**。

---

## 1. Get a game folder

Three realistic options, easiest first:

1. **Ask someone who already plays to copy the whole game folder to you** (fastest).
   It looks like:
   ```
   Pandemonium Classic - Unity Edition.exe
   Pandemonium Classic - Unity Edition_Data\
   ```
   Put it somewhere **without spaces or non-ASCII characters**, e.g. `D:\PDM-Coop\`.
   ⚠️ That folder contains the original game assets: keep it for personal/friends use only and
   **do not redistribute it publicly**.

2. **Download the module package from this repository's Releases** (`pdm-coop-module-*.zip`).
   It contains the **module source + tools + docs** and needs a recovered project to build →
   see [§3](#3-build-from-source). This is for people who integrate and build, not a one-click game.

3. **Integrate and build yourself** (most complete, needs Unity) → [§3](#3-build-from-source) + [§4](#4-integrate-the-module).

**First launch**: Windows shows a firewall prompt → tick **Private networks** and **Public networks**,
then **Allow access**. The host must allow it or nobody can connect.

---

## 2. Multiplayer

Prerequisites: every player runs the **same build** (protocol 20, `GameBuild = Fork0.7.1`);
up to **8 players** (host + 7).

### 2.1 Host

**In-game UI**

1. Launch the game and reach the main menu.
2. Click the **联机 (Multiplayer)** button in the **top-right corner** of the screen.
3. Enter a **username** → click **创建房间 (Create room)** (default port **7777**, change it if taken).
4. Start a new game normally; joiners are pulled into the same map and seed automatically.

**Command line (starts the game too)**

```powershell
"C:\path\to\Pandemonium Classic - Unity Edition.exe" -coopHost 7777 -coopStartGame -coopUsername HostName
```

Tell your friends your **LAN IP** (`ipconfig`, IPv4) or your **public IP + forwarded port**.

### 2.2 Joiner

**In-game UI**: **联机** (top-right) → username → **房主 IP (host IP)** = host address
(e.g. `192.168.1.10`) → **端口 (port)** = the host's port → **加入房间 (Join room)** → wait for the
host to start; you enter the same game automatically. The status line shows **客户端已验证
(client verified)** when you are in.

**Command line**

```powershell
"C:\path\to\Pandemonium Classic - Unity Edition.exe" -coopJoin 192.168.1.10:7777 -coopUsername Player1
```

### 2.3 Networking and ports

| Scenario | How |
|---|---|
| Same LAN | Use the host's LAN IP; allow the UDP port in the firewall |
| Direct Internet | Port-forward the chosen UDP port on the router to the host; allow it in the Windows firewall |
| No router access | Use a virtual LAN such as **ZeroTier / Tailscale / Radmin VPN** and join via the virtual IP |

Only UDP is used; the default port is **7777** (customisable, but everyone must use the same one).

### 2.4 Did it actually connect?

Logs default to `%USERPROFILE%\AppData\LocalLow\tl\Pandemonium Classic - Unity Edition\Player.log`
(or the path given to `-logFile`). Look for:

| Keyword | Meaning |
|---|---|
| `[Coop] Command line: hosting UDP 7777` | host is up |
| `[Coop] Command line: joining 192.168.x.x:7777` | a joiner is connecting |
| `character registered; remote=True` | the host created the authoritative character for a joiner |
| `Input stream active for player N` | that player's input stream is arriving (movement can work) |
| `Remote player N moved to x,y,z` | that character really moved on the host |

---

## 3. Build from source

**Only possible if you own the complete recovered project** (this repository has no original code,
scenes or assets; see [§4.1](#41-put-the-module-in-place)).

1. Install **Unity 2021.3.15f1** (with Windows Build Support (Mono)). This project is **Mono** +
   Built-in RP; do not switch to IL2CPP.
2. Put the original asset folders your project needs back under `Assets/` (`StreamingAssets/`,
   `Texture2D/`, `Resources/`, `AudioClip/`, `Mesh/`, `GameObject/`, …).
3. Unity Hub → **Open** → select the project folder (the first import takes a while).
4. Trigger the build and read the result:

```powershell
Set-Content .\RecoveredUnityProject\ExportedProject\CoopBuild.request -Value 'release'
Get-Content .\RecoveredUnityProject\ExportedProject\CoopBuild.result.json   # result=Succeeded when done
```

5. Output: `Builds\ConnectionPrototype\Pandemonium Classic - Unity Edition.exe`.
   Check `..._Data\Managed\Assembly-CSharp.dll`'s timestamp to tell how fresh a build is
   (**not** the .exe timestamp).

## 4. Integrate the module

### 4.1 Put the module in place

Copy `Assets/Scripts/Multiplayer/` (including the `.meta` files) from this repository into your
project's `Assets/Scripts/`.

### 4.2 Insert the hooks

Follow the checklist in **[docs/PATCH_SITES.md](docs/PATCH_SITES.md)**: 99 files / 582 hooks in the
original code. Suggested order:

1. Boot and transport (`KcpTransport` + `CoopNetworkManager` + `CoopConnectionPanel`)
2. The replica branch in `PlayerScript.Update` and `CoopOriginalUpdate`
3. `CoopActorContext` around `CharacterStatus.FixedUpdate`
4. Wrapping coroutines with `CoopActorRoutine`
5. Routing movement input through `IPlayerMovementInputSource`
6. Replacing direct keyboard/mouse reads with `CoopActions`
7. `if (!CoopRoomSettings.CanEdit) return;` on every host-only settings entry point
8. Registering entity enumeration and appearance capture entry points
9. Ignoring the original project's unrecoverable background-window errors (`ClipCursor failed`, …)
10. Marking a player's UI session with `CoopUiBridge`

### 4.3 Verify

```powershell
.\Tools\StopCoopTestPlayers.ps1 -Tag clean    # clean up first so ports are free
.\Tools\StartLiveSession.ps1 -Players 2       # 2-player session with probe + input trace
.\Tools\StopCoopTestPlayers.ps1 -Tag live
.\Tools\RunCoopRegression.ps1 -Prefix ds-reg  # regression suite (appearance/graph/view/identity/actions/interaction/lifecycle)
```

---

## 5. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Others cannot connect to the host | ① firewall prompt was not allowed; ② the router does not forward the UDP port; ③ host gave a LAN IP to someone outside the LAN (use the public IP or a virtual LAN) |
| Kicked / rejected on join | Build mismatch (protocol 20, `Fork0.7.1`) |
| "Port must be 1–65535" / room creation fails | Port already in use (often a leftover process): change the port or kill the leftovers |
| Garbled username | Percent-escape non-ASCII (`-coopUsername %E7%8E%A9%E5%AE%B6`) or type it in the UI |
| **Host at 30 FPS** | VSync 60/30 cliff. The host now sets `vSyncCount = 0` and caps at 60; a quality-level change is re-corrected within 0.5 s |
| Low or jittery FPS | Close `PresentMon`, recorders and boosters before measuring |
| `Failed to confine the cursor - ClipCursor failed: access denied` | Background windows cannot lock the cursor — harmless |
| Running several instances locally | Give each a different port and `-logFile`; run `Tools\StopCoopTestPlayers.ps1` first |
| Joiner "cannot move but can look around" | Use `-coopInputTrace`. When scripting key presses note that Windows does not send keyboard input to a non-foreground window — click the game window first |

---

## 6. Uninstall

- Delete the game folder (nothing is written to system directories, no service, no registry changes;
  quality settings live under `%USERPROFILE%\AppData\LocalLow\tl\Pandemonium Classic - Unity Edition\`).
- Source builds: delete the clone and Unity's generated `Library/`, `Temp/`.

---

## 7. Distribution and compliance

- This repository publishes **module source only** (the authors' own code, MIT). It contains **no
  original game code or assets**.
- Use your own legally obtained copy of the game and **do not redistribute the original game files**.
- Handing a "full package including the game" to friends is a grey area: personal/small-scale only,
  at your own risk, and **not a good idea to publish as a public Release**.
- Want to help more people? Integrate this module into **your own** project and contribute improvements
  (see [docs/PATCH_SITES.md](docs/PATCH_SITES.md)) rather than redistributing the game itself.
- Third-party components: Mirror (MIT), kcp2k (MIT) and others — see
  [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

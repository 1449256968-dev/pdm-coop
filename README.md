# Pandemonium Classic — Unity Edition · 多人联机合作模块

**中文** | [English](README.en.md)

让《Pandemonium Classic》支持**最多 8 人**一起玩：房主权威模拟、加入者本地预测、支持直连 / 域名 / 虚拟局域网。

> 版本 **v0.7.1-p20** · 协议 **20** · Unity **2021.3.15f1** · 下载见 [Releases](https://github.com/1449256968-dev/pdm-coop/releases)

---

## 能做什么

- **最多 8 人同图合作**（1 名房主 + 7 名加入者），共用同一个世界、NPC 与剧情进度。
- **加入者操作即时响应**：本地预测移动，与房主结果冲突时自动回拉，不会"按了过一会才动"。
- **三种连接方式**：直接填 IP、填域名（自动解析）、或走虚拟局域网 / 内网穿透。
- **游戏内连接界面**：填地址、端口、昵称，显示连接状态与实时延迟；保留原「联机」入口。
- **房主权威**：攻击、交互、道具、技能、剧情判定都由房主裁决，加入者不会篡改世界。
- **表现完整**：加入者能看到其他玩家、外观与手持物、攻击光弧、名字与状态条，可旁观查看。
- **独立选角**：每名玩家可选自己的种族，不再是全员同一个。
- **弱网可用**：按实际链路能力限制同步带宽，拥塞时自动降速，不会出现"世界落后十几秒"。
- **不改变原版**：不加任何参数启动就是原版单人游戏。

## 快速开始

1. 从 [Releases](https://github.com/1449256968-dev/pdm-coop/releases) 下载模块包 `pdm-coop-module-*.zip`。
2. 解压并覆盖到工程目录，保持 `Assets/Scripts/Multiplayer/` 结构不变。
3. 用 Unity 打开工程，等待导入完成（首次会为新文件生成 `.meta`）。
4. 按你项目原有的方式构建游戏。
5. 联机：房主启动游戏并开一局，加入者用游戏内「联机」面板填房主 IP / 域名加入。也可用命令行：

   ```powershell
   # 房主
   "Pandemonium Classic - Unity Edition.exe" -coopHost 7777 -coopStartGame
   # 加入者
   "Pandemonium Classic - Unity Edition.exe" -coopJoin 26.72.60.203:7777
   ```

6. 房主放行防火墙 **UDP 7777**；异地联机用虚拟局域网（Radmin VPN / Tailscale / ZeroTier）或端口转发。

完整步骤（含虚拟局域网、端口转发、常见问题）见 **[INSTALL.md](INSTALL.md)**。

## 要求与兼容性

| 项目 | 要求 |
|---|---|
| 引擎 | Unity **2021.3.15f1**（Mono、Built-in 渲染管线；其他版本需自行验证） |
| 网络库 | Mirror + kcp2k（模块包内已包含所需源码） |
| 协议 | **20** — 所有玩家必须使用**同一份构建**，不能与旧版本混连 |
| 平台 | 仅 Windows x64 验证过 |
| 人数 | 1 房主 + 最多 7 加入者 |

## 命令行速查

| 参数 | 作用 |
|---|---|
| `-coopHost <端口>` | 作为房主监听（默认 7777） |
| `-coopStartGame` | 启动后自动开一局（配合 `-coopHost`） |
| `-coopJoin <地址>:<端口>` | 加入房间，地址可为 IP 或域名 |
| `-coopUsername <昵称>` | 指定昵称（非 ASCII 用百分号转义） |
| `-coopLowLatency` | 低延迟模式（更短的插值缓冲，适合直连/局域网） |
| `-coopNoPrediction` | 关闭本地移动预测（排查用） |
| `-coopUploadKBps <KB/s>` | 指定房主总上传预算（弱网/限速网络用） |

## 常见问题

- **加入者动不了 / 有人卡死** → 请使用 **v0.7.1-p20 或更高**：该版本修复了连接面板抑制输入、以及快照拥塞导致成员被踢的问题。
- **连不上** → 确认房主已开房、防火墙放行 UDP 7777、地址端口正确、双方**协议一致**。
- **异地怎么连** → 装 Radmin VPN / Tailscale / ZeroTier 组虚拟局域网，用对方的虚拟 IP 直连；或做端口转发。
- **延迟高 / 别人漂移** → 先看面板 RTT 是否偏高（可能走了中继）；尝试 `-coopLowLatency`，弱网再配 `-coopUploadKBps`。
- 更多排错见 [INSTALL.md](INSTALL.md) 第 5 节。

## 文档

| 文件 | 内容 |
|---|---|
| [INSTALL.md](INSTALL.md) / [INSTALL.en.md](INSTALL.en.md) | 安装、联机、端口与防火墙、排错 |
| [CHANGELOG.md](CHANGELOG.md) / [CHANGELOG.en.md](CHANGELOG.en.md) | 版本变更与升级步骤 |
| [docs/IMPLEMENTATION.md](docs/IMPLEMENTATION.md) | 架构、协议、复制模型（二次开发参考） |
| [docs/PATCH_SITES.md](docs/PATCH_SITES.md) | 对原版脚本的改动位置清单 |
| [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) | 第三方组件与许可 |

## 许可

模块与工具以 **MIT** 发布（见 [LICENSE](LICENSE)）；游戏本体素材版权归原作者，不在本仓库内。

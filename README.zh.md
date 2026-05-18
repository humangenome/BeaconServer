# BeaconServer

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-blueviolet.svg)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows_x64-blue.svg)](#前置条件)
[![Game](https://img.shields.io/badge/Game-Subnautica_2-darkgreen.svg)](https://store.steampowered.com/app/1962700/)

[English](README.md)

面向 **Subnautica 2** Beacon 多人联机的开源专用服务器监管程序。它与 Beacon 启动器（闭源）配套使用，为 Subnautica 2 提供真正基于 IP/端口 的专用服务器能力：标准 Unreal 监听服务器传输、存档快照与回滚、Source A2S 查询、Source RCON、基于 HMAC 签名的 HTTP 管理 API，以及 Lua/C++ 模组扩展接口。

Beacon **启动器**为闭源二进制分发；而本 **服务器**完全开源，并采用 MIT 许可证发布。两者会一同在 [HumanGenome/Beacon](https://github.com/HumanGenome/Beacon) 中发布。

_Beacon 是一个社区项目，与 Subnautica 2 的开发者不存在隶属关系，也未获得其认可或背书。_

> **官方托管：** [SurvivalServers.com 的 Subnautica 2 托管服务](https://www.survivalservers.com/games/subnautica_2/?utm_source=github&utm_medium=readme&utm_campaign=beaconserver) 提供预装并托管 Beacon 的 Subnautica 2 服务器。

---

## 目录

- [功能特性](#功能特性)
- [前置条件](#前置条件)
- [安装](#安装)
- [运行 BeaconServer](#运行-beaconserver)
- [管理与 RCON](#管理与-rcon)
- [模组](#模组)
- [从源码构建](#从源码构建)
- [许可证](#许可证)
- [鸣谢](#鸣谢)

---

## 功能特性

### Subnautica 2 监管进程
BeaconServer 负责管理 Subnautica 2 游戏进程：使用正确的启动 URL 启动游戏，通过进程内插件提供的命名管道 IPC 通道进行监控，在崩溃后自动重启，并在快照恢复期间阻止重启。

### 存档快照与原子回滚
世界存档会按计划任务和管理员触发进行快照。恢复过程采用原子操作，即在文件系统层面直接替换在线存档，而不是原地打补丁，因此即使在恢复中途失败，也不会留下只恢复了一半的世界。

### Source A2S 查询
服务器浏览器、监控工具和 Discord 状态机器人都可以查看你的服务器名称、地图、玩家数量和运行时长。使用标准 A2S，无需特殊客户端。

### Source RCON
标准 Source RCON 监听端口位于 `gameplay port + 3`。可与 `mcrcon`、BattleMetrics 以及任何标准 RCON 客户端配合使用。

### 基于 HMAC 签名的管理 HTTP API
HTTP API 监听在 `gameplay port + 4`，用于快照、恢复、世界传输和玩家列表等操作，请求使用 `HMAC_SHA256(SHA256(RconPassword), ...)` 进行签名。支持 5 分钟重放窗口与防重放跟踪机制。请求体上传会以流方式写入临时文件，并使用分块 SHA256 计算摘要，因此大文件上传不会在内存中发生三重分配。

### 模组扩展接口
Lua 和 C++ 模组可通过 UE4SS 在服务端运行。只需将模组文件夹放入 `ue4ss\Mods\` 目录下，即可自动加载。

---

## 前置条件

- **Windows 10 或 11**（64 位）: BeaconServer 是自包含的 Windows 二进制程序
- **Subnautica 2** 已安装在同一台机器上（Steam、Epic Games 或 Microsoft Store 版本均可）
- 一个用于游戏连接的 UDP 端口（默认 `7777`），以及其派生出的查询/RCON/HTTP 端口

你**不需要**额外安装 .NET, 发布产物已自包含运行时。

---

## 安装

最简单的方式是使用 [SurvivalServers.com](https://www.survivalservers.com/games/subnautica_2/?utm_source=github&utm_medium=readme_install&utm_campaign=beaconserver) 的托管服务，BeaconServer 已预装完成，端口、快照和更新也由其负责处理。

如果要自行托管：

1. 从[最新发布版本](https://github.com/HumanGenome/BeaconServer/releases/latest)下载 `Beacon-Server-Windows-x64-<version>.zip`（也可从总仓库 [HumanGenome/Beacon](https://github.com/HumanGenome/Beacon/releases/latest) 下载）。
2. 将压缩包解压到承载服务器的 Windows 机器上的某个目录（例如 `C:\Beacon\`）。
3. 打开 `appsettings.json` 并设置：
   - `Beacon:SnInstallRoot`：你的 Subnautica 2 安装目录完整路径（即包含 `Subnautica2.exe` 的文件夹）
   - `Beacon:RconPassword`：设置一个强密码。这也是 Beacon HTTP 管理接口使用的密钥。
   - `Beacon:GameplayPort`：玩家连接使用的 UDP 端口（默认 `7777`）
4. 在防火墙和路由器上放行或转发以下端口：
   - `<GameplayPort>` UDP：游戏连接
   - `<GameplayPort> + 2` UDP：服务器查询（A2S）
   - `<GameplayPort> + 3` TCP：RCON（可选）
   - `<GameplayPort> + 4` TCP：管理 HTTP API（可选）
5. 运行 `BeaconServer.exe`。控制台会打印监听地址，这就是玩家通过 Beacon 启动器加入时使用的地址。

---

## 运行 BeaconServer

```text
BeaconServer.exe
```

就是这么简单，它会保持在前台运行，并持续输出自身控制台日志。日志会写入 `logs/beaconserver-<date>.ndjson`。玩家通过 Beacon 启动器连接到 `<HostAddress>:<GameplayPort>`。

如果要将其作为 Windows 服务运行，可使用 NSSM 或 `sc.exe create` 进行封装。监管进程负责管理整个 SN2 生命周期，因此停止过程是优雅的：它会通知进程内插件刷写最终快照，然后终止 SN2 进程。

---

## 管理与 RCON

完整指南请参见 [docs/ADMIN.md](docs/ADMIN.md)：

- RCON 命令参考（`status`、`players`、`ping`、`save snapshot`、`save list`）
- 管理 HTTP API 端点（`/api/v1/health`、`/info`、`/snapshots`、`/snapshots/{id}/restore` 等）及其 HMAC 签名方式
- 快照生命周期与恢复机制

---

## 模组

Lua 和 C++ 模组通过 UE4SS 在服务器端加载。有关入口点、宿主 API 和完整示例，请参见 [docs/MODS.md](docs/MODS.md)。

---

## 从源码构建

需要 Windows 和 .NET 8 SDK。

```bash
git clone https://github.com/HumanGenome/BeaconServer.git
cd BeaconServer
dotnet build BeaconServer.sln -c Release
dotnet test BeaconServer.sln -c Release --no-build
dotnet publish src/server/BeaconServer/BeaconServer.csproj -c Release -r win-x64 --self-contained true
```

发布产物位于 `src/server/BeaconServer/bin/Release/net8.0/win-x64/publish/`。

或者使用 GitHub Actions 发布工作流，打上 `vX.Y.Z` 标签后，流水线会生成 `Beacon-Server-Windows-x64-vX.Y.Z.zip`，并同步发布到总仓库 [Beacon](https://github.com/HumanGenome/Beacon/releases) 的 release 页面。

---

## 参与贡献

欢迎提交 Issue 和 Pull Request。报告 Bug 时，请附上 BeaconServer 版本信息（可通过 RCON 执行 `status` 查看，或访问 `/api/v1/health`）、Subnautica 2 的构建 ID，以及相关日志（`logs/beaconserver-*.ndjson`）。

参见 [CONTRIBUTING.md](CONTRIBUTING.md)。

---

## 许可证

MIT。参见 [LICENSE](LICENSE)。

## 鸣谢

- [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) - Unreal Engine 脚本与模组框架

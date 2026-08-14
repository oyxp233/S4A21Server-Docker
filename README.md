# Server Emulator

2D 横版动作游戏服务端模拟器，用于研究学习网络游戏服务端架构与协议。

## 协作

目前项目仍处于研究和开源完善阶段，还存在非常多 BUG。欢迎有能力的朋友一起提交 Issue、PR，或加入 Discord 共同完善服务端、客户端适配、封包、数据库和 PVF 相关内容。

多人或 AI 协作前请先阅读：

- `AGENTS.md`
- `Docs/服务端业务开发规范.md`
- `CONTRIBUTING.md`

修改具体领域时，还要阅读对应专项规范：副本 `Docs/副本架构业务接入规范.md`、背包/GM `Docs/新版背包架构业务接入规范.md` 和 `Docs/GM工具_新背包表结构与ItemCore语义.md`、副职业 `Docs/副职业架构业务接入规范.md`、计时器 `Docs/ClockService.md`。

## 社区交流

Discord 社区: https://discord.gg/xdd2HGkQnd (永久邀请链接)

## 补丁源码

补丁源码位于 `Patch/` 目录。补丁成品已经生成好，并且已经放在客户端中；普通用户直接使用客户端即可，不需要重新编译补丁。

## 快速启动

**仅下载仓库源码的普通用户，目前无法直接运行服务端。** 仓库不再附带预编译输出；需要先自行构建，或下载预编译压缩包。

### 使用预编译压缩包

解压后使用启动脚本运行服务端（压缩包内已包含）：

- Windows: `start-server.bat`
- macOS / Linux: `./start-server.sh`

1. 将客户端的 `Script.pvf` 放到解压后文件夹内的 `Data/Pvf/`
2. 在本机测试可直接运行启动脚本（默认 `127.0.0.1`）
3. 虚拟机或局域网连接时，使用 `--server-ip auto` 自动检测本机 LAN IP，或手动指定：

```bash
./start-server.sh --server-ip auto
./start-server.sh --server-ip 192.168.0.63
```

Windows:

```bat
start-server.bat --server-ip auto
start-server.bat --server-ip 192.168.0.63
```

macOS 首次从浏览器下载后若提示无法验证开发者，在解压目录执行：

```bash
xattr -dr com.apple.quarantine .
```

### 从源码构建

```bash
./publish.sh          # macOS / Linux
publish.bat           # Windows
./start-server.sh     # macOS / Linux
start-server.bat      # Windows
```

虚拟机或局域网连接时：

```bash
./start-server.sh --server-ip auto
./start-server.sh --server-ip 192.168.0.63
```

## 构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)（提供 `dotnet` CLI）。NuGet 依赖自动下载，无需额外配置。

```bash
dotnet build Server/DfoServer.sln -c Debug
```

或双击 `build.bat`。开发调试可用 `dotnet run`；给最终用户分发请用 `publish.sh` / `publish.bat` 生成自带运行时的自包含包（输出到 `dist/`）。

## 运行

1. 将客户端的 `Script.pvf` 放到运行目录下的 `Data/Pvf/`
2. 启动服务端：
   - **预编译压缩包**：`./start-server.sh` 或 `start-server.bat`
   - **源码仓库**：`./start-server.sh` / `start-server.bat`（会自动查找 `dist/<平台>/` 或 Debug 构建）
3. 服务端监听 7001 (Channel) + 10011 (Game) 端口

## 数据库

项目不再附带或发布 SQLite 种子库。服务端启动时检查 `Data/inventory.db`：

- 文件不存在：按 `Sqlite/item_schema.sql` 直接创建当前版本数据库，不执行历史迁移。
- 文件已存在且 `schema_metadata.baseline_id=86jp-database-v1`：校验版本，并执行新基线发布后的增量迁移。
- 文件已存在但没有正确基线标识：拒绝启动。请先备份并移走旧库，再由服务端创建新库。

新基线从 `PRAGMA user_version=1` 开始。后续功能迁移从新体系 v2 开始。新库不预置玩家账号或角色，账号在首次登录时自动创建，角色由客户端正常创建。

## 项目结构

```
Server/DfoServer/    服务端主程序 (.NET 10)
Tool/PvfLib/         PVF 档案解析库
publish.sh / publish.bat   发布自包含包（输出到 dist/）
start-server.sh / start-server.bat   启动脚本（源码仓库与预编译压缩包）
cleanup.sh / cleanup.bat   清理构建输出
build.bat            开发构建脚本
```

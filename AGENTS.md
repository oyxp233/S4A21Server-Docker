# 多人和 AI 开发约束

本文件适用于整个 `D:\86JP-main` 工作区。开始任何代码、SelfTest、数据库、PVF 或文档修改前，必须先阅读：

1. `Docs/服务端业务开发规范.md`
2. `CONTRIBUTING.md`
3. `Docs/副本架构业务接入规范.md`（修改 Dungeon、Quest-Dungeon、Settlement、Tower、Tournament、Blood Altar 时）
4. `Docs/新版背包架构业务接入规范.md`、`Docs/GM工具_新背包表结构与ItemCore语义.md`（修改 Inventory、GM、ItemCore、装备、宠物、仓库时）
5. `Docs/副职业架构业务接入规范.md`（修改 ExpertJob、分解、附魔、炼金、控偶时）
6. `Docs/ClockService.md`（新增或修改 timer、在线恢复、周期任务时）
7. 与目标业务域对应的 SelfTest

## 强制规则

- 先检查现有工作区改动；不得回滚、覆盖或重置他人无关修改。
- 先定位真实文件、命名空间、调用链和业务所有者，再编辑；禁止凭文件名或方法名猜测。
- 手工代码和文档编辑使用 `apply_patch` 或等价的可审阅小补丁；禁止未经审阅的全局替换、整目录格式化和批量改名。
- 禁止使用 `git reset --hard`、强制 checkout、递归删除或其他破坏性操作处理不明来源的工作区变化。
- 不得修改或覆盖 `Script.pvf`，不得覆盖只读参考目录 `D:\DXF_ServerS4A12_dungeon_mr_20260801`。
- 不得恢复历史 SQLite v1-v52 迁移、种子库、默认账号或运行时隐式建表。当前基线是 `86jp-database-v1 / schema v1`。
- 在线 Inventory 必须使用当前 owned `InventoryLease`；业务写入必须经过现有 Repository、CommitService 或 Coordinator 的事务边界。
- 不得新增重复业务真源、重复缓存、重复随机源、重复生命周期管理器或绕过 generation 检查的异步任务。
- 协议修改必须有抓包、客户端逆向、PVF 或既有验证实现等证据；不确定时标记待确认，不凭推断改字段。
- 修改 Dungeon、Quest、Settlement 或共享 Inventory 路径时，必须显式设置 `PVF_ARCHIVE_PATH`，运行聚焦自测和 `--selftest-all`；未完成真机协议/UI/断线时序验证时，不得写“功能完成”。
- `ClockService` 只负责进程内提醒和短阶段推进，不是跨重启持久化真源；计时器不能凭空发奖、扣次数、加货币或创建进度。
- SelfTest 必须串行执行，因为共享 `Server/DfoServer/bin/Debug/server.log`；不得用并行结果作为最终验收。
- 每个批次必须同步更新规范/版本信息和修改说明；编译错误、测试失败、路径错误也要在提交或 MR 描述中登记。
- 不得终止 PVF MCP 进程；遇到占用或环境阻塞时记录原因并停止扩大修改。

## 完成条件

修改只有在以下条件全部满足时才算完成：

- 业务所有权和事务边界清楚。
- 成功、失败、重复、旧 generation 和重连场景已验证（适用时）。
- 相关 SelfTest 串行通过，Rebuild 为 `0 warning / 0 error`。
- 数据库、协议、PVF 和生成文件没有非预期变化。
- 规范、版本、修改信息和验证结果已同步。

详细规则以 `Docs/服务端业务开发规范.md` 为准；本文件只作为 AI 和协作者的强制入口。

# Sequential Dungeon ETC 真源、奖励与全队翻牌屏障设计

## 目标

修正当前安徒恩 sequential dungeon 实现中四个相互关联的根因：

1. 进度、门禁和奖励业务散落着配置 key、243–247 与 route mask 的硬编码，
   没有统一以 `etc/sequential_dungeon_info.etc` 为运行时真源。
2. 特殊翻牌把 `[clear reward item]` 中的奖励物品组 ID 当作最终物品 ID，
   没有继续解析 `stackable.lst` 对应 STK 的 `[int data]`。
3. 怪物掉落按角色和副本整体限次，并在通关时才记账，导致中途退出可重复、
   同一副本后续配置怪物被错误屏蔽。
4. 单名玩家的免费牌提交即可触发特殊四卡，没有等待全队普通免费牌、付费牌
   和共同倒计时结束，也没有通过统一队伍发送器广播同一份多成员记录。

完成后，当前 PVF 中 key 41 的进度、门禁、怪物每日掉落与特殊翻牌都消费同一份
不可变 Definition；特殊奖励按两级权重独立抽取；配置怪物按
`characterId + sequentialGroupKey + monsterId` 独立限次；特殊四卡只在全队普通
翻牌阶段结束后统一发送。

## 适用规范与边界

本设计遵守 `AGENTS.md`、`CONTRIBUTING.md`、
`Docs/服务端业务开发规范.md`、`Docs/副本架构业务接入规范.md`、
`Docs/新版背包架构业务接入规范.md`、
`Docs/GM工具_新背包表结构与ItemCore语义.md` 和 `Docs/ClockService.md`。

- 服务端 Definition、运行实例状态、Repository 和背包事务分别保持自己的
  权威边界，handler 不直接写 SQL。
- 当前 SQLite 基线不变，不增加表，不运行时隐式建表。
- 当前 PVF 和客户端只作为证据，不修改、不重打包、不提交。
- 不增加客户端协议或客户端补丁；A21 opcode 继续从 `PacketTypesA21.cs` 获取。
- 不增加第二套调度器。阶段计时器只推进当前运行，持久化日界仍由
  `DailyResetService` 负责。
- 不保留固定奖励、强制金牌或其他测试 override。
- 不改变任务、普通非 sequential dungeon、任务掉落、捕获掉落或经验规则。

## 当前证据

设计使用的当前发布 PVF 为：

```text
bin/publish/Data/Pvf/Script.pvf
SHA256 125C3A92C1456AFBC32C14CC723EBDA5E2CEC81B9C1FEA08E2C4742214D0C5D5
```

`etc/sequential_dungeon_info.etc` 的 key 41 包含：

```text
[dungeon index check] 243 244 245 246 247
[monster index check]
56675 63413 56678 56677 56679 56680 63415 56683 56691
56692 56685 56686 63417 63418 56693 56695 56694 63455 56697
[show individual process]
[entrance except dungeon] 247
[rewardable dungeon index] 247
[clear reward item]
915 10157831 0
10  10157832 1
5   10157833 2
70  10157834 1
[always visible dungeon] 247
```

`[clear reward item]` 的每组三元组语义为：

```text
weight, rewardGroupItemId, cardState
```

四个 `rewardGroupItemId` 都指向 `[upgradable legacy]` STK，其 `[int data]`
条目语义为：

```text
finalItemId, weight, count
```

`StackableItemFile.UpgradableLegacyRewards` 已能表达最终 `ItemId`、`Weight`
和 `Count`，因此不需要增加另一套 STK 解析器。

当前 `DropService.GenerateAndRegister` 返回 `MonsterDropResult.Drops` 与
`GoldAmount`。当前每日掉落 guard 只按副本 ID 判断，且
`AntonNormalConquestApplicationService` 在通关时调用 `TryMarkLootClaimed`；
这与怪物级生成时记账的业务要求不符。

当前普通翻牌只有免费牌自动翻牌计时器。手动翻免费牌会取消该计时器，
`CardRewardCoordinator` 随后直接调用 `OnFreeCardCommittedAsync`，使单名成员可能
在其他队员尚未完成普通翻牌时投影特殊四卡。当前项目已有运行身份、
participant effect journal、instance projection gate 与 11 秒投影后自动发奖路径，
应扩展这些 owner，不复制第二套生命周期系统。

## 唯一 ETC Definition Catalog

新增只读 `SequentialDungeonDefinitionCatalog`，在 PVF 初始化阶段解析
`etc/sequential_dungeon_info.etc`，发布不可变快照。每个 Definition 至少包含：

- `GroupKey`；
- 有序 `DungeonIds`；
- `MonsterIds` 集合；
- `ShowIndividualProcess`；
- `EntranceExceptDungeonIds`；
- `RewardableDungeonIds`；
- `AlwaysVisibleDungeonIds`；
- 有序 `ClearRewardGroups`，每项为外层权重、奖励组物品 ID 和卡牌状态。

Catalog 建立副本 ID 到 Definition 的索引。所有消费者通过当前副本 ID 解析
Definition，不再在生产业务中用 key 41、243–247 或固定 route mask 识别玩法。
同一副本若被多个 Definition 包含且无法按现有 PVF 规则消歧，则对应机制
fail-closed，并记录冲突 key；不得依赖加载顺序取第一个结果。

每个 `DungeonInstance` 在创建或首次进入机制时冻结其 Definition 快照引用。
同一运行中的进度、掉落、普通翻牌屏障和特殊奖励始终使用该快照；PVF catalog
若在未来支持重载，也不能让一局运行前后消费两套规则。

以下组件统一改为消费 Definition：

- `AntonNormalConquest` 及其应用服务：序列、进度和 route mask；
- 每日进度 Service/Repository：动态副本集合与当前游戏日状态；
- 副本准入：动态前置集合和逐队员验证；
- 怪物掉落 guard：group key、当前副本与配置怪物集合；
- 特殊奖励 Service/Coordinator：可奖励副本、奖励组和卡牌状态。

整个 ETC 文件缺失、目标段结构非法、字段重复冲突或协议容量无法表达时，
对应 Definition 不发布。依赖该 Definition 的门禁、受限掉落和特殊奖励机制
fail-closed，记录文件、group key、字段和原因；禁止回退到生产硬编码。

## 动态进度、门禁与 06:00 游戏日

对启用 `[show individual process]` 的 Definition：

- `ProgressDungeonIds` 使用有序 `DungeonIds`，记录每个配置副本的实际通关；
- `PrerequisiteDungeonIds` 为 `DungeonIds` 排除
  `EntranceExceptDungeonIds` 后的有序集合；
- route bit 位置由 `PrerequisiteDungeonIds` 的顺序决定；
- `AlwaysVisibleDungeonIds` 只控制已有的可见性投影，不能绕过准入验证。

当前 key 41 因此自然得到 243、244、245、246 四个前置位，247 是可见且需要
前置条件的最终副本。成功通关某个配置副本时，为每名有效参与者记录该副本的
当前游戏日进度。重复通关保持幂等且不能降低已有状态。

进入 `EntranceExceptDungeonIds` 中的目标副本前，在任何入场成本提交之前，
对计划中的每名队员分别加载其当前游戏日状态，并要求全部
`PrerequisiteDungeonIds` 已完成。一名队员不满足即拒绝整个队伍，保持当前已
验证的 A21 原生“队伍中有人未满足条件”投影；不得误报为角色次数上限。

进度 Repository 接收 Definition 的动态副本集合，SQL 不再列出 243–247。
现有 `character_dungeon_permissions` 与 `character_daily_counters` 继续承担
持久化；涉及日标记、进度清理和权限写入的组合操作保持一个明确 SQLite
事务。每次事务捕获一个 UTC 时间锚并贯穿操作，避免在 06:00 跨界时观察两个
游戏日。

进度、怪物掉落和特殊奖励领取统一使用 `DailyResetService` 的北京时间每日
06:00 游戏日。06:00 后首次读取或写入自动进入新游戏日；不增加批量删除定时器，
也不在业务代码中用 `DateTime.Now` 自行判断日期。服务重启和错过内存计时器
不能保留上一日资格。

## 两级特殊奖励解析与冻结

每名当日仍有对应 `RewardableDungeonId` 奖励资格的有效参与者独立抽奖：

1. 从 Definition 的 `ClearRewardGroups` 按外层正权重选择一项，得到
   `RewardGroupItemId` 与 `CardState`。
2. 用 `RewardGroupItemId` 查询现有 `stackable.lst` 模型。
3. 要求该 STK 提供合法、非空的 `UpgradableLegacyRewards`。
4. 按内层正权重选择一个条目，得到最终 `ItemId` 与 `Count`。
5. 为角色冻结 `GroupKey`、`RewardableDungeonId`、`RewardGroupItemId`、
   最终 `ItemId`、`Quantity`、`CardState`、source event ID 与 run identity。

队员各自使用独立随机调用，禁止全队只抽一次后复用结果。外层
`CardState` 原样进入已验证的 `0x0319` 字段：当前 PVF 中 10157831 对应普通牌，
其余奖励组自然投影金牌状态，不以组 ID 写条件分支。

`0x0319` 每名成员记录的 item 与 quantity 字段分别使用冻结的最终 `ItemId` 和
`Quantity`。奖励组 ID 不进入客户端奖励记录；因此客户端展示内容和最终入包内容
来自同一个不可变结果。

随机结果只创建一次。投影重试、计时器重入、网络重发、短线恢复和入包重试
只能读取冻结结果，不能重新抽取。`RewardGroupItemId` 仅用于解析和诊断，绝不
进入背包；最终只发放 `FinalItemId × Quantity`。

每日特殊奖励资格的逻辑键为
`(characterId, sequentialGroupKey, rewardableDungeonId)`，物理上使用现有
day-period counter 的版本化命名空间 key，cap 为 1。当前配置仍表现为角色每天
最多领取一次 247 特殊奖励，但生产代码不维护固定 `anton_awakening_card_247`
作为玩法判断或唯一配置来源。

奖励组不存在、STK 类型不支持、内层为空、权重总和溢出、最终物品或数量非法
时，不生成虚假条目、不回退到奖励组 ID，也不领取每日资格。日志包含 group、
rewardable dungeon、角色、source event、reward group 和失败分类。

## 怪物级每日独立掉落限制

怪物限制的逻辑键为：

```text
(characterId, sequentialGroupKey, monsterId)
```

物理上继续复用 `character_daily_counters`，使用版本化、带命名空间且无歧义的
counter key 编码 `GroupKey + MonsterId`，cap 为 1，period 为 day。无需 schema
迁移。不同 group 中相同 monster ID 不共享计数，同一 group 的不同 monster ID
也不互相影响。

限制只包裹普通怪物掉落生成路径。经验、任务掉落、捕获掉落、脚本结算奖励和
特殊翻牌保持原 owner 与原行为。每名参与者在怪物死亡时执行：

1. 从当前运行冻结的 Definition 判断当前副本和 monster ID 是否属于同一配置组；
2. 不属于时直接走原有掉落流程；
3. 属于且当前游戏日已经记账时，仅屏蔽该角色此次普通怪物掉落；
4. 尚未记账时，在该逻辑键的同步保护内重新检查，再调用现有掉落生成与注册；
5. `Drops.Count > 0` 或 `GoldAmount > 0` 即为成功生成，立即原子增加一次计数；
6. 同一次死亡无论生成一个物品、多个物品、纯金币或金币加物品，都只记一次；
   全部未命中则不记账。

只要至少一个物品或金币成功生成，未拾取、中途退出、掉线或未通关都不返还
资格。删除 `AntonNormalConquestApplicationService` 通关阶段的掉落记账；第一个
配置怪物成功掉落后只屏蔽当天后续相同 monster ID，不影响该组其余 monster ID。

同一逻辑键的检查、生成和记账在现有运行同步边界与专用 guard 串行化，数据库
cap-one 操作是最终权威。生成结果在怪物死亡通知组包前完成记账；若 cap-one
提交失败或发现资格已被其他执行占用，必须在同一运行同步边界内撤销本批新增的
drop registration，并把 `Drops` 与 `GoldAmount` 归零，因此客户端和后续拾取路径
不会看到未记账掉落。读取或原子记账异常时，该角色的当前受限掉落 fail-closed，
并记录角色、group、dungeon、monster、run identity 与失败阶段；未配置怪物和
普通副本不能被该故障误伤。

## 普通翻牌阶段与实例级全队屏障

在 `DungeonInstance` 的现有机制运行时中保存一次性的翻牌屏障，不新建 session
级玩法状态。最终结算开始时冻结参与者 roster、source event、run generation 和
普通翻牌共同截止时间。

每名冻结参与者有以下终态：

```text
FreeCommitted
PaidCommitted | PaidSkipped
```

实例还有：

```text
NormalCardPhaseClosed
SpecialProjectionReady
```

现有免费牌自动翻牌计时器继续负责单名玩家未选择时的免费牌提交。玩家手动提前
翻免费牌只取消自己的免费牌计时器，不能取消或提前完成新增的普通翻牌阶段截止
计时器。截止前仍可购买并翻付费牌。

共同截止回调在 instance projection gate 下按以下顺序执行：

1. 设置 `NormalCardPhaseClosed`，先关闭付费操作入口；
2. 将所有未购买付费牌的有效参与者设置为 `PaidSkipped`；
3. 对仍未提交免费牌的在线有效参与者执行一次现有自动选牌和交付路径；
4. 重新核对 roster 中仍绑定相同 instance、source event 和 generation 的成员；
5. 只有每名有效成员都满足
   `FreeCommitted + (PaidCommitted | PaidSkipped)` 才发布特殊投影。

付费请求与截止回调使用同一同步边界。请求先取得边界且验证阶段仍开放，才允许
进入扣费与交付事务；截止先取得边界时，请求必须在扣费前拒绝。手动提前完成
免费或付费牌只更新个人状态，任何个人回调都不能直接发送特殊四卡。

离开旧局、主动回城、掉线后 run identity 已失效的成员不阻塞屏障，也不能收到
旧局投影或领取旧局奖励。本设计不新增跨 EndRun 的补发保证；在线恢复只复用
现有生命周期允许的同一有效运行。

## 统一队伍投影与自动发奖

屏障满足时，从冻结奖励计划按稳定 roster 顺序构建一次包含全部奖励参与者记录的
同一份 `ANTON_AWAKENING_MODE_REWARD` 正文。业务代码不维护 opcode 数字常量。

新增或提取通用 `PartyPacketSender.SendToPartyAsync`：

- 在 instance gate 下捕获仍绑定相同 instance/source event/generation 的在线会话；
- 对所有目标发送完全相同的冻结 payload；
- 一名成员发送失败不阻止其他成员；
- participant effect journal 分别记录每个接收者的投影状态；
- 重试只面向尚未成功且身份仍有效的接收者，已成功者不重复发送；
- payload 不因接收者、重试或会话顺序重新构建。

当前已实机验证的 11 秒特殊投影后自动入包延迟保留，但共同起点改为全队特殊
投影发布成功的时间。每名成功收到投影且运行仍有效的角色，仅按自己的冻结结果
安排 grant ticket。ticket 执行时重新检查 session、角色、instance、source event、
generation 和 ticket version；旧回调不能修改新运行。

每日领取占位和 `FinalItemId × Quantity` 入包在现有 owned `InventoryLease` 与
同一 SQLite 事务中提交。投影成功不等于领取成功；只有背包事务提交后才消耗
当日奖励资格。单名成员失败不回滚其他成员，并可在同一有效运行中用原冻结结果
重试，禁止重新随机或重复入包。

## 失败与恢复语义

- Definition 未发布：对应玩法机制 fail-closed，不使用陈旧硬编码兜底。
- 进度/准入数据库失败：在任何入场成本提交前拒绝，不能伪装为玩家次数问题。
- 普通牌提交失败：不伪造 `FreeCommitted` 或 `PaidCommitted`；只有现有交付事务
  成功才进入终态。
- 截止时未购买付费牌：确定性进入 `PaidSkipped`；截止后的迟到请求不扣费。
- 奖励解析失败：该角色不投影虚假物品、不占每日领取资格。
- 队伍广播部分失败：成功成员保持成功；失败成员仅在原运行有效时重试相同 payload。
- 入包事务失败：没有部分背包写入，也不占每日奖励资格；同运行重试使用冻结结果。
- 旧计时器、重复 handler、重连回调和并发 deadline：全部经过 identity、journal、
  gate 与 cap-one 状态校验，保持幂等。
- 运行结束：清理阶段计时器和易失屏障；持久化的进度、怪物计数和已领取状态
  保持权威，不由计时器创造或撤销。

## 组件职责

### 新增或提取

- `SequentialDungeonDefinitionCatalog`
  - 唯一解析并索引 sequential ETC，不包含 session、SQL 或 packet 发送。
- sequential daily progress Repository/Service
  - 接收 Definition 的动态集合，拥有游戏日进度事务与门禁决策。
- sequential monster daily loot guard
  - 拥有 `(character, group, monster)` 计数与生成时记账策略。
- sequential reward resolver
  - 只负责两级权重解析、随机和不可变冻结结果。
- instance card barrier/coordinator
  - 只编排普通翻牌终态、共同 deadline、特殊投影和 grant ticket。
- `PartyPacketSender`
  - 只负责 generation-safe 的相同 payload 队伍发送与逐接收者结果。

### 复用

- `DailyResetService`：06:00 游戏日和 cap-one counter；
- `StackableItemFile.UpgradableLegacyRewards`：内层奖励条目；
- `DungeonParticipantEffectJournal`：投影与发奖幂等；
- `DungeonRunTimerRegistry`/`ClockService`：短阶段 ticket；
- `CardRewardCoordinator` 与 `CardRewardRules`：免费和付费牌选择/交付；
- 现有 Inventory Repository/CommitService：奖励入包事务；
- `PacketTypesA21.cs`：A21 NOTI 枚举。

具体类名可在实现计划中按现有目录命名约定收敛，但不得复制现有 catalog、
repository、random source、journal、timer registry 或 inventory transaction owner。

## 自动化验证

聚焦 SelfTest 至少覆盖：

1. 多 group ETC 解析、字段顺序、动态副本索引、monster 集合、rewardable 与
   entrance-except 规则。
2. 重复副本归属、非法 token、空奖励、容量溢出和缺失 ETC 的 fail-closed。
3. 动态 prerequisite 顺序及 route mask；当前配置依次得到
   `0x00/0x01/0x03/0x07/0x0F`。
4. 每名队员独立门禁、未满足时不提交入场成本、全部满足时放行。
5. 北京时间 `05:59:59` 与 `06:00:00` 的进度、monster counter 和奖励资格切换，
   包括服务重建后读取同一数据库。
6. 外层权重边界、内层权重边界、数量、CardState、队员独立随机和非法 STK。
7. 最终背包只收到 `FinalItemId × Quantity`，永远不收到 `RewardGroupItemId`。
8. 纯物品、纯金币、金币加物品、多个物品和全部未命中的怪物结果。
9. 同 monster 当日重复被拒绝、同 group 不同 monster 独立、不同 group 相同
   monster 独立、中途退出后不返还、跨 06:00 恢复。
10. 非配置怪物、任务掉落、经验和普通副本不受 guard 影响。
11. 手动提前免费牌不会提前打开屏障；提前付费成功；deadline 把未购买者设置为
    `PaidSkipped`；deadline 后的付费请求在扣费前失败。
12. 两名及以上队员以不同顺序完成普通翻牌时，只有共同 deadline 后投影一次。
13. 每个在线有效成员收到字节完全相同的多成员正文，奖励结果按成员独立。
14. 重复回调、并发 deadline、部分发送失败、旧 generation 和 grant 重试保持幂等。
15. 11 秒 ticket 从统一特殊投影时刻计算，旧运行 ticket 不向新运行入包。

涉及 Dungeon、Settlement、共享 Inventory、SQLite 和 PVF 的测试显式设置当前
`PVF_ARCHIVE_PATH` 并严格串行执行。聚焦 SelfTest 通过后运行
`--selftest-all`，再按当前服务端解决方案实际定义的 `Release|Any CPU` 执行
Rebuild，要求 0 warning、0 error。最后运行 `git diff --check`、检查工作区和暂存文件，
确保 PVF、数据库、日志、抓包、密钥及 `bin/obj/publish` 未进入变更。

## 实机验证边界

自动测试不能代替当前 A21 客户端的 UI 和真实组队时序。部署正式构建后至少验证：

1. 243–246 每通关一项只更新对应进度，队员未满足时 247 正确拒绝；
2. 多角色进入 247，手动提前翻免费牌或付费牌均不会提前出现特殊四卡；
3. 普通翻牌共同倒计时结束后，全队同时看到内容一致的多成员特殊四卡；
4. 每名角色的卡色、最终物品和数量符合各自独立冻结结果；
5. 特殊投影后自动入包顺序正确且无重复；
6. 同组不同配置怪物各自首次成功掉落，中途退出后相同 monster 当日不再掉落；
7. 次日 06:00 后进度、monster 资格和特殊奖励资格按新游戏日重新开始。

未完成实机步骤前，只能报告自动化和协议构造验证通过，不能宣称客户端表现已经
最终完成。

## 验收条件

- 生产逻辑不再用 key 41、243–247 或固定 route mask 判断该玩法。
- sequential 进度、门禁、怪物和奖励只消费同一冻结 Definition。
- 特殊奖励完成两级权重解析，并为每名参与者独立冻结最终物品、数量和卡态。
- 配置怪物按角色、group、monster 在成功生成任意物品或金币时立即独立记账。
- 手动翻牌不能提前触发；全队普通翻牌共同 deadline 后才统一广播特殊四卡。
- 每位在线有效成员收到相同多成员 payload，只领取自己的冻结奖励且每日最多一次。
- 数据库、PVF、客户端和生成产物边界无非预期变化。
- 聚焦测试、全量 SelfTest、Release Rebuild 与 diff 检查达到项目要求。
- 客户端 UI、组队同步和时序只在完成上述实机验证后标记为已验证。

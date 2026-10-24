using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    // 龙跃武器（PVF [equipment grow]）的成长值累积。
    //
    // PVF 证据：
    //   - equipment.lst 中 1204 件武器带 [equipment grow] 1 N，N 为星级上限(1..7)。
    //   - 武器描述："击杀普通、稀有、精英以及领主怪物时，可以获得一定的成长值。
    //     (地下城难度越高，获得的成长值越多；在亡者峡谷中无效)
    //     若每天不消耗一定的蓄能值，则第二天会减少20%的成长值。"
    //   - 星级通过 [emancipate](材料 10000224) 提升，输出为下一星级的同款武器。
    //
    // 成长值属于装备实例状态：每件武器各自累积。持久化在
    // character_equipment_growth，按 (character_id, item_uid) 关联
    // character_inventory_items 的行；跨列表移动时 item_uid 会变，
    // 迁移由 InventoryPersistenceService 在同一事务内完成。
    internal static class EquipmentGrowthService
    {
        // 龙跃武器最高星级。PVF 的 [equipment grow] 参数 N 取值范围是 1..7。
        internal const int MaxStage = 7;

        // 星级进度的客户端换算基数来自 PVF 的 etc/equipment_grow.etc [exp base]：
        //   1星 1794 / 2星 3174 / 3星 4830 / 4星 6141 / 5星 8694 / 6星 11868。
        // 每档需求不同，必须按当前星级取对应值，不能再写死一个常量。
        // 实机已验证：1星下 909/1000/1111/1800 与 3星下 1800/2400/4800
        // 的显示百分比与该表逐项吻合。
        internal const int MaxChargeValue = 30;

        // 可免费自动升星的最高星级。PVF [exp base] 第三列：1、2 星为 1（免费），
        // 3 星起为 0（需要龙跃之息卷轴），与官方资料一致。
        internal const int AutoUpgradeMaxStage = 3;

        // 指定星级升下一星所需的成长值；0 表示该星级已满级或表里没有定义。
        internal static int GetRequiredExp(int stage)
            => EquipmentGrowDefinition.Default.GetRequiredExp(stage);

        // 该星级升级是否必须用材料。
        internal static bool RequiresMaterial(int stage)
            => EquipmentGrowDefinition.Default.RequiresMaterial(stage);

        // 把成长值写进 ItemCore 的线上字段，供客户端 UI 读取。
        // 客户端按 "当前值 / 该星级需求" 换算百分比，因此这里应写入
        // 该星级下的绝对成长值（封顶在需求值），而不是跨星级的余数。
        internal static void ApplyGrowthProjection(ItemCore core, int growthValue, int stage)
        {
            if (core == null)
                return;

            var required = GetRequiredExp(stage);
            var clamped = Math.Max(0, growthValue);
            var progress = required > 0 ? Math.Min(clamped, required) : clamped;
            if (progress > ushort.MaxValue)
                progress = ushort.MaxValue;

            core.TailUnknown0 = (ushort)progress;
            // 蓄能值对应角色疲劳消耗，当前没有疲劳值接入点，保持 0。
            // 不能用进度折算：两者在协议和玩法上都是独立字段。
            core.TailUnknown1 = 0;
        }


        // 成长值满档且星级还允许免费升级时，把武器换成下一星模板。
        // 返回 true 表示发生了升星，调用方需要重新读取 core。
        internal static bool TryAutoUpgradeStage(
            ref ItemCore core,
            int growthValue,
            int currentStage,
            out int newStage)
        {
            newStage = currentStage;
            if (core == null || currentStage < 1)
                return false;

            // 该星级是否免费自动升：PVF [exp base] 第三列为 0 时要求材料。
            if (RequiresMaterial(currentStage))
                return false;

            var required = GetRequiredExp(currentStage);
            if (required <= 0 || growthValue < required)
                return false;

            if (!ItemMetadataResolver.TryLoadEquipmentFile(core.ItemId, out var source)
                || source?.Emancipate == null
                || source.Emancipate.Outputs.Count != 1)
            {
                return false;
            }

            var output = source.Emancipate.Outputs[0];
            if (output.ItemId <= 0 || output.Count != 1)
                return false;

            // 保留实例状态，只替换模板，沿用解放路径的属性继承规则。
            var upgraded = ItemCore.Create(ItemCore.KindEquipment, output.ItemId);
            if (upgraded == null)
                return false;

            upgraded.Upgrade = core.Upgrade;
            upgraded.GenuineUpgrade = core.GenuineUpgrade;
            upgraded.EnchantCardId = core.EnchantCardId;
            upgraded.EnchantUpgradeCount = core.EnchantUpgradeCount;
            upgraded.AmplifyType = core.AmplifyType;
            upgraded.AmplifyValue = core.AmplifyValue;
            upgraded.SealFlag = core.SealFlag;
            upgraded.Durability = core.Durability;

            if (!TryResolveGrowthDefinition(output.ItemId, out _))
                return false;

            core = upgraded;
            newStage = currentStage + 1;
            return true;
        }

        // 单次击杀的成长值增量。
        // 基础值随怪物等级线性上升，副本难度再乘以系数：
        // 普通/稀有/精英/领主都有效。
        // 武器描述明确"在亡者峡谷中无效"：CastleoftheDead 系列副本
        // (PVF dungeon.lst 中的 190..195) 不产生成长值。
        internal static int CalculateGrowthGain(
            int monsterLevel,
            byte dungeonDifficulty,
            int monsterRewardKind,
            int dungeonId = 0)
        {
            if (monsterLevel <= 0)
                return 0;

            if (IsGrowthDisabledDungeon(dungeonId))
                return 0;

            // 怪物种类：0 普通 / 1 精英 / 2 超级精英 / 3 领主。
            // 非怪物的 actor（机关、召唤物等）不产生成长值。
            if (monsterRewardKind < 0 || monsterRewardKind > 3)
                return 0;

            var levelGain = Math.Max(1, monsterLevel);
            var difficultyMultiplier = 1 + (int)Math.Max((byte)0, dungeonDifficulty);
            return checked(levelGain * difficultyMultiplier);
        }

        // "在亡者峡谷中无效"：亡者峡谷是一个区域，包含绝望之塔、极限祭坛、
        // 死亡之塔、无尽祭坛、迷惘之塔等多组副本。这些副本在 PVF dungeon.lst
        // 里分别落在 Towers/ 与 AdvanceAltar/ 目录下（Towers/ 下 305 条，
        // 涵盖 TowerOfDeath / TowerOfIllusion / TowerOfDespair /
        // TowerOfBloodyFight / TowerOfUltimate 等），因此按 dungeon 路径前缀
        // 判断，比维护 ID 白名单稳定。
        private static readonly string[] GrowthDisabledDungeonPathPrefixes =
        {
            "towers/",
            "advancealtar/",
        };

        // 显式补充的亡者峡谷副本 ID（CastleoftheDead，死之城系列）。
        private static readonly int[] GrowthDisabledDungeonIds = { 190, 191, 192, 193, 194, 195 };

        internal static bool IsGrowthDisabledDungeon(int dungeonId)
        {
            if (dungeonId <= 0)
                return false;

            for (var i = 0; i < GrowthDisabledDungeonIds.Length; i++)
            {
                if (GrowthDisabledDungeonIds[i] == dungeonId)
                    return true;
            }

            // 按 dungeon.lst 的路径判断塔/祭坛类副本。
            try
            {
                var resolved = DungeonCatalog.GetDungeonFileWithPath(dungeonId);
                var path = (resolved.FilePath ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
                for (var i = 0; i < GrowthDisabledDungeonPathPrefixes.Length; i++)
                {
                    if (path.StartsWith(GrowthDisabledDungeonPathPrefixes[i], StringComparison.Ordinal))
                        return true;
                }
            }
            catch
            {
                // 拿不到路径时按"不排除"处理，避免误伤正常副本。
            }

            return false;
        }

        // 该物品是否为龙跃武器，以及它的星级上限。
        // PVF 的 [equipment grow] 1 N 里的 N 是该模板的星级(1..7)，不是成长值上限：
        // 1 星武器的 N 就是 1，它照样能累积成长值，攒够后用 [emancipate] 解放成 2 星。
        internal static bool TryResolveGrowthDefinition(
            int itemTemplateId,
            out int stageLimit)
        {
            stageLimit = 0;
            if (itemTemplateId <= 0)
                return false;

            if (!ItemMetadataResolver.TryLoadEquipmentFile(itemTemplateId, out var equipment)
                || equipment == null
                || equipment.Grow == null
                || !equipment.Grow.HasValue)
            {
                return false;
            }

            stageLimit = equipment.Grow.StageLimit;
            return true;
        }

        // 为当前角色身上所有龙跃武器实例累加成长值。
        // 只处理已穿戴的武器与主背包中的武器（其余容器的武器同样属于角色，
        // 但成长值只在战斗中累积，穿戴中的武器是主要场景）。
        internal static int AccumulateForEquippedWeapons(
            InventoryService inventory,
            SqliteConnection connection,
            SqliteTransaction transaction,
            int growthGain)
        {
            return AccumulateForEquippedWeapons(
                inventory, connection, transaction, growthGain, out _);
        }

        // stageUpgraded 为 true 表示本次有武器升级了星级（模板变了），
        // 调用方必须用整栏刷新而不是单槽更新。
        internal static int AccumulateForEquippedWeapons(
            InventoryService inventory,
            SqliteConnection connection,
            SqliteTransaction transaction,
            int growthGain,
            out bool stageUpgraded)
        {
            stageUpgraded = false;
            if (inventory == null
                || connection == null
                || transaction == null
                || growthGain <= 0)
            {
                return 0;
            }

            var affected = 0;
            affected += AccumulateForList(
                inventory,
                connection,
                transaction,
                InventoryListType.Equipment,
                growthGain,
                ref stageUpgraded);
            affected += AccumulateForList(
                inventory,
                connection,
                transaction,
                InventoryListType.Main,
                growthGain,
                ref stageUpgraded);
            return affected;
        }

        // 只把已持久化的成长值回填到 ItemCore 的线上字段，不改变成长值本身。
        // 用于进号/穿戴等"没有击杀但客户端要立即显示"的场景。
        internal static void ProjectPersistedGrowth(
            InventoryService inventory,
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            if (inventory == null || connection == null || transaction == null)
                return;

            var records = EquipmentGrowthRepository.LoadForCharacter(
                connection,
                inventory.CharacterId);
            if (records.Count == 0)
                return;

            foreach (var listType in new[] { InventoryListType.Equipment, InventoryListType.Main })
            {
                foreach (var pair in inventory.GetItems(listType))
                {
                    var core = pair.Value;
                    if (core == null
                        || (core.ItemKind != ItemCore.KindEquipment
                            && core.ItemKind != ItemCore.KindCreatureEquipment))
                    {
                        continue;
                    }

                    var itemUid = InventoryItemRepository.LoadCharacterSlotUid(
                        connection,
                        transaction,
                        inventory.CharacterId,
                        listType,
                        pair.Key);
                    if (itemUid <= 0
                        || !records.TryGetValue(itemUid, out var record))
                    {
                        continue;
                    }

                    ApplyGrowthProjection(core, record.GrowthValue, record.Stage);
                    inventory.SetItem(listType, pair.Key, core);
                }
            }
        }

        private static int AccumulateForList(
            InventoryService inventory,
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryListType listType,
            int growthGain,
            ref bool stageUpgraded)
        {
            var affected = 0;
            foreach (var pair in inventory.GetItems(listType))
            {
                var core = pair.Value;
                if (core == null
                    || (core.ItemKind != ItemCore.KindEquipment
                        && core.ItemKind != ItemCore.KindCreatureEquipment))
                {
                    continue;
                }

                if (!TryResolveGrowthDefinition(core.ItemId, out var stageLimit))
                    continue;

                // 刚获得的武器可能还没落库，item_uid 此时不存在。
                // 先确保物品行存在（与背包落库同一事务），再取它的 item_uid。
                var itemUid = InventoryItemRepository.UpsertCharacterSlot(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    listType,
                    pair.Key,
                    core);
                if (itemUid <= 0)
                    continue;

                var current = EquipmentGrowthRepository.LoadGrowth(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    itemUid);
                // 星级只在用 [emancipate] 解放时提升；累积期间保持当前星级。
                // 7 星满级后不再累积（原版：满级后经验值不再衰减也不再涨）。
                var stage = current?.Stage ?? stageLimit;
                if (stage >= MaxStage)
                    continue;

                // 已满档但该星级必须用材料升级时，不再继续堆成长值：
                // 客户端进度条已经封顶，继续累加只会让数字偏离真实需求。
                if (current != null && RequiresMaterial(stage))
                {
                    var cap = GetRequiredExp(stage);
                    if (cap > 0 && current.GrowthValue >= cap)
                        continue;
                }

                if (!EquipmentGrowthRepository.AddGrowth(
                        connection,
                        transaction,
                        inventory.CharacterId,
                        itemUid,
                        core.ItemId,
                        growthGain,
                        stage))
                {
                    continue;
                }

                // 把累计后的成长值投影回 ItemCore 的线上字段，客户端 UI
                // 才能刷新 "N星 X%" 与 "蓄能值 N/30"。字段语义已由实机探测确认。
                var updated = EquipmentGrowthRepository.LoadGrowth(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    itemUid);
                if (updated == null)
                    continue;

                // 满需求且该星级可免费自动升星时：换模板并把成长值清零重攒。
                // PVF [exp base] 第三列为 0 的星级（3星起）不走这里，要用卷轴。
                if (TryAutoUpgradeStage(
                        ref core,
                        updated.GrowthValue,
                        updated.Stage,
                        out var newStage))
                {
                    // 直接按槽位落库：UpsertCharacterSlot 是 UPDATE，
                    // 模板变了 uid 也不变，因此成长值行原地改写即可。
                    var newItemUid = InventoryItemRepository.UpsertCharacterSlot(
                        connection,
                        transaction,
                        inventory.CharacterId,
                        listType,
                        pair.Key,
                        core);
                    if (newItemUid > 0)
                    {
                        EquipmentGrowthRepository.WriteGrowthSnapshot(
                            connection,
                            transaction,
                            inventory.CharacterId,
                            newItemUid,
                            core.ItemId,
                            0,
                            newStage);

                        var verify = EquipmentGrowthRepository.LoadGrowth(
                            connection,
                            transaction,
                            inventory.CharacterId,
                            newItemUid);
                        FileLogger.Log(
                            $"[EquipmentGrowth] upgrade-write cid={inventory.CharacterId} "
                            + $"uid={newItemUid} oldUid={itemUid} item={core.ItemId} stage={newStage} "
                            + $"rowAfterWrite={(verify == null ? "null" : verify.GrowthValue + "/" + verify.Stage)}");

                        ApplyGrowthProjection(core, 0, newStage);
                        inventory.SetItem(listType, pair.Key, core);
                        InventoryItemRepository.UpsertCharacterSlot(
                            connection,
                            transaction,
                            inventory.CharacterId,
                            listType,
                            pair.Key,
                            core);

                        FileLogger.Log(
                            $"[EquipmentGrowth] auto-upgrade cid={inventory.CharacterId} "
                            + $"item={updated.ItemId}->{core.ItemId} stage={updated.Stage}->{newStage}");
                        stageUpgraded = true;
                        affected++;
                        continue;
                    }
                }

                ApplyGrowthProjection(core, updated.GrowthValue, updated.Stage);
                inventory.SetItem(listType, pair.Key, core);
                InventoryItemRepository.UpsertCharacterSlot(
                    connection,
                    transaction,
                    inventory.CharacterId,
                    listType,
                    pair.Key,
                    core);

                affected++;
            }

            return affected;
        }
    }
}

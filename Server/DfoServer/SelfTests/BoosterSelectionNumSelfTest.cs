using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class BoosterSelectionNumSelfTest
    {
        private const int ArmorBoxItemTemplateId = 10006309;
        private const int WeaponBoxItemTemplateId = 10003675;
        private const int WeaponBoxSelectedItemTemplateId = 101000027;
        private const int AccountId = 9802001;
        private const int CharacterId = 9802002;

        private const short ArmorSilkSlot = 10;
        private const short ArmorLeatherSlot = 11;
        private const short ArmorPlateSlot = 12;
        private const short WeaponSlot = 13;
        private const short ArmorForeignIdSlot = 14;
        private const short ArmorBadCategorySlot = 15;
        private const short WeaponOverCountSlot = 16;
        private const short ArmorDuplicateIdSlot = 17;

        private static readonly int[] SilkCategoryItemIds = { 10113, 12111, 14101, 16087, 18092 };
        private static readonly int[] LeatherCategoryItemIds = { 10489, 12489, 14480, 16479, 18484 };
        private static readonly int[] PlateCategoryItemIds = { 11655, 13654, 15650, 17650, 19654 };

        // 线上抓包: 10006309 传承防具礼盒, body[2..4]=分类索引, 后接整组 5 个物品 id + flag。
        private static readonly byte[] SilkRequestBody =
        {
            0x5C, 0x00, 0x00, 0x00,
            0x81, 0x27, 0x00, 0x00,
            0x4F, 0x2F, 0x00, 0x00,
            0x15, 0x37, 0x00, 0x00,
            0xD7, 0x3E, 0x00, 0x00,
            0xAC, 0x46, 0x00, 0x00,
            0x00
        };

        private static readonly byte[] LeatherRequestBody =
        {
            0x5C, 0x00, 0x01, 0x00,
            0xF9, 0x28, 0x00, 0x00,
            0xC9, 0x30, 0x00, 0x00,
            0x90, 0x38, 0x00, 0x00,
            0x5F, 0x40, 0x00, 0x00,
            0x34, 0x48, 0x00, 0x00,
            0x00
        };

        private static readonly byte[] PlateRequestBody =
        {
            0x5C, 0x00, 0x04, 0x00,
            0x87, 0x2D, 0x00, 0x00,
            0x56, 0x35, 0x00, 0x00,
            0x22, 0x3D, 0x00, 0x00,
            0xF2, 0x44, 0x00, 0x00,
            0xC6, 0x4C, 0x00, 0x00,
            0x00
        };

        // 线上抓包: 10003675 终极镇魂武器礼盒 selnum=1, 单选 1 个 id。
        private static readonly byte[] WeaponRequestBody =
        {
            0x5C, 0x00, 0x01, 0x00,
            0x5B, 0x23, 0x05, 0x06,
            0x00
        };

        // 装扮多选布局抓包(77B), 解析行为必须保持现状。
        private static readonly byte[] AvatarRequestBody =
        {
            0x44, 0x00, 0x00, 0x00,
            0x27, 0x8A, 0x0D, 0x06,
            0xCE, 0xB1, 0x0D, 0x06,
            0xE4, 0xD7, 0x0D, 0x06,
            0xDF, 0x14, 0x0D, 0x06,
            0x8C, 0xC7, 0x0C, 0x06,
            0x3A, 0xEF, 0x0C, 0x06,
            0xBE, 0x3B, 0x0D, 0x06,
            0x71, 0x63, 0x0D, 0x06,
            0x08,
            0x27, 0x8A, 0x0D, 0x06, 0x01,
            0xCE, 0xB1, 0x0D, 0x06, 0x01,
            0xE4, 0xD7, 0x0D, 0x06, 0x02,
            0xDF, 0x14, 0x0D, 0x06, 0x02,
            0x8C, 0xC7, 0x0C, 0x06, 0x09,
            0x3A, 0xEF, 0x0C, 0x06, 0x00,
            0xBE, 0x3B, 0x0D, 0x06, 0x04,
            0x71, 0x63, 0x0D, 0x06, 0x00
        };

        private static readonly int[] AvatarItemTemplateIds =
        {
            101550631,
            101560782,
            101570532,
            101520607,
            101500812,
            101510970,
            101530558,
            101540721
        };

        public static int Run()
        {
            Console.WriteLine("=== BOOSTER_SELECTION_NUM selftest ===");
            var failures = 0;

            VerifyRequestLayoutParsing(ref failures);

            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath))
            {
                Console.WriteLine("[SKIP] booster selection PVF/DB checks: PVF_ARCHIVE_PATH is not set");
                PrintSummary(failures);
                return failures == 0 ? 0 : 1;
            }

            var pvfReady = VerifyDefinitionCategories(ref failures)
                && VerifyGrantResolverRules(ref failures)
                && VerifyFallbackResolverConsistency(ref failures);
            if (pvfReady)
                VerifySelectablePackageEndToEnd(ref failures);

            PrintSummary(failures);
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyRequestLayoutParsing(ref int failures)
        {
            Check("25B 布甲包解析全部 5 个 id", SelectablePackageOpenRequest.TryParse(SilkRequestBody, out var silk)
                && silk.SelectionContext == 0
                && silk.SelectedItemTemplateId == 10113
                && SameIds(silk.SelectedItemTemplateIds, SilkCategoryItemIds)
                && !silk.HasAvatarChoices, ref failures);

            Check("25B 皮甲包解析全部 5 个 id", SelectablePackageOpenRequest.TryParse(LeatherRequestBody, out var leather)
                && leather.SelectionContext == 1
                && SameIds(leather.SelectedItemTemplateIds, LeatherCategoryItemIds), ref failures);

            Check("25B 板甲包解析全部 5 个 id", SelectablePackageOpenRequest.TryParse(PlateRequestBody, out var plate)
                && plate.SelectionContext == 4
                && SameIds(plate.SelectedItemTemplateIds, PlateCategoryItemIds), ref failures);

            Check("9B 单选包解析 1 个 id", SelectablePackageOpenRequest.TryParse(WeaponRequestBody, out var weapon)
                && weapon.SelectionContext == 1
                && weapon.SelectedItemTemplateId == WeaponBoxSelectedItemTemplateId
                && SameIds(weapon.SelectedItemTemplateIds, new[] { WeaponBoxSelectedItemTemplateId })
                && weapon.SelectionFlag == 0, ref failures);

            Check("2B 短包解析失败", !SelectablePackageOpenRequest.TryParse(new byte[] { 0x42, 0x00 }, out _), ref failures);

            Check("装扮多选布局仍解析为 avatar choices",
                SelectablePackageOpenRequest.TryParse(AvatarRequestBody, out var avatar)
                && avatar.HasAvatarChoices
                && avatar.AvatarChoices.Count == AvatarItemTemplateIds.Length
                && avatar.SelectedItemTemplateIds.Count == 0
                && avatar.AvatarChoices.Select(choice => choice.ItemTemplateId)
                    .SequenceEqual(AvatarItemTemplateIds), ref failures);
        }

        private static bool VerifyDefinitionCategories(ref int failures)
        {
            var resolved = SelectablePackageDefinitionResolver.TryResolve(ArmorBoxItemTemplateId, out var armor);
            Check("10006309 definition 解析", resolved, ref failures);
            if (!resolved)
                return false;

            Check("10006309 收集全部 5 个分类", armor.Categories.Count == 5, ref failures);
            Check("分类 0 = 布甲", SameIds(CategoryIds(armor, 0), SilkCategoryItemIds), ref failures);
            Check("分类 1 = 皮甲", SameIds(CategoryIds(armor, 1), LeatherCategoryItemIds), ref failures);
            Check("分类 4 = 板甲", SameIds(CategoryIds(armor, 4), PlateCategoryItemIds), ref failures);
            Check("扁平 Rewards 保留全部 25 件", armor.Rewards.Count == 25, ref failures);
            Check("按分类查询命中且不串分类",
                armor.TryGetReward(1, 10489, out _)
                && !armor.TryGetReward(0, 10489, out _)
                && !armor.TryGetReward(9, 10489, out _), ref failures);

            var weaponResolved = SelectablePackageDefinitionResolver.TryResolve(WeaponBoxItemTemplateId, out var weaponDef);
            Check("10003675 definition 解析", weaponResolved, ref failures);
            if (!weaponResolved)
                return false;

            Check("10003675 分类 1 含选中武器",
                CategoryIds(weaponDef, 1).Contains(WeaponBoxSelectedItemTemplateId), ref failures);

            return true;
        }

        private static bool VerifyGrantResolverRules(ref int failures)
        {
            var resolved = SelectablePackageDefinitionResolver.TryResolve(ArmorBoxItemTemplateId, out var armor);
            if (!resolved)
                return false;

            var categories = new List<IReadOnlyList<int>>();
            for (var i = 0; i < armor.Categories.Count; i++)
                categories.Add(CategoryIds(armor, i).ToList());

            Check("selnum=0 分类 0 发整组布甲",
                BoosterSelectionGrantResolver.TryResolveGrantedItemIds(
                    0, categories, 0, SilkCategoryItemIds, out var silkGranted)
                && SameIds(silkGranted, SilkCategoryItemIds), ref failures);

            Check("selnum=0 分类 4 发整组板甲",
                BoosterSelectionGrantResolver.TryResolveGrantedItemIds(
                    0, categories, 4, PlateCategoryItemIds, out var plateGranted)
                && SameIds(plateGranted, PlateCategoryItemIds), ref failures);

            Check("selnum=1 只发 1 个选中 id",
                BoosterSelectionGrantResolver.TryResolveGrantedItemIds(
                    1, categories, 1, new[] { 10489 }, out var singleGranted)
                && SameIds(singleGranted, new[] { 10489 }), ref failures);

            Check("selnum=1 发 2 个 id 整体拒绝",
                !BoosterSelectionGrantResolver.TryResolveGrantedItemIds(
                    1, categories, 1, new[] { 10489, 12489 }, out _), ref failures);

            Check("selnum=0 选中 id 跨分类整体拒绝",
                !BoosterSelectionGrantResolver.TryResolveGrantedItemIds(
                    0, categories, 0, new[] { 10113, 10489 }, out _), ref failures);

            Check("分类索引越界整体拒绝",
                !BoosterSelectionGrantResolver.TryResolveGrantedItemIds(
                    0, categories, 5, SilkCategoryItemIds, out _), ref failures);

            Check("重复选中 id 整体拒绝",
                !BoosterSelectionGrantResolver.TryResolveGrantedItemIds(
                    0, categories, 0, new[] { 10113, 10113, 12111 }, out _), ref failures);

            Check("无分类索引时按选中 id 反查唯一分类",
                BoosterSelectionGrantResolver.TryResolveGrantedItemIds(
                    0, categories, -1, LeatherCategoryItemIds, out var derivedGranted)
                && SameIds(derivedGranted, LeatherCategoryItemIds), ref failures);

            return true;
        }

        private static bool VerifyFallbackResolverConsistency(ref int failures)
        {
            var stackable = StackableItemProvider.Load(ArmorBoxItemTemplateId);
            Check("10006309 stackable 载入", stackable != null, ref failures);
            if (stackable == null)
                return false;

            var stackableType = InventoryPackageRewardResolver.NormalizeStackableType(stackable.StackableType);
            Check("10006309 类型为 [booster selection]",
                string.Equals(stackableType, "[booster selection]", StringComparison.OrdinalIgnoreCase), ref failures);

            Check("兜底 selnum=0 布甲发整组",
                InventoryPackageRewardResolver.TryResolvePackageRewards(
                    ArmorBoxItemTemplateId, stackable, stackableType, SilkCategoryItemIds, null, out var silkRewards)
                && SameIds(silkRewards.Select(reward => reward.ItemId), SilkCategoryItemIds), ref failures);

            Check("兜底 selnum=0 板甲发整组",
                InventoryPackageRewardResolver.TryResolvePackageRewards(
                    ArmorBoxItemTemplateId, stackable, stackableType, PlateCategoryItemIds, null, out var plateRewards)
                && SameIds(plateRewards.Select(reward => reward.ItemId), PlateCategoryItemIds), ref failures);

            Check("兜底 id 不属于任何分类整体拒绝",
                !InventoryPackageRewardResolver.TryResolvePackageRewards(
                    ArmorBoxItemTemplateId, stackable, stackableType, new[] { 10113, 999999 }, null, out _),
                ref failures);

            Check("兜底分组不唯一时退化为发选中 id",
                InventoryPackageRewardResolver.TryResolvePackageRewards(
                    ArmorBoxItemTemplateId, stackable, stackableType, new[] { 10113, 10489 }, null, out var degraded)
                && SameIds(degraded.Select(reward => reward.ItemId), new[] { 10113, 10489 }), ref failures);

            Check("兜底无选中信息时保留整表兼容",
                InventoryPackageRewardResolver.TryResolvePackageRewards(
                    ArmorBoxItemTemplateId, stackable, stackableType, Array.Empty<int>(), null, out var allRewards)
                && allRewards.Count == 25, ref failures);

            var weaponStackable = StackableItemProvider.Load(WeaponBoxItemTemplateId);
            Check("10003675 stackable 载入", weaponStackable != null, ref failures);
            if (weaponStackable == null)
                return false;

            var weaponType = InventoryPackageRewardResolver.NormalizeStackableType(weaponStackable.StackableType);
            Check("兜底 selnum=1 单选 1 件",
                InventoryPackageRewardResolver.TryResolvePackageRewards(
                    WeaponBoxItemTemplateId, weaponStackable, weaponType,
                    new[] { WeaponBoxSelectedItemTemplateId }, null, out var weaponRewards)
                && weaponRewards.Count == 1
                && weaponRewards[0].ItemId == WeaponBoxSelectedItemTemplateId, ref failures);

            Check("兜底 selnum=1 发 2 个 id 整体拒绝",
                !InventoryPackageRewardResolver.TryResolvePackageRewards(
                    WeaponBoxItemTemplateId, weaponStackable, weaponType,
                    new[] { WeaponBoxSelectedItemTemplateId, 101040045 }, null, out _), ref failures);

            return true;
        }

        private static void VerifySelectablePackageEndToEnd(ref int failures)
        {
            const int accountId = AccountId;
            const int characterId = CharacterId;
            var sessionId = Guid.NewGuid();
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"s4a21-booster-selection-{Guid.NewGuid():N}.db");
            InventoryLease lease = null;

            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                SeedCharacter(database, accountId, characterId);
                lease = RegisterInventoryWithBoxes(database, sessionId, characterId, accountId);

                CheckMainPathOpen("主路径 布甲 发整组 5 件", lease,
                    WithSlot(SelectablePackageOpenRequest.TryParse(SilkRequestBody, out var silkRequest)
                        ? silkRequest
                        : null, ArmorSilkSlot),
                    SilkCategoryItemIds, ArmorSilkSlot, ArmorBoxItemTemplateId, ref failures);

                CheckMainPathOpen("主路径 皮甲 发整组 5 件", lease,
                    WithSlot(SelectablePackageOpenRequest.TryParse(LeatherRequestBody, out var leatherRequest)
                        ? leatherRequest
                        : null, ArmorLeatherSlot),
                    LeatherCategoryItemIds, ArmorLeatherSlot, ArmorBoxItemTemplateId, ref failures);

                CheckMainPathOpen("主路径 板甲 发整组 5 件", lease,
                    WithSlot(SelectablePackageOpenRequest.TryParse(PlateRequestBody, out var plateRequest)
                        ? plateRequest
                        : null, ArmorPlateSlot),
                    PlateCategoryItemIds, ArmorPlateSlot, ArmorBoxItemTemplateId, ref failures);

                CheckMainPathOpen("主路径 selnum=1 只发 1 件", lease,
                    WithSlot(SelectablePackageOpenRequest.TryParse(WeaponRequestBody, out var weaponRequest)
                        ? weaponRequest
                        : null, WeaponSlot),
                    new[] { WeaponBoxSelectedItemTemplateId }, WeaponSlot, WeaponBoxItemTemplateId, ref failures);

                var foreign = new SelectablePackageOpenRequest
                {
                    SlotIndex = ArmorForeignIdSlot,
                    SelectionContext = 0,
                    SelectedItemTemplateId = 10113,
                };
                foreign.SelectedItemTemplateIds.AddRange(new[] { 10113, 12111, 14101, 16087, 10489 });
                Check("主路径 id 跨分类整体拒绝且不消耗",
                    !TryOpen(lease, foreign, out _)
                    && SlotKeepsBox(lease, ArmorForeignIdSlot, ArmorBoxItemTemplateId), ref failures);

                var badCategory = new SelectablePackageOpenRequest
                {
                    SlotIndex = ArmorBadCategorySlot,
                    SelectionContext = 5,
                    SelectedItemTemplateId = 10113,
                };
                badCategory.SelectedItemTemplateIds.AddRange(SilkCategoryItemIds);
                Check("主路径 分类索引越界整体拒绝且不消耗",
                    !TryOpen(lease, badCategory, out _)
                    && SlotKeepsBox(lease, ArmorBadCategorySlot, ArmorBoxItemTemplateId), ref failures);

                var overCount = new SelectablePackageOpenRequest
                {
                    SlotIndex = WeaponOverCountSlot,
                    SelectionContext = 1,
                    SelectedItemTemplateId = WeaponBoxSelectedItemTemplateId,
                };
                overCount.SelectedItemTemplateIds.AddRange(new[] { WeaponBoxSelectedItemTemplateId, 101040045 });
                Check("主路径 selnum=1 超额整体拒绝且不消耗",
                    !TryOpen(lease, overCount, out _)
                    && SlotKeepsBox(lease, WeaponOverCountSlot, WeaponBoxItemTemplateId), ref failures);

                var duplicate = new SelectablePackageOpenRequest
                {
                    SlotIndex = ArmorDuplicateIdSlot,
                    SelectionContext = 0,
                    SelectedItemTemplateId = 10113,
                };
                duplicate.SelectedItemTemplateIds.AddRange(new[] { 10113, 10113, 12111, 14101, 16087, 18092 });
                Check("主路径 重复选中 id 整体拒绝且不消耗",
                    !TryOpen(lease, duplicate, out _)
                    && SlotKeepsBox(lease, ArmorDuplicateIdSlot, ArmorBoxItemTemplateId), ref failures);
            }
            catch (Exception ex)
            {
                Check("boosterselection 端到端异常: " + ex.Message, false, ref failures);
            }
            finally
            {
                if (lease != null)
                    InventoryContext.Unregister(sessionId, characterId);

                SqliteConnection.ClearAllPools();
                TryDeleteDatabase(tempDbPath);
            }
        }

        private static void SeedCharacter(GameDatabase database, int accountId, int characterId)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'booster-selection-selftest', '');
INSERT INTO characters (character_id, account_id, name, job, level)
VALUES (@cid, @aid, 'booster-selection-selftest', 0, 85);";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }
        }

        private static InventoryLease RegisterInventoryWithBoxes(
            GameDatabase database,
            Guid sessionId,
            int characterId,
            int accountId)
        {
            InventoryService inventory;
            using (var connection = database.OpenConnection())
            {
                inventory = InventoryService.LoadFromDb(connection, characterId, accountId, database);
            }

            var seeds = new (short Slot, int ItemId)[]
            {
                (ArmorSilkSlot, ArmorBoxItemTemplateId),
                (ArmorLeatherSlot, ArmorBoxItemTemplateId),
                (ArmorPlateSlot, ArmorBoxItemTemplateId),
                (WeaponSlot, WeaponBoxItemTemplateId),
                (ArmorForeignIdSlot, ArmorBoxItemTemplateId),
                (ArmorBadCategorySlot, ArmorBoxItemTemplateId),
                (WeaponOverCountSlot, WeaponBoxItemTemplateId),
                (ArmorDuplicateIdSlot, ArmorBoxItemTemplateId),
            };
            foreach (var seed in seeds)
            {
                inventory.SetItem(
                    InventoryListType.Main,
                    seed.Slot,
                    new ItemCore
                    {
                        ItemKind = ItemCore.KindConsumable,
                        ItemId = seed.ItemId,
                        Count = 1,
                    });
            }

            var lease = InventoryContext.Register(sessionId, characterId, inventory);
            if (!OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "selftest-seed-booster-selection"))
                throw new InvalidOperationException("failed to persist booster selection selftest boxes");

            return lease;
        }

        private static bool TryOpen(
            InventoryLease lease,
            SelectablePackageOpenRequest request,
            out SelectablePackageOpenResult result)
        {
            result = null;
            lock (lease.SyncRoot)
            {
                return InventorySpecialConsumableCommitService.TryCommitSelectablePackage(
                    lease,
                    request,
                    null,
                    out result,
                    out _);
            }
        }

        private static SelectablePackageOpenRequest WithSlot(
            SelectablePackageOpenRequest request,
            short slotIndex)
        {
            if (request != null)
                request.SlotIndex = slotIndex;
            return request;
        }

        private static void CheckMainPathOpen(
            string name,
            InventoryLease lease,
            SelectablePackageOpenRequest request,
            IReadOnlyList<int> expectedItemIds,
            short slotIndex,
            int boxItemTemplateId,
            ref int failures)
        {
            if (request == null)
            {
                Check(name, false, ref failures);
                return;
            }

            var opened = TryOpen(lease, request, out var result);
            var grantedItemIds = result == null
                ? new List<int>()
                : result.GrantedItems.Select(item => item.ItemTemplateId).ToList();
            var boxConsumed = !SlotKeepsBox(lease, slotIndex, boxItemTemplateId);
            var ok = opened && SameIds(grantedItemIds, expectedItemIds) && boxConsumed;
            Check(
                ok
                    ? name
                    : $"{name} [opened={opened} granted={string.Join(",", grantedItemIds)} boxConsumed={boxConsumed}]",
                ok,
                ref failures);
        }

        private static bool SlotKeepsBox(InventoryLease lease, short slotIndex, int boxItemTemplateId)
        {
            var item = lease.Inventory.GetItem(InventoryListType.Main, slotIndex);
            return item != null && item.ItemId == boxItemTemplateId && item.Count == 1;
        }

        private static IEnumerable<int> CategoryIds(SelectablePackageDefinition definition, int categoryIndex)
            => definition.TryGetCategory(categoryIndex, out var category)
                ? category.Select(entry => entry.ItemTemplateId)
                : Enumerable.Empty<int>();

        private static bool SameIds(IEnumerable<int> actual, IReadOnlyList<int> expected)
            => actual != null
               && actual.OrderBy(id => id).SequenceEqual(expected.OrderBy(id => id));

        private static void TryDeleteDatabase(string path)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    var file = path + suffix;
                    if (File.Exists(file))
                        File.Delete(file);
                }
                catch
                {
                }
            }
        }

        private static void PrintSummary(int failures)
            => Console.WriteLine(failures == 0
                ? "BOOSTER_SELECTION_NUM selftest passed"
                : $"BOOSTER_SELECTION_NUM selftest failed: {failures}");

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}

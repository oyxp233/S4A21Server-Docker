using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class DeathTowerDropSelfTest
    {
        private const int DeathTowerDungeonId = 11000;
        private const uint FixedStageSeed = 0x12345678;

        public static int Run()
        {
            Console.WriteLine("=== DEATH_TOWER_DROP selftest ===");

            var failures = 0;
            Check("AiConfigFile parses every typed [death tower item] pair",
                HasParsedTowerItems(), ref failures);
            Check("quest 932 parses Strength Essence as a Death Tower APC reward",
                HasParsedStrengthEssenceQuestDrop(), ref failures);

            var loadStageItems = typeof(DeathTowerMapLoader)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "LoadStageItems"
                    && method.GetParameters().Length == 2);
            Check("DeathTowerMapLoader exposes LoadStageItems(tower, monsters)",
                loadStageItems != null, ref failures);

            var buildStageMap = typeof(DeathTowerPacketBuilder)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(method => method.Name == "BuildStageMap"
                    && method.GetParameters().Length == 4
                    && method.GetParameters()[3].ParameterType == typeof(uint));
            Check("BuildStageMap accepts monsters, items, and one fixed uint seed",
                buildStageMap != null, ref failures);
            Check("0x008F caps serialized monster records to its byte count",
                StagePacketCapsMonsterCount(), ref failures);

            var generateTowerDrops = typeof(DeathTowerCoordinator).GetMethod(
                "TryGenerateDropsForMonster",
                BindingFlags.Public | BindingFlags.Instance);
            Check("tower DIE_MONSTER exposes drops without owning the whole combat pipeline",
                generateTowerDrops != null, ref failures);

            if (loadStageItems != null && buildStageMap != null)
            {
                Check("all tower floors expose confirmed 6515/6518/6521/6524 APC configs",
                    CollectConfiguredTowerItemIds(loadStageItems).IsSupersetOf(
                        new[] { 6515, 6518, 6521, 6524 }),
                    ref failures);
                Check("real PVF stages expose tower-exclusive APC items",
                    TryFindStageWithItems(loadStageItems, out var snapshot), ref failures);

                if (snapshot != null)
                {
                    Check("stage items bind to the same APC list index and monster unique id",
                        StageItemsBindToApcs(snapshot), ref failures);
                    Check("stage item unique ids are non-zero and unique",
                        StageItemIdsAreStable(snapshot), ref failures);
                    Check("0x008F keeps 9B+14B*n+1B+18B*n and documented item offsets",
                        StagePacketMatchesLayout(buildStageMap, snapshot), ref failures);
                    Check("0x008F writes the caller-provided stage seed",
                        StagePacketUsesFixedSeed(buildStageMap, snapshot), ref failures);
                }
            }

            var generateDrops = typeof(DeathTowerSession).GetMethod(
                "GenerateDropsForMonster",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(ushort) },
                null);
            Check("DeathTowerSession exposes seeded tower drop generation",
                generateDrops != null, ref failures);
            if (generateDrops != null)
            {
                Check("full/zero drop rates and duplicate deaths are handled in tower state",
                    TowerDropRatesAndDedupe(generateDrops, out var firstDrops, out var tower),
                    ref failures);
                Check("0x0026 keeps dropCount and 39B itemId offsets",
                    TowerMonsterDiePacketMatchesLayout(firstDrops), ref failures);
                Check("same stage seed produces the same tower drop decision",
                    TowerDropDecisionIsDeterministic(generateDrops, firstDrops), ref failures);
                Check("advancing a floor clears unpicked tower ground items",
                    AdvancingFloorClearsGroundItems(tower), ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool HasParsedTowerItems()
        {
            const string content =
                "[death tower item]\n6515 10000\n6518 7500\n[/death tower item]\n";
            var parsed = AiConfigFile.Parse(content);
            var property = typeof(AiConfigFile).GetProperty("DeathTowerItems");
            var items = (property?.GetValue(parsed) as IEnumerable)?.Cast<object>().ToList();
            if (items == null || items.Count != 2)
                return false;

            return ReadInt(items[0], "ItemId") == 6515
                && ReadInt(items[0], "DropRate") == 10000
                && ReadInt(items[1], "ItemId") == 6518
                && ReadInt(items[1], "DropRate") == 7500;
        }

        private static bool HasParsedStrengthEssenceQuestDrop()
        {
            const int questId = 932;
            const int apcCode = 10504;
            const int itemId = 10089420;
            var quest = QuestData.GetQuestFile(questId);
            var candidates = QuestDropProvider.CheckEnemyDrop(
                new[] { questId },
                DeathTowerDungeonId,
                0,
                apcCode,
                QuestDropProvider.EnemyTypeAiCharacter);
            return quest != null
                && quest.IntData == "10089420 10"
                && candidates != null
                && candidates.Count == 1
                && candidates[0].ItemId == itemId
                && candidates[0].Count == 2
                && candidates[0].DropRate == 35
                && candidates[0].MaxStack == -1;
        }

        private static bool StagePacketCapsMonsterCount()
        {
            var tower = new DeathTowerSession(
                DeathTowerSelfTestFactory.CreateConfig(
                    DeathTowerDungeonId,
                    new[] { 1 },
                    50));
            var monsters = new List<StageMonster>();
            for (var index = 0; index <= byte.MaxValue; index++)
            {
                monsters.Add(new StageMonster
                {
                    ListIndex = index,
                    MonsterUniqueId = (ushort)(index + 1),
                    MonsterIndex = 1,
                    MonsterLevel = 1,
                });
            }

            var body = DeathTowerPacketBuilder.BuildStageMap(
                tower,
                monsters,
                Array.Empty<StageTowerItem>(),
                FixedStageSeed);
            return body[8] == byte.MaxValue
                && body.Length == 9 + byte.MaxValue * 14 + 1;
        }

        private static bool TryFindStageWithItems(MethodInfo loadStageItems, out StageSnapshot snapshot)
        {
            snapshot = null;
            var config = DeathTowerData.GetConfig(DeathTowerDungeonId);
            if (config == null)
                return false;

            var tower = new DeathTowerSession(config);
            for (var stage = 0; stage < config.TotalStages; stage++)
            {
                if (stage > 0)
                {
                    tower.SetFighting();
                    if (!tower.TryAdvanceStage())
                        return false;
                }

                var monsters = DeathTowerMapLoader.LoadStageMonsters(tower);
                var items = loadStageItems.Invoke(null, new object[] { tower, monsters }) as IEnumerable;
                var itemList = items?.Cast<object>().ToList();
                if (itemList != null && itemList.Count > 0)
                {
                    snapshot = new StageSnapshot(tower, monsters, itemList, items);
                    return true;
                }
            }

            return false;
        }

        private static HashSet<int> CollectConfiguredTowerItemIds(MethodInfo loadStageItems)
        {
            var result = new HashSet<int>();
            var config = DeathTowerData.GetConfig(DeathTowerDungeonId);
            if (config == null)
                return result;

            var tower = new DeathTowerSession(config);
            for (var stage = 0; stage < config.TotalStages; stage++)
            {
                if (stage > 0)
                {
                    tower.SetFighting();
                    if (!tower.TryAdvanceStage())
                        break;
                }

                var monsters = DeathTowerMapLoader.LoadStageMonsters(tower);
                var items = loadStageItems.Invoke(null, new object[] { tower, monsters }) as IEnumerable;
                if (items == null)
                    continue;
                foreach (var item in items)
                {
                    var itemId = ReadInt(item, "ItemId");
                    if (itemId > 0)
                        result.Add(itemId);
                }
            }
            return result;
        }

        private static bool StageItemsBindToApcs(StageSnapshot snapshot)
        {
            foreach (var item in snapshot.Items)
            {
                var sourceListIndex = ReadInt(item, "SourceListIndex");
                var sourceUniqueId = ReadInt(item, "SourceMonsterUniqueId");
                var monster = snapshot.Monsters.FirstOrDefault(candidate =>
                    candidate.ListIndex == sourceListIndex);
                if (monster.MonsterType < 5
                    || monster.MonsterType > 8
                    || monster.MonsterUniqueId != sourceUniqueId)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool StageItemIdsAreStable(StageSnapshot snapshot)
        {
            var ids = snapshot.Items.Select(item => ReadInt(item, "ItemUniqueId")).ToList();
            return snapshot.Items.Count <= byte.MaxValue
                && ids.All(id => id > 0)
                && ids.Distinct().Count() == ids.Count;
        }

        private static bool StagePacketMatchesLayout(MethodInfo buildStageMap, StageSnapshot snapshot)
        {
            var body = InvokeBuildStageMap(buildStageMap, snapshot);
            if (body == null)
                return false;

            var itemCountOffset = 9 + snapshot.Monsters.Count * 14;
            var expectedLength = itemCountOffset + 1 + snapshot.Items.Count * 18;
            if (body.Length != expectedLength || body[itemCountOffset] != snapshot.Items.Count)
                return false;

            for (var i = 0; i < snapshot.Items.Count; i++)
            {
                var item = snapshot.Items[i];
                var offset = itemCountOffset + 1 + i * 18;
                if (BitConverter.ToUInt32(body, offset) != (uint)ReadInt(item, "SourceListIndex")
                    || BitConverter.ToUInt16(body, offset + 4) != (ushort)ReadInt(item, "ItemUniqueId")
                    || BitConverter.ToUInt32(body, offset + 6) != (uint)ReadInt(item, "ItemId")
                    || BitConverter.ToUInt32(body, offset + 10) != (uint)ReadInt(item, "DropRate")
                    || BitConverter.ToUInt32(body, offset + 14) != (uint)ReadInt(item, "StackCount"))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool StagePacketUsesFixedSeed(MethodInfo buildStageMap, StageSnapshot snapshot)
        {
            var body = InvokeBuildStageMap(buildStageMap, snapshot);
            return body != null
                && body.Length >= 6
                && BitConverter.ToUInt32(body, 2) == FixedStageSeed;
        }

        private static byte[] InvokeBuildStageMap(MethodInfo buildStageMap, StageSnapshot snapshot)
        {
            try
            {
                return buildStageMap.Invoke(
                    null,
                    new object[] { snapshot.Tower, snapshot.Monsters, snapshot.RawItems, FixedStageSeed })
                    as byte[];
            }
            catch (TargetInvocationException ex)
            {
                Console.WriteLine($"[FAIL] BuildStageMap threw: {ex.InnerException?.Message ?? ex.Message}");
                return null;
            }
        }

        private static bool TowerDropRatesAndDedupe(
            MethodInfo generateDrops,
            out List<DropInfo> firstDrops,
            out DeathTowerSession tower)
        {
            tower = CreateDropTestTower();
            firstDrops = InvokeGenerateDrops(generateDrops, tower, 77);
            var duplicateDrops = InvokeGenerateDrops(generateDrops, tower, 77);
            var groundCount = ReadEnumerablePropertyCount(tower, "GroundItems");

            return firstDrops.Count == 1
                && firstDrops[0].SceneSlot == 11
                && firstDrops[0].TemplateId == 6515
                && firstDrops[0].StackCount == 1
                && duplicateDrops.Count == 0
                && groundCount == 1;
        }

        private static bool TowerMonsterDiePacketMatchesLayout(IReadOnlyList<DropInfo> drops)
        {
            var body = DungeonNotificationBuilder.BuildMonsterDie(77, drops, 88);
            return body.Length == 3 + drops.Count * 39 + 4
                && body[2] == drops.Count
                && drops.Count == 1
                && BitConverter.ToUInt16(body, 3) == drops[0].SceneSlot
                && BitConverter.ToUInt32(body, 3 + 2) == drops[0].TemplateId;
        }

        private static bool TowerDropDecisionIsDeterministic(
            MethodInfo generateDrops,
            IReadOnlyList<DropInfo> firstDrops)
        {
            var secondTower = CreateDropTestTower();
            var secondDrops = InvokeGenerateDrops(generateDrops, secondTower, 77);
            return firstDrops.Select(drop => drop.SceneSlot).SequenceEqual(
                    secondDrops.Select(drop => drop.SceneSlot))
                && firstDrops.Select(drop => drop.TemplateId).SequenceEqual(
                    secondDrops.Select(drop => drop.TemplateId));
        }

        private static bool AdvancingFloorClearsGroundItems(DeathTowerSession tower)
        {
            tower.SetFighting();
            return tower.TryAdvanceStage()
                && ReadEnumerablePropertyCount(tower, "GroundItems") == 0;
        }

        private static DeathTowerSession CreateDropTestTower()
        {
            var tower = new DeathTowerSession(DeathTowerData.GetConfig(DeathTowerDungeonId));
            tower.BeginStage(FixedStageSeed, new[]
            {
                new StageTowerItem
                {
                    SourceListIndex = 1,
                    SourceMonsterUniqueId = 77,
                    ItemUniqueId = 11,
                    ItemId = 6515,
                    DropRate = 10000,
                    StackCount = 1,
                },
                new StageTowerItem
                {
                    SourceListIndex = 1,
                    SourceMonsterUniqueId = 77,
                    ItemUniqueId = 12,
                    ItemId = 6518,
                    DropRate = 0,
                    StackCount = 1,
                },
                new StageTowerItem
                {
                    SourceListIndex = 1,
                    SourceMonsterUniqueId = 77,
                    ItemUniqueId = 13,
                    ItemId = 6521,
                    DropRate = 5000,
                    StackCount = 1,
                },
            });
            return tower;
        }

        private static List<DropInfo> InvokeGenerateDrops(
            MethodInfo generateDrops,
            DeathTowerSession tower,
            ushort monsterUniqueId)
        {
            try
            {
                var result = generateDrops.Invoke(tower, new object[] { monsterUniqueId }) as IEnumerable;
                return result?.Cast<object>().Select(value => (DropInfo)value).ToList()
                    ?? new List<DropInfo>();
            }
            catch (TargetInvocationException ex)
            {
                Console.WriteLine($"[FAIL] GenerateDropsForMonster threw: {ex.InnerException?.Message ?? ex.Message}");
                return new List<DropInfo>();
            }
        }

        private static int ReadEnumerablePropertyCount(object value, string name)
        {
            var property = value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            var enumerable = property?.GetValue(value) as IEnumerable;
            return enumerable?.Cast<object>().Count() ?? -1;
        }

        private static int ReadInt(object value, string name)
        {
            var type = value.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null)
                return Convert.ToInt32(property.GetValue(value));
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field == null ? int.MinValue : Convert.ToInt32(field.GetValue(value));
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private sealed class StageSnapshot
        {
            public StageSnapshot(
                DeathTowerSession tower,
                List<StageMonster> monsters,
                List<object> items,
                object rawItems)
            {
                Tower = tower;
                Monsters = monsters;
                Items = items;
                RawItems = rawItems;
            }

            public DeathTowerSession Tower { get; }
            public List<StageMonster> Monsters { get; }
            public List<object> Items { get; }
            public object RawItems { get; }
        }
    }
}

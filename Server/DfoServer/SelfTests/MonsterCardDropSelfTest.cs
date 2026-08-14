using System;
using System.Text.RegularExpressions;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;

namespace DfoServer.SelfTests
{
    public static class MonsterCardDropSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== MONSTER_CARD_DROP selftest ===");
            var failures = 0;

            Check("drop rate API exposes only consumed categories",
                DropRateApiExposesOnlyConsumedCategories(), ref failures);

            Check("independent drop PVF entry mob=907 can produce card 3610",
                IndependentDropCanProduceKnownCard(907, 3610, 1, 37), ref failures);

            Check("independent drop PVF entry mob=61236 can produce its card 3726",
                IndependentDropCanProduceKnownCard(61236, 3726, 61, 421), ref failures);

            Check("mapped mob=61236 card 3726 respects PVF probability when seed 0 misses",
                !IndependentDropCanProduceKnownCard(61236, 3726, 61, 0), ref failures);

            Check("independent drop uses party attempts and fifth count cap",
                IndependentDropUsesPartyAttemptsAndCap(), ref failures);

            Check("world drop PVF table does not contain monster cards",
                !WorldDropContainsMonsterCards(), ref failures);

            Check("monster without independent drop mapping cannot produce monster cards",
                MonsterWithoutIndependentDropCannotProduceMonsterCard(), ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static bool DropRateApiExposesOnlyConsumedCategories()
        {
            var method = typeof(MonsterDropConfig).GetMethod(nameof(MonsterDropConfig.GetAllDropRates));
            if (method == null) return false;

            var parameters = method.GetParameters();
            if (parameters.Length != 6) return false;

            string[] expectedNames =
            {
                "monsterLevel", "monsterType", "goldRate",
                "type1Rate", "type2Rate", "type3Rate"
            };
            for (int i = 0; i < parameters.Length; i++)
            {
                if (!string.Equals(parameters[i].Name, expectedNames[i], StringComparison.Ordinal))
                    return false;
                if (i >= 2 && (!parameters[i].IsOut
                    || parameters[i].ParameterType != typeof(int).MakeByRefType()))
                    return false;
            }
            return true;
        }

        private static bool IndependentDropCanProduceKnownCard(
            int monsterCode, int cardItemId, int dungeonLevel, uint seed)
        {
            ushort slotCounter = 0;
            var drops = IndependentDropSystem.GenerateDrops(
                monsterCode: monsterCode,
                difficulty: 0,
                dungeonLevel: dungeonLevel,
                partyMemberCount: 1,
                chronicleDropJobGroup: -1,
                lcg: new DnfLcg(seed),
                slotCounter: ref slotCounter);

            for (int i = 0; i < drops.Count; i++)
            {
                if (drops[i].TemplateId == cardItemId && IsMonsterCard((int)drops[i].TemplateId))
                    return true;
            }
            return false;
        }

        private static bool MonsterWithoutIndependentDropCannotProduceMonsterCard()
        {
            const int MonsterLevel = 16;
            const int MonsterTypeNormal = 0;
            const int NoIndependentDropMonsterCode = -392000;
            const int DifficultyNormal = 0;
            const int DungeonLevel = MonsterLevel;
            const uint LegacyGlobalCardRegressionSeed = 3939;

            ushort slotCounter = 0;
            var generator = new DropGenerator(new DnfLcg(LegacyGlobalCardRegressionSeed));
            var (_, drops) = generator.GenerateMonsterDrops(
                monsterLevel: MonsterLevel,
                monsterType: MonsterTypeNormal,
                monsterCode: NoIndependentDropMonsterCode,
                difficulty: DifficultyNormal,
                dungeonLevel: DungeonLevel,
                partyMemberCount: 1,
                chronicleDropJobGroup: -1,
                dropPolicy: DungeonDropPolicy.Standard,
                slotCounter: ref slotCounter);

            for (int i = 0; i < drops.Count; i++)
            {
                if (IsMonsterCard((int)drops[i].TemplateId))
                    return false;
            }
            return true;
        }

        private static bool IndependentDropUsesPartyAttemptsAndCap()
        {
            const int MonsterCode = 61128;
            const int ItemId = 3241;
            const int Difficulty = 2;
            const int DungeonLevel = 65;
            var sawSingleDrop = false;
            var sawFourPlayerCap = false;

            for (uint seed = 0; seed < 128; seed++)
            {
                ushort singleSlotCounter = 0;
                var singleDrops = IndependentDropSystem.GenerateDrops(
                    MonsterCode,
                    Difficulty,
                    DungeonLevel,
                    partyMemberCount: 1,
                    chronicleDropJobGroup: -1,
                    lcg: new DnfLcg(seed),
                    slotCounter: ref singleSlotCounter);
                var singleCount = CountItem(singleDrops, ItemId);
                if (singleCount > 1)
                    return false;
                sawSingleDrop |= singleCount == 1;

                ushort partySlotCounter = 0;
                var partyDrops = IndependentDropSystem.GenerateDrops(
                    MonsterCode,
                    Difficulty,
                    DungeonLevel,
                    partyMemberCount: 4,
                    chronicleDropJobGroup: -1,
                    lcg: new DnfLcg(seed),
                    slotCounter: ref partySlotCounter);
                var partyDropCount = CountItem(partyDrops, ItemId);
                if (partyDropCount > 4)
                    return false;
                sawFourPlayerCap |= partyDropCount == 4;

                if (sawSingleDrop && sawFourPlayerCap)
                    return true;
            }

            return false;
        }

        private static int CountItem(System.Collections.Generic.List<DropInfo> drops, int itemId)
        {
            var count = 0;
            for (var i = 0; i < drops.Count; i++)
            {
                if (drops[i].TemplateId == itemId)
                    count++;
            }
            return count;
        }

        private static bool WorldDropContainsMonsterCards()
        {
            string text;
            try { text = PvfArchiveAccessor.ReadText("Etc/WorldDrop.etc"); }
            catch { return false; }

            var match = Regex.Match(text, @"\[world drop\]\s*([\s\S]*?)\s*\[/world drop\]", RegexOptions.IgnoreCase);
            if (!match.Success) return false;

            var section = Regex.Replace(match.Groups[1].Value ?? string.Empty, @"//.*$", string.Empty, RegexOptions.Multiline);
            var values = Regex.Matches(section, @"-?\d+");
            int index = 0;
            while (index + 1 < values.Count)
            {
                index += 2;
                while (index < values.Count)
                {
                    int itemId = int.Parse(values[index++].Value);
                    if (itemId == -1)
                        break;
                    if (index >= values.Count)
                        break;

                    index++;
                    if (itemId > 0 && IsMonsterCard(itemId))
                        return true;
                }
            }

            return false;
        }

        private static bool IsMonsterCard(int itemTemplateId)
        {
            var metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            var path = (metadata.PvfFilePath ?? string.Empty).Replace('\\', '/');
            return path.StartsWith("monsterCard/", StringComparison.OrdinalIgnoreCase);
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}

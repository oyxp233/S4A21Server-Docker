using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Dungeon
{
    public sealed class DropGenerator
    {
        private readonly DnfLcg _lcg;

        private const int DropDenominator = 10000;
        private const int MobItemDropRate = 1000;

        private static readonly float[] DifficultyGoldBonus = { 1.0f, 1.2f, 1.4f, 1.6f, 1.8f };

        public DropGenerator(DnfLcg lcg)
        {
            _lcg = lcg;
        }

        public (int goldAmount, List<DropInfo> drops) GenerateMonsterDrops(
            int monsterLevel, int monsterType, int monsterCode,
            int difficulty, int dungeonLevel, int partyMemberCount,
            int chronicleDropJobGroup,
            DungeonDropPolicy dropPolicy,
            ref ushort slotCounter,
            IReadOnlyList<MonsterDropTable.DropPoolEntry> dropPool = null)
        {
            dropPolicy ??= DungeonDropPolicy.Standard;
            var drops = new List<DropInfo>();
            var diffBonus = difficulty >= 0 && difficulty < DifficultyGoldBonus.Length
                ? DifficultyGoldBonus[difficulty] : 1.0f;

            var goldRate = 0;
            var type1Rate = 0;
            var type2Rate = 0;
            var type3Rate = 0;
            if (dropPolicy.Allows(DungeonMonsterDropSource.Gold)
                || dropPolicy.Allows(DungeonMonsterDropSource.GenericItems))
            {
                MonsterDropConfig.GetAllDropRates(
                    monsterLevel,
                    monsterType,
                    out goldRate,
                    out type1Rate,
                    out type2Rate,
                    out type3Rate);

                goldRate = Math.Min((int)(goldRate * diffBonus), DropDenominator);
                type1Rate = Math.Min((int)(type1Rate * diffBonus), DropDenominator);
                type2Rate = Math.Min((int)(type2Rate * diffBonus), DropDenominator);
                type3Rate = Math.Min((int)(type3Rate * diffBonus), DropDenominator);
            }

            var goldAmount = 0;
            if (dropPolicy.Allows(DungeonMonsterDropSource.Gold))
            {
                var goldBase = ExpTableProvider.GetMonsterGold(
                    monsterLevel,
                    out var variancePct);
                var goldVariance = variancePct > 0
                    ? (_lcg.Next(2 * variancePct + 1) - variancePct)
                        * goldBase / 100
                    : 0;
                var rolledGoldAmount = Math.Max(
                    1,
                    (int)((goldBase + goldVariance) * diffBonus));

                if (goldRate > _lcg.Next(DropDenominator))
                {
                    goldAmount = rolledGoldAmount;
                    slotCounter++;
                    drops.Add(DropInfo.CreateGold(slotCounter, goldAmount));
                }
            }

            if (dropPolicy.Allows(DungeonMonsterDropSource.GenericItems)
                && type1Rate > _lcg.Next(DropDenominator))
            {
                int rarity = MonsterDropConfig.RollRarity(_lcg);
                int itemId = MonsterDropConfig.ChooseStackable(_lcg, monsterLevel, rarity);
                if (itemId > 0)
                {
                    slotCounter++;
                    drops.Add(DropInfo.CreateItem(slotCounter, itemId, 1));
                }
            }

            if (dropPolicy.Allows(DungeonMonsterDropSource.GenericItems)
                && type2Rate > _lcg.Next(DropDenominator))
            {
                int rarity = MonsterDropConfig.RollRarity(_lcg);
                int itemId = MonsterDropConfig.ChooseEquipment(_lcg, monsterLevel, rarity);
                if (itemId > 0)
                {
                    slotCounter++;
                    drops.Add(DropInfo.CreateItem(slotCounter, itemId, 1));
                }
            }

            if (dropPolicy.Allows(DungeonMonsterDropSource.GenericItems)
                && type3Rate > _lcg.Next(DropDenominator))
            {
                int rarity = MonsterDropConfig.RollRarity(_lcg);
                int itemId = MonsterDropConfig.ChooseStackable(_lcg, monsterLevel, rarity);
                if (itemId > 0)
                {
                    slotCounter++;
                    drops.Add(DropInfo.CreateItem(slotCounter, itemId, 1));
                }
            }

            if ((dropPolicy.Allows(DungeonMonsterDropSource.MonsterTemplateItems)
                    || dropPolicy.Allows(DungeonMonsterDropSource.AreaMaterials))
                && dropPool != null && dropPool.Count > 0
                && MobItemDropRate > _lcg.Next(DropDenominator))
            {
                int totalWeight = 0;
                for (int i = 0; i < dropPool.Count; i++)
                    totalWeight += dropPool[i].Weight;

                if (totalWeight > 0)
                {
                    var roll = _lcg.Next(totalWeight);
                    int cum = 0;
                    for (int i = 0; i < dropPool.Count; i++)
                    {
                        cum += dropPool[i].Weight;
                        if (roll < cum)
                        {
                            slotCounter++;
                            drops.Add(DropInfo.CreateItem(slotCounter, dropPool[i].ItemId, 1));
                            break;
                        }
                    }
                }
            }

            if (dropPolicy.Allows(DungeonMonsterDropSource.Independent))
            {
                var independentDrops = IndependentDropSystem.GenerateDrops(
                    monsterCode, difficulty, dungeonLevel, partyMemberCount,
                    chronicleDropJobGroup,
                    _lcg, ref slotCounter);
                drops.AddRange(independentDrops);
            }

            if (dropPolicy.Allows(DungeonMonsterDropSource.World))
            {
                var worldDrops = WorldDropSystem.GenerateDrops(
                    monsterLevel,
                    _lcg,
                    ref slotCounter);
                drops.AddRange(worldDrops);
            }

            return (goldAmount, drops);
        }
    }
}

using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Mercenary
{
    public interface IMercenaryMonsterDropSource
    {
        IReadOnlyList<MonsterDropTable.DropPoolEntry> GetDropPool(int monsterCode);
    }

    public interface IMercenaryRandomSource
    {
        int Next(int exclusiveMax);
        long NextLong(long exclusiveMax);
    }

    public sealed class ServerMercenaryRandomSource : IMercenaryRandomSource
    {
        public static readonly ServerMercenaryRandomSource Instance = new ServerMercenaryRandomSource();

        private ServerMercenaryRandomSource()
        {
        }

        public int Next(int exclusiveMax)
            => exclusiveMax <= 1 ? 0 : ServerRandom.Next(exclusiveMax);

        public long NextLong(long exclusiveMax)
        {
            if (exclusiveMax <= 1)
                return 0;
            if (exclusiveMax <= int.MaxValue)
                return ServerRandom.Next((int)exclusiveMax);

            var value = ((long)ServerRandom.Next() << 31) | (uint)ServerRandom.Next();
            return value % exclusiveMax;
        }
    }

    public sealed class PvfMercenaryMonsterDropSource : IMercenaryMonsterDropSource
    {
        public static readonly PvfMercenaryMonsterDropSource Instance = new PvfMercenaryMonsterDropSource();

        private PvfMercenaryMonsterDropSource()
        {
        }

        public IReadOnlyList<MonsterDropTable.DropPoolEntry> GetDropPool(int monsterCode)
            => MonsterDropTable.GetDropPool(monsterCode);
    }

    public sealed class MercenaryRewardCalculator
    {
        private readonly IMercenaryMonsterDropSource _monsterDrops;
        private readonly IMercenaryRandomSource _random;

        public MercenaryRewardCalculator(
            IMercenaryMonsterDropSource monsterDrops = null,
            IMercenaryRandomSource random = null)
        {
            _monsterDrops = monsterDrops ?? PvfMercenaryMonsterDropSource.Instance;
            _random = random ?? ServerMercenaryRandomSource.Instance;
        }

        public MercenaryReward Calculate(MercenaryAssignment assignment, MercenaryConfig config, int nowUnixSeconds)
        {
            if (assignment == null)
                throw new ArgumentNullException(nameof(assignment));
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            var levelReward = config.GetLevelReward(assignment.CharacterLevel)
                ?? throw new InvalidOperationException("mercenary assignment level has no reward table");
            var period = config.GetPeriod(assignment.PeriodIndex)
                ?? throw new InvalidOperationException("mercenary assignment period is invalid");
            var area = config.GetArea(assignment.AreaIndex)
                ?? throw new InvalidOperationException("mercenary assignment area is invalid");

            var elapsedSeconds = Math.Max(0L, (long)nowUnixSeconds - assignment.StartTime);
            var completedHours = (int)Math.Min(period.Hours, elapsedSeconds / config.BaseTimeUnitSeconds);
            var earlyReturn = completedHours < period.Hours;
            var avatarMultiplier = config.GetAvatarMultiplier(assignment.AvatarBonusTier);
            var criticalMultiplier = SelectCritical(config.CriticalOptions);

            var preCriticalTotal = FloorToInt(
                levelReward.BaseGoldPerHour
                * period.BonusMultiplier
                * completedHours
                * avatarMultiplier
                + 0.01);
            var totalGold = FloorToInt(preCriticalTotal * criticalMultiplier + 0.01);
            var baseGold = FloorToInt(
                levelReward.BaseGoldPerHour
                * period.BonusMultiplier
                * completedHours
                * criticalMultiplier
                + 0.01);

            var reward = new MercenaryReward
            {
                BaseGold = baseGold,
                BonusGold = Math.Max(0, totalGold - baseGold),
                CompletedHours = completedHours,
                IsEarlyReturn = earlyReturn,
                CriticalMultiplier = criticalMultiplier,
            };

            var dropRatePerSlot = FloorToInt(
                levelReward.ItemProbabilityPerHour
                * period.BonusMultiplier
                * completedHours);
            var lootSlotCount = Math.Max(1, FloorToInt(avatarMultiplier + 0.01));
            for (var slot = 0; slot < lootSlotCount; slot++)
            {
                if (dropRatePerSlot <= 0
                    || _random.Next(10000) >= Math.Min(10000, dropRatePerSlot))
                {
                    continue;
                }

                var group = SelectWeighted(area.RewardGroups, entry => entry.Weight);
                if (group == null)
                    continue;

                var itemId = ResolveItem(group);
                if (itemId <= 0)
                    continue;

                AddOrIncrementItem(reward.Items, itemId);
                if (reward.Items.Count == 1)
                {
                    reward.MailMessageKey = string.IsNullOrWhiteSpace(group.MessageKey)
                        ? reward.MailMessageKey
                        : group.MessageKey;
                }
            }

            if (reward.Items.Count > 0)
            {
                reward.ItemTemplateId = reward.Items[0].ItemTemplateId;
                reward.ItemCount = reward.Items[0].ItemCount;
            }
            return reward;
        }

        private static void AddOrIncrementItem(ICollection<MercenaryRewardItem> items, int itemId)
        {
            if (IsStackableReward(itemId))
            {
                foreach (var item in items)
                {
                    if (item.ItemTemplateId != itemId)
                        continue;
                    item.ItemCount++;
                    return;
                }
            }

            items.Add(new MercenaryRewardItem
            {
                ItemTemplateId = itemId,
                ItemCount = 1,
            });
        }

        private static bool IsStackableReward(int itemId)
        {
            return ItemMetadataResolver.TryResolveItemKind(itemId, out var itemKind)
                && InventoryStackRuleService.IsStackable(ItemCore.Create(itemKind, itemId));
        }

        private int ResolveItem(MercenaryRewardGroup group)
        {
            if (group.Monsters.Count > 0)
            {
                var monster = SelectWeighted(group.Monsters, entry => entry.Weight);
                if (monster == null)
                    return 0;

                var drop = SelectWeighted(
                    _monsterDrops.GetDropPool(monster.Value),
                    entry => entry.Weight);
                return drop.ItemId;
            }

            var item = SelectWeighted(group.Items, entry => entry.Weight);
            return item?.Value ?? 0;
        }

        private double SelectCritical(IReadOnlyList<MercenaryCriticalOption> options)
        {
            var selected = SelectWeighted(options, entry => entry.Weight);
            return selected?.Multiplier ?? 1.0;
        }

        private T SelectWeighted<T>(
            IReadOnlyList<T> entries,
            Func<T, int> getWeight)
            where T : class
        {
            if (entries == null || entries.Count == 0)
                return null;

            var total = 0L;
            for (var i = 0; i < entries.Count; i++)
                total += Math.Max(0, getWeight(entries[i]));
            if (total <= 0)
                return null;

            var roll = _random.NextLong(total);
            var cumulative = 0L;
            for (var i = 0; i < entries.Count; i++)
            {
                cumulative += Math.Max(0, getWeight(entries[i]));
                if (roll < cumulative)
                    return entries[i];
            }
            return entries[entries.Count - 1];
        }

        private MonsterDropTable.DropPoolEntry SelectWeighted(
            IReadOnlyList<MonsterDropTable.DropPoolEntry> entries,
            Func<MonsterDropTable.DropPoolEntry, int> getWeight)
        {
            if (entries == null || entries.Count == 0)
                return default;

            var total = 0L;
            for (var i = 0; i < entries.Count; i++)
                total += Math.Max(0, getWeight(entries[i]));
            if (total <= 0)
                return default;

            var roll = _random.NextLong(total);
            var cumulative = 0L;
            for (var i = 0; i < entries.Count; i++)
            {
                cumulative += Math.Max(0, getWeight(entries[i]));
                if (roll < cumulative)
                    return entries[i];
            }
            return entries[entries.Count - 1];
        }

        private static int FloorToInt(double value)
        {
            if (double.IsNaN(value) || value <= 0)
                return 0;
            if (value >= int.MaxValue)
                return int.MaxValue;
            return (int)Math.Floor(value);
        }

    }
}

using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Linq;

namespace DfoServer.Game.Guilds
{
    internal sealed class GuildFacilityService
    {
        private readonly IGameDatabase _database;
        private readonly int _goldPerPoint;
        private readonly int _cargoSlotLimit;
        private readonly GuildAgitStep[] _agit;
        private readonly GuildCargoStep[] _cargo;

        internal GuildFacilityService(IGameDatabase database, int goldPerPoint, int cargoSlotLimit,
            GuildAgitStep[] agit, GuildCargoStep[] cargo)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            if (goldPerPoint <= 0) throw new ArgumentOutOfRangeException(nameof(goldPerPoint));
            if (cargoSlotLimit <= 0) throw new ArgumentOutOfRangeException(nameof(cargoSlotLimit));
            _goldPerPoint = goldPerPoint;
            _cargoSlotLimit = cargoSlotLimit;
            _agit = agit ?? throw new ArgumentNullException(nameof(agit));
            _cargo = cargo ?? throw new ArgumentNullException(nameof(cargo));
        }

        internal static GuildFacilityService FromPvf(IGameDatabase database)
            => new(database, GuildFacilityRules.ReadDonateGoldPerPoint(), GuildFacilityRules.ReadCargoSlotLimit(),
                GuildFacilityRules.ReadAgitSteps().ToArray(), GuildFacilityRules.ReadCargoSteps().ToArray());

        internal int GoldPerPoint => _goldPerPoint;

        internal GuildFacilityResult Donate(InventoryLease lease, int gold)
        {
            if (gold <= 0 || gold % _goldPerPoint != 0) return new(GuildResult.InvalidAmount);
            int points = gold / _goldPerPoint;
            return Commit(lease, (c, t, guild, facility, held) =>
            {
                if (held < gold) return new(GuildResult.InsufficientGold, held, facility.Contribution);
                int mine = GuildRepository.ReadMemberContribution(c, t, lease.CharacterId);
                if (!lease.Inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 0, held - gold))
                    return null;
                if (!GuildRepository.TryAddContribution(c, t, guild.Id, lease.CharacterId, points))
                    return null;
                return new(GuildResult.Success, held - gold, facility.Contribution + points, MemberContribution: mine + points);
            });
        }

        internal GuildFacilityResult CreateOrUpgradeAgit(InventoryLease lease)
            => Commit(lease, (c, t, guild, facility, held) =>
            {
                if (guild.LeaderId != lease.CharacterId) return new(GuildResult.NotLeader);
                var next = _agit.FirstOrDefault(step => step.Level == facility.AgitLevel + 1);
                if (next.Level == 0) return new(GuildResult.InvalidAmount, Level: facility.AgitLevel);
                if (facility.Contribution < next.Contribution)
                    return new(GuildResult.InsufficientContribution, held, facility.Contribution, facility.AgitLevel);
                if (held < next.Gold) return new(GuildResult.InsufficientGold, held, facility.Contribution, facility.AgitLevel);
                if (!lease.Inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 0, held - next.Gold))
                    return null;
                if (!GuildRepository.TryAdvanceAgit(c, t, guild.Id, lease.CharacterId, facility.AgitLevel, next.Level, next.Contribution))
                    return null;
                return new(GuildResult.Success, held - next.Gold, facility.Contribution, next.Level);
            });

        internal GuildFacilityResult UpgradeCargo(InventoryLease lease)
            => Commit(lease, (c, t, guild, facility, held) =>
            {
                if (guild.LeaderId != lease.CharacterId) return new(GuildResult.NotLeader);
                var next = _cargo.Where(step => step.Slots <= _cargoSlotLimit).OrderBy(step => step.Slots)
                    .FirstOrDefault(step => step.Slots > facility.CargoSlots);
                if (next.Slots == 0) return new(GuildResult.InvalidAmount, Slots: facility.CargoSlots);
                if (facility.Contribution < next.Contribution)
                    return new(GuildResult.InsufficientContribution, held, facility.Contribution, Slots: facility.CargoSlots);
                if (held < next.Gold) return new(GuildResult.InsufficientGold, held, facility.Contribution, Slots: facility.CargoSlots);
                if (!lease.Inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 0, held - next.Gold))
                    return null;
                if (!GuildRepository.TryAdvanceCargo(c, t, guild.Id, lease.CharacterId, facility.CargoSlots, next.Slots, next.Contribution, _cargoSlotLimit))
                    return null;
                return new(GuildResult.Success, held - next.Gold, facility.Contribution, Slots: next.Slots);
            });

        private GuildFacilityResult Commit(InventoryLease lease,
            Func<SqliteConnection, SqliteTransaction, GuildRecord, (int Contribution, int AgitLevel, int CargoSlots), int, GuildFacilityResult> apply)
        {
            if (lease == null || !InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId))
                return new(GuildResult.StaleSession);
            if (!string.Equals(lease.Inventory.Database?.ConnectionString, _database.ConnectionString, StringComparison.Ordinal))
                return new(GuildResult.PersistenceFailed);
            var result = new GuildFacilityResult(GuildResult.PersistenceFailed);
            bool committed = OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "guild-facility", (c, t) =>
            {
                if (!InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId))
                    result = new(GuildResult.StaleSession);
                else
                {
                    var guild = GuildRepository.GetForMember(c, t, lease.CharacterId);
                    if (guild == null) result = new(GuildResult.NotFound);
                    else
                    {
                        var applied = apply(c, t, guild, GuildRepository.ReadFacility(c, t, guild.Id), CurrentGold(lease));
                        if (applied == null) return false;
                        result = applied;
                    }
                }
                return true;
            });
            return committed ? result : new(GuildResult.PersistenceFailed);
        }

        private static int CurrentGold(InventoryLease lease)
            => lease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
    }
}

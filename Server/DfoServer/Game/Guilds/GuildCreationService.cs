using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Guilds
{
    internal sealed class GuildCreationService
    {
        private readonly GuildRepository _repository;
        private readonly int _creationCost;

        internal GuildCreationService(GuildRepository repository, int creationCost)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            if (creationCost <= 0) throw new ArgumentOutOfRangeException(nameof(creationCost));
            _creationCost = creationCost;
        }

        internal static int ReadCreationCost()
        {
            string text = PvfArchiveAccessor.ReadText("etc/(r)guild.etc");
            var match = Regex.Match(text, @"\[guild create cost\]\s+(\d+)", RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int cost) || cost <= 0)
                throw new InvalidOperationException("PVF guild creation cost is missing or invalid.");
            return cost;
        }

        internal GuildCreateResult Create(InventoryLease lease, byte[] rawName, string promotion)
        {
            if (lease == null || !InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId))
                return new(GuildResult.StaleSession);
            if (!GuildCreationRules.TryValidateName(rawName, out string name)) return new(GuildResult.InvalidName);
            if (!GuildCreationRules.IsValidPromotion(promotion)) return new(GuildResult.InvalidPromotion);
            if (!string.Equals(lease.Inventory.Database?.ConnectionString, _repository.Database.ConnectionString,
                    StringComparison.Ordinal))
                return new(GuildResult.PersistenceFailed);

            var result = new GuildCreateResult(GuildResult.PersistenceFailed);
            bool committed = OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "guild-create", (c, t) =>
            {
                if (!InventoryContext.IsCurrentLease(lease, lease.SessionId, lease.CharacterId))
                    result = new(GuildResult.StaleSession);
                else if (!GuildRepository.IsActiveCharacter(c, t, lease.CharacterId))
                    result = new(GuildResult.MissingCharacter);
                else if (GuildRepository.GetForMember(c, t, lease.CharacterId) != null)
                    result = new(GuildResult.AlreadyMember);
                else if (GuildRepository.NameExists(c, t, name))
                    result = new(GuildResult.DuplicateName);
                else
                {
                    int gold = lease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
                    if (gold < _creationCost) result = new(GuildResult.InsufficientGold, GoldAfter: gold);
                    else
                    {
                        if (!lease.Inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 0, gold - _creationCost))
                            return false;
                        var guild = GuildRepository.Insert(c, t, lease.CharacterId, name, promotion);
                        result = new(GuildResult.Success, guild, gold - _creationCost);
                    }
                }
                // Business rejection changes no guild/currency; persistence failures are
                // handled by the existing coordinator, including online inventory reload.
                return true;
            });
            return committed ? result : new(GuildResult.PersistenceFailed);
        }
    }
}

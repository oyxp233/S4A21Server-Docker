using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DungeonEntryCostService
    {
        private readonly IGameDatabase _database;

        internal DungeonEntryCostService()
            : this(GameDatabase.CreateDefault())
        {
        }

        internal DungeonEntryCostService(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal EntryCostResult TryConsumeAbyssPartyTicket(
            InventoryService inventory,
            WorldMapArea area, int dungeonMinLevel)
        {
            var result = new EntryCostResult();
            if (inventory == null || inventory.CharacterId <= 0)
                return result.Fail("invalid character");

            if (area == null)
                return result.Fail("worldmap area missing");

            if (!area.HellDungeon)
                return result.Fail("area is not hell dungeon");

            if (!CheckHellQuestRequirement(
                    inventory.CharacterId,
                    area,
                    out var missingQuestId))
                return result.Fail($"hell quest not cleared quest={missingQuestId}");

            try
            {
                foreach (var ticket in area.HellFreePassItems)
                {
                    if (ticket.ItemId <= 0 || ticket.Count <= 0)
                        continue;

                    if (inventory.CountMainItem(ticket.ItemId) < ticket.Count)
                        continue;

                    if (inventory.TryConsumeMainItem(ticket.ItemId, ticket.Count, out var consumed) && consumed.Success)
                    {
                        result.Success = true;
                        result.IsFreePass = true;
                        result.ConsumedItems.Add(new ItemConsumeUpdate
                        {
                            ItemId = ticket.ItemId,
                            Count = ticket.Count,
                            SlotIndex = consumed.SlotIndex,
                            RemainingCount = consumed.RemainingCount,
                        });
                        return result;
                    }
                }

                var normalNeedCount = WorldMap.GetHellNormalTicketNeedCount(dungeonMinLevel);
                if (normalNeedCount <= 0)
                    return result.Fail($"dungeon min level too low minLevel={dungeonMinLevel}");

                var normalTicketItemIds = area.HellNormalTicketItemIds;
                if (normalTicketItemIds.Count == 0)
                    return result.Fail("normal ticket item missing");

                var selectedNormalTicketItemId = 0;
                foreach (var itemId in normalTicketItemIds)
                {
                    if (itemId > 0 && inventory.CountMainItem(itemId) >= normalNeedCount)
                    {
                        selectedNormalTicketItemId = itemId;
                        break;
                    }
                }

                if (selectedNormalTicketItemId <= 0)
                    return result.Fail($"ticket missing normalNeed={normalNeedCount}");

                if (inventory.TryConsumeMainItem(selectedNormalTicketItemId, normalNeedCount, out var normalConsumed)
                    && normalConsumed.Success)
                {
                    result.Success = true;
                    result.IsFreePass = false;
                    result.ConsumedItems.Add(new ItemConsumeUpdate
                    {
                        ItemId = selectedNormalTicketItemId,
                        Count = normalNeedCount,
                        SlotIndex = normalConsumed.SlotIndex,
                        RemainingCount = normalConsumed.RemainingCount,
                    });
                }
                else
                {
                    return result.Fail($"ticket delete failed item={selectedNormalTicketItemId} normalNeed={normalNeedCount}");
                }

                return result;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonEntryCost] TryConsumeAbyssPartyTicket ERROR: {ex.Message}");
                return result.Fail(ex.Message);
            }
        }

        private bool CheckHellQuestRequirement(
            int characterId,
            WorldMapArea area,
            out int missingQuestId)
        {
            missingQuestId = 0;
            if (area.HellQuestIds.Count == 0)
                return true;

            var connStr = _database.ConnectionString;
            foreach (var questId in area.HellQuestIds)
            {
                if (questId <= 0)
                    continue;

                if (questId > ushort.MaxValue || !new QuestRepository(connStr).IsQuestCleared(characterId, (ushort)questId))
                {
                    missingQuestId = questId;
                    return false;
                }
            }

            return true;
        }
    }

    internal sealed class EntryCostResult
    {
        public bool Success;
        public bool IsFreePass;
        public string FailReason;
        public List<ItemConsumeUpdate> ConsumedItems { get; } = new List<ItemConsumeUpdate>();

        internal EntryCostResult Fail(string reason)
        {
            FailReason = reason;
            return this;
        }
    }

    internal sealed class ItemConsumeUpdate
    {
        public int ItemId { get; set; }
        public int Count { get; set; }
        public short SlotIndex { get; set; }
        public int RemainingCount { get; set; }
    }
}

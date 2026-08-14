using System;
using System.Security.Cryptography;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryAuditEvent
    {
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public Guid SessionId { get; set; }

        public string OwnerScope { get; set; } = "character";

        public int OwnerId { get; set; }

        public int CharacterId { get; set; }

        public int AccountId { get; set; }

        public string ActionName { get; set; } = string.Empty;

        public InventoryListType? ListType { get; set; }

        public short? SlotIndex { get; set; }

        public int ItemId { get; set; }

        public int ItemKind { get; set; }

        public int ValueBefore { get; set; }

        public int ValueAfter { get; set; }

        public int CountBefore { get; set; }

        public int CountAfter { get; set; }

        public int CountDelta { get; set; }

        public string BeforeCoreHash { get; set; }

        public string AfterCoreHash { get; set; }

        public string PayloadJson { get; set; } = "{}";

        public static InventoryAuditEvent FromSlotChange(
            Guid sessionId,
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            ItemCore before,
            ItemCore after)
        {
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));

            before = Normalize(before);
            after = Normalize(after);
            if (HasSameCore(before, after))
                return null;

            var subject = after ?? before;
            return new InventoryAuditEvent
            {
                SessionId = sessionId,
                OwnerScope = ResolveOwnerScope(listType, slotIndex),
                OwnerId = ResolveOwnerId(inventory, listType, slotIndex),
                CharacterId = inventory.CharacterId,
                AccountId = inventory.AccountId,
                ActionName = ResolveSlotActionName(before, after),
                ListType = listType,
                SlotIndex = slotIndex,
                ItemId = subject?.ItemId ?? 0,
                ItemKind = subject?.ItemKind ?? 0,
                ValueBefore = before?.Value ?? 0,
                ValueAfter = after?.Value ?? 0,
                CountBefore = ResolveCount(before),
                CountAfter = ResolveCount(after),
                CountDelta = ResolveCount(after) - ResolveCount(before),
                BeforeCoreHash = ComputeCoreHash(before),
                AfterCoreHash = ComputeCoreHash(after),
            };
        }

        public static InventoryAuditEvent FromVirtualCountChange(
            Guid sessionId,
            InventoryService inventory,
            short slotIndex,
            int itemId,
            int beforeCount,
            int afterCount)
        {
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));

            beforeCount = Math.Max(0, beforeCount);
            afterCount = Math.Max(0, afterCount);
            if (beforeCount == afterCount)
                return null;

            var beforeCore = CreateVirtualCore(slotIndex, itemId, beforeCount);
            var afterCore = CreateVirtualCore(slotIndex, itemId, afterCount);
            return new InventoryAuditEvent
            {
                SessionId = sessionId,
                OwnerScope = ResolveOwnerScope(InventoryListType.Main, slotIndex),
                OwnerId = ResolveOwnerId(inventory, InventoryListType.Main, slotIndex),
                CharacterId = inventory.CharacterId,
                AccountId = inventory.AccountId,
                ActionName = "inventory_virtual_count_update",
                ListType = InventoryListType.Main,
                SlotIndex = slotIndex,
                ItemId = itemId,
                ItemKind = ItemCore.KindSpecialMaterial,
                ValueBefore = beforeCount,
                ValueAfter = afterCount,
                CountBefore = beforeCount,
                CountAfter = afterCount,
                CountDelta = afterCount - beforeCount,
                BeforeCoreHash = ComputeCoreHash(beforeCore),
                AfterCoreHash = ComputeCoreHash(afterCore),
            };
        }

        private static ItemCore Normalize(ItemCore core)
        {
            return core != null && !core.IsEmpty ? core : null;
        }

        private static string ResolveSlotActionName(ItemCore before, ItemCore after)
        {
            if (before == null)
                return "inventory_slot_create";
            if (after == null)
                return "inventory_slot_delete";

            return "inventory_slot_update";
        }

        private static string ResolveOwnerScope(InventoryListType listType, short slotIndex)
        {
            if (listType == InventoryListType.AccountCargo)
                return "account";
            if (listType == InventoryListType.Main
                && slotIndex >= InventoryService.MainVirtualCubeSlotStart
                && slotIndex <= InventoryService.MainVirtualCubeSlotEnd)
                return "account";

            return "character";
        }

        private static int ResolveOwnerId(InventoryService inventory, InventoryListType listType, short slotIndex)
        {
            return ResolveOwnerScope(listType, slotIndex) == "account"
                ? inventory.AccountId
                : inventory.CharacterId;
        }

        private static int ResolveCount(ItemCore core)
        {
            if (core == null || core.IsEmpty)
                return 0;

            return InventoryStackRuleService.IsStackable(core)
                ? Math.Max(0, core.Count)
                : 1;
        }

        private static bool HasSameCore(ItemCore left, ItemCore right)
        {
            if (left == null || right == null)
                return left == null && right == null;

            var leftBytes = left.ToBytes();
            var rightBytes = right.ToBytes();
            if (leftBytes.Length != rightBytes.Length)
                return false;

            for (var index = 0; index < leftBytes.Length; index++)
            {
                if (leftBytes[index] != rightBytes[index])
                    return false;
            }

            return true;
        }

        private static string ComputeCoreHash(ItemCore core)
        {
            core = Normalize(core);
            if (core == null)
                return null;

            return Convert.ToHexString(SHA256.HashData(core.ToBytes()));
        }

        private static ItemCore CreateVirtualCore(short slotIndex, int itemId, int count)
        {
            return new ItemCore
            {
                ItemKind = ItemCore.KindSpecialMaterial,
                ItemId = itemId > 0 ? itemId : slotIndex,
                Count = Math.Max(0, count),
            };
        }
    }
}

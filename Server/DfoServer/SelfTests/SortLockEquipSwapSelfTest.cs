using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.SelfTests
{
    public static class SortLockEquipSwapSelfTest
    {
        private const short MainSlotX = 9;
        private const short WeaponSlot = (short)EquipmentType.Weapon;
        private const byte ActorJob = 0;

        public static int Run()
        {
            Console.WriteLine("=== SORT_LOCK_EQUIP_SWAP selftest ===");
            var failures = 0;

            var fixtures = ResolveEquipmentFixtures(ref failures);
            if (fixtures != null)
            {
                VerifyWearSwapKeepsLockOutOfEquipment(fixtures, ref failures);
                VerifyClientAutoUnlockToggleRelocksSlot(fixtures, ref failures);
                VerifyWearBackCycleKeepsLock(fixtures, ref failures);
                VerifyLockedItemIntoEmptyEquipmentSlotClearsLock(fixtures, ref failures);
                VerifyToggleOnEmptySlotFails(ref failures);
                VerifyLegacyEquipmentLockDirtIsClearedOnUnequip(fixtures, ref failures);
            }

            VerifySameListKindMatchSwapKeepsLock(ref failures);
            VerifyCrossListMoveClearsLock(fixtures, ref failures);
            VerifyQuickSlotMoveClearsLock(fixtures, ref failures);

            Console.WriteLine(failures == 0
                ? "SORT_LOCK_EQUIP_SWAP selftest passed"
                : $"SORT_LOCK_EQUIP_SWAP selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        // 锁定 Main X 的装备 A 右键穿戴，与身上装备 B 交换：锁标志不随物品进穿戴栏，
        // 也不做锁转移——换入 X 的 B 以 flag=0 落地，等客户端 auto-unlock toggle 重新加锁。
        private static void VerifyWearSwapKeepsLockOutOfEquipment(EquipmentFixtures fixtures, ref int failures)
        {
            var inventory = CreateInventory();
            var a = CreateEquipment(fixtures.WeaponItemIdA, sortLock: 1);
            var b = CreateEquipment(fixtures.WeaponItemIdB, sortLock: 0);
            inventory.SetItem(InventoryListType.Main, MainSlotX, a);
            inventory.SetItem(InventoryListType.Equipment, WeaponSlot, b);

            var moved = TryMove(inventory, InventoryListType.Main, MainSlotX, InventoryListType.Equipment, WeaponSlot, out var result);

            var equipped = inventory.GetItem(InventoryListType.Equipment, WeaponSlot);
            var fallen = inventory.GetItem(InventoryListType.Main, MainSlotX);
            Check(
                "wear swap: A equips with lock cleared, B falls to X unlocked (no server-side transfer)",
                moved
                && result.Success
                && equipped != null
                && equipped.ItemId == fixtures.WeaponItemIdA
                && equipped.SortLockFlag == 0
                && fallen != null
                && fallen.ItemId == fixtures.WeaponItemIdB
                && fallen.SortLockFlag == 0,
                ref failures);
        }

        // 客户端行为：右键穿戴锁定格的物品时客户端自行解锁该格并主动发 toggle，
        // 服务端盲翻转应答是“重新加锁”通道——换入物品 flag=0 时被 toggle 翻成 1。
        private static void VerifyClientAutoUnlockToggleRelocksSlot(EquipmentFixtures fixtures, ref int failures)
        {
            var inventory = CreateInventory();
            var a = CreateEquipment(fixtures.WeaponItemIdA, sortLock: 1);
            var b = CreateEquipment(fixtures.WeaponItemIdB, sortLock: 0);
            inventory.SetItem(InventoryListType.Main, MainSlotX, a);
            inventory.SetItem(InventoryListType.Equipment, WeaponSlot, b);
            TryMove(inventory, InventoryListType.Main, MainSlotX, InventoryListType.Equipment, WeaponSlot, out _);

            var toggled = InventoryLockService.TryToggleSortItemLock(
                inventory,
                InventoryListType.Main,
                MainSlotX,
                out var entry);

            var fallen = inventory.GetItem(InventoryListType.Main, MainSlotX);
            Check(
                "client auto-unlock toggle: slot X re-locks onto the fallen item",
                toggled
                && entry != null
                && entry.State == 1
                && fallen != null
                && fallen.ItemId == fixtures.WeaponItemIdB
                && fallen.SortLockFlag == 1,
                ref failures);
        }

        // 完整往返：穿戴（toggle 重新加锁）→ 穿回 → 再 toggle，锁始终留在 X 格。
        private static void VerifyWearBackCycleKeepsLock(EquipmentFixtures fixtures, ref int failures)
        {
            var inventory = CreateInventory();
            var a = CreateEquipment(fixtures.WeaponItemIdA, sortLock: 1);
            var b = CreateEquipment(fixtures.WeaponItemIdB, sortLock: 0);
            inventory.SetItem(InventoryListType.Main, MainSlotX, a);
            inventory.SetItem(InventoryListType.Equipment, WeaponSlot, b);

            TryMove(inventory, InventoryListType.Main, MainSlotX, InventoryListType.Equipment, WeaponSlot, out _);
            Toggle(inventory, InventoryListType.Main, MainSlotX);
            var woreBack = TryMove(inventory, InventoryListType.Main, MainSlotX, InventoryListType.Equipment, WeaponSlot, out var moveBack);

            var equipped = inventory.GetItem(InventoryListType.Equipment, WeaponSlot);
            var backAtX = inventory.GetItem(InventoryListType.Main, MainSlotX);
            var midCycleOk = woreBack
                && moveBack.Success
                && equipped != null
                && equipped.ItemId == fixtures.WeaponItemIdB
                && equipped.SortLockFlag == 0
                && backAtX != null
                && backAtX.ItemId == fixtures.WeaponItemIdA
                && backAtX.SortLockFlag == 0;

            var toggledAgain = Toggle(inventory, InventoryListType.Main, MainSlotX);
            backAtX = inventory.GetItem(InventoryListType.Main, MainSlotX);
            Check(
                "full wear/wear-back cycle: lock stays on slot X via client auto-unlock toggles",
                midCycleOk
                && toggledAgain
                && backAtX != null
                && backAtX.ItemId == fixtures.WeaponItemIdA
                && backAtX.SortLockFlag == 1,
                ref failures);
        }

        // 锁定物品移到空的穿戴栏槽：装备清锁（穿戴栏不携带位置锁）。
        private static void VerifyLockedItemIntoEmptyEquipmentSlotClearsLock(EquipmentFixtures fixtures, ref int failures)
        {
            var inventory = CreateInventory();
            var coatSlot = (short)EquipmentType.Coat;
            var coat = CreateEquipment(fixtures.CoatItemId, sortLock: 1);
            inventory.SetItem(InventoryListType.Main, 10, coat);

            var moved = TryMove(inventory, InventoryListType.Main, 10, InventoryListType.Equipment, coatSlot, out var result);

            var equipped = inventory.GetItem(InventoryListType.Equipment, coatSlot);
            Check(
                "locked item into empty equipment slot: lock cleared on equip",
                moved
                && result.Success
                && equipped != null
                && equipped.ItemId == fixtures.CoatItemId
                && equipped.SortLockFlag == 0
                && inventory.GetItem(InventoryListType.Main, 10) == null,
                ref failures);
        }

        // 空槽没有物品，客户端 auto-unlock toggle 无处落锁，必须失败。
        private static void VerifyToggleOnEmptySlotFails(ref int failures)
        {
            var inventory = CreateInventory();

            var toggled = InventoryLockService.TryToggleSortItemLock(
                inventory,
                InventoryListType.Main,
                10,
                out var entry);

            Check(
                "client auto-unlock toggle on empty slot: rejected",
                !toggled && entry == null,
                ref failures);
        }

        // 历史遗留：老 bug 留在穿戴栏物品上的锁，在卸下移到空格时清除，不跟物品回背包。
        private static void VerifyLegacyEquipmentLockDirtIsClearedOnUnequip(EquipmentFixtures fixtures, ref int failures)
        {
            var inventory = CreateInventory();
            var legacy = CreateEquipment(fixtures.CoatItemId, sortLock: 1);
            inventory.SetItem(InventoryListType.Equipment, (short)EquipmentType.Coat, legacy);

            var moved = TryMove(
                inventory,
                InventoryListType.Equipment,
                (short)EquipmentType.Coat,
                InventoryListType.Main,
                11,
                out var result);

            var unequipped = inventory.GetItem(InventoryListType.Main, 11);
            Check(
                "legacy lock dirt on equipment item: lock dropped when moving to empty bag slot",
                moved
                && result.Success
                && unequipped != null
                && unequipped.ItemId == fixtures.CoatItemId
                && unequipped.SortLockFlag == 0
                && inventory.GetItem(InventoryListType.Equipment, (short)EquipmentType.Coat) == null,
                ref failures);
        }

        // 回归：非穿戴栏的移动行为不变——同 Main 列表内 kind 匹配交换时锁跟物品保留。
        private static void VerifySameListKindMatchSwapKeepsLock(ref int failures)
        {
            var inventory = CreateInventory();
            var p = CreateEquipment(100010001, sortLock: 1);
            var q = CreateEquipment(100010002, sortLock: 0);
            inventory.SetItem(InventoryListType.Main, 20, p);
            inventory.SetItem(InventoryListType.Main, 21, q);

            var moved = TryMove(inventory, InventoryListType.Main, 20, InventoryListType.Main, 21, out var result);

            var swappedP = inventory.GetItem(InventoryListType.Main, 21);
            var swappedQ = inventory.GetItem(InventoryListType.Main, 20);
            Check(
                "same-list kind-match swap: sort lock still follows the item",
                moved
                && result.Success
                && swappedP != null
                && swappedP.ItemId == 100010001
                && swappedP.SortLockFlag == 1
                && swappedQ != null
                && swappedQ.ItemId == 100010002
                && swappedQ.SortLockFlag == 0,
                ref failures);
        }

        // 回归：跨列表（Main→PersonalCargo）移动时锁清除。
        private static void VerifyCrossListMoveClearsLock(EquipmentFixtures fixtures, ref int failures)
        {
            if (fixtures == null)
                return;

            var inventory = CreateInventory();
            var r = CreateEquipment(fixtures.CoatItemId, sortLock: 1);
            inventory.SetItem(InventoryListType.Main, 22, r);

            var moved = TryMove(inventory, InventoryListType.Main, 22, InventoryListType.PersonalCargo, 0, out var result);

            var stored = inventory.GetItem(InventoryListType.PersonalCargo, 0);
            Check(
                "cross-list move to personal cargo: sort lock cleared",
                moved
                && result.Success
                && stored != null
                && stored.ItemId == fixtures.CoatItemId
                && stored.SortLockFlag == 0
                && inventory.GetItem(InventoryListType.Main, 22) == null,
                ref failures);
        }

        // 回归：快捷栏移动锁清除（原有行为保持）。
        private static void VerifyQuickSlotMoveClearsLock(EquipmentFixtures fixtures, ref int failures)
        {
            if (fixtures == null)
                return;

            var inventory = CreateInventory();
            var s = CreateEquipment(fixtures.WeaponItemIdA, sortLock: 1);
            inventory.SetItem(InventoryListType.Main, 23, s);

            var moved = TryMove(inventory, InventoryListType.Main, 23, InventoryListType.Main, 3, out var result);

            var quick = inventory.GetItem(InventoryListType.Main, 3);
            Check(
                "move into main quick slot: sort lock cleared",
                moved
                && result.Success
                && quick != null
                && quick.ItemId == fixtures.WeaponItemIdA
                && quick.SortLockFlag == 0,
                ref failures);
        }

        private static bool Toggle(InventoryService inventory, InventoryListType listType, short slotIndex)
            => InventoryLockService.TryToggleSortItemLock(inventory, listType, slotIndex, out _);

        private sealed class EquipmentFixtures
        {
            public int WeaponItemIdA;
            public int WeaponItemIdB;
            public int CoatItemId;
        }

        private static EquipmentFixtures ResolveEquipmentFixtures(ref int failures)
        {
            var weapons = new List<int>();
            var coats = new List<int>();
            for (byte job = 0; job <= 13; job++)
            {
                var entries = InitialCharacterEquipment.Get(job);
                if (entries == null)
                    continue;

                foreach (var (slot, itemId) in entries)
                {
                    if (slot == (short)EquipmentType.Weapon && !weapons.Contains(itemId))
                        weapons.Add(itemId);
                    if (slot == (short)EquipmentType.Coat && !coats.Contains(itemId))
                        coats.Add(itemId);
                }
            }

            var ok = weapons.Count >= 2 && coats.Count >= 1;
            Check(
                $"PVF initial equipment fixtures: weapons={weapons.Count} coats={coats.Count}",
                ok,
                ref failures);
            if (!ok)
                return null;

            return new EquipmentFixtures
            {
                WeaponItemIdA = weapons[0],
                WeaponItemIdB = weapons[1],
                CoatItemId = coats[0],
            };
        }

        private static InventoryService CreateInventory()
            => new InventoryService(92021, 92020);

        private static ItemCore CreateEquipment(int itemId, byte sortLock)
        {
            return new ItemCore
            {
                ItemKind = ItemCore.KindEquipment,
                ItemId = itemId,
                SortLockFlag = sortLock,
            };
        }

        private static bool TryMove(
            InventoryService inventory,
            InventoryListType sourceListType,
            short sourceSlotIndex,
            InventoryListType destinationListType,
            short destinationSlotIndex,
            out InventoryMoveServiceResult result)
        {
            var request = new InventoryMoveRequest
            {
                SourceListType = sourceListType,
                SourceSlotIndex = sourceSlotIndex,
                MoveCount = 1,
                DestinationListType = destinationListType,
                DestinationSlotIndex = destinationSlotIndex,
            };
            return InventoryMoveService.TryMove(inventory, request, ActorJob, 0, out result);
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}

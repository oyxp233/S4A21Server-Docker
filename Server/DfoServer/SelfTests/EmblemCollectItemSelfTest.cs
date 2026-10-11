using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Network.Handlers;
using System;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class EmblemCollectItemSelfTest
    {
        // 2026-10-10 实机抓包 body: boxSlot=4 | 未知=0x32 | count=1 | targetSlot=9 |
        // boxSlot=4 | boxTemplateId=0x0028F056 | targetSlot=9 | targetUid=0x05F7DE8C。
        private static readonly byte[] CapturedBody =
        {
            0x04, 0x00, 0x00, 0x00,
            0x32, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00,
            0x09, 0x00,
            0x04, 0x00,
            0x56, 0xF0, 0x28, 0x00,
            0x09, 0x00,
            0x8C, 0xDE, 0xF7, 0x05,
        };

        private const int BrilliantBoxItemId = 2682966;  // 灿烂的徽章回收箱, [emblem collector] 3
        private const int GorgeousBoxItemId = 2682963;   // 华丽的徽章回收箱, [emblem collector] 2
        private const int PlatinumBoxItemId = 2682964;   // 白金的徽章回收箱, [emblem collector] 4
        private const int BrilliantEmblemId1 = 2520035;  // 灿烂的绿色徽章 [物理暴击] grade 3
        private const int BrilliantEmblemId2 = 2520037;  // 灿烂的绿色徽章 [物理暴击] grade 3
        private const int GorgeousEmblemId1 = 2520020;   // 华丽的绿色徽章 [物理暴击] grade 2
        private const int GorgeousEmblemId2 = 2520021;   // 华丽的绿色徽章 [物理暴击] grade 2
        private const int PlatinumEmblemItemId = 2540006; // 白金徽章 [鬼斩+上挑] grade 4, [avatar emblem target type]=[S socket]
        private const int EquipmentItemId = 27850;
        // 丝绒臂章: PVF equipment/character/common/support/support_440001.equ, [equipment type]=[support]。
        // 辅助装备/魔法石只有 1 个徽章孔(IsSingleMiddleEquipmentSocket), 白金(S socket)徽章镶在此类装备上。
        private const int SupportEquipmentItemId = 440001;
        private const int FillerItemId = 2682963;        // 华丽回收箱, 仅用于填满背包的占位堆叠物
        private const short BoxSlot = 4;
        private const short EquipmentSlot = 9;

        public static int Run()
        {
            Console.WriteLine("=== EMBLEM_COLLECT_ITEM selftest ===");
            var failures = 0;

            VerifyRequestParsing(ref failures);
            VerifyMutationRules(ref failures);

            Console.WriteLine(failures == 0
                ? "EMBLEM_COLLECT_ITEM selftest passed"
                : $"EMBLEM_COLLECT_ITEM selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyRequestParsing(ref int failures)
        {
            Check(
                "0x03B8 request parses the captured body",
                InventoryHandler.TryParseEmblemCollectBody(CapturedBody, out var request)
                && request.BoxSlotIndex == 4
                && request.BoxTemplateId == 0x0028F056
                && request.TargetSlotIndex == 9
                && request.TargetUid == 0x05F7DE8C,
                ref failures);

            Check(
                "0x03B8 request rejects a short body",
                !InventoryHandler.TryParseEmblemCollectBody(new byte[25], out _),
                ref failures);

            Check(
                "0x03B8 request rejects null body",
                !InventoryHandler.TryParseEmblemCollectBody(null, out _),
                ref failures);

            var countMismatch = (byte[])CapturedBody.Clone();
            countMismatch[8] = 0x02;
            Check(
                "0x03B8 request rejects count != 1",
                !InventoryHandler.TryParseEmblemCollectBody(countMismatch, out _),
                ref failures);

            var duplicateBoxSlotMismatch = (byte[])CapturedBody.Clone();
            duplicateBoxSlotMismatch[14] = 0x05;
            Check(
                "0x03B8 request rejects duplicate box slot mismatch",
                !InventoryHandler.TryParseEmblemCollectBody(duplicateBoxSlotMismatch, out _),
                ref failures);

            var duplicateTargetSlotMismatch = (byte[])CapturedBody.Clone();
            duplicateTargetSlotMismatch[20] = 0x0A;
            Check(
                "0x03B8 request rejects duplicate target slot mismatch",
                !InventoryHandler.TryParseEmblemCollectBody(duplicateTargetSlotMismatch, out _),
                ref failures);
        }

        private static void VerifyMutationRules(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine("emblem collect mutation checks skipped: PVF_ARCHIVE_PATH is not set");
                return;
            }

            VerifyBrilliantCollect(ref failures);
            VerifyGorgeousCollect(ref failures);
            VerifyPlatinumCollect(ref failures);
            VerifyEquippedTargetCollect(ref failures);
            VerifyRejections(ref failures);
        }

        private static void VerifyBrilliantCollect(ref int failures)
        {
            var inventory = CreateCollectInventory(BrilliantBoxItemId, BrilliantEmblemId1, BrilliantEmblemId2);

            var ok = InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                inventory,
                BoxSlot,
                BrilliantBoxItemId,
                EquipmentSlot,
                EquipmentItemId,
                out var result);

            var returnedSlot = FindMainSlot(inventory, BrilliantEmblemId1);
            Check(
                "brilliant box returns one grade-3 emblem, destroys the other and consumes the box",
                ok
                && result != null
                && result.ReturnedEmblemTemplateId == BrilliantEmblemId1
                && result.DestroyedEmblemTemplateId == BrilliantEmblemId2
                && result.ReturnedEmblemSlotIndex == returnedSlot
                && result.BoxRemainingCount == 0
                && returnedSlot >= 0
                && inventory.GetItem(InventoryListType.Main, returnedSlot)?.Count == 1
                && inventory.GetItem(InventoryListType.Main, BoxSlot) == null,
                ref failures);

            var collected = inventory.GetItem(InventoryListType.Main, EquipmentSlot);
            Check(
                "emblem collect keeps the open socket count and marks both sockets empty",
                collected != null
                && collected.EmblemSocketCount == 2
                && collected.EmblemId1 == -1
                && collected.EmblemId2 == -1,
                ref failures);
        }

        private static void VerifyGorgeousCollect(ref int failures)
        {
            var inventory = CreateCollectInventory(GorgeousBoxItemId, GorgeousEmblemId1, GorgeousEmblemId2);

            var ok = InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                inventory,
                BoxSlot,
                GorgeousBoxItemId,
                EquipmentSlot,
                EquipmentItemId,
                out var result);

            Check(
                "gorgeous box returns one grade-2 emblem",
                ok
                && result != null
                && result.ReturnedEmblemTemplateId == GorgeousEmblemId1
                && result.DestroyedEmblemTemplateId == GorgeousEmblemId2
                && FindMainSlot(inventory, GorgeousEmblemId1) >= 0
                && inventory.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId1 == -1
                && inventory.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId2 == -1,
                ref failures);
        }

        private static void VerifyPlatinumCollect(ref int failures)
        {
            var inventory = new InventoryService(characterId: 92021, accountId: 92020);
            var box = ItemCore.Create(ItemCore.KindConsumable, PlatinumBoxItemId);
            box.Count = 1;
            inventory.SetItem(InventoryListType.Main, BoxSlot, box);

            var support = ItemCore.Create(ItemCore.KindEquipment, SupportEquipmentItemId);
            support.EmblemSocketCount = 1;
            support.EmblemId1 = PlatinumEmblemItemId;
            support.EmblemId2 = 0;
            inventory.SetItem(InventoryListType.Main, EquipmentSlot, support);

            var ok = InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                inventory,
                BoxSlot,
                PlatinumBoxItemId,
                EquipmentSlot,
                SupportEquipmentItemId,
                out var result);

            var returnedSlot = FindMainSlot(inventory, PlatinumEmblemItemId);
            Check(
                "platinum box collects the single support emblem and destroys nothing",
                ok
                && result != null
                && result.ReturnedEmblemTemplateId == PlatinumEmblemItemId
                && result.DestroyedEmblemTemplateId == 0
                && result.ReturnedEmblemSlotIndex == returnedSlot
                && result.BoxRemainingCount == 0
                && returnedSlot >= 0
                && inventory.GetItem(InventoryListType.Main, returnedSlot)?.Count == 1
                && inventory.GetItem(InventoryListType.Main, BoxSlot) == null,
                ref failures);

            var collected = inventory.GetItem(InventoryListType.Main, EquipmentSlot);
            Check(
                "single-socket collect keeps one open socket, Id1=-1 and Id2=0",
                collected != null
                && collected.EmblemSocketCount == 1
                && collected.EmblemId1 == -1
                && collected.EmblemId2 == 0,
                ref failures);
        }

        private static void VerifyEquippedTargetCollect(ref int failures)
        {
            var inventory = new InventoryService(characterId: 92011, accountId: 92010);
            var box = ItemCore.Create(ItemCore.KindConsumable, BrilliantBoxItemId);
            box.Count = 1;
            inventory.SetItem(InventoryListType.Main, BoxSlot, box);

            var equipped = ItemCore.Create(ItemCore.KindEquipment, EquipmentItemId);
            equipped.EmblemSocketCount = 2;
            equipped.EmblemId1 = BrilliantEmblemId1;
            equipped.EmblemId2 = BrilliantEmblemId2;
            var weaponSlot = (short)EquipmentType.Weapon;
            inventory.SetItem(InventoryListType.Equipment, weaponSlot, equipped);

            var ok = InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                inventory,
                BoxSlot,
                BrilliantBoxItemId,
                weaponSlot,
                EquipmentItemId,
                out var result);

            Check(
                "emblem collect supports an equipped target and reports the equipment list",
                ok
                && result != null
                && result.TargetEquipped
                && result.TargetListType == InventoryListType.Equipment
                && inventory.GetItem(InventoryListType.Equipment, weaponSlot)?.EmblemId1 == -1
                && inventory.GetItem(InventoryListType.Equipment, weaponSlot)?.EmblemId2 == -1,
                ref failures);
        }

        private static void VerifyRejections(ref int failures)
        {
            var gradeMismatch = CreateCollectInventory(GorgeousBoxItemId, BrilliantEmblemId1, BrilliantEmblemId2);
            Check(
                "grade-2 box rejects grade-3 emblems without mutating state",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    gradeMismatch,
                    BoxSlot,
                    GorgeousBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _)
                && gradeMismatch.GetItem(InventoryListType.Main, BoxSlot)?.Count == 1
                && gradeMismatch.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId1 == BrilliantEmblemId1,
                ref failures);

            var singleEmblem = CreateCollectInventory(BrilliantBoxItemId, GorgeousEmblemId1, 0);
            Check(
                "single emblem whose grade is outside the box parameter set is rejected",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    singleEmblem,
                    BoxSlot,
                    BrilliantBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _)
                && singleEmblem.GetItem(InventoryListType.Main, BoxSlot)?.Count == 1
                && singleEmblem.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId1 == GorgeousEmblemId1,
                ref failures);

            var brilliantOnPlatinum = new InventoryService(characterId: 92031, accountId: 92030);
            var brilliantBox = ItemCore.Create(ItemCore.KindConsumable, BrilliantBoxItemId);
            brilliantBox.Count = 1;
            brilliantOnPlatinum.SetItem(InventoryListType.Main, BoxSlot, brilliantBox);
            var supportWithPlatinum = ItemCore.Create(ItemCore.KindEquipment, SupportEquipmentItemId);
            supportWithPlatinum.EmblemSocketCount = 1;
            supportWithPlatinum.EmblemId1 = PlatinumEmblemItemId;
            brilliantOnPlatinum.SetItem(InventoryListType.Main, EquipmentSlot, supportWithPlatinum);
            Check(
                "brilliant box rejects a single grade-4 platinum emblem without mutating state",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    brilliantOnPlatinum,
                    BoxSlot,
                    BrilliantBoxItemId,
                    EquipmentSlot,
                    SupportEquipmentItemId,
                    out _)
                && brilliantOnPlatinum.GetItem(InventoryListType.Main, BoxSlot)?.Count == 1
                && brilliantOnPlatinum.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId1 == PlatinumEmblemItemId,
                ref failures);

            var platinumOnBrilliant = CreateCollectInventory(PlatinumBoxItemId, BrilliantEmblemId1, BrilliantEmblemId2);
            Check(
                "platinum box rejects grade-3 emblems on a two-socket equipment",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    platinumOnBrilliant,
                    BoxSlot,
                    PlatinumBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _)
                && platinumOnBrilliant.GetItem(InventoryListType.Main, BoxSlot)?.Count == 1
                && platinumOnBrilliant.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId1 == BrilliantEmblemId1,
                ref failures);

            var emptyBoxSlot = CreateCollectInventory(0, BrilliantEmblemId1, BrilliantEmblemId2);
            Check(
                "missing box in the declared slot is rejected",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    emptyBoxSlot,
                    BoxSlot,
                    BrilliantBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _),
                ref failures);

            var wrongBoxTemplate = CreateCollectInventory(BrilliantBoxItemId, BrilliantEmblemId1, BrilliantEmblemId2);
            Check(
                "box template mismatch is rejected",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    wrongBoxTemplate,
                    BoxSlot,
                    GorgeousBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _),
                ref failures);

            var wrongTargetUid = CreateCollectInventory(BrilliantBoxItemId, BrilliantEmblemId1, BrilliantEmblemId2);
            Check(
                "target uid mismatch is rejected",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    wrongTargetUid,
                    BoxSlot,
                    BrilliantBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId + 1,
                    out _),
                ref failures);

            var fullBackpack = CreateCollectInventory(BrilliantBoxItemId, BrilliantEmblemId1, BrilliantEmblemId2);
            FillRemainingMainSlots(fullBackpack);
            Check(
                "a full backpack rejects the collect without consuming the box or clearing emblems",
                !InventoryEquipmentMutationService.TryCollectEquipmentEmblems(
                    fullBackpack,
                    BoxSlot,
                    BrilliantBoxItemId,
                    EquipmentSlot,
                    EquipmentItemId,
                    out _)
                && fullBackpack.GetItem(InventoryListType.Main, BoxSlot)?.Count == 1
                && fullBackpack.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId1 == BrilliantEmblemId1
                && fullBackpack.GetItem(InventoryListType.Main, EquipmentSlot)?.EmblemId2 == BrilliantEmblemId2,
                ref failures);
        }

        private static void FillRemainingMainSlots(InventoryService inventory)
        {
            var filler = ItemCore.Create(ItemCore.KindConsumable, FillerItemId);
            filler.Count = 1;
            for (short slot = InventoryService.MainSlotStart; slot <= InventoryService.MainSlotEnd; slot++)
            {
                if (slot == BoxSlot || slot == EquipmentSlot)
                    continue;

                inventory.SetItem(InventoryListType.Main, slot, filler.Copy());
            }
        }

        private static InventoryService CreateCollectInventory(int boxItemId, int emblemId1, int emblemId2)
        {
            var inventory = new InventoryService(characterId: 92001, accountId: 92000);
            if (boxItemId > 0)
            {
                var box = ItemCore.Create(ItemCore.KindConsumable, boxItemId);
                box.Count = 1;
                inventory.SetItem(InventoryListType.Main, BoxSlot, box);
            }

            var equipment = ItemCore.Create(ItemCore.KindEquipment, EquipmentItemId);
            equipment.EmblemSocketCount = 2;
            equipment.EmblemId1 = emblemId1;
            equipment.EmblemId2 = emblemId2;
            inventory.SetItem(InventoryListType.Main, EquipmentSlot, equipment);
            return inventory;
        }

        private static short FindMainSlot(InventoryService inventory, int itemTemplateId)
        {
            for (short slot = InventoryService.MainSlotStart; slot <= InventoryService.MainSlotEnd; slot++)
            {
                var item = inventory.GetItem(InventoryListType.Main, slot);
                if (item != null && item.ItemId == itemTemplateId && item.Count > 0)
                    return slot;
            }

            return -1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}

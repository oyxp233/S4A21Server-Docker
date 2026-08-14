using DfoServer.Game.CraneMiniGame;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class CraneMiniGameSelfTest
    {
        private const int MaterialItemId = 2660547;
        private const short MaterialSlot = 120;

        public static int Run()
        {
            Console.WriteLine("=== CRANE_MINIGAME selftest ===");
            var failures = 0;
            var catalog = CraneMiniGameCatalog.Parse(BuildCatalogText());

            Check("parse view count", catalog.ViewCount == 6, ref failures);
            Check("parse material", catalog.MaterialItemId == MaterialItemId && catalog.MaterialCount == 1, ref failures);
            Check("parse crane coin exchange from final need-material row",
                catalog.TryResolveCoinExchange(MaterialItemId, out var exchangeItemId, out var exchangeCount)
                && exchangeItemId == 3333
                && exchangeCount == 3
                && !catalog.TryResolveCoinExchange(MaterialItemId + 1, out _, out _), ref failures);
            Check("parse item fields", catalog.Items.Count == 7
                && catalog.Items[0].CatalogIndex == 0
                && catalog.Items[0].ItemId == 10000001
                && catalog.Items[0].Count == 2
                && Math.Abs(catalog.Items[0].ViewWeight - 43.96d) < 0.001d
                && Math.Abs(catalog.Items[0].PickChance - 90d) < 0.001d, ref failures);

            var selected = CraneMiniGameStartService.SelectDisplayItems(catalog.Items, 6, _ => 0);
            Check("select six unique display items", selected.Count == 6
                && selected.Select(item => item.ItemId).Distinct().Count() == 6, ref failures);

            var inventory = new InventoryService(990486, 990486);
            var material = InventoryCreateService.CreateCore(
                ItemCore.KindMaterial,
                MaterialItemId,
                ItemCreateReason.Unknown,
                2);
            material.Count = 2;
            inventory.AttachItem(InventoryListType.Main, MaterialSlot, material);
            var service = new CraneMiniGameStartService(catalog);
            Check("start consumes configured material", service.TryStart(inventory, 140, out var result)
                && result.MachineId == 140
                && result.MaterialSlot == MaterialSlot
                && result.MaterialRemainingCount == 1
                && result.DisplayItems.Count == 6
                && inventory.CountMainItem(MaterialItemId) == 1, ref failures);

            var ack = CraneMiniGameStartAckBuilder.BuildSuccess(result);
            Check("success ack has result plus 30-byte payload", ack.Length == 31 && ack[0] == 1, ref failures);
            Check("success ack field order", BitConverter.ToUInt16(ack, 1) == 140
                && BitConverter.ToUInt32(ack, 3) == 1
                && BitConverter.ToUInt32(ack, 7) == unchecked((uint)result.DisplayItems[0].CatalogIndex)
                && BitConverter.ToUInt32(ack, 27) == unchecked((uint)result.DisplayItems[5].CatalogIndex), ref failures);

            var pickupItem = result.DisplayItems[0];
            Check("pickup validates display slot and item", CraneMiniGamePickupService.TryResolveSelection(
                    result,
                    checked((ushort)pickupItem.CatalogIndex),
                    pickupItem.ItemId,
                    out var resolvedPickup)
                && ReferenceEquals(pickupItem, resolvedPickup)
                && !CraneMiniGamePickupService.TryResolveSelection(result, 0, -1, out _), ref failures);
            Check("pickup chance boundaries", CraneMiniGamePickupService.RollSuccess(
                    new CraneMiniGameItem { PickChance = 90 }, _ => 8999)
                && !CraneMiniGamePickupService.RollSuccess(
                    new CraneMiniGameItem { PickChance = 90 }, _ => 9000), ref failures);

            var pickupSuccess = CraneMiniGamePickupAckBuilder.BuildSuccess(pickupItem);
            var pickupFailure = CraneMiniGamePickupAckBuilder.BuildFailure();
            Check("pickup ack shapes", pickupSuccess.Length == 7
                && pickupSuccess[0] == 1
                && BitConverter.ToInt32(pickupSuccess, 1) == pickupItem.ItemId
                && BitConverter.ToInt16(pickupSuccess, 5) == pickupItem.Count
                && pickupFailure.SequenceEqual(new byte[] { 0, 4 }), ref failures);

            var coordinator = new CraneMiniGameSessionCoordinator();
            var sessionId = Guid.NewGuid();
            coordinator.Set(sessionId, result);
            var rollCount = 0;
            Check("pickup reservation fixes result across retry",
                coordinator.TryReservePickup(
                    sessionId,
                    checked((ushort)pickupItem.CatalogIndex),
                    pickupItem.ItemId,
                    out var reservation,
                    _ =>
                    {
                        rollCount++;
                        return true;
                    })
                && reservation.Won
                && coordinator.ReleasePickup(sessionId, reservation)
                && coordinator.TryReservePickup(
                    sessionId,
                    checked((ushort)pickupItem.CatalogIndex),
                    pickupItem.ItemId,
                    out var retriedReservation)
                && ReferenceEquals(reservation, retriedReservation)
                && rollCount == 1
                && coordinator.CompletePickup(sessionId, retriedReservation)
                && !coordinator.TryGet(sessionId, out _), ref failures);

            var noMaterialInventory = new InventoryService(990487, 990486);
            Check("missing material rejects without state", !service.TryStart(noMaterialInventory, 140, out var rejected)
                && rejected == null, ref failures);

            var exchangeInventory = new InventoryService(990488, 990486);
            exchangeInventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, 1_000_000);
            var seededExchangeMaterial = InventoryRewardGrantService.TryCreateAndInsert(
                exchangeInventory,
                3333,
                ItemCreateReason.NpcShopPurchase,
                3,
                out var seededExchangeGrant);
            var realCatalog = CraneMiniGameCatalog.Load();
            var resolvedRealExchange = realCatalog.TryResolveCoinExchange(
                MaterialItemId,
                out var realExchangeItemId,
                out var realExchangeCount);
            var exchangeOk = InventoryShopRuntimeService.TryBuyNpcItem(
                exchangeInventory,
                MaterialItemId,
                1,
                out var exchangeResult);
            Check("NPC exchange uses crane script material instead of coin item need-material",
                seededExchangeMaterial
                && resolvedRealExchange
                && realExchangeItemId == 3333
                && realExchangeCount == 3
                && exchangeOk
                && exchangeResult != null
                && exchangeResult.CostItemTemplateId == 3333
                && exchangeResult.CostItemNewStackCount == 0
                && exchangeInventory.CountMainItem(3333) == 0
                && exchangeInventory.CountMainItem(MaterialItemId) == 1, ref failures);
            Check("malformed request failure shape", CraneMiniGameStartAckBuilder.BuildFailure().SequenceEqual(new byte[] { 0, 4 }), ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static string BuildCatalogText()
        {
            var text = "[viewCnt]\n6\n";
            for (var i = 1; i <= 7; i++)
            {
                text += $"[item]\n{10000000 + i}\n[cnt]\n2\n[viewRatio]\n{(i == 1 ? "43.96" : "10")}\n[pickRatio]\n90\n";
            }
            return text + $"[material]\n{MaterialItemId}\t1\n[need material]\n3332\t5\n3341\t10\n3333\t3\n[/need material]\n";
        }

        private static void Check(string label, bool ok, ref int failures)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (!ok)
                failures++;
        }
    }
}

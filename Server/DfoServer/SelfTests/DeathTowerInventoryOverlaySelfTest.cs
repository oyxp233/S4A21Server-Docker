using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;

namespace DfoServer.SelfTests
{
    public static class DeathTowerInventoryOverlaySelfTest
    {
        private const int TowerColorlessCubeItemId = 6515;
        private const int TowerHastePotionItemId = 6518;
        private const int TowerAlternateWasteItemId = 6521;
        private const int TowerAlternateWasteItemId2 = 6524;
        private const int PersistentConsumableItemId = 700001;
        private const int PersistentMaterialItemId = 3200;
        private const int PersistentColorlessCubeItemId = 3037;

        public static int Run()
        {
            Console.WriteLine("=== DEATH_TOWER_INVENTORY_OVERLAY selftest ===");
            var failures = 0;

            TestStackableFamilyKindProjection(ref failures);
            TestPickupUsesIndependentVirtualSlots(ref failures);
            TestMaterialInstantItemUsesBothTowerSlots(ref failures);
            TestPickupMergesAllowedQuickSlotStack(ref failures);
            TestReverseMoveMergesSameTemplateStacks(ref failures);
            TestPickupFallsBackAfterVirtualQuickSlots(ref failures);
            TestPersistentItemsFallThroughTowerOverlay(ref failures);
            TestTransientMoveRejectsPersistentTarget(ref failures);
            TestTransientMoveAndReverseSource(ref failures);
            TestTransientMoveRecoversStaleIdentity(ref failures);
            TestTransientIdentitySwapIsAtomic(ref failures);
            TestTransientSingleIdentityReverseSwap(ref failures);
            TestAmbiguousTransientIdentityFailsClosed(ref failures);
            TestTransientMaterialMove(ref failures);
            TestSortOnlyChangesTransientItems(ref failures);
            TestSortSupportsTypedQuickSlot(ref failures);
            TestTransientSkillMaterialDelete(ref failures);
            TestFailedDeleteHasNoPersistentSideEffect(ref failures);
            TestTransientNeverEntersPersistentInventory(ref failures);
            TestProjectionDoesNotLeakPersistentItems(ref failures);
            TestFullListProjectionMergesSharedTowerContainer(ref failures);

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void TestStackableFamilyKindProjection(ref int failures)
        {
            var material = new ItemMetadata
            {
                ItemKind = "stackable",
                StackableType = "`[material instant item]` 2",
            };
            var quest = new ItemMetadata
            {
                ItemKind = "stackable",
                StackableType = "`[quest throw item]` 1",
            };

            var materialResolved = ItemMetadataResolver.TryResolveItemKind(
                TowerColorlessCubeItemId,
                material,
                out var materialKind);
            var questResolved = ItemMetadataResolver.TryResolveItemKind(
                900001,
                quest,
                out var questKind);
            Check("stackable family variants use the same item kind as their slot range",
                materialResolved
                    && materialKind == ItemCore.KindMaterial
                    && !DeathTowerItemSlotPolicy.PrefersQuickSlotAllocation(material)
                    && DeathTowerItemSlotPolicy.CanMoveToQuickSlot(material)
                    && !DeathTowerItemSlotPolicy.IsQuickSlotConsumable(material)
                    && questResolved
                    && questKind == ItemCore.KindQuest,
                ref failures);
        }

        private static void TestMaterialInstantItemUsesBothTowerSlots(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990119);
            var tower = CreateTowerWithGroundItem(
                TowerColorlessCubeItemId,
                2);

            var picked = service.TryPickup(
                tower,
                lease,
                51,
                out var pickup);
            var pickupWasMain = picked
                && pickup.DestinationEndpoint.Equals(
                    new DeathTowerInventoryEndpoint(
                        InventoryListType.Main,
                        121))
                && TowerItemMatches(
                    tower,
                    InventoryListType.Main,
                    121,
                    TowerColorlessCubeItemId,
                    2);
            var movedToQuick = service.TryMove(
                tower,
                lease,
                new InventoryMoveRequest
                {
                    SourceListType = InventoryListType.QuickSlot,
                    SourceSlotIndex = 4,
                    SourceInstanceValue = 0,
                    MoveCount = 0,
                    DestinationListType = InventoryListType.Main,
                    DestinationSlotIndex = 121,
                    DestinationInstanceValue = TowerColorlessCubeItemId,
                },
                0,
                0,
                out var moveToQuickHandled,
                out var moveToQuickResult);
            var quickMoveApplied = movedToQuick
                && moveToQuickHandled
                && moveToQuickResult.IdentityResolved
                && TowerItemMatches(
                    tower,
                    InventoryListType.QuickSlot,
                    4,
                    TowerColorlessCubeItemId,
                    2);
            var movedToMain = service.TryMove(
                tower,
                lease,
                new InventoryMoveRequest
                {
                    SourceListType = InventoryListType.Main,
                    SourceSlotIndex = 121,
                    SourceInstanceValue = 0,
                    MoveCount = 0,
                    DestinationListType = InventoryListType.QuickSlot,
                    DestinationSlotIndex = 4,
                    DestinationInstanceValue = TowerColorlessCubeItemId,
                },
                0,
                0,
                out var moveToMainHandled,
                out var moveToMainResult);
            var mainMoveApplied = movedToMain
                && moveToMainHandled
                && moveToMainResult.IdentityResolved
                && TowerItemMatches(
                    tower,
                    InventoryListType.Main,
                    121,
                    TowerColorlessCubeItemId,
                    2);

            Check("material instant tower items allocate to Main and move across both virtual lists",
                pickupWasMain
                    && mainMoveApplied
                    && quickMoveApplied
                    && TowerItemMatches(
                        tower,
                        InventoryListType.Main,
                        121,
                        TowerColorlessCubeItemId,
                        2)
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestPickupMergesAllowedQuickSlotStack(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990121);
            var tower = CreateTowerWithGroundItem(
                TowerColorlessCubeItemId,
                2);
            tower.ReplaceInventoryItems(
                new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>
                {
                    [new DeathTowerInventoryEndpoint(
                        InventoryListType.QuickSlot,
                        7)] = new TowerInventoryItem
                    {
                        ItemId = TowerColorlessCubeItemId,
                        Count = 1,
                        StackLimit = int.MaxValue,
                    },
                });

            var success = service.TryPickup(
                tower,
                lease,
                51,
                out var result);

            Check("tower pickup merges an allowed QuickSlot stack before Main allocation",
                success
                    && result.DestinationEndpoint.Equals(
                        new DeathTowerInventoryEndpoint(
                            InventoryListType.QuickSlot,
                            7))
                    && result.ChangedEndpoints.Count == 1
                    && TowerItemMatches(
                        tower,
                        InventoryListType.QuickSlot,
                        7,
                        TowerColorlessCubeItemId,
                        3)
                    && !tower.InventoryItems.ContainsKey(
                        new DeathTowerInventoryEndpoint(
                            InventoryListType.Main,
                            121))
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestReverseMoveMergesSameTemplateStacks(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990122);
            var tower = CreateTower();
            tower.ReplaceInventoryItems(
                new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>
                {
                    [new DeathTowerInventoryEndpoint(
                        InventoryListType.QuickSlot,
                        7)] = new TowerInventoryItem
                    {
                        ItemId = TowerColorlessCubeItemId,
                        Count = 1,
                        StackLimit = int.MaxValue,
                    },
                    [new DeathTowerInventoryEndpoint(
                        InventoryListType.Main,
                        137)] = new TowerInventoryItem
                    {
                        ItemId = TowerColorlessCubeItemId,
                        Count = 2,
                        StackLimit = int.MaxValue,
                    },
                });

            var success = service.TryMove(
                tower,
                lease,
                new InventoryMoveRequest
                {
                    SourceListType = InventoryListType.QuickSlot,
                    SourceSlotIndex = 7,
                    SourceInstanceValue = 0,
                    MoveCount = 0,
                    DestinationListType = InventoryListType.Main,
                    DestinationSlotIndex = 137,
                    DestinationInstanceValue = TowerColorlessCubeItemId,
                },
                0,
                0,
                out var handled,
                out var result);

            Check("A14 reverse drag merges same-template Main and QuickSlot stacks",
                success
                    && handled
                    && result.IdentityResolved
                    && result.ReversedRequest
                    && TowerItemMatches(
                        tower,
                        InventoryListType.QuickSlot,
                        7,
                        TowerColorlessCubeItemId,
                        3)
                    && !tower.InventoryItems.ContainsKey(
                        new DeathTowerInventoryEndpoint(
                            InventoryListType.Main,
                            137))
                    && tower.GetItemCountsSnapshot()[TowerColorlessCubeItemId] == 3
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestPickupUsesIndependentVirtualSlots(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var quickLease = CreateLease(990101);
            quickLease.Inventory.SetItem(
                InventoryListType.Main,
                3,
                CreateStackable(ItemCore.KindConsumable, PersistentConsumableItemId, 1));
            quickLease.Inventory.ClearDirtyState();
            var quickTower = CreateTowerWithGroundItem(TowerHastePotionItemId, 2);

            var quickSuccess = service.TryPickup(
                quickTower,
                quickLease,
                51,
                out var quickPickup);

            var materialLease = CreateLease(990113);
            materialLease.Inventory.SetItem(
                InventoryListType.Main,
                121,
                CreateStackable(ItemCore.KindMaterial, PersistentMaterialItemId, 1));
            materialLease.Inventory.ClearDirtyState();
            var materialTower = CreateTowerWithGroundItem(
                TowerColorlessCubeItemId,
                2);
            var materialSuccess = service.TryPickup(
                materialTower,
                materialLease,
                51,
                out var materialPickup);

            Check("tower pickup keeps QuickSlot independent and reserves occupied Main slots",
                quickSuccess
                    && quickPickup.DestinationEndpoint.Equals(
                        new DeathTowerInventoryEndpoint(
                            InventoryListType.QuickSlot,
                            3))
                    && quickLease.Inventory.GetItem(InventoryListType.Main, 3)?.ItemId
                        == PersistentConsumableItemId
                    && TowerItemMatches(
                        quickTower,
                        InventoryListType.QuickSlot,
                        3,
                        TowerHastePotionItemId,
                        2)
                    && quickLease.Inventory.DirtyListTypes.Count == 0
                    && materialSuccess
                    && materialPickup.DestinationEndpoint.Equals(
                        new DeathTowerInventoryEndpoint(
                            InventoryListType.Main,
                            122))
                    && materialLease.Inventory.GetItem(InventoryListType.Main, 121)?.ItemId
                        == PersistentMaterialItemId
                    && TowerItemMatches(
                        materialTower,
                        InventoryListType.Main,
                        122,
                        TowerColorlessCubeItemId,
                        2)
                    && materialLease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestPickupFallsBackAfterVirtualQuickSlots(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990114);
            var tower = CreateTower();
            var initial = new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>();
            for (short slot = 3; slot <= 8; slot++)
            {
                initial[new DeathTowerInventoryEndpoint(
                    InventoryListType.QuickSlot,
                    slot)] = new TowerInventoryItem
                {
                    ItemId = slot % 2 == 0
                        ? TowerAlternateWasteItemId
                        : TowerAlternateWasteItemId2,
                    Count = 1,
                    StackLimit = 1,
                    IsWaste = true,
                };
            }
            tower.ReplaceInventoryItems(initial);
            tower.BeginStage(
                0x12345679,
                new[]
                {
                    new StageTowerItem
                    {
                        SourceMonsterUniqueId = 41,
                        ItemUniqueId = 52,
                        ItemId = TowerHastePotionItemId,
                        DropRate = 10000,
                        StackCount = 1,
                    },
                });
            tower.GenerateDropsForMonster(41);

            var success = service.TryPickup(
                tower,
                lease,
                52,
                out var pickup);
            Check("tower pickup uses Main only after virtual QuickSlot is full",
                success
                    && pickup.DestinationEndpoint.Equals(
                        new DeathTowerInventoryEndpoint(
                            InventoryListType.Main,
                            65))
                    && TowerItemMatches(
                        tower,
                        InventoryListType.Main,
                        65,
                        TowerHastePotionItemId,
                        1)
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestPersistentItemsFallThroughTowerOverlay(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990102);
            lease.Inventory.SetItem(
                InventoryListType.Main,
                3,
                CreateStackable(ItemCore.KindConsumable, PersistentConsumableItemId, 2));
            lease.Inventory.ClearDirtyState();
            var tower = CreateTower();

            var success = service.TryMove(
                tower,
                lease,
                CreateMove(3, 4, 1),
                0,
                0,
                out var handled,
                out var result);
            Check("persistent-only moves fall through the tower overlay",
                !success
                    && !handled
                    && result.ChangedSlots.Count == 0
                    && lease.Inventory.GetItem(InventoryListType.Main, 3)?.Count == 2
                    && lease.Inventory.GetItem(InventoryListType.Main, 4) == null
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestTransientMoveAndReverseSource(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990103);
            var tower = CreateTowerWithTransient(
                3,
                TowerHastePotionItemId,
                2,
                isWaste: true);

            var success = service.TryMove(
                tower,
                lease,
                CreateMove(3, 4, 1),
                0,
                0,
                out var handled,
                out var result);
            var firstMoveOk = success
                    && handled
                    && result.ChangedSlots.Count == 2
                    && TowerItemMatches(tower, 3, TowerHastePotionItemId, 1)
                    && TowerItemMatches(tower, 4, TowerHastePotionItemId, 1)
                    && lease.Inventory.DirtyListTypes.Count == 0;

            var reverseSuccess = service.TryMove(
                tower,
                lease,
                CreateMove(
                    InventoryListType.Main,
                    76,
                    InventoryListType.QuickSlot,
                    4,
                    0),
                0,
                0,
                out var reverseHandled,
                out var reverseResult);
            Check("tower move supports an empty-source reverse request",
                firstMoveOk
                    && reverseSuccess
                    && reverseHandled
                    && reverseResult.ChangedSlots.Count == 2
                    && TowerItemMatches(tower, 76, TowerHastePotionItemId, 1)
                    && !tower.InventoryItems.ContainsKey(
                        new DeathTowerInventoryEndpoint(
                            InventoryListType.QuickSlot,
                            4))
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestTransientMoveRecoversStaleIdentity(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990116);
            var tower = CreateTower();
            tower.ReplaceInventoryItems(
                new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>
                {
                    [new DeathTowerInventoryEndpoint(
                        InventoryListType.Main,
                        100)] = new TowerInventoryItem
                    {
                        ItemId = TowerAlternateWasteItemId,
                        Count = 1,
                        StackLimit = 1000,
                        IsWaste = true,
                    },
                });

            var success = service.TryMove(
                tower,
                lease,
                new InventoryMoveRequest
                {
                    SourceListType = InventoryListType.QuickSlot,
                    SourceSlotIndex = 4,
                    SourceInstanceValue = 0,
                    MoveCount = 0,
                    DestinationListType = InventoryListType.QuickSlot,
                    DestinationSlotIndex = 6,
                    DestinationInstanceValue = TowerAlternateWasteItemId,
                },
                0,
                0,
                out var handled,
                out var result);

            Check("tower move resolves a stale reverse endpoint by item identity",
                success
                    && handled
                    && result.IdentityResolved
                    && result.ReversedRequest
                    && TowerItemMatches(
                        tower,
                        InventoryListType.QuickSlot,
                        4,
                        TowerAlternateWasteItemId,
                        1)
                    && !tower.InventoryItems.ContainsKey(
                        new DeathTowerInventoryEndpoint(
                            InventoryListType.Main,
                            100))
                    && tower.GetItemCountsSnapshot().Count == 1
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestTransientIdentitySwapIsAtomic(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990117);
            var tower = CreateTower();
            tower.ReplaceInventoryItems(
                new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>
                {
                    [new DeathTowerInventoryEndpoint(
                        InventoryListType.Main,
                        100)] = new TowerInventoryItem
                    {
                        ItemId = TowerAlternateWasteItemId,
                        Count = 1,
                        StackLimit = 1000,
                        IsWaste = true,
                    },
                    [new DeathTowerInventoryEndpoint(
                        InventoryListType.QuickSlot,
                        7)] = new TowerInventoryItem
                    {
                        ItemId = TowerAlternateWasteItemId2,
                        Count = 1,
                        StackLimit = 1000,
                        IsWaste = true,
                    },
                });

            var success = service.TryMove(
                tower,
                lease,
                new InventoryMoveRequest
                {
                    SourceListType = InventoryListType.QuickSlot,
                    SourceSlotIndex = 5,
                    SourceInstanceValue = TowerAlternateWasteItemId,
                    MoveCount = 0,
                    DestinationListType = InventoryListType.QuickSlot,
                    DestinationSlotIndex = 7,
                    DestinationInstanceValue = TowerAlternateWasteItemId2,
                },
                0,
                0,
                out var handled,
                out var result);

            Check("tower identity exchange relocates both stale items atomically",
                success
                    && handled
                    && result.IdentityResolved
                    && TowerItemMatches(
                        tower,
                        InventoryListType.QuickSlot,
                        5,
                        TowerAlternateWasteItemId2,
                        1)
                    && TowerItemMatches(
                        tower,
                        InventoryListType.QuickSlot,
                        7,
                        TowerAlternateWasteItemId,
                        1)
                    && tower.GetItemCountsSnapshot().Count == 2
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestTransientSingleIdentityReverseSwap(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990120);
            var tower = CreateTower();
            tower.ReplaceInventoryItems(
                new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>
                {
                    [new DeathTowerInventoryEndpoint(
                        InventoryListType.QuickSlot,
                        3)] = new TowerInventoryItem
                    {
                        ItemId = TowerHastePotionItemId,
                        Count = 1,
                        StackLimit = 1000,
                        IsWaste = true,
                        IsQuickSlotConsumable = true,
                    },
                    [new DeathTowerInventoryEndpoint(
                        InventoryListType.Main,
                        100)] = new TowerInventoryItem
                    {
                        ItemId = TowerAlternateWasteItemId,
                        Count = 1,
                        StackLimit = 1000,
                        IsWaste = true,
                        IsQuickSlotConsumable = true,
                    },
                });

            var success = service.TryMove(
                tower,
                lease,
                new InventoryMoveRequest
                {
                    SourceListType = InventoryListType.QuickSlot,
                    SourceSlotIndex = 3,
                    SourceInstanceValue = 0,
                    MoveCount = 0,
                    DestinationListType = InventoryListType.Main,
                    DestinationSlotIndex = 100,
                    DestinationInstanceValue = TowerAlternateWasteItemId,
                },
                0,
                0,
                out var handled,
                out var result);

            Check("single-identity reverse move atomically exchanges QuickSlot and Main tower items",
                success
                    && handled
                    && result.IdentityResolved
                    && result.ReversedRequest
                    && TowerItemMatches(
                        tower,
                        InventoryListType.QuickSlot,
                        3,
                        TowerAlternateWasteItemId,
                        1)
                    && TowerItemMatches(
                        tower,
                        InventoryListType.Main,
                        100,
                        TowerHastePotionItemId,
                        1)
                    && tower.InventoryItems.Count == 2
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestAmbiguousTransientIdentityFailsClosed(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990118);
            var tower = CreateTower();
            tower.ReplaceInventoryItems(
                new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>
                {
                    [new DeathTowerInventoryEndpoint(
                        InventoryListType.QuickSlot,
                        3)] = new TowerInventoryItem
                    {
                        ItemId = TowerAlternateWasteItemId,
                        Count = 1,
                        StackLimit = 1000,
                        IsWaste = true,
                    },
                    [new DeathTowerInventoryEndpoint(
                        InventoryListType.Main,
                        100)] = new TowerInventoryItem
                    {
                        ItemId = TowerAlternateWasteItemId,
                        Count = 1,
                        StackLimit = 1000,
                        IsWaste = true,
                    },
                });

            var success = service.TryMove(
                tower,
                lease,
                new InventoryMoveRequest
                {
                    SourceListType = InventoryListType.QuickSlot,
                    SourceSlotIndex = 4,
                    SourceInstanceValue = 0,
                    MoveCount = 0,
                    DestinationListType = InventoryListType.QuickSlot,
                    DestinationSlotIndex = 6,
                    DestinationInstanceValue = TowerAlternateWasteItemId,
                },
                0,
                0,
                out var handled,
                out var result);

            Check("ambiguous tower item identity fails closed without duplication",
                !success
                    && handled
                    && result.IdentityResolved == false
                    && tower.InventoryItems.Count == 2
                    && TowerItemMatches(
                        tower,
                        InventoryListType.QuickSlot,
                        3,
                        TowerAlternateWasteItemId,
                        1)
                    && TowerItemMatches(
                        tower,
                        InventoryListType.Main,
                        100,
                        TowerAlternateWasteItemId,
                        1)
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestTransientMoveRejectsPersistentTarget(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990115);
            lease.Inventory.SetItem(
                InventoryListType.Main,
                122,
                CreateStackable(ItemCore.KindMaterial, PersistentMaterialItemId, 1));
            lease.Inventory.ClearDirtyState();
            var tower = CreateTowerWithTransient(
                121,
                TowerColorlessCubeItemId,
                2,
                isWaste: false);

            var success = service.TryMove(
                tower,
                lease,
                CreateMove(
                    InventoryListType.Main,
                    121,
                    InventoryListType.Main,
                    122,
                    0),
                0,
                0,
                out var handled,
                out var result);
            Check("tower transient move rejects an occupied physical Main target without loss",
                !success
                    && handled
                    && result.ChangedSlots.Count == 0
                    && TowerItemMatches(
                        tower,
                        InventoryListType.Main,
                        121,
                        TowerColorlessCubeItemId,
                        2)
                    && lease.Inventory.GetItem(
                        InventoryListType.Main,
                        122)?.ItemId == PersistentMaterialItemId
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestTransientMaterialMove(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990104);
            var tower = CreateTowerWithTransient(
                121,
                TowerColorlessCubeItemId,
                2,
                isWaste: false);

            var success = service.TryMove(
                tower,
                lease,
                CreateMove(
                    InventoryListType.Main,
                    121,
                    InventoryListType.Main,
                    122,
                    1),
                0,
                0,
                out var handled,
                out var result);
            Check("tower material overlay slots can move within the material range",
                success
                    && handled
                    && result.ChangedSlots.Count == 2
                    && TowerItemMatches(tower, 121, TowerColorlessCubeItemId, 1)
                    && TowerItemMatches(tower, 122, TowerColorlessCubeItemId, 1)
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestSortOnlyChangesTransientItems(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990105);
            lease.Inventory.SetItem(
                InventoryListType.Main,
                123,
                CreateStackable(ItemCore.KindMaterial, PersistentMaterialItemId, 1));
            lease.Inventory.ClearDirtyState();
            var tower = CreateTower();
            tower.ReplaceInventoryItems(
                new Dictionary<short, TowerInventoryItem>
                {
                    [121] = new TowerInventoryItem
                    {
                        ItemId = TowerColorlessCubeItemId,
                        Count = 2,
                        StackLimit = 1000,
                        IsQuickSlotConsumable = true,
                        IsWaste = false,
                    },
                    [122] = new TowerInventoryItem
                    {
                        ItemId = PersistentMaterialItemId,
                        Count = 1,
                        StackLimit = 1000,
                        IsWaste = false,
                    },
                });

            var success = service.TrySort(
                tower,
                lease,
                InventoryListType.Main,
                ItemCore.KindMaterial,
                out var handled,
                out var result);
            var projection = DeathTowerInventoryProjectionBuilder.BuildFullListBody(
                tower,
                lease.Inventory);
            Check("sort changes only transient material slots",
                success
                    && handled
                    && result.Success
                    && result.Mutated
                    && result.ChangedSlots.Contains((short)121)
                    && result.ChangedSlots.Contains((short)122)
                    && TowerItemMatches(tower, 121, PersistentMaterialItemId, 1)
                    && TowerItemMatches(tower, 122, TowerColorlessCubeItemId, 2)
                    && lease.Inventory.GetItem(InventoryListType.Main, 123)?.ItemId
                        == PersistentMaterialItemId
                    && lease.Inventory.DirtyListTypes.Count == 0
                    && HasFullProjectedItem(
                        projection,
                        121,
                        PersistentMaterialItemId,
                        1)
                    && HasFullProjectedItem(
                        projection,
                        122,
                        TowerColorlessCubeItemId,
                        2)
                    && HasFullProjectedItem(
                        projection,
                        123,
                        PersistentMaterialItemId,
                        1)
                    && HasNoDuplicateFullProjectedSlots(projection),
                ref failures);
        }

        private static void TestSortSupportsTypedQuickSlot(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990121);
            var tower = CreateTower();
            tower.ReplaceInventoryItems(
                new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>
                {
                    [new DeathTowerInventoryEndpoint(
                        InventoryListType.QuickSlot,
                        3)] = new TowerInventoryItem
                    {
                        ItemId = TowerAlternateWasteItemId,
                        Count = 1,
                        StackLimit = 1000,
                        IsQuickSlotConsumable = true,
                        IsWaste = true,
                    },
                    [new DeathTowerInventoryEndpoint(
                        InventoryListType.QuickSlot,
                        4)] = new TowerInventoryItem
                    {
                        ItemId = TowerHastePotionItemId,
                        Count = 1,
                        StackLimit = 1000,
                        IsQuickSlotConsumable = true,
                        IsWaste = true,
                    },
                });

            var success = service.TrySort(
                tower,
                lease,
                InventoryListType.QuickSlot,
                ItemCore.KindConsumable,
                true,
                out var handled,
                out var result);
            Check("typed tower sort reorders QuickSlot transient items without touching the entity lease",
                success
                    && handled
                    && result.Success
                    && result.Mutated
                    && TowerItemMatches(
                        tower,
                        InventoryListType.QuickSlot,
                        3,
                        TowerHastePotionItemId,
                        1)
                    && TowerItemMatches(
                        tower,
                        InventoryListType.QuickSlot,
                        4,
                        TowerAlternateWasteItemId,
                        1)
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);

            var noCategorySuccess = service.TrySort(
                tower,
                lease,
                InventoryListType.QuickSlot,
                byte.MaxValue,
                false,
                out var noCategoryHandled,
                out var noCategoryResult);
            Check("one-byte-equivalent tower sort treats category as omitted rather than item id 255",
                noCategorySuccess
                    && noCategoryHandled
                    && noCategoryResult.Success
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestTransientSkillMaterialDelete(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990106);
            var tower = CreateTowerWithTransient(
                121,
                TowerColorlessCubeItemId,
                5,
                isWaste: false);
            var command = CreateDeleteCommand(
                new DeathTowerDeleteItemEntry(
                    2,
                    121,
                    TowerColorlessCubeItemId,
                    3));

            var success = service.TryDeleteSkillMaterials(
                tower,
                lease,
                command,
                out var handled,
                out var result);
            Check("tower crystal uses DELETE_ITEM count instead of active-use single decrement",
                success
                    && handled
                    && result.Success
                    && result.Mutations.Count == 1
                    && result.Mutations[0].AppliedCount == 3
                    && TowerItemMatches(tower, 121, TowerColorlessCubeItemId, 2),
                ref failures);
        }

        private static void TestFailedDeleteHasNoPersistentSideEffect(ref int failures)
        {
            var service = new DeathTowerTransientInventoryService();
            var lease = CreateLease(990107);
            var persistentSlot = SetPersistentColorlessCubeCount(lease, 1);
            lease.Inventory.ClearDirtyState();
            var tower = CreateTowerWithTransient(
                121,
                TowerColorlessCubeItemId,
                4,
                isWaste: false);
            var command = CreateDeleteCommand(
                new DeathTowerDeleteItemEntry(
                    2,
                    121,
                    TowerColorlessCubeItemId,
                    2),
                new DeathTowerDeleteItemEntry(
                    2,
                    persistentSlot,
                    PersistentColorlessCubeItemId,
                    1));

            var success = service.TryDeleteSkillMaterials(
                tower,
                lease,
                command,
                out var handled,
                out var result);
            Check("persistent virtual crystal is never mixed into tower deletion",
                !success
                    && handled
                    && TowerItemMatches(tower, 121, TowerColorlessCubeItemId, 4)
                    && lease.Inventory.GetMainVirtualCount(persistentSlot)?.Count == 1
                    && lease.Inventory.DirtyMainVirtualCountSlots.Count == 0,
                ref failures);
        }

        private static void TestTransientNeverEntersPersistentInventory(ref int failures)
        {
            var lease = CreateLease(990111);
            var tower = CreateTowerWithTransient(
                121,
                TowerColorlessCubeItemId,
                2,
                isWaste: false);

            Check("tower overlay stays outside ItemCore persistence state",
                TowerItemMatches(tower, 121, TowerColorlessCubeItemId, 2)
                    && lease.Inventory.GetItem(InventoryListType.Main, 121) == null
                    && lease.Inventory.GetItems(InventoryListType.Main).Count == 0
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestProjectionDoesNotLeakPersistentItems(ref int failures)
        {
            var lease = CreateLease(990112);
            lease.Inventory.SetItem(
                InventoryListType.Main,
                121,
                CreateStackable(ItemCore.KindMaterial, PersistentMaterialItemId, 1));
            lease.Inventory.SetItem(
                InventoryListType.Main,
                3,
                CreateStackable(ItemCore.KindConsumable, PersistentConsumableItemId, 1));
            lease.Inventory.ClearDirtyState();
            var tower = CreateTower();

            var projection = DeathTowerInventoryProjectionBuilder.BuildFullListBody(
                tower,
                lease.Inventory);
            Check("tower full projection restores physical Main items without duplicate slots",
                HasFullProjectedItem(
                    projection,
                    3,
                    PersistentConsumableItemId,
                    1)
                    && HasFullProjectedItem(
                        projection,
                        121,
                        PersistentMaterialItemId,
                        1)
                    && HasNoDuplicateFullProjectedSlots(projection)
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static void TestFullListProjectionMergesSharedTowerContainer(
            ref int failures)
        {
            var lease = CreateLease(990122);
            lease.Inventory.SetItem(
                InventoryListType.Main,
                3,
                CreateStackable(
                    ItemCore.KindConsumable,
                    PersistentConsumableItemId,
                    1));
            lease.Inventory.SetItem(
                InventoryListType.Main,
                121,
                CreateStackable(
                    ItemCore.KindMaterial,
                    PersistentMaterialItemId,
                    1));
            lease.Inventory.SetItem(
                InventoryListType.Main,
                200,
                CreateStackable(
                    ItemCore.KindMaterial,
                    PersistentMaterialItemId,
                    2));
            lease.Inventory.ClearDirtyState();

            var tower = CreateTower();
            tower.ReplaceInventoryItems(
                new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>
                {
                    [new DeathTowerInventoryEndpoint(
                        InventoryListType.QuickSlot,
                        3)] = CreateTowerItem(
                            TowerHastePotionItemId,
                            1,
                            isWaste: true),
                    [new DeathTowerInventoryEndpoint(
                        InventoryListType.Main,
                        121)] = CreateTowerItem(
                            TowerColorlessCubeItemId,
                            2,
                            isWaste: false),
                    [new DeathTowerInventoryEndpoint(
                        InventoryListType.Avatar,
                        10)] = CreateTowerItem(
                            PersistentMaterialItemId,
                            1,
                            isWaste: false),
                });

            var body = DeathTowerInventoryProjectionBuilder.BuildFullListBody(
                tower,
                lease.Inventory);
            Check("tower full snapshot merges transient Main/QuickSlot over the entity view",
                body != null
                    && body.Length >= 5
                    && body[0] == (byte)InventoryListType.Main
                    && HasFullProjectedItem(
                        body,
                        3,
                        TowerHastePotionItemId,
                        1)
                    && HasFullProjectedItem(
                        body,
                        121,
                        TowerColorlessCubeItemId,
                        2)
                    && HasFullProjectedItem(
                        body,
                        200,
                        PersistentMaterialItemId,
                        2)
                    && !HasFullProjectedItem(
                        body,
                        3,
                        PersistentConsumableItemId,
                        1)
                    && !HasFullProjectedItem(
                        body,
                        121,
                        PersistentMaterialItemId,
                        1)
                    && !HasFullProjectedSlot(body, 10)
                    && HasNoDuplicateFullProjectedSlots(body)
                    && lease.Inventory.DirtyListTypes.Count == 0,
                ref failures);

            var materialTower = CreateTowerWithGroundItem(
                TowerColorlessCubeItemId,
                2);
            var materialLease = CreateLease(990123);
            var picked = new DeathTowerTransientInventoryService().TryPickup(
                materialTower,
                materialLease,
                51,
                out var pickup);
            var pickupBody = DeathTowerInventoryProjectionBuilder.BuildFullListBody(
                materialTower,
                materialLease.Inventory);
            var moved = new DeathTowerTransientInventoryService().TryMove(
                materialTower,
                materialLease,
                new InventoryMoveRequest
                {
                    SourceListType = InventoryListType.Main,
                    SourceSlotIndex = pickup?.DestinationEndpoint.SlotIndex ?? -1,
                    SourceInstanceValue = 0,
                    MoveCount = 0,
                    DestinationListType = InventoryListType.QuickSlot,
                    DestinationSlotIndex = 4,
                    DestinationInstanceValue = 0,
                },
                0,
                0,
                out var handled,
                out var moveResult);
            var movedBody = DeathTowerInventoryProjectionBuilder.BuildFullListBody(
                materialTower,
                materialLease.Inventory);
            Check("6515 full snapshots keep Main-first allocation and shared QuickSlot coordinates",
                picked
                    && pickup.DestinationEndpoint.Equals(
                        new DeathTowerInventoryEndpoint(
                            InventoryListType.Main,
                            121))
                    && HasFullProjectedItem(
                        pickupBody,
                        121,
                        TowerColorlessCubeItemId,
                        2)
                    && moved
                    && handled
                    && moveResult != null
                    && moveResult.ChangedEndpoints.Count > 0
                    && HasFullProjectedItem(
                        movedBody,
                        4,
                        TowerColorlessCubeItemId,
                        2)
                    && !HasFullProjectedSlot(movedBody, 121)
                    && materialLease.Inventory.DirtyListTypes.Count == 0,
                ref failures);
        }

        private static InventoryLease CreateLease(int characterId)
        {
            var inventory = new InventoryService(characterId, 1);
            inventory.ClearDirtyState();
            return new InventoryLease(Guid.NewGuid(), characterId, inventory, 1);
        }

        private static short SetPersistentColorlessCubeCount(
            InventoryLease lease,
            int count)
        {
            if (!InventoryService.TryResolveMainVirtualSlotByItemId(
                    PersistentColorlessCubeItemId,
                    out var slot,
                    out var itemId)
                || !lease.Inventory.SetMainVirtualCount(slot, itemId, count))
            {
                throw new InvalidOperationException(
                    "persistent colorless cube virtual slot is unavailable");
            }
            return slot;
        }

        private static DeathTowerSession CreateTower(
            DeathTowerRewardProfile rewardProfile = DeathTowerRewardProfile.Standard)
        {
            return new DeathTowerSession(
                DeathTowerSelfTestFactory.CreateConfig(
                    11000,
                    new[] { 1 },
                    50,
                    rewardProfile: rewardProfile));
        }

        private static DeathTowerSession CreateTowerWithTransient(
            short slot,
            int itemId,
            int count,
            bool isWaste,
            DeathTowerRewardProfile rewardProfile = DeathTowerRewardProfile.Standard)
        {
            var tower = CreateTower(rewardProfile);
            tower.ReplaceInventoryItems(
                new Dictionary<short, TowerInventoryItem>
                {
                    [slot] = new TowerInventoryItem
                    {
                        ItemId = itemId,
                        Count = count,
                        StackLimit = 1000,
                        IsQuickSlotConsumable = isWaste
                            || DeathTowerItemSlotPolicy.IsQuickSlotConsumable(
                                ItemMetadataResolver.Resolve(itemId)),
                        IsWaste = isWaste,
                    },
                });
            return tower;
        }

        private static TowerInventoryItem CreateTowerItem(
            int itemId,
            int count,
            bool isWaste)
        {
            var metadata = ItemMetadataResolver.Resolve(itemId);
            return new TowerInventoryItem
            {
                ItemId = itemId,
                Count = count,
                StackLimit = 1000,
                IsQuickSlotConsumable = isWaste
                    || DeathTowerItemSlotPolicy.IsQuickSlotConsumable(metadata),
                IsWaste = isWaste,
            };
        }

        private static DeathTowerSession CreateTowerWithGroundItem(
            int itemId,
            int count)
        {
            var tower = CreateTower();
            tower.BeginStage(
                0x12345678,
                new[]
                {
                    new StageTowerItem
                    {
                        SourceListIndex = 1,
                        SourceMonsterUniqueId = 41,
                        ItemUniqueId = 51,
                        ItemId = itemId,
                        DropRate = 10000,
                        StackCount = count,
                    },
                });
            tower.GenerateDropsForMonster(41);
            return tower;
        }

        private static InventoryMoveRequest CreateMove(
            short source,
            short destination,
            int count)
        {
            return CreateMove(
                InventoryListType.QuickSlot,
                source,
                InventoryListType.QuickSlot,
                destination,
                count);
        }

        private static InventoryMoveRequest CreateMove(
            InventoryListType sourceListType,
            short source,
            InventoryListType destinationListType,
            short destination,
            int count)
        {
            return new InventoryMoveRequest
            {
                SourceListType = sourceListType,
                SourceSlotIndex = source,
                SourceInstanceValue = count,
                MoveCount = count,
                DestinationListType = destinationListType,
                DestinationSlotIndex = destination,
                DestinationInstanceValue = 0,
            };
        }

        private static DeathTowerDeleteItemCommand CreateDeleteCommand(
            params DeathTowerDeleteItemEntry[] entries)
        {
            return new DeathTowerDeleteItemCommand(
                InventoryListType.Main,
                entries);
        }

        private static ItemCore CreateStackable(
            byte kind,
            int itemId,
            int count)
        {
            var core = ItemCore.Create(kind, itemId);
            core.Count = count;
            return core;
        }

        private static bool TowerItemMatches(
            DeathTowerSession tower,
            short slot,
            int itemId,
            int count)
        {
            return tower.TryGetInventoryItem(slot, out var item)
                && item.ItemId == itemId
                && item.Count == count;
        }

        private static bool TowerItemMatches(
            DeathTowerSession tower,
            InventoryListType listType,
            short slot,
            int itemId,
            int count)
        {
            return tower.TryGetInventoryItem(
                    new DeathTowerInventoryEndpoint(listType, slot),
                    out var item)
                && item.ItemId == itemId
                && item.Count == count;
        }

        private static string DescribePersistent(
            InventoryService inventory,
            short slot)
        {
            var item = inventory.GetItem(InventoryListType.Main, slot);
            return item == null ? "empty" : $"{item.ItemId}:{item.Count}:{item.ItemKind}";
        }

        private static string DescribeTransient(
            DeathTowerSession tower,
            short slot)
        {
            return tower.TryGetInventoryItem(slot, out var item)
                ? $"{item.ItemId}:{item.Count}"
                : "empty";
        }

        private static bool HasFullProjectedItem(
            byte[] body,
            short wantedSlot,
            int wantedItemId,
            int wantedCount)
        {
            if (body == null || body.Length < 5
                || body[0] != (byte)InventoryListType.Main)
                return false;

            var count = BitConverter.ToUInt16(body, 3);
            if (body.Length < 5 + count * 84)
                return false;

            for (var index = 0; index < count; index++)
            {
                var offset = 5 + index * 84;
                if (BitConverter.ToInt16(body, offset) == wantedSlot
                    && BitConverter.ToInt32(body, offset + 2) == wantedItemId
                    && BitConverter.ToInt32(body, offset + 6) == wantedCount)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasFullProjectedSlot(byte[] body, short wantedSlot)
        {
            if (body == null || body.Length < 5
                || body[0] != (byte)InventoryListType.Main)
                return false;

            var count = BitConverter.ToUInt16(body, 3);
            if (body.Length < 5 + count * 84)
                return false;

            for (var index = 0; index < count; index++)
            {
                if (BitConverter.ToInt16(body, 5 + index * 84) == wantedSlot)
                    return true;
            }
            return false;
        }

        private static bool HasNoDuplicateFullProjectedSlots(byte[] body)
        {
            if (body == null || body.Length < 5
                || body[0] != (byte)InventoryListType.Main)
                return false;

            var count = BitConverter.ToUInt16(body, 3);
            if (body.Length < 5 + count * 84)
                return false;

            var slots = new HashSet<short>();
            for (var index = 0; index < count; index++)
            {
                if (!slots.Add(BitConverter.ToInt16(body, 5 + index * 84)))
                    return false;
            }
            return true;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using DfoServer.Game.DeathTower;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;

namespace DfoServer.SelfTests
{
    public static class DeathTowerProtocolSelfTest
    {
        private const int TowerHastePotionItemId = 6518;
        private const int TowerColorlessCubeItemId = 6515;
        private const int TowerWasteItemId = 6521;
        private const int TowerWasteItemId2 = 6524;
        private const int PersistentMaterialItemId = 3200;
        private const int PersistentConsumableItemId = 700001;
        private const byte QuickSlotListType = 0x1D;

        public static int Run()
        {
            Console.WriteLine("=== DEATH_TOWER_PROTOCOL selftest ===");
            var failures = 0;

            using (var materialFixture = ProtocolFixture.Create(TowerColorlessCubeItemId))
            {
                var occupiedQuickSlots = new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>();
                for (short slot = 3; slot <= 8; slot++)
                {
                    occupiedQuickSlots[
                        new DeathTowerInventoryEndpoint(
                            InventoryListType.QuickSlot,
                            slot)] = CreateTowerItem(
                                slot % 2 == 0
                                    ? TowerWasteItemId
                                    : TowerWasteItemId2,
                                1,
                                true);
                }
                materialFixture.Tower.ReplaceInventoryItems(occupiedQuickSlots);
                var handler = new DeathTowerCoordinator();
                var handledPickup = handler.TryHandleGetItem(materialFixture.Session, 51)
                    .GetAwaiter().GetResult();
                var pickup = materialFixture.ReadPacket();
                var hasPickupUpdate = materialFixture.TryReadPacket(out var pickupUpdate);
                Check("tower material pickup sends slot 121 ACK and one silent full 0x000D snapshot",
                    handledPickup
                        && pickup.Command == 0x00
                        && pickup.Type == 0x0027
                        && pickup.Body.Length >= 17
                        && BitConverter.ToUInt16(pickup.Body, 14) == 121
                        && hasPickupUpdate
                        && pickupUpdate.Command == 0x00
                         && pickupUpdate.Type == 0x000D
                        && HasCommonUpdate(pickupUpdate.Body, 0, 121, TowerColorlessCubeItemId, 2),
                    ref failures);

                var handledDelete = handler.TryHandleDeleteItem(
                    materialFixture.Session,
                    new GamePacketHeader(),
                    BuildDeleteBody(
                        2,
                        121,
                        TowerColorlessCubeItemId,
                        1)).GetAwaiter().GetResult();
                var deleteAck = materialFixture.ReadPacket();
                var deleteUpdate = materialFixture.ReadPacket();
                Check("tower skill material delete echoes applied count and one full remainder snapshot",
                    handledDelete
                        && deleteAck.Command == 0x01
                        && deleteAck.Type == 0x0012
                        && deleteAck.Body.Length >= 9
                        && deleteAck.Body[0] == 1
                        && BitConverter.ToInt16(deleteAck.Body, 3) == 121
                        && BitConverter.ToInt32(deleteAck.Body, 5) == 1
                        && deleteUpdate.Command == 0x00
                        && deleteUpdate.Type == 0x000D
                        && HasCommonUpdate(
                            deleteUpdate.Body,
                            0,
                            121,
                            TowerColorlessCubeItemId,
                            1),
                    ref failures);

                var preparedSort = materialFixture.Tower.TryMoveItem(
                    121,
                    122,
                    0,
                    out _);
                materialFixture.Inventory.SetItem(
                    InventoryListType.Main,
                    123,
                    CreateStackable(
                        ItemCore.KindMaterial,
                        PersistentMaterialItemId,
                        1));
                materialFixture.Inventory.ClearDirtyState();
                var handledSort = handler.TryHandleSortItem(
                    materialFixture.Session,
                    new GamePacketHeader(),
                    new[] { (byte)InventoryListType.Main, ItemCore.KindMaterial })
                    .GetAwaiter().GetResult();
                var sortAck = materialFixture.ReadPacket();
                var sortSnapshot = materialFixture.ReadPacket();
                var hasUnexpectedSortPacket = materialFixture.TryReadPacket(out _);
                Check("tower sort reorders transient items and sends one full snapshot",
                    preparedSort
                        && handledSort
                        && sortAck.Command == 0x01
                        && sortAck.Type == 0x0014
                        && sortAck.Body.Length == 2
                        && sortAck.Body[0] == 1
                         && sortSnapshot.Type == 0x000D
                         && HasCommonUpdate(
                             sortSnapshot.Body,
                             0,
                             121,
                             TowerColorlessCubeItemId,
                             1)
                         && materialFixture.Inventory.GetItem(
                            InventoryListType.Main,
                            123)?.ItemId == PersistentMaterialItemId
                        && TowerItemMatches(
                            materialFixture.Tower,
                            InventoryListType.Main,
                            121,
                            TowerColorlessCubeItemId,
                            1)
                        && materialFixture.Inventory.DirtyListTypes.Count == 0
                        && !hasUnexpectedSortPacket,
                    ref failures);
            }

            using (var sortShapeFixture = ProtocolFixture.Create())
            {
                sortShapeFixture.Tower.ReplaceInventoryItems(
                    new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>
                    {
                        [new DeathTowerInventoryEndpoint(
                            InventoryListType.Main,
                            121)] = CreateTowerItem(
                                TowerColorlessCubeItemId,
                                1,
                                false),
                        [new DeathTowerInventoryEndpoint(
                            InventoryListType.Main,
                            122)] = CreateTowerItem(
                                PersistentMaterialItemId,
                                1,
                                false),
                    });
                var handler = new DeathTowerCoordinator();
                var oneByteHandled = handler.TryHandleSortItem(
                    sortShapeFixture.Session,
                    new GamePacketHeader(),
                    new[] { (byte)InventoryListType.Main })
                    .GetAwaiter().GetResult();
                var oneByteAck = sortShapeFixture.ReadPacket();
                var oneByteSnapshot = sortShapeFixture.ReadPacket();
                var oneByteExtra = sortShapeFixture.TryReadPacket(out _);

                var threeByteHandled = handler.TryHandleSortItem(
                    sortShapeFixture.Session,
                    new GamePacketHeader(),
                    new[]
                    {
                        (byte)InventoryListType.Main,
                        ItemCore.KindMaterial,
                        (byte)0x03,
                    })
                    .GetAwaiter().GetResult();
                var threeByteAck = sortShapeFixture.ReadPacket();
                var threeByteSnapshot = sortShapeFixture.ReadPacket();
                var threeByteExtra = sortShapeFixture.TryReadPacket(out _);

                Check("tower sort accepts one-byte and three-byte local command shapes with full refresh",
                    oneByteHandled
                        && oneByteAck.Type == 0x0014
                         && oneByteSnapshot.Type == 0x000D
                         && !oneByteExtra
                        && threeByteHandled
                        && threeByteAck.Type == 0x0014
                         && threeByteSnapshot.Type == 0x000D
                         && !threeByteExtra,
                    ref failures);
            }

            using (var materialQuickFixture = ProtocolFixture.Create(
                TowerColorlessCubeItemId))
            {
                var handler = new DeathTowerCoordinator();
                var handledPickup = handler.TryHandleGetItem(
                    materialQuickFixture.Session,
                    51).GetAwaiter().GetResult();
                var pickup = materialQuickFixture.ReadPacket();
                var pickupUpdate = materialQuickFixture.ReadPacket();

                var handledToQuick = handler.TryHandleMoveItem(
                    materialQuickFixture.Session,
                    new GamePacketHeader(),
                    BuildMoveBody24(
                        InventoryListType.QuickSlot,
                        4,
                        0,
                        0,
                        InventoryListType.Main,
                        121,
                        TowerColorlessCubeItemId,
                        0)).GetAwaiter().GetResult();
                var toQuickAck = materialQuickFixture.ReadPacket();
                var toQuickSnapshot = materialQuickFixture.ReadPacket();
                var hasUnexpectedToQuick = materialQuickFixture.TryReadPacket(out _);
                var toQuickStateOk = TowerItemMatches(
                    materialQuickFixture.Tower,
                    InventoryListType.QuickSlot,
                    4,
                    TowerColorlessCubeItemId,
                    2);

                var handledToMain = handler.TryHandleMoveItem(
                    materialQuickFixture.Session,
                    new GamePacketHeader(),
                    BuildMoveBody24(
                        InventoryListType.Main,
                        121,
                        0,
                        0,
                        InventoryListType.QuickSlot,
                        4,
                        TowerColorlessCubeItemId,
                        0)).GetAwaiter().GetResult();
                var toMainAck = materialQuickFixture.ReadPacket();
                var toMainSnapshot = materialQuickFixture.ReadPacket();
                var hasUnexpectedToMain = materialQuickFixture.TryReadPacket(out _);

                var pickupShapeOk = handledPickup
                    && pickup.Type == 0x0027
                    && pickup.Body.Length >= 17
                    && BitConverter.ToUInt16(pickup.Body, 14) == 121;
                var pickupUpdateOk = pickupUpdate.Type == 0x000D
                    && HasCommonUpdate(
                        pickupUpdate.Body,
                        (byte)InventoryListType.Main,
                        121,
                        TowerColorlessCubeItemId,
                        2);
                var toQuickAckOk = handledToQuick
                    && IsCanonicalTowerMoveAck(toQuickAck, 4, 121);
                var toQuickSnapshotOk = toQuickSnapshot.Type == 0x000D
                    && HasCommonUpdate(
                        toQuickSnapshot.Body,
                        0,
                        4,
                        TowerColorlessCubeItemId,
                        2);
                var toMainAckOk = handledToMain
                    && IsCanonicalTowerMoveAck(toMainAck, 121, 4);
                var toMainSnapshotOk = toMainSnapshot.Type == 0x000D
                    && HasCommonUpdate(
                        toMainSnapshot.Body,
                        0,
                        121,
                        TowerColorlessCubeItemId,
                        2);
                var toMainStateOk = TowerItemMatches(
                    materialQuickFixture.Tower,
                    InventoryListType.Main,
                    121,
                    TowerColorlessCubeItemId,
                    2);
                var materialRoundTripOk = pickupShapeOk
                    && pickupUpdateOk
                    && toQuickAckOk
                    && toQuickStateOk
                    && toQuickSnapshotOk
                    && toMainAckOk
                    && toMainStateOk
                    && toMainSnapshotOk
                    && materialQuickFixture.Tower.InventoryItems.Count == 1
                    && materialQuickFixture.Inventory.DirtyListTypes.Count == 0
                    && !hasUnexpectedToQuick
                    && !hasUnexpectedToMain;
                Check("6515 defaults to Main and supports protocol QuickSlot/Main round-trip",
                    materialRoundTripOk,
                    ref failures);
            }

            using (var fixture = ProtocolFixture.Create())
            {
                var handler = new DeathTowerCoordinator();

                var handledPickup = handler.TryHandleGetItem(fixture.Session, 51)
                    .GetAwaiter().GetResult();
                var pickup = fixture.ReadPacket();
                var hasPickupUpdate = fixture.TryReadPacket(out var pickupUpdate);
                Check("tower pickup routes to 0x0027 with tower slot 3 and one full snapshot",
                    handledPickup
                        && pickup.Command == 0x00
                        && pickup.Type == 0x0027
                        && pickup.Body.Length >= 17
                        && BitConverter.ToUInt16(pickup.Body, 0) == 51
                        && BitConverter.ToUInt16(pickup.Body, 14) == 3
                        && hasPickupUpdate
                         && pickupUpdate.Type == 0x000D
                        && HasCommonUpdate(pickupUpdate.Body, QuickSlotListType, 3, TowerHastePotionItemId, 2),
                    ref failures);

                var handledUse = handler.TryHandleUseStackable(
                    fixture.Session,
                    new GamePacketHeader(),
                    BuildUseBody(3, TowerHastePotionItemId, 0x127))
                    .GetAwaiter().GetResult();
                CapturedPacket useAck = null;
                CapturedPacket useUpdate = null;
                var hasUseAck = handledUse && fixture.TryReadPacket(out useAck);
                var hasUseUpdate = hasUseAck && fixture.TryReadPacket(out useUpdate);
                Check("captured list 0x1D tower use sends echoed success ACK before full snapshot",
                    handledUse
                        && hasUseAck
                        && useAck.Command == 0x01
                        && useAck.Type == 0x002C
                        && useAck.Body.Length >= 4
                        && useAck.Body[0] == 1
                        && useAck.Body[3] == QuickSlotListType
                        && hasUseUpdate
                        && useUpdate.Command == 0x00
                         && useUpdate.Type == 0x000D
                        && HasCommonUpdate(useUpdate.Body, QuickSlotListType, 3, TowerHastePotionItemId, 1),
                    ref failures);

                var handledMove = handler.TryHandleMoveItem(
                    fixture.Session,
                    new GamePacketHeader(),
                    BuildMoveBody(3, 4, 1))
                    .GetAwaiter().GetResult();
                var moveAck = fixture.ReadPacket();
                var moveSnapshot = fixture.ReadPacket();
                var hasUnexpectedMoveUpdate = fixture.TryReadPacket(out _);
                Check("tower move uses a canonical ACK and one full snapshot",
                    handledMove
                        && IsCanonicalTowerMoveAck(moveAck, 3, 4)
                         && moveSnapshot.Type == 0x000D
                         && HasCommonUpdate(
                             moveSnapshot.Body,
                             0,
                             4,
                             TowerHastePotionItemId,
                             1)
                         && TowerItemMatches(
                            fixture.Tower,
                            InventoryListType.QuickSlot,
                            4,
                            TowerHastePotionItemId,
                            1)
                        && !hasUnexpectedMoveUpdate,
                    ref failures);

                var handledInvalidUse = handler.TryHandleUseStackable(
                    fixture.Session,
                    new GamePacketHeader(),
                    BuildUseBody(4, 6515, 1))
                    .GetAwaiter().GetResult();
                var invalidAck = fixture.ReadPacket();
                var invalidUpdate = fixture.ReadPacket();
                Check("invalid tower use returns error and authoritative refresh",
                    handledInvalidUse
                        && invalidAck.Command == 0x01
                        && invalidAck.Type == 0x002C
                        && invalidAck.Body.Length >= 1
                        && invalidAck.Body[0] == 0
                        && invalidUpdate.Command == 0x00
                         && invalidUpdate.Type == 0x000D
                        && HasCommonUpdate(
                            invalidUpdate.Body,
                            QuickSlotListType,
                            4,
                            TowerHastePotionItemId,
                            1)
                        && GetTowerItemCount(fixture.Tower, TowerHastePotionItemId) == 1,
                    ref failures);

                var handledSevenByteUse = handler.TryHandleUseStackable(
                    fixture.Session,
                    new GamePacketHeader(),
                    BuildUseBodyWithoutItemId(4, 1))
                    .GetAwaiter().GetResult();
                var sevenByteAck = fixture.ReadPacket();
                var sevenByteUpdate = fixture.ReadPacket();
                Check("7-byte 0x002C derives the authoritative tower item from its slot",
                    handledSevenByteUse
                        && sevenByteAck.Command == 0x01
                        && sevenByteAck.Type == 0x002C
                        && sevenByteAck.Body[0] == 1
                         && sevenByteUpdate.Type == 0x000D
                        && HasCommonUpdate(sevenByteUpdate.Body, QuickSlotListType, 4, -1, 0)
                        && GetTowerItemCount(fixture.Tower, TowerHastePotionItemId) == 0,
                    ref failures);
            }

            using (var routingFixture = ProtocolFixture.Create())
            {
                var handler = new DeathTowerCoordinator();
                var petBody = BuildUseBody(3, TowerHastePotionItemId, 1);
                petBody[2] = 1;
                var handledPetList = handler.TryHandleUseStackable(
                    routingFixture.Session,
                    new GamePacketHeader(),
                    petBody).GetAwaiter().GetResult();
                Check("tower 0x002C leaves non-main inventory lists to later handlers",
                    !handledPetList
                        && GetTowerItemCount(routingFixture.Tower, TowerHastePotionItemId) == 0
                        && routingFixture.Tower.GroundItems.Count == 1,
                    ref failures);

                var petMoveBody = BuildMoveBody(3, 4, 1);
                petMoveBody[0] = 1;
                petMoveBody[11] = 1;
                var handledPetMove = handler.TryHandleMoveItem(
                    routingFixture.Session,
                    new GamePacketHeader(),
                    petMoveBody).GetAwaiter().GetResult();
                Check("tower 0x0013 leaves non-main inventory lists to later handlers",
                    !handledPetMove
                        && routingFixture.Tower.GroundItems.Count == 1,
                    ref failures);

                routingFixture.Inventory.SetItem(
                    InventoryListType.Main,
                    68,
                    CreateStackable(
                        ItemCore.KindConsumable,
                        PersistentConsumableItemId,
                        1));
                routingFixture.Inventory.ClearDirtyState();
                var handledPersistentMove = handler.TryHandleMoveItem(
                    routingFixture.Session,
                    new GamePacketHeader(),
                    BuildMoveBody(
                        InventoryListType.Main,
                        68,
                        InventoryListType.Main,
                        67,
                        0)).GetAwaiter().GetResult();
                var hasPersistentMovePacket = routingFixture.TryReadPacket(out _);
                Check("tower 0x0013 leaves persistent-only moves to the inventory handler",
                    !handledPersistentMove
                        && !hasPersistentMovePacket
                        && routingFixture.Inventory.GetItem(
                            InventoryListType.Main,
                            68)?.ItemId == PersistentConsumableItemId
                        && routingFixture.Inventory.GetItem(
                            InventoryListType.Main,
                            67) == null
                        && routingFixture.Inventory.DirtyListTypes.Count == 0,
                    ref failures);
            }

            using (var crossListFixture = ProtocolFixture.Create())
            {
                var handler = new DeathTowerCoordinator();
                var handledPickup = handler.TryHandleGetItem(
                    crossListFixture.Session,
                    51).GetAwaiter().GetResult();
                crossListFixture.ReadPacket();
                crossListFixture.ReadPacket();
                crossListFixture.Inventory.SetItem(
                    InventoryListType.Main,
                    3,
                    CreateStackable(
                        ItemCore.KindConsumable,
                        PersistentConsumableItemId,
                        1));
                crossListFixture.Inventory.ClearDirtyState();

                var handledQuickToMain = handler.TryHandleMoveItem(
                    crossListFixture.Session,
                    new GamePacketHeader(),
                    BuildMoveBody(
                        InventoryListType.QuickSlot,
                        3,
                        InventoryListType.Main,
                        76,
                        0)).GetAwaiter().GetResult();
                var quickToMainAck = crossListFixture.ReadPacket();
                var quickToMainSnapshot = crossListFixture.ReadPacket();
                var hasUnexpectedQuickToMainPacket =
                    crossListFixture.TryReadPacket(out _);
                Check("cross-list tower move canonicalizes only the wire ACK",
                    handledPickup
                        && handledQuickToMain
                        && IsCanonicalTowerMoveAck(quickToMainAck, 3, 76)
                        && quickToMainSnapshot.Type == 0x000D
                        && HasCommonUpdate(
                            quickToMainSnapshot.Body,
                            0,
                            76,
                            TowerHastePotionItemId,
                            2)
                        && crossListFixture.Inventory.GetItem(
                            InventoryListType.Main,
                            3)?.ItemId == PersistentConsumableItemId
                        && TowerItemMatches(
                            crossListFixture.Tower,
                            InventoryListType.Main,
                            76,
                            TowerHastePotionItemId,
                            2)
                        && GetTowerItemCount(
                            crossListFixture.Tower,
                            TowerHastePotionItemId) == 2
                        && crossListFixture.Inventory.DirtyListTypes.Count == 0
                        && !hasUnexpectedQuickToMainPacket,
                    ref failures);

                var handledMainToQuick = handler.TryHandleMoveItem(
                    crossListFixture.Session,
                    new GamePacketHeader(),
                    BuildMoveBody(
                        InventoryListType.Main,
                        76,
                        InventoryListType.QuickSlot,
                        3,
                        0)).GetAwaiter().GetResult();
                var mainToQuickAck = crossListFixture.ReadPacket();
                var mainToQuickSnapshot = crossListFixture.ReadPacket();
                var hasUnexpectedMainToQuickPacket =
                    crossListFixture.TryReadPacket(out _);
                Check("cross-list tower move supports the reverse direction over an entity slot",
                    handledMainToQuick
                        && IsCanonicalTowerMoveAck(mainToQuickAck, 76, 3)
                        && mainToQuickSnapshot.Type == 0x000D
                        && HasCommonUpdate(
                            mainToQuickSnapshot.Body,
                            0,
                            3,
                            TowerHastePotionItemId,
                            2)
                        && crossListFixture.Inventory.GetItem(
                            InventoryListType.Main,
                            3)?.ItemId == PersistentConsumableItemId
                        && TowerItemMatches(
                            crossListFixture.Tower,
                            InventoryListType.QuickSlot,
                            3,
                            TowerHastePotionItemId,
                            2)
                        && crossListFixture.Inventory.DirtyListTypes.Count == 0
                        && !hasUnexpectedMainToQuickPacket,
                    ref failures);

                var handledEmptyTargetMove = handler.TryHandleMoveItem(
                    crossListFixture.Session,
                    new GamePacketHeader(),
                    BuildMoveBody(
                        InventoryListType.QuickSlot,
                        3,
                        InventoryListType.Main,
                        77,
                        0)).GetAwaiter().GetResult();
                var emptyTargetAck = crossListFixture.ReadPacket();
                var emptyTargetSnapshot = crossListFixture.ReadPacket();
                var hasUnexpectedEmptyTargetPacket =
                    crossListFixture.TryReadPacket(out _);
                Check("cross-list tower move still accepts an empty target",
                    handledEmptyTargetMove
                        && IsCanonicalTowerMoveAck(emptyTargetAck, 3, 77)
                        && emptyTargetSnapshot.Type == 0x000D
                        && HasCommonUpdate(
                            emptyTargetSnapshot.Body,
                            0,
                            77,
                            TowerHastePotionItemId,
                            2)
                        && crossListFixture.Inventory.GetItem(
                            InventoryListType.Main,
                            3)?.ItemId == PersistentConsumableItemId
                        && TowerItemMatches(
                            crossListFixture.Tower,
                            InventoryListType.Main,
                            77,
                            TowerHastePotionItemId,
                            2)
                        && crossListFixture.Inventory.DirtyListTypes.Count == 0
                        && !hasUnexpectedEmptyTargetPacket,
                    ref failures);
            }

            using (var sameStackFixture = ProtocolFixture.Create())
            {
                sameStackFixture.Tower.ReplaceInventoryItems(
                    new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>
                    {
                        [new DeathTowerInventoryEndpoint(
                            InventoryListType.QuickSlot,
                            7)] = CreateTowerItem(
                                TowerColorlessCubeItemId,
                                1,
                                false),
                        [new DeathTowerInventoryEndpoint(
                            InventoryListType.Main,
                            137)] = CreateTowerItem(
                                TowerColorlessCubeItemId,
                                2,
                                false),
                    });
                var handler = new DeathTowerCoordinator();
                var handled = handler.TryHandleMoveItem(
                    sameStackFixture.Session,
                    new GamePacketHeader(),
                    BuildMoveBody24(
                        InventoryListType.QuickSlot,
                        7,
                        0,
                        0,
                        InventoryListType.Main,
                        137,
                        TowerColorlessCubeItemId,
                        2))
                    .GetAwaiter().GetResult();
                var ack = sameStackFixture.ReadPacket();
                var snapshot = sameStackFixture.ReadPacket();
                var hasUnexpectedUpdate = sameStackFixture.TryReadPacket(out _);
                Check("24-byte reverse move merges identical tower stacks into its drop target",
                    handled
                        && IsCanonicalTowerMoveAck(ack, 7, 137)
                        && snapshot.Type == 0x000D
                        && HasCommonUpdate(
                            snapshot.Body,
                            0,
                            7,
                            TowerColorlessCubeItemId,
                            3)
                        && !HasCommonUpdate(
                            snapshot.Body,
                            0,
                            137,
                            TowerColorlessCubeItemId,
                            2)
                        && TowerItemMatches(
                            sameStackFixture.Tower,
                            InventoryListType.QuickSlot,
                            7,
                            TowerColorlessCubeItemId,
                            3)
                        && sameStackFixture.Tower.InventoryItems.Count == 1
                        && GetTowerItemCount(
                            sameStackFixture.Tower,
                            TowerColorlessCubeItemId) == 3
                        && !hasUnexpectedUpdate
                        && sameStackFixture.Inventory.DirtyListTypes.Count == 0,
                    ref failures);
            }

            using (var staleIdentityFixture = ProtocolFixture.Create())
            {
                staleIdentityFixture.Tower.ReplaceInventoryItems(
                    new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>
                    {
                        [new DeathTowerInventoryEndpoint(
                            InventoryListType.Main,
                            100)] = CreateTowerItem(
                                TowerWasteItemId,
                                1,
                                true),
                    });
                var handler = new DeathTowerCoordinator();
                var handled = handler.TryHandleMoveItem(
                    staleIdentityFixture.Session,
                    new GamePacketHeader(),
                    BuildMoveBody24(
                        InventoryListType.QuickSlot,
                        4,
                        0,
                        0,
                        InventoryListType.QuickSlot,
                        6,
                        TowerWasteItemId,
                        0))
                    .GetAwaiter().GetResult();
                var ack = staleIdentityFixture.ReadPacket();
                var snapshot = staleIdentityFixture.ReadPacket();
                var hasUnexpectedUpdate = staleIdentityFixture.TryReadPacket(out _);
                Check("24-byte tower reverse move restores stale identity with one full snapshot",
                    handled
                        && IsCanonicalTowerMoveAck(ack, 4, 6)
                        && snapshot.Type == 0x000D
                        && HasCommonUpdate(
                            snapshot.Body,
                            0,
                            4,
                            TowerWasteItemId,
                            1)
                        && TowerItemMatches(
                            staleIdentityFixture.Tower,
                            InventoryListType.QuickSlot,
                            4,
                            TowerWasteItemId,
                            1)
                        && !hasUnexpectedUpdate,
                    ref failures);

                var retryHandled = handler.TryHandleMoveItem(
                    staleIdentityFixture.Session,
                    new GamePacketHeader(),
                    BuildMoveBody24(
                        InventoryListType.QuickSlot,
                        4,
                        TowerWasteItemId,
                        0,
                        InventoryListType.Main,
                        76,
                        0,
                        0))
                    .GetAwaiter().GetResult();
                var retryAck = staleIdentityFixture.ReadPacket();
                var retrySnapshot = staleIdentityFixture.ReadPacket();
                var hasUnexpectedRetryUpdate = staleIdentityFixture.TryReadPacket(out _);
                Check("24-byte tower identity recovery leaves the item movable after the first move",
                    retryHandled
                        && IsCanonicalTowerMoveAck(retryAck, 4, 76)
                        && retrySnapshot.Type == 0x000D
                        && HasCommonUpdate(
                            retrySnapshot.Body,
                            0,
                            76,
                            TowerWasteItemId,
                            1)
                        && TowerItemMatches(
                            staleIdentityFixture.Tower,
                            InventoryListType.Main,
                            76,
                            TowerWasteItemId,
                            1)
                        && staleIdentityFixture.Tower.InventoryItems.Count == 1
                        && !hasUnexpectedRetryUpdate
                        && staleIdentityFixture.Inventory.DirtyListTypes.Count == 0,
                    ref failures);
            }

            using (var wireSwapFixture = ProtocolFixture.Create())
            {
                wireSwapFixture.Tower.ReplaceInventoryItems(
                    new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>
                    {
                        [new DeathTowerInventoryEndpoint(
                            InventoryListType.Main,
                            100)] = CreateTowerItem(
                                TowerWasteItemId,
                                1,
                                true),
                        [new DeathTowerInventoryEndpoint(
                            InventoryListType.QuickSlot,
                            7)] = CreateTowerItem(
                                TowerWasteItemId2,
                                1,
                                true),
                    });
                var handler = new DeathTowerCoordinator();
                var handled = handler.TryHandleMoveItem(
                    wireSwapFixture.Session,
                    new GamePacketHeader(),
                    BuildMoveBody24(
                        InventoryListType.QuickSlot,
                        5,
                        TowerWasteItemId,
                        0,
                        InventoryListType.QuickSlot,
                        7,
                        TowerWasteItemId2,
                        0))
                    .GetAwaiter().GetResult();
                var ack = wireSwapFixture.ReadPacket();
                var snapshot = wireSwapFixture.ReadPacket();
                var hasUnexpectedUpdate = wireSwapFixture.TryReadPacket(out _);
                Check("24-byte tower identity exchange keeps both item identities unique",
                    handled
                        && IsCanonicalTowerMoveAck(ack, 5, 7)
                        && snapshot.Type == 0x000D
                        && HasCommonUpdate(
                            snapshot.Body,
                            0,
                            5,
                            TowerWasteItemId2,
                            1)
                        && HasCommonUpdate(
                            snapshot.Body,
                            0,
                            7,
                            TowerWasteItemId,
                            1)
                        && TowerItemMatches(
                            wireSwapFixture.Tower,
                            InventoryListType.QuickSlot,
                            5,
                            TowerWasteItemId2,
                            1)
                        && TowerItemMatches(
                            wireSwapFixture.Tower,
                            InventoryListType.QuickSlot,
                            7,
                            TowerWasteItemId,
                            1)
                        && wireSwapFixture.Tower.InventoryItems.Count == 2
                        && !hasUnexpectedUpdate
                        && wireSwapFixture.Inventory.DirtyListTypes.Count == 0,
                    ref failures);
            }

            using (var rejectedMoveFixture = ProtocolFixture.Create())
            {
                rejectedMoveFixture.Tower.ReplaceInventoryItems(
                    new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>
                    {
                        [new DeathTowerInventoryEndpoint(
                            InventoryListType.Main,
                            121)] = CreateTowerItem(
                                PersistentMaterialItemId,
                                2,
                                false),
                    });
                var handler = new DeathTowerCoordinator();
                var handled = handler.TryHandleMoveItem(
                    rejectedMoveFixture.Session,
                    new GamePacketHeader(),
                    BuildMoveBody24(
                        InventoryListType.QuickSlot,
                        4,
                        0,
                        0,
                        InventoryListType.QuickSlot,
                        6,
                        PersistentMaterialItemId,
                        0))
                    .GetAwaiter().GetResult();
                var errorAck = rejectedMoveFixture.ReadPacket();
                var refresh = rejectedMoveFixture.ReadPacket();
                var hasUnexpectedRefresh = rejectedMoveFixture.TryReadPacket(out _);
                Check("rejected 24-byte tower move sends one authoritative snapshot without loss",
                    handled
                        && errorAck.Command == 0x01
                        && errorAck.Type == 0x0013
                        && errorAck.Body.Length == MoveItemSpaceAckBuilder.ErrorBodyLength
                        && errorAck.Body[0] == 0
                        && refresh.Type == 0x000D
                        && HasCommonUpdate(
                            refresh.Body,
                            0,
                            121,
                            PersistentMaterialItemId,
                            2)
                        && TowerItemMatches(
                            rejectedMoveFixture.Tower,
                            InventoryListType.Main,
                            121,
                            PersistentMaterialItemId,
                            2)
                        && rejectedMoveFixture.Tower.InventoryItems.Count == 1
                        && !hasUnexpectedRefresh
                        && rejectedMoveFixture.Inventory.DirtyListTypes.Count == 0,
                    ref failures);

                var retryHandled = handler.TryHandleMoveItem(
                    rejectedMoveFixture.Session,
                    new GamePacketHeader(),
                    BuildMoveBody24(
                        InventoryListType.Main,
                        121,
                        PersistentMaterialItemId,
                        0,
                        InventoryListType.Main,
                        122,
                        0,
                        0))
                    .GetAwaiter().GetResult();
                var retryAck = rejectedMoveFixture.ReadPacket();
                var retrySnapshot = rejectedMoveFixture.ReadPacket();
                var hasUnexpectedRetryPacket = rejectedMoveFixture.TryReadPacket(out _);
                Check("tower item remains movable after a rejected cross-list request",
                    retryHandled
                        && IsCanonicalTowerMoveAck(retryAck, 121, 122)
                        && retrySnapshot.Type == 0x000D
                        && HasCommonUpdate(
                            retrySnapshot.Body,
                            0,
                            122,
                            PersistentMaterialItemId,
                            2)
                        && TowerItemMatches(
                            rejectedMoveFixture.Tower,
                            InventoryListType.Main,
                            122,
                            PersistentMaterialItemId,
                            2)
                        && rejectedMoveFixture.Tower.InventoryItems.Count == 1
                        && !hasUnexpectedRetryPacket,
                    ref failures);
            }

            using (var databaseScope = TemporaryInventoryDatabaseScope.Create())
            using (var returnFixture = ProtocolFixture.Create(
                TowerHastePotionItemId))
            {
                for (short slot = 3; slot <= 8; slot++)
                {
                    returnFixture.Inventory.SetItem(
                        InventoryListType.Main,
                        slot,
                        CreateStackable(
                            ItemCore.KindConsumable,
                            PersistentConsumableItemId + slot,
                            1));
                }
                returnFixture.Inventory.ClearDirtyState();
                returnFixture.Tower.ReplaceInventoryItems(
                    new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>
                    {
                        [new DeathTowerInventoryEndpoint(
                            InventoryListType.QuickSlot,
                            3)] = CreateTowerItem(TowerHastePotionItemId, 1, true),
                        [new DeathTowerInventoryEndpoint(
                            InventoryListType.QuickSlot,
                            4)] = CreateTowerItem(TowerWasteItemId, 1, true),
                        [new DeathTowerInventoryEndpoint(
                            InventoryListType.QuickSlot,
                            5)] = CreateTowerItem(TowerWasteItemId2, 1, true),
                    });

                var returnIdentity = returnFixture.Session.Player.CurrentRun
                    .CaptureIdentity();
                var ended = DungeonRunLifecycle.EndRunAsync(
                        returnFixture.Session,
                        DungeonRunEndReason.ReturnToTown,
                        returnIdentity)
                    .GetAwaiter()
                    .GetResult();
                var hasRestore = returnFixture.TryReadPacket(out var restore);
                var hasUnexpectedRestore = returnFixture.TryReadPacket(out _);
                var persistentQuickSlotsRestored = hasRestore;
                for (short slot = 3; slot <= 8; slot++)
                {
                    persistentQuickSlotsRestored &= HasCommonUpdate(
                        restore?.Body,
                        (byte)InventoryListType.Main,
                        slot,
                        PersistentConsumableItemId + slot,
                        1);
                }

                Check("tower return sends exactly one persistent full list after detaching its overlay",
                    ended
                        && returnFixture.Session.Player.CurrentRun == null
                        && hasRestore
                        && restore.Command == 0x00
                        && restore.Type == 0x000D
                        && persistentQuickSlotsRestored
                        && !HasCommonUpdate(
                            restore.Body,
                            (byte)InventoryListType.Main,
                            3,
                            TowerHastePotionItemId,
                            1)
                        && !hasUnexpectedRestore,
                    ref failures);
            }

            using (var staleReturnFixture = ProtocolFixture.Create(
                TowerHastePotionItemId))
            {
                var staleIdentity = staleReturnFixture.Session.Player.CurrentRun
                    .CaptureIdentity();
                var replacementTower = new DeathTowerSession(
                    DeathTowerSelfTestFactory.CreateConfig(
                        11000,
                        new[] { 1 },
                        50));
                DungeonRunLifecycle.BeginTowerRun(
                    staleReturnFixture.Session,
                    11000,
                    replacementTower);
                var rejected = DungeonRunLifecycle.TryEndRunToTownAsync(
                        staleReturnFixture.Session,
                        staleIdentity)
                    .GetAwaiter()
                    .GetResult();
                var hasStaleProjection = staleReturnFixture.TryReadPacket(out _);
                Check("stale tower end cannot restore inventory over a replacement run",
                    !rejected
                        && ReferenceEquals(
                            staleReturnFixture.Session.Player.CurrentRun?.Tower,
                            replacementTower)
                        && !hasStaleProjection,
                    ref failures);
            }

            Console.WriteLine(failures == 0 ? "PASS" : $"FAIL: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static byte[] BuildUseBody(short slot, int itemId, int instanceValue)
        {
            var body = new byte[15];
            BitConverter.GetBytes(slot).CopyTo(body, 0);
            body[2] = QuickSlotListType;
            BitConverter.GetBytes(instanceValue).CopyTo(body, 3);
            BitConverter.GetBytes(itemId).CopyTo(body, 7);
            return body;
        }

        private static byte[] BuildDeleteBody(
            ushort operationType,
            short slot,
            int itemId,
            int deleteCount)
        {
            var body = new byte[14];
            body[0] = (byte)InventoryListType.Main;
            body[1] = 1;
            BitConverter.GetBytes(operationType).CopyTo(body, 2);
            BitConverter.GetBytes(slot).CopyTo(body, 4);
            BitConverter.GetBytes(itemId).CopyTo(body, 6);
            BitConverter.GetBytes(deleteCount).CopyTo(body, 10);
            return body;
        }

        private static ItemCore CreateStackable(byte kind, int itemId, int count)
        {
            var item = ItemCore.Create(kind, itemId);
            item.Count = count;
            return item;
        }

        private static int GetTowerItemCount(DeathTowerSession tower, int itemId)
        {
            var snapshot = tower.GetItemCountsSnapshot();
            return snapshot.TryGetValue(itemId, out var count) ? count : 0;
        }

        private static byte[] BuildMoveBody(short source, short destination, int count)
        {
            return BuildMoveBody(
                InventoryListType.QuickSlot,
                source,
                InventoryListType.QuickSlot,
                destination,
                count);
        }

        private static byte[] BuildMoveBody(
            InventoryListType sourceListType,
            short source,
            InventoryListType destinationListType,
            short destination,
            int count)
        {
            var body = new byte[14];
            body[0] = (byte)sourceListType;
            BitConverter.GetBytes(source).CopyTo(body, 1);
            BitConverter.GetBytes(count).CopyTo(body, 3);
            BitConverter.GetBytes(count).CopyTo(body, 7);
            body[11] = (byte)destinationListType;
            BitConverter.GetBytes(destination).CopyTo(body, 12);
            return body;
        }

        private static byte[] BuildMoveBody24(
            InventoryListType sourceListType,
            short sourceSlot,
            int sourceInstanceValue,
            int sourceStackCount,
            InventoryListType destinationListType,
            short destinationSlot,
            int destinationInstanceValue,
            int destinationStackCount)
        {
            var body = new byte[24];
            body[0] = (byte)sourceListType;
            BitConverter.GetBytes(sourceSlot).CopyTo(body, 1);
            BitConverter.GetBytes(sourceInstanceValue).CopyTo(body, 3);
            BitConverter.GetBytes(sourceStackCount).CopyTo(body, 7);
            body[11] = (byte)destinationListType;
            BitConverter.GetBytes(destinationSlot).CopyTo(body, 12);
            BitConverter.GetBytes(destinationInstanceValue).CopyTo(body, 14);
            BitConverter.GetBytes(destinationStackCount).CopyTo(body, 18);
            return body;
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

        private static byte[] BuildUseBodyWithoutItemId(short slot, int instanceValue)
        {
            var body = new byte[7];
            BitConverter.GetBytes(slot).CopyTo(body, 0);
            body[2] = QuickSlotListType;
            BitConverter.GetBytes(instanceValue).CopyTo(body, 3);
            return body;
        }

        private static bool IsCanonicalTowerMoveAck(
            CapturedPacket packet,
            short sourceSlot,
            short destinationSlot)
        {
            return packet != null
                && packet.Command == 0x01
                && packet.Type == 0x0013
                && packet.Body.Length == MoveItemSpaceAckBuilder.SuccessBodyLength
                && packet.Body[0] == 1
                && packet.Body[1] == (byte)InventoryListType.Main
                && BitConverter.ToInt16(packet.Body, 2) == sourceSlot
                && packet.Body[8] == (byte)InventoryListType.Main
                && BitConverter.ToInt16(packet.Body, 9) == destinationSlot;
        }

        private static bool HasCommonUpdate(
            byte[] body,
            byte wantedItemSpace,
            short wantedSlot,
            int wantedItemId,
            int wantedCount)
        {
            // 0x000D is the authoritative merged Main view. The server keeps
            // typed Main/QuickSlot endpoints, but the A14 client parses both
            // through this one container and expects the extra Main parameter.
            if (body == null || body.Length < 5
                || body[0] != (byte)InventoryListType.Main)
                return false;
            var count = BitConverter.ToUInt16(body, 3);
            if (body.Length < 5 + count * 84)
                return false;

            if (wantedItemId < 0)
                return !HasFullListSlot(body, wantedSlot);

            for (var index = 0; index < count; index++)
            {
                var offset = 5 + index * 84;
                if (BitConverter.ToInt16(body, offset) == wantedSlot
                    && BitConverter.ToInt32(body, offset + 2) == wantedItemId)
                {
                    return BitConverter.ToInt32(body, offset + 6) == wantedCount;
                }
            }
            return false;
        }

        private static bool HasFullListSlot(byte[] body, short wantedSlot)
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
                if (BitConverter.ToInt16(body, offset) == wantedSlot)
                    return true;
            }
            return false;
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private sealed class TemporaryInventoryDatabaseScope : IDisposable
        {
            private readonly string _databasePath;
            private readonly string _previousDatabasePath;

            private TemporaryInventoryDatabaseScope(
                string databasePath,
                string previousDatabasePath)
            {
                _databasePath = databasePath;
                _previousDatabasePath = previousDatabasePath;
            }

            internal static TemporaryInventoryDatabaseScope Create()
            {
                var databasePath = Path.Combine(
                    Path.GetTempPath(),
                    $"dfo-death-tower-protocol-{Guid.NewGuid():N}.db");
                SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var previousDatabasePath = Environment.GetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH");
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    databasePath);
                return new TemporaryInventoryDatabaseScope(
                    databasePath,
                    previousDatabasePath);
            }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    _previousDatabasePath);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var path in new[]
                {
                    _databasePath,
                    _databasePath + "-wal",
                    _databasePath + "-shm",
                })
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
            }
        }

        private sealed class ProtocolFixture : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly TcpClient _client;
            private readonly TcpClient _accepted;
            private readonly InventoryLease _inventoryLease;

            private ProtocolFixture(
                TcpListener listener,
                TcpClient client,
                TcpClient accepted,
                EnhancedClientSession session,
                DeathTowerSession tower,
                InventoryLease inventoryLease)
            {
                _listener = listener;
                _client = client;
                _accepted = accepted;
                Session = session;
                Tower = tower;
                _inventoryLease = inventoryLease;
            }

            public EnhancedClientSession Session { get; }
            public DeathTowerSession Tower { get; }
            public InventoryService Inventory => _inventoryLease.Inventory;

            public static ProtocolFixture Create(int itemId = TowerHastePotionItemId)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var client = new TcpClient();
                var connectTask = client.ConnectAsync(IPAddress.Loopback, port);
                var accepted = listener.AcceptTcpClient();
                connectTask.GetAwaiter().GetResult();
                client.ReceiveTimeout = 2000;

                var session = new EnhancedClientSession(accepted, new GamePacketHeader());
                session.Player.CharacterId = 990002;
                session.Player.UserId = 88;
                var inventory = new InventoryService(
                    session.Player.CharacterId,
                    1);
                inventory.ClearDirtyState();
                var inventoryLease = InventoryContext.Register(
                    session.SessionId,
                    inventory);

                var tower = new DeathTowerSession(
                    DeathTowerSelfTestFactory.CreateConfig(
                        11000,
                        new[] { 1 },
                        50));
                tower.BeginStage(0x12345678, new[]
                {
                    new StageTowerItem
                    {
                        SourceListIndex = 1,
                        SourceMonsterUniqueId = 41,
                        ItemUniqueId = 51,
                        ItemId = itemId,
                        DropRate = 10000,
                        StackCount = 2,
                    },
                });
                tower.GenerateDropsForMonster(41);
                DungeonRunLifecycle.BeginTowerRun(session, 11000, tower);
                return new ProtocolFixture(
                    listener,
                    client,
                    accepted,
                    session,
                    tower,
                    inventoryLease);
            }

            public CapturedPacket ReadPacket()
            {
                var header = ReadExact(15);
                var length = BitConverter.ToInt32(header, 3);
                return new CapturedPacket
                {
                    Command = header[0],
                    Type = BitConverter.ToUInt16(header, 1),
                    Body = length > 15 ? ReadExact(length - 15) : Array.Empty<byte>(),
                };
            }

            public bool TryReadPacket(out CapturedPacket packet)
            {
                packet = null;
                if (!_client.Client.Poll(100000, SelectMode.SelectRead) || _client.Available == 0)
                    return false;
                packet = ReadPacket();
                return true;
            }

            public void Dispose()
            {
                if (_inventoryLease != null)
                {
                    lock (_inventoryLease.SyncRoot)
                        _inventoryLease.Inventory.ClearDirtyState();
                    InventoryContext.Unregister(
                        Session.SessionId,
                        Session.Player.CharacterId);
                }
                _accepted.Dispose();
                _client.Dispose();
                _listener.Stop();
            }

            private byte[] ReadExact(int count)
            {
                var result = new byte[count];
                var offset = 0;
                var stream = _client.GetStream();
                while (offset < count)
                {
                    var read = stream.Read(result, offset, count - offset);
                    if (read <= 0)
                        throw new InvalidOperationException("connection closed before packet completed");
                    offset += read;
                }
                return result;
            }
        }

        private sealed class CapturedPacket
        {
            public byte Command { get; set; }
            public ushort Type { get; set; }
            public byte[] Body { get; set; }
        }
    }
}

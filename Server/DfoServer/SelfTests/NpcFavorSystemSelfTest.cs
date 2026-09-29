using DfoServer.Game.Inventory;
using DfoServer.Game.NpcFavor;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    internal static class NpcFavorSystemSelfTest
    {
        public static int Run()
        {
            var failures = 0;

            var system = NpcFavorSystemFile.Parse(@"
[favor condition level]
20
[favor level point up]
3037 100 300
3033 200 600
3262 400 900
[/favor level point up]
[favor gift item count]
100
[favor gift limit]
5
[favor talk rate]
40
[favor appear dungeon rate]
25
[favor level point down]
0 21 500
1 14 1000
2 7 1500
[/favor level point down]
");

            Check("favor system parses level gate", system.ConditionLevel == 20, ref failures);
            Check("favor system parses special-gift action limit", system.SpecialGiftActionLimit == 5, ref failures);
            Check("favor system parses required special-gift item count", system.SpecialGiftItemCount == 100, ref failures);
            Check(
                "favor system parses configured point ranges",
                system.PointRules.Count == 3
                    && system.PointRules[0].ItemId == 3037
                    && system.PointRules[0].MinimumPointGain == 100
                    && system.PointRules[0].MaximumPointGain == 300
                    && system.PointRules[2].ItemId == 3262,
                ref failures);
            Check(
                "favor system parses decay rules",
                system.DecayRules.Count == 3
                    && system.DecayRules[1].FavorLevel == 1
                    && system.DecayRules[1].InactiveDays == 14
                    && system.DecayRules[1].PointLoss == 1000,
                ref failures);

            var npc = NpcFile.Parse(@"
[name]
`测试NPC`
[default favor]
1300
[max gift per day]
20
[gift item]
[favor rate per mood]
120 100 80
[prefer item]
1 3052 5 30 3 3019 5 30
[/prefer item]
[prefer item group]
1 `food` 5 30 2 `mineral stuff` 5 30
[/prefer item group]
[unprefer item]
4 3212 50
[/unprefer item]
[unprefer item group]
1 `potion` 50 4 `cube stuff` 50
[/unprefer item group]
[/gift item]
");

            Check("npc favor parses mood rates", npc.FavorRatePerMood.SequenceEqual(new[] { 120, 100, 80 }), ref failures);
            Check(
                "npc favor parses preferred explicit items",
                npc.PreferredGiftRules.Any(x => x.FavorLevel == 1 && x.ItemId == 3052 && x.MinimumCount == 5 && x.FavorPointChange == 30)
                    && npc.PreferredGiftRules.Any(x => x.FavorLevel == 3 && x.ItemId == 3019),
                ref failures);
            Check(
                "npc favor parses preferred item groups",
                npc.PreferredGiftRules.Any(x => x.FavorLevel == 1 && x.ItemGroupName == "food")
                    && npc.PreferredGiftRules.Any(x => x.FavorLevel == 2 && x.ItemGroupName == "mineral stuff"),
                ref failures);
            Check(
                "npc favor parses disliked items and groups",
                npc.UnpreferredGiftRules.Any(x => x.FavorLevel == 4 && x.ItemId == 3212)
                    && npc.UnpreferredGiftRules.Any(x => x.FavorLevel == 4 && x.ItemGroupName == "cube stuff")
                    && npc.UnpreferredGiftRules.All(x => x.MinimumCount == 0),
                ref failures);

            var preferred = NpcFavorGiftRuleMatcher.Match(
                npc,
                favorLevel: 1,
                itemId: 9999,
                itemGroupName: "food",
                itemCount: 5,
                moodIndex: 0);
            Check(
                "preferred group gift uses PVF minimum count, point change and mood rate",
                preferred.Disposition == NpcFavorGiftDisposition.Preferred
                    && preferred.BaseFavorPointDelta == 30
                    && preferred.FavorPointDelta == 36,
                ref failures);
            Check(
                "preferred gift below PVF minimum is accepted as insufficient rather than liked",
                NpcFavorGiftRuleMatcher.Match(npc, 1, 9999, "food", 4, 1).Disposition
                    == NpcFavorGiftDisposition.InsufficientCount,
                ref failures);
            var disliked = NpcFavorGiftRuleMatcher.Match(
                npc,
                favorLevel: 1,
                itemId: 9999,
                itemGroupName: "potion",
                itemCount: 1,
                moodIndex: 1);
            Check(
                "disliked gift produces the negative PVF point change",
                disliked.Disposition == NpcFavorGiftDisposition.Unpreferred
                    && disliked.FavorPointDelta == -50,
                ref failures);
            Check(
                "unlisted item remains neutral",
                NpcFavorGiftRuleMatcher.Match(npc, 1, 9999, "quest", 5, 0).Disposition
                    == NpcFavorGiftDisposition.Neutral,
                ref failures);

            var extendedNpc = NpcFile.Parse(@"
[name]
`新版好感NPC`
[favorable relationship 2]
[favor level point]
50000 100000 110000
[/favor level point]
[/favorable relationship 2]
");
            Check(
                "npc parser recognizes favorable relationship 2 point bands",
                extendedNpc.FavorableRelationshipVersion == 2
                    && extendedNpc.FavorLevelPoints.SequenceEqual(
                        new[] { 50000, 100000, 110000 }),
                ref failures);

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["etc/npcfavorsystem.etc"] = system.Content,
                ["npc/npc.lst"] = "10 `Lorian.npc` 11 `NoFavor.npc` 78 `Ria.npc`",
                ["npc/Lorian.npc"] = npc.Content,
                ["npc/NoFavor.npc"] = "[name] `无好感配置`",
                ["npc/Ria.npc"] = extendedNpc.Content,
                ["stackable/stackable.lst"] = "9999 `material/food.stk`",
                ["stackable/material/food.stk"] = "[name]\n`测试食物`\n[item group name]\n`food`",
                ["equipment/equipment.lst"] = string.Empty,
            };
            var catalog = NpcFavorDefinitionCatalog.Load(path => files[path.Replace('\\', '/')]);
            Check(
                "favor catalog loads only NPCs with favorable-relationship definitions",
                catalog.Npcs.Count == 2
                    && catalog.TryGetNpc(10, out var catalogNpc)
                    && catalogNpc.Name == "测试NPC"
                    && catalog.TryGetNpc(78, out var catalogExtendedNpc)
                    && catalogExtendedNpc.FavorableRelationshipVersion == 2
                    && !catalog.TryGetNpc(11, out _),
                ref failures);
            Check(
                "favor catalog resolves gift item group from stackable PVF",
                catalog.TryResolveItemGroup(9999, out var itemGroup)
                    && itemGroup == "food",
                ref failures);
            Check(
                "favor catalog exposes special cube gift rule",
                catalog.TryGetSpecialGiftRule(3037, out var special)
                    && special.MinimumPointGain == 100
                    && special.MaximumPointGain == 300,
                ref failures);
            Check(
                "favor stages follow the client four-stage point bands",
                NpcFavorProgressionPolicy.ResolveFavorLevel(0) == 1
                    && NpcFavorProgressionPolicy.ResolveFavorLevel(999) == 1
                    && NpcFavorProgressionPolicy.ResolveFavorLevel(1000) == 2
                    && NpcFavorProgressionPolicy.ResolveFavorLevel(2000) == 3
                    && NpcFavorProgressionPolicy.ResolveFavorLevel(3000) == 4,
                ref failures);

            var ordinaryPlan = NpcFavorGiftPlanner.Plan(
                catalog,
                characterLevel: 20,
                npcId: 10,
                currentFavorPoint: 500,
                slotIndex: 3,
                itemId: 9999,
                itemCount: 5,
                moodIndex: 0,
                gameDayId: 20260925,
                randomInclusive: (minimum, maximum) => maximum);
            Check(
                "ordinary gift plan derives favor level, mood delta and NPC daily cap from PVF",
                ordinaryPlan.Success
                    && !ordinaryPlan.IsSpecialGift
                    && ordinaryPlan.Disposition == NpcFavorGiftDisposition.Preferred
                    && ordinaryPlan.Request.FavorPointDelta == 36
                    && ordinaryPlan.Request.NpcDailyGiftLimit == 20,
                ref failures);

            var invalidSpecialPlan = NpcFavorGiftPlanner.Plan(
                catalog, 20, 10, 1300, 3, 3037, 99, 1, 20260925, (minimum, maximum) => maximum);
            var specialPlan = NpcFavorGiftPlanner.Plan(
                catalog, 20, 10, 1300, 3, 3037, 100, 1, 20260925, (minimum, maximum) => maximum);
            var extendedSpecialPlan = NpcFavorGiftPlanner.Plan(
                catalog, 20, 78, 0, 358, 3037, 100, 1, 20260925, (minimum, maximum) => maximum);
            Check(
                "special cube gift requires exact PVF count and uses configured random point range",
                !invalidSpecialPlan.Success
                    && invalidSpecialPlan.Error == NpcFavorGiftPlanError.InvalidItemCount
                    && specialPlan.Success
                    && specialPlan.IsSpecialGift
                    && specialPlan.Request.FavorPointDelta == 300
                    && specialPlan.Request.NpcDailyGiftLimit == 5,
                ref failures);
            Check(
                "relationship-2 special gift uses PVF trusted and maximum point bands",
                extendedSpecialPlan.Success
                    && extendedSpecialPlan.Request.DefaultFavorPoint == 0
                    && extendedSpecialPlan.Request.TrustedFavorPoint == 100000
                    && extendedSpecialPlan.Request.MaximumFavorPoint == 110000,
                ref failures);
            Check(
                "character below PVF level gate cannot build a gift plan",
                NpcFavorGiftPlanner.Plan(
                    catalog, 19, 10, 1300, 3, 9999, 5, 0, 20260925, (minimum, maximum) => minimum).Error
                    == NpcFavorGiftPlanError.CharacterLevelTooLow,
                ref failures);

            VerifyCapturedA21GiftRequestCodec(ref failures);
            VerifyCapturedA21GiftAckBuilder(ref failures);

            VerifyPersistenceAndDailyLimits(ref failures);
            VerifyV33Migration(ref failures);
            VerifyAtomicInventoryGift(catalog, ref failures);
            VerifyLivePvf(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "NPC_FAVOR selftest passed."
                    : $"NPC_FAVOR selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyCapturedA21GiftRequestCodec(ref int failures)
        {
            // 2026-09-25 S4A21 live captures against NPC 78:
            // gold cubes  = 00 4E000000 24 6701 (main virtual slot 359)
            // clear cubes = 00 4E000000 24 6601 (main virtual slot 358)
            var captured = new byte[]
            {
                0x00,
                0x4E, 0x00, 0x00, 0x00,
                0x24, 0x67, 0x01,
            };
            Check(
                "captured 0x032A gift request decodes operation, NPC, space and slot",
                NpcFavorOperationRequestCodec.TryParse(captured, out var request)
                    && request.Operation == 0
                    && request.NpcId == 78
                    && request.RawListType == 0x24
                    && request.SlotIndex == 359,
                ref failures);
            Check(
                "0x032A gift request rejects truncated and trailing bodies",
                !NpcFavorOperationRequestCodec.TryParse(captured.Take(7).ToArray(), out _)
                    && !NpcFavorOperationRequestCodec.TryParse(
                        captured.Concat(new byte[] { 0x00 }).ToArray(),
                        out _),
                ref failures);
        }

        private static void VerifyCapturedA21GiftAckBuilder(ref int failures)
        {
            // DNF.exe 1F8244D3... registers CMD 0x032A response handler
            // 0x01133D90. The command dispatcher consumes the leading
            // u8 result code. The successful gift branch then consumes:
            // u8 operation, u32 NPC id, u16 applied point gain,
            // u32 resulting favor point, u8 gift reaction.
            var body = NpcFavorOperationAckBuilder.BuildGift(
                npcId: 78,
                appliedFavorPointDelta: 300,
                favorPoint: 2216,
                disposition: NpcFavorGiftDisposition.Preferred);
            Check(
                "0x032A gift ACK includes success result before the 12-byte payload",
                body.Length == NpcFavorOperationAckBuilder.GiftBodyLength
                    && body.SequenceEqual(new byte[]
                {
                    0x01,
                    0x00,
                    0x4E, 0x00, 0x00, 0x00,
                    0x2C, 0x01,
                    0xA8, 0x08, 0x00, 0x00,
                    0x01,
                }),
                ref failures);

            Check(
                "0x032A daily gift limit sends the client-localized failure ACK",
                NpcFavorOperationAckBuilder.TryBuildGiftFailure(
                    NpcFavorGiftError.NpcDailyLimit,
                    out var dailyLimitBody)
                    && dailyLimitBody.SequenceEqual(new byte[] { 0x00, 0x66 }),
                ref failures);
        }

        private static void VerifyPersistenceAndDailyLimits(ref int failures)
        {
            const int accountId = 9734101;
            const int characterId = 9734102;
            var path = Path.Combine(
                Path.GetTempPath(),
                $"npc-favor-{Guid.NewGuid():N}.db");

            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@accountId, 'npc-favor-test', '');
INSERT INTO characters(character_id, account_id, name, level)
VALUES(@characterId, @accountId, 'favor', 20);";
                    command.Parameters.AddWithValue("@accountId", accountId);
                    command.Parameters.AddWithValue("@characterId", characterId);
                    command.ExecuteNonQuery();
                });

                var repository = new NpcFavorRepository(database);
                var first = Apply(
                    database,
                    repository,
                    characterId,
                    npcId: 10,
                    defaultFavorPoint: 1300,
                    gameDayId: 20260925,
                    itemCount: 5,
                    favorPointDelta: 300,
                    npcDailyGiftLimit: 2);
                Check(
                    "first gift persists default favor plus delta and daily counters",
                    first.Success
                        && first.State.FavorPoint == 1600
                        && first.State.GiftActionCount == 1
                        && first.State.GiftItemCount == 5,
                    ref failures);

                var second = Apply(
                    database,
                    repository,
                    characterId,
                    npcId: 10,
                    defaultFavorPoint: 1300,
                    gameDayId: 20260925,
                    itemCount: 1,
                    favorPointDelta: -50,
                    npcDailyGiftLimit: 2);
                Check(
                    "second gift updates persisted favor and reaches both limits",
                    second.Success
                        && second.State.FavorPoint == 1550
                        && second.State.GiftActionCount == 2
                        && second.State.GiftItemCount == 6,
                    ref failures);

                var npcLimit = Apply(
                    database,
                    repository,
                    characterId,
                    npcId: 10,
                    defaultFavorPoint: 1300,
                    gameDayId: 20260925,
                    itemCount: 1,
                    favorPointDelta: 100,
                    npcDailyGiftLimit: 2);
                Check(
                    "per-NPC action limit rejects without changing durable state",
                    !npcLimit.Success
                        && npcLimit.Error == NpcFavorGiftApplyError.NpcDailyLimit
                        && repository.GetState(characterId, 10, 1300, 20260925).FavorPoint == 1550,
                    ref failures);

                var otherNpc = Apply(
                    database,
                    repository,
                    characterId,
                    npcId: 13,
                    defaultFavorPoint: 0,
                    gameDayId: 20260925,
                    itemCount: 1,
                    favorPointDelta: 100,
                    npcDailyGiftLimit: 20);
                Check(
                    "daily gift limits are isolated per NPC",
                    otherNpc.Success
                        && repository.GetState(characterId, 13, 0, 20260925).FavorPoint == 100,
                    ref failures);

                var nextDay = Apply(
                    database,
                    repository,
                    characterId,
                    npcId: 10,
                    defaultFavorPoint: 1300,
                    gameDayId: 20260926,
                    itemCount: 5,
                    favorPointDelta: 100,
                    npcDailyGiftLimit: 2);
                Check(
                    "new game day resets counters but preserves favor points",
                    nextDay.Success
                        && nextDay.State.FavorPoint == 1650
                        && nextDay.State.GiftActionCount == 1
                        && nextDay.State.GiftItemCount == 5,
                    ref failures);

                var trusted = new[] { 20, 21, 22 }
                    .Select(npcId => Apply(
                        database,
                        repository,
                        characterId,
                        npcId,
                        defaultFavorPoint: 0,
                        gameDayId: 20260926,
                        itemCount: 1,
                        favorPointDelta: 4000,
                        npcDailyGiftLimit: 20))
                    .ToArray();
                var fourthTrusted = Apply(
                    database,
                    repository,
                    characterId,
                    npcId: 23,
                    defaultFavorPoint: 0,
                    gameDayId: 20260926,
                    itemCount: 1,
                    favorPointDelta: 4000,
                    npcDailyGiftLimit: 20);
                Check(
                    "favor points cap at 3200 and only three NPCs may enter trusted stage",
                    trusted.All(result => result.Success && result.State.FavorPoint == 3200)
                        && fourthTrusted.Success
                        && fourthTrusted.State.FavorPoint == 2999,
                    ref failures);

                using var connection = database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM character_npc_favor WHERE character_id=@id;";
                command.Parameters.AddWithValue("@id", characterId);
                Check(
                    "new schema stores one durable row per character and NPC",
                    Convert.ToInt32(command.ExecuteScalar()) == 6,
                    ref failures);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var candidate = path + suffix;
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
            }
        }

        private static NpcFavorGiftApplyResult Apply(
            IGameDatabase database,
            NpcFavorRepository repository,
            int characterId,
            int npcId,
            int defaultFavorPoint,
            int gameDayId,
            int itemCount,
            int favorPointDelta,
            int npcDailyGiftLimit)
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var result = repository.TryApplyGift(
                connection,
                transaction,
                characterId,
                npcId,
                defaultFavorPoint,
                gameDayId,
                itemCount,
                favorPointDelta,
                npcDailyGiftLimit);
            transaction.Commit();
            return result;
        }

        private static void VerifyAtomicInventoryGift(
            NpcFavorDefinitionCatalog catalog,
            ref int failures)
        {
            const int accountId = 9734201;
            const int characterId = 9734202;
            const int itemId = 3037;
            const short slotIndex = 3;
            var sessionId = Guid.NewGuid();
            var path = Path.Combine(
                Path.GetTempPath(),
                $"npc-favor-atomic-{Guid.NewGuid():N}.db");
            InventoryLease lease = null;

            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@accountId, 'npc-favor-atomic', '');
INSERT INTO characters(character_id, account_id, name, level)
VALUES(@characterId, @accountId, 'favor-atomic', 20);";
                    command.Parameters.AddWithValue("@accountId", accountId);
                    command.Parameters.AddWithValue("@characterId", characterId);
                    command.ExecuteNonQuery();
                });

                var inventory = new InventoryService(characterId, accountId, database);
                inventory.SetItem(
                    InventoryListType.Main,
                    slotIndex,
                    new ItemCore
                    {
                        ItemKind = ItemCore.KindMaterial,
                        ItemId = itemId,
                        Count = 5,
                    });
                lease = InventoryContext.Register(sessionId, characterId, inventory);
                Check(
                    "gift fixture inventory persists",
                    InventoryPersistenceService.SaveDirty(lease),
                    ref failures);

                var repository = new NpcFavorRepository(database);
                var service = new NpcFavorGiftService(repository);
                var success = service.TryGift(
                    lease,
                    new NpcFavorGiftRequest
                    {
                        NpcId = 10,
                        SlotIndex = slotIndex,
                        ExpectedItemId = itemId,
                        ItemCount = 2,
                        DefaultFavorPoint = 1300,
                        FavorPointDelta = 300,
                        GameDayId = 20260925,
                        NpcDailyGiftLimit = 20,
                    });
                Check(
                    "gift atomically consumes inventory and persists favor",
                    success.Success
                        && success.InventoryDeletion?.DeletedCount == 2
                        && success.AppliedFavorPointDelta == 300
                        && lease.Inventory.GetItem(InventoryListType.Main, slotIndex)?.Count == 3
                        && repository.GetState(characterId, 10, 1300, 20260925).FavorPoint == 1600,
                    ref failures);

                inventory.SetItem(
                    InventoryListType.Main,
                    4,
                    new ItemCore
                    {
                        ItemKind = ItemCore.KindMaterial,
                        ItemId = 91940,
                        Count = 60,
                    });
                inventory.SetItem(
                    InventoryListType.Main,
                    5,
                    new ItemCore
                    {
                        ItemKind = ItemCore.KindMaterial,
                        ItemId = 91940,
                        Count = 50,
                    });
                var capturedProtocolGift = service.TryGift(
                    lease,
                    new NpcFavorGiftRequest
                    {
                        NpcId = 78,
                        ExpectedItemId = 91940,
                        ItemCount = 100,
                        ConsumeByTemplateId = true,
                        DefaultFavorPoint = 0,
                        FavorPointDelta = 100,
                        GameDayId = 20260925,
                        NpcDailyGiftLimit = 5,
                    });
                Check(
                    "captured 0x032A gift consumes required cubes across main-inventory stacks",
                    capturedProtocolGift.Success
                        && capturedProtocolGift.AppliedFavorPointDelta == 100
                        && capturedProtocolGift.InventoryChanges.Slots.Count == 2
                        && lease.Inventory.GetItem(InventoryListType.Main, 4) == null
                        && lease.Inventory.GetItem(InventoryListType.Main, 5)?.Count == 10
                        && repository.GetState(characterId, 78, 0, 20260925).FavorPoint == 100,
                    ref failures);

                inventory.SetMainVirtualCount(358, 3037, 150);
                InventoryPersistenceService.SaveDirty(lease);
                var commandService = new NpcFavorGiftCommandService(
                    catalog,
                    repository,
                    (minimum, maximum) => maximum);
                var capturedVirtualCubeGift = commandService.TryGift(
                    lease,
                    characterLevel: 20,
                    npcId: 10,
                    slotIndex: 358,
                    gameDayId: 20260925);
                Check(
                    "captured 0x032A command derives and consumes exactly 100 virtual cubes",
                    capturedVirtualCubeGift.Gift.Success
                        && capturedVirtualCubeGift.Plan.Request.ItemCount == 100
                        && capturedVirtualCubeGift.Gift.InventoryChanges.Slots.Count == 1
                        && capturedVirtualCubeGift.Gift.InventoryChanges.Slots[0].SlotIndex == 358
                        && inventory.GetMainVirtualCount(358)?.Count == 50
                        && repository.GetState(characterId, 10, 0, 20260925).FavorPoint == 1900,
                    ref failures);

                inventory.SetMainVirtualCount(358, 3037, 100);
                InventoryPersistenceService.SaveDirty(lease);
                var relationship2Gift = commandService.TryGift(
                    lease,
                    characterLevel: 20,
                    npcId: 78,
                    slotIndex: 358,
                    gameDayId: 20260925);
                Check(
                    "captured relationship-2 gift commits with its PVF point bands",
                    relationship2Gift.Success
                        && relationship2Gift.Gift.AppliedFavorPointDelta == 300
                        && relationship2Gift.Plan.Request.TrustedFavorPoint == 100000
                        && relationship2Gift.Plan.Request.MaximumFavorPoint == 110000
                        && inventory.GetMainVirtualCount(358)?.Count == 0
                        && repository.GetState(characterId, 78, 0, 20260925).FavorPoint == 400,
                    ref failures);

                database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
CREATE TRIGGER reject_npc_favor_insert
BEFORE INSERT ON character_npc_favor
BEGIN SELECT RAISE(ABORT, 'selftest'); END;";
                    command.ExecuteNonQuery();
                });
                var rejected = service.TryGift(
                    lease,
                    new NpcFavorGiftRequest
                    {
                        NpcId = 13,
                        SlotIndex = slotIndex,
                        ExpectedItemId = itemId,
                        ItemCount = 1,
                        DefaultFavorPoint = 0,
                        FavorPointDelta = 100,
                        GameDayId = 20260925,
                        NpcDailyGiftLimit = 20,
                    });
                Check(
                    "failed favor write rolls inventory consumption back and reloads the lease",
                    !rejected.Success
                        && lease.Inventory.GetItem(InventoryListType.Main, slotIndex)?.Count == 3
                        && repository.GetState(characterId, 13, 0, 20260925).FavorPoint == 0,
                    ref failures);
            }
            finally
            {
                if (lease != null)
                    InventoryContext.Unregister(sessionId, characterId);
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var candidate = path + suffix;
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
            }
        }

        private static void VerifyV33Migration(ref int failures)
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"npc-favor-migration-{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
DROP TABLE character_npc_favor;
PRAGMA user_version=32;
UPDATE schema_metadata SET schema_version=32;
CREATE TRIGGER reject_npc_favor_migration
BEFORE UPDATE OF schema_version ON schema_metadata
BEGIN SELECT RAISE(ABORT, 'selftest'); END;";
                    command.ExecuteNonQuery();
                });

                var failed = false;
                using (var connection = database.OpenConnection())
                {
                    try
                    {
                        SqliteMigrations.Apply(connection);
                    }
                    catch (SqliteException)
                    {
                        failed = true;
                    }
                }

                var rollbackIntact = database.Read(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = @"
SELECT
    (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='character_npc_favor'),
    (SELECT schema_version FROM schema_metadata),
    (SELECT user_version FROM pragma_user_version);";
                    using var reader = command.ExecuteReader();
                    return reader.Read()
                        && reader.GetInt32(0) == 0
                        && reader.GetInt32(1) == 32
                        && reader.GetInt32(2) == 32;
                });
                Check(
                    "failed v33 migration rolls table creation and both versions back",
                    failed && rollbackIntact,
                    ref failures);

                database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "DROP TRIGGER reject_npc_favor_migration;";
                    command.ExecuteNonQuery();
                });
                using (var connection = database.OpenConnection())
                {
                    SqliteMigrations.Apply(connection);
                    SqliteMigrations.Apply(connection);
                }

                var migrated = database.Read(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = @"
SELECT
    (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='character_npc_favor'),
    (SELECT schema_version FROM schema_metadata),
    (SELECT user_version FROM pragma_user_version);";
                    using var reader = command.ExecuteReader();
                    return reader.Read()
                        && reader.GetInt32(0) == 1
                        && reader.GetInt32(1) == SqliteMigrations.CurrentVersion
                        && reader.GetInt32(2) == SqliteMigrations.CurrentVersion;
                });
                Check(
                    "v33 migration creates NPC favor storage and is idempotent",
                    migrated,
                    ref failures);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    var candidate = path + suffix;
                    if (File.Exists(candidate))
                        File.Delete(candidate);
                }
            }
        }

        private static void VerifyLivePvf(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine("[SKIP] live NPC favor PVF catalog (PVF_ARCHIVE_PATH not set)");
                return;
            }

            try
            {
                var catalog = NpcFavorDefinitionCatalog.Load(PvfArchiveAccessor.ReadText);
                Check(
                    "live PVF exposes the configured NPC favor catalog",
                    catalog.Npcs.Count > 0
                        && catalog.TryGetNpc(10, out var lorian)
                        && lorian.DefaultFavor == 1300
                        && lorian.MaxGiftPerDay == 20,
                    ref failures);
                Check(
                    "live PVF exposes Mia Richter (npc 78) for captured gift requests",
                    catalog.TryGetNpc(78, out var mia)
                        && mia.FavorableRelationshipVersion == 2
                        && mia.FavorLevelPoints.SequenceEqual(
                            new[] { 50000, 100000, 110000 }),
                    ref failures);
                Check(
                    "live PVF resolves clear cube as a special cube-stuff gift",
                    catalog.TryGetSpecialGiftRule(3037, out var clearCubeRule)
                        && clearCubeRule.MinimumPointGain == 100
                        && clearCubeRule.MaximumPointGain == 300
                        && catalog.TryResolveItemGroup(3037, out var group)
                        && string.Equals(group, "cube stuff", StringComparison.OrdinalIgnoreCase),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] live NPC favor PVF catalog: " + ex.Message);
                failures++;
            }
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}

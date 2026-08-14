using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Mercenary;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    public static class MercenarySelfTest
    {
        private const int AccountId = 1;
        private const int OtherAccountId = 2;
        private const int Level85CharacterId = 998801;
        private const int Level70CharacterId = 998802;
        private const int RandomCharacterId = 998803;
        private const int LowLevelCharacterId = 998804;
        private const int OtherCharacterId = 998805;
        private const int RewardItemId = 10007330;
        private const int SecondaryRewardItemId = 10088439;
        private static readonly int[] AvatarItemIds =
        {
            108550662, 108560645, 108570739, 108520635,
            108500651, 108510647, 108530609, 108540619,
        };

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== MERCENARY selftest ===");

            RunCase("current PVF", TestCurrentPvf);
            RunCase("wire bodies", TestWireBodies);
            RunCase("reward calculation", TestRewardCalculation);
            RunCase("persistence lifecycle", TestPersistenceLifecycle);
            RunCase("mailbox reward delivery", TestMailboxRewardDelivery);
            RunCase("avatar bonus tier", TestAvatarBonusTier);

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void TestCurrentPvf()
        {
            var config = MercenaryConfigProvider.Current;
            Check("PVF mercenary rewards and periods",
                config.BaseTimeUnitSeconds == 3600
                && config.LevelRewards.Count == 3
                && Matches(config.LevelRewards.Select(x => x.MinimumLevel), 70, 80, 85)
                && Matches(config.LevelRewards.Select(x => x.BaseGoldPerHour), 345, 415, 495)
                && Matches(config.LevelRewards.Select(x => x.ItemProbabilityPerHour), 225, 225, 225)
                && Matches(config.Periods.Select(x => x.Hours), 2, 6, 24, 168, 336)
                && Matches(config.Periods.Select(x => x.BonusMultiplier), 1.3, 1.2, 1.2, 1.0, 0.9));
            Check("PVF mercenary avatar and area settings",
                config.AvatarBonuses.Count == 4
                && config.GetAvatarMultiplier(0) == 1.0
                && config.GetAvatarMultiplier(1) == 2.0
                && config.GetAvatarMultiplier(2) == 2.0
                && config.GetAvatarMultiplier(3) == 3.0
                && Matches(config.Areas.Select(x => x.WorldMapId), 7, 12, 15, 13, 17, 19, 26, 32, -1)
                && config.Areas.Count == 9
                && config.Areas[8].IsRandom
                && config.CriticalOptions.Count == 1
                && config.CriticalOptions[0].Weight == 10000
                && config.CriticalOptions[0].Multiplier == 1.0);
        }

        private static void TestWireBodies()
        {
            var snapshot = new MercenaryInfoSnapshot
            {
                ManageLevel = 7,
                ManagePoint = 1002,
            };
            var multibyteName = new byte[] { 0xE4, 0xBD, 0xA3, 0xE5, 0x85, 0xB5 };
            snapshot.Records.Add(new MercenaryCharacterInfo
            {
                CharacterId = 101,
                Name = multibyteName,
                State = MercenaryExpeditionState.Waiting,
                RemainingSeconds = -1700000000,
                AreaIndex = byte.MaxValue,
                PeriodIndex = byte.MaxValue,
                AvatarBonusTier = 2,
            });
            snapshot.Records.Add(new MercenaryCharacterInfo
            {
                CharacterId = 202,
                Name = Encoding.ASCII.GetBytes("active"),
                State = MercenaryExpeditionState.InProgress,
                RemainingSeconds = 7200,
                AreaIndex = 3,
                PeriodIndex = 1,
                AvatarBonusTier = 1,
            });
            snapshot.Records.Add(new MercenaryCharacterInfo
            {
                CharacterId = 303,
                Name = Array.Empty<byte>(),
                State = MercenaryExpeditionState.Complete,
                RemainingSeconds = -5,
                AreaIndex = 4,
                PeriodIndex = 2,
                AvatarBonusTier = 3,
            });

            var info = MercenaryExpeditionBodyBuilder.BuildInfoSuccess(snapshot);
            Check("0x01BA header uses adventure level, point, and record count",
                info[0] == 1 && info[1] == 7
                && BitConverter.ToInt32(info, 2) == 1002 && info[6] == 3);

            var offset = 7;
            Check("0x01BA records preserve names and states",
                ReadMercenaryRecord(info, ref offset, out var waiting)
                && waiting.CharacterId == 101
                && waiting.Name.SequenceEqual(multibyteName)
                && waiting.State == MercenaryExpeditionState.Waiting
                && waiting.RemainingSeconds == -1700000000
                && waiting.AreaIndex == byte.MaxValue
                && waiting.PeriodIndex == byte.MaxValue
                && waiting.AvatarBonusTier == 2
                && ReadMercenaryRecord(info, ref offset, out var inProgress)
                && inProgress.CharacterId == 202
                && inProgress.Name.SequenceEqual(Encoding.ASCII.GetBytes("active"))
                && inProgress.State == MercenaryExpeditionState.InProgress
                && inProgress.RemainingSeconds == 7200
                && inProgress.AreaIndex == 3
                && inProgress.PeriodIndex == 1
                && inProgress.AvatarBonusTier == 1
                && ReadMercenaryRecord(info, ref offset, out var complete)
                && complete.CharacterId == 303
                && complete.Name.Length == 0
                && complete.State == MercenaryExpeditionState.Complete
                && complete.RemainingSeconds == -5
                && complete.AreaIndex == 4
                && complete.PeriodIndex == 2
                && complete.AvatarBonusTier == 3
                && offset == info.Length);

            var fifteenCharacters = new MercenaryInfoSnapshot { ManageLevel = 7, ManagePoint = 1002 };
            for (var i = 0; i < 15; i++)
            {
                fifteenCharacters.Records.Add(new MercenaryCharacterInfo
                {
                    CharacterId = 1000 + i,
                    Name = Encoding.ASCII.GetBytes("char-" + i),
                });
            }
            var fifteenCharacterInfo = MercenaryExpeditionBodyBuilder.BuildInfoSuccess(fifteenCharacters);
            Check("0x01BA character count cannot overwrite adventure level",
                fifteenCharacterInfo[1] == 7 && fifteenCharacterInfo[6] == 15);

            var returned = MercenaryExpeditionBodyBuilder.BuildReturnSuccess(
                202, 7, RewardItemId, 3, hasReward: true);
            var goldOnlyReturn = MercenaryExpeditionBodyBuilder.BuildReturnSuccess(
                203, 1, 0, 0, hasReward: true);
            var zeroHourReturn = MercenaryExpeditionBodyBuilder.BuildReturnSuccess(
                204, 1, 0, 0, hasReward: false);
            Check("0x01B9 reward and zero-hour flags match client semantics",
                returned.Length == 15
                && returned[0] == 1
                && returned[1] == 7
                && BitConverter.ToInt32(returned, 2) == 202
                && BitConverter.ToInt32(returned, 6) == RewardItemId
                && BitConverter.ToInt32(returned, 10) == 3
                && returned[14] == 1
                && goldOnlyReturn.Length == 15
                && BitConverter.ToInt32(goldOnlyReturn, 2) == 203
                && BitConverter.ToInt32(goldOnlyReturn, 6) == 0
                && BitConverter.ToInt32(goldOnlyReturn, 10) == 0
                && goldOnlyReturn[14] == 1
                && zeroHourReturn.Length == 15
                && BitConverter.ToInt32(zeroHourReturn, 6) == 0
                && BitConverter.ToInt32(zeroHourReturn, 10) == 0
                && zeroHourReturn[14] == 0);

            var competition = MercenaryExpeditionBodyBuilder.BuildCompetitionSuccess(202, 3, 1);
            Check("0x01BB success and visible failure use current client layout",
                competition.Length == 7
                && competition[0] == 1
                && BitConverter.ToInt32(competition, 1) == 202
                && competition[5] == 3
                && competition[6] == 1
                && MercenaryExpeditionBodyBuilder.BuildError(MercenaryExpeditionHandler.CompetitionErrorCode)
                    .SequenceEqual(new byte[] { 0, 21 }));

            Check("assigned-character start-game rejection uses the current client recall prompt",
                DungeonEntryHandler.BuildMercenaryContentErrorBody()
                    .SequenceEqual(new byte[] { 0, DungeonEntryHandler.MercenaryContentErrorCode })
                && DungeonEntryHandler.StartGameResponseType == 0x000F
                && DungeonEntryHandler.MercenaryContentErrorCode == 0xEB);

            var equipAvatar = new InventoryMoveRequest
            {
                SourceListType = InventoryListType.Avatar,
                DestinationListType = InventoryListType.Equipment,
            };
            var unequipAvatar = new InventoryMoveRequest
            {
                SourceListType = InventoryListType.Equipment,
                DestinationListType = InventoryListType.Avatar,
            };
            var equipNormalItem = new InventoryMoveRequest
            {
                SourceListType = InventoryListType.Main,
                DestinationListType = InventoryListType.Equipment,
            };
            Check("avatar mutation restriction uses the current-client prompt",
                InventoryHandler.IsAvatarEquipMutation(equipAvatar)
                && InventoryHandler.IsAvatarEquipMutation(unequipAvatar)
                && !InventoryHandler.IsAvatarEquipMutation(equipNormalItem)
                && MoveItemSpaceAckBuilder.BuildError(
                        InventoryHandler.MercenaryAvatarMutationErrorCode,
                        (byte)InventoryListType.Avatar,
                        (byte)InventoryListType.Equipment)
                    .SequenceEqual(new byte[]
                    {
                        0,
                        InventoryHandler.MercenaryAvatarMutationErrorCode,
                        (byte)InventoryListType.Avatar,
                        (byte)InventoryListType.Equipment,
                    })
                && InventoryHandler.MercenaryAvatarMutationErrorCode == 0xB4);
        }

        private static void TestRewardCalculation()
        {
            var config = BuildConfig();
            var assignment = new MercenaryAssignment
            {
                AssignmentId = 42,
                AccountId = AccountId,
                CharacterId = Level85CharacterId,
                CharacterLevel = 85,
                StartTime = 1000,
                FinishTime = 1000 + 6 * 3600,
                AreaIndex = 0,
                PeriodIndex = 1,
                AvatarBonusTier = 1,
            };
            var calculator = new MercenaryRewardCalculator(random: FixedRandom.Instance);
            var first = calculator.Calculate(assignment, config, 1000 + 3 * 3600 + 59);
            Check("reward applies whole hours, avatar gold, and independent loot slots",
                first.CompletedHours == 3
                && first.IsEarlyReturn
                && first.BaseGold == 720
                && first.BonusGold == 720
                && first.Items.Count == 1
                && first.ItemTemplateId == RewardItemId
                && first.ItemCount == 2);

            var noFullHour = calculator.Calculate(assignment, config, assignment.StartTime + 3599);
            Check("partial hour gives no reward hour",
                noFullHour.CompletedHours == 0
                && noFullHour.BaseGold == 0
                && noFullHour.BonusGold == 0
                && noFullHour.ItemTemplateId == 0);

            var monsterReward = new MercenaryRewardCalculator(new FixedMonsterDrops(), FixedRandom.Instance)
                .Calculate(assignment, BuildConfig(monsterReward: true), assignment.FinishTime);
            Check("monster reward resolves through drop source",
                monsterReward.Items.Count == 2
                && monsterReward.Items.All(item =>
                    item.ItemTemplateId == 777777 && item.ItemCount == 1));

            var mixedConfig = BuildConfig();
            var secondGroup = new MercenaryRewardGroup { Weight = 10000, MessageKey = "second" };
            secondGroup.Items.Add(new MercenaryWeightedEntry
            {
                Value = SecondaryRewardItemId,
                Weight = 10000,
            });
            mixedConfig.Areas[0].RewardGroups.Add(secondGroup);
            var mixed = new MercenaryRewardCalculator(
                random: new WeightedSequenceRandom(0, 0, 0, 0, 0, 10000, 0))
                .Calculate(assignment, mixedConfig, assignment.FinishTime);
            Check("independent loot slots preserve distinct reward items",
                mixed.Items.Count == 2
                && mixed.Items[0].ItemTemplateId == RewardItemId
                && mixed.Items[0].ItemCount == 1
                && mixed.Items[1].ItemTemplateId == SecondaryRewardItemId
                && mixed.Items[1].ItemCount == 1);

            var equipmentConfig = BuildConfig();
            equipmentConfig.Areas[0].RewardGroups[0].Items.Clear();
            equipmentConfig.Areas[0].RewardGroups[0].Items.Add(
                new MercenaryWeightedEntry { Value = AvatarItemIds[0], Weight = 10000 });
            var equipmentRewards = calculator.Calculate(
                assignment, equipmentConfig, assignment.FinishTime);
            Check("duplicate non-stackable rewards remain separate mail attachments",
                equipmentRewards.Items.Count == 2
                && equipmentRewards.Items.All(item =>
                    item.ItemTemplateId == AvatarItemIds[0] && item.ItemCount == 1));
        }

        private static void TestMailboxRewardDelivery()
        {
            using (var fixture = new Fixture())
            {
                var config = BuildConfig();
                var clock = new FixedTime { Now = 1700000000 };
                var repository = new MercenaryRepository(fixture.DatabasePath, ServerPaths.SchemaFilePath);
                var mailboxRepository = new MailboxRepository(fixture.DatabasePath, ServerPaths.SchemaFilePath);
                var delivery = new MailboxMercenaryMailDelivery(new MailboxService(mailboxRepository));
                var service = CreateService(
                    fixture, repository, clock, config, avatarTier: 0, mailDelivery: delivery);

                var dispatch = service.Dispatch(AccountId, 0, Level85CharacterId, 0, 0);
                clock.Now += 3600;
                var returned = service.Return(AccountId, Level85CharacterId, 1);
                var delivered = repository.GetOutboxByAssignment(dispatch.Assignment.AssignmentId);
                var mail = mailboxRepository.LoadInboxPage(Level85CharacterId, 20).Entries
                    .SingleOrDefault(entry => entry.MessageId == delivered?.MailboxMessageId);
                Check("return creates official reward mail and persists its message id",
                    returned.Success
                    && delivered != null
                    && delivered.DeliveryStatus == "delivered"
                    && delivered.DeliveryAttempts == 1
                    && delivered.MailboxMessageId > 0
                    && mail != null
                    && mail.MailType == 1
                    && mail.SenderName == "DNFadmin"
                    && mail.Gold == delivered.BaseGold + delivered.BonusGold
                    && mail.Attachments.Count == 1
                    && mail.Attachments[0].ItemTemplateId == RewardItemId
                    && mail.Attachments[0].ItemCount == 1);

                var replay = delivery.Deliver(delivered);
                Check("reward delivery replay reuses the same mailbox message",
                    replay.Disposition == MercenaryMailDeliveryDisposition.Delivered
                    && replay.MailboxMessageId == delivered.MailboxMessageId
                    && CountMessages(fixture.DatabasePath, $"mercenary:{delivered.AssignmentId}") == 1);

                var multiItemEntry = new MercenaryRewardOutboxEntry
                {
                    OutboxId = 999001,
                    AssignmentId = 999001,
                    AccountId = AccountId,
                    CharacterId = Level85CharacterId,
                    BaseGold = 10,
                    MailTitleKey = "game_server_msg_225",
                    MailMessageKey = "selftest",
                };
                multiItemEntry.Items.Add(new MercenaryRewardItem
                {
                    ItemTemplateId = RewardItemId,
                    ItemCount = 1,
                });
                multiItemEntry.Items.Add(new MercenaryRewardItem
                {
                    ItemTemplateId = SecondaryRewardItemId,
                    ItemCount = 2,
                });
                var multiItemDelivery = delivery.Deliver(multiItemEntry);
                var multiItemMail = mailboxRepository.LoadInboxPage(Level85CharacterId, 20).Entries
                    .SingleOrDefault(entry => entry.MessageId == multiItemDelivery.MailboxMessageId);
                Check("mercenary mail delivers every distinct loot attachment",
                    multiItemDelivery.Disposition == MercenaryMailDeliveryDisposition.Delivered
                    && multiItemMail != null
                    && multiItemMail.Attachments.Count == 2
                    && multiItemMail.Attachments[0].ItemTemplateId == RewardItemId
                    && multiItemMail.Attachments[0].ItemCount == 1
                    && multiItemMail.Attachments[1].ItemTemplateId == SecondaryRewardItemId
                    && multiItemMail.Attachments[1].ItemCount == 2);

                var failingService = CreateService(
                    fixture, repository, clock, config, avatarTier: 0, mailDelivery: FailedDelivery.Instance);
                var retryDispatch = failingService.Dispatch(AccountId, 0, Level70CharacterId, 0, 0);
                clock.Now += 3600;
                failingService.Return(AccountId, Level70CharacterId, 2);
                var failed = repository.GetOutboxByAssignment(retryDispatch.Assignment.AssignmentId);
                var remainedPending = failed.DeliveryStatus == "pending" && failed.DeliveryAttempts == 1;

                var retryService = CreateService(
                    fixture, repository, clock, config, avatarTier: 0, mailDelivery: delivery);
                retryService.DeliverPendingRewardsForAccount(AccountId);
                var retried = repository.GetOutboxByAssignment(retryDispatch.Assignment.AssignmentId);
                Check("failed reward remains pending and retries idempotently",
                    remainedPending
                    && retried.DeliveryStatus == "delivered"
                    && retried.DeliveryAttempts == 2
                    && retried.MailboxMessageId > 0
                    && CountMessages(fixture.DatabasePath, $"mercenary:{retried.AssignmentId}") == 1);

                var mailCountBeforeZeroReward = CountRows(fixture.DatabasePath, "mailbox_messages");
                var zeroDispatch = service.Dispatch(AccountId, 0, RandomCharacterId, 0, 0);
                var zeroReturn = service.Return(AccountId, RandomCharacterId, 3);
                var zeroReward = repository.GetOutboxByAssignment(zeroDispatch.Assignment.AssignmentId);
                Check("zero-hour return closes outbox without creating empty mail",
                    zeroReturn.Success
                    && zeroReward.DeliveryStatus == "delivered"
                    && zeroReward.MailboxMessageId == 0
                    && CountRows(fixture.DatabasePath, "mailbox_messages") == mailCountBeforeZeroReward);
            }
        }

        private static void TestPersistenceLifecycle()
        {
            using (var fixture = new Fixture())
            {
                var config = BuildConfig();
                var clock = new FixedTime { Now = 1700000000 };
                var repository = new MercenaryRepository(fixture.DatabasePath, ServerPaths.SchemaFilePath);
                var service = CreateService(fixture, repository, clock, config, avatarTier: 1);
                var restrictions = new MercenaryRestrictionService(repository);

                Check("dispatch rejects invalid character choices",
                    service.Dispatch(AccountId, Level85CharacterId, Level85CharacterId, 0, 0).Status
                    == MercenaryOperationStatus.ActiveCharacter
                    && service.Dispatch(AccountId, 0, OtherCharacterId, 0, 0).Status
                        == MercenaryOperationStatus.CharacterNotOwned
                    && service.Dispatch(AccountId, 0, LowLevelCharacterId, 0, 0).Status
                    == MercenaryOperationStatus.LevelTooLow);

                var dispatch = service.Dispatch(AccountId, 0, Level85CharacterId, 0, 0);
                Check("dispatch persists assignment and activates restrictions",
                    dispatch.Success
                    && dispatch.Assignment.AssignmentId > 0
                    && dispatch.Assignment.StartTime == clock.Now
                    && dispatch.Assignment.FinishTime == clock.Now + 2 * 3600
                    && dispatch.Assignment.AvatarBonusTier == 1
                    && restrictions.IsAssigned(Level85CharacterId)
                    && !restrictions.CanDelete(Level85CharacterId)
                    && !restrictions.CanMutateAppearance(Level85CharacterId)
                    && !restrictions.CanEnterContent(Level85CharacterId));
                var firstAssignment = dispatch.Assignment;

                var restoredService = CreateService(
                    fixture,
                    new MercenaryRepository(fixture.DatabasePath, ServerPaths.SchemaFilePath),
                    clock,
                    config,
                    avatarTier: 0);
                var restored = restoredService.GetInfo(AccountId);
                var expectedAdventureGroup = AdventureGroupDataProvider.Calculate(
                    fixture.Characters.ListByAccount(AccountId));
                var restoredAssignment = restored.Records.Single(x => x.CharacterId == Level85CharacterId);
                var restoredWaiting = restored.Records.Single(x => x.CharacterId == Level70CharacterId);
                Check("relogin restores filtered roster, adventure group, and assignment",
                    restored.ManageLevel == expectedAdventureGroup.ManageLevel
                    && restored.ManagePoint == expectedAdventureGroup.TotalPoint
                    && restored.Records.Count == 3
                    && restored.Records.All(record => record.CharacterId != LowLevelCharacterId)
                    && restoredAssignment.State == MercenaryExpeditionState.InProgress
                    && restoredAssignment.RemainingSeconds == 2 * 3600
                    && restoredAssignment.AreaIndex == 0
                    && restoredAssignment.PeriodIndex == 0
                    && restoredAssignment.AvatarBonusTier == 1
                    && restoredWaiting.State == MercenaryExpeditionState.Waiting
                    && restoredWaiting.RemainingSeconds == -clock.Now
                    && restoredWaiting.AreaIndex == byte.MaxValue
                    && restoredWaiting.PeriodIndex == byte.MaxValue);

                clock.Now = firstAssignment.StartTime + 3600 + 5;
                var earlyReturn = service.Return(AccountId, Level85CharacterId, 7);
                Check("early return settles atomically and releases restrictions",
                    earlyReturn.Success
                    && earlyReturn.Reward.CompletedHours == 1
                    && earlyReturn.Reward.IsEarlyReturn
                    && earlyReturn.Reward.ReturnPurpose == 7
                    && earlyReturn.Reward.DeliveryStatus == "pending"
                    && earlyReturn.Reward.DeliveryAttempts == 0
                    && repository.ListPendingOutbox().Any(x => x.OutboxId == earlyReturn.Reward.OutboxId)
                    && restrictions.CanDelete(Level85CharacterId)
                    && restrictions.CanMutateAppearance(Level85CharacterId)
                    && restrictions.CanEnterContent(Level85CharacterId));

                var repeatedSettlement = repository.Settle(
                    firstAssignment,
                    new MercenaryRewardCalculator().Calculate(firstAssignment, config, clock.Now),
                    99);
                Check("outbox settlement is idempotent",
                    repeatedSettlement.OutboxId == earlyReturn.Reward.OutboxId
                    && repeatedSettlement.ReturnPurpose == 7
                    && CountRows(fixture.DatabasePath, "mercenary_reward_outbox") == 1);

                clock.Now += 100;
                var oldDispatch = service.Dispatch(AccountId, 0, RandomCharacterId, 0, 0);
                clock.Now += 3600 + 1;
                var redeploy = service.Dispatch(AccountId, 0, RandomCharacterId, 2, 1);
                Check("valid redeploy atomically settles old task and creates new task",
                    redeploy.Success
                    && redeploy.SettledPreviousReward != null
                    && redeploy.SettledPreviousReward.AssignmentId == oldDispatch.Assignment.AssignmentId
                    && redeploy.SettledPreviousReward.ReturnPurpose == MercenaryService.RedeployReturnPurpose
                    && redeploy.SettledPreviousReward.CompletedHours == 1
                    && redeploy.Assignment.AssignmentId != oldDispatch.Assignment.AssignmentId
                    && repository.GetAssignment(AccountId, RandomCharacterId)?.AssignmentId
                        == redeploy.Assignment.AssignmentId);

                CreateInsertAbortTrigger(fixture.DatabasePath, RandomCharacterId, 0);
                var beforeFailedRedeploy = repository.GetAssignment(AccountId, RandomCharacterId);
                clock.Now += 3600;
                var failedRedeploy = service.Dispatch(AccountId, 0, RandomCharacterId, 0, 0);
                DropTrigger(fixture.DatabasePath, "mercenary_selftest_insert_abort");
                Check("failed redeploy rolls back outbox and old assignment deletion",
                    failedRedeploy.Status == MercenaryOperationStatus.PersistenceFailure
                    && repository.GetAssignment(AccountId, RandomCharacterId)?.AssignmentId
                        == beforeFailedRedeploy.AssignmentId
                    && repository.GetOutboxByAssignment(beforeFailedRedeploy.AssignmentId) == null);

                clock.Now += 100;
                var rollbackDispatch = service.Dispatch(AccountId, 0, Level70CharacterId, 0, 0);
                CreateDeleteAbortTrigger(fixture.DatabasePath, Level70CharacterId);
                clock.Now += 3600;
                var failedReturn = service.Return(AccountId, Level70CharacterId, 3);
                DropTrigger(fixture.DatabasePath, "mercenary_selftest_delete_abort");
                Check("failed return rolls back outbox insert",
                    failedReturn.Status == MercenaryOperationStatus.PersistenceFailure
                    && repository.GetAssignment(AccountId, Level70CharacterId)?.AssignmentId
                        == rollbackDispatch.Assignment.AssignmentId
                    && repository.GetOutboxByAssignment(rollbackDispatch.Assignment.AssignmentId) == null);

                var randomConfig = BuildHiddenRandomConfig();
                var randomService = CreateService(fixture, repository, clock, randomConfig, avatarTier: 0);
                var hiddenArea = randomService.Dispatch(AccountId, 0, Level70CharacterId, 0, 0);
                var randomDispatch = randomService.Dispatch(AccountId, 0, Level70CharacterId, 1, 0);
                Check("random entry filters by level without exposing hidden real areas",
                    hiddenArea.Status == MercenaryOperationStatus.InvalidArea
                    && randomDispatch.Success
                    && randomDispatch.Assignment.AreaIndex == 0);
            }
        }

        private static void TestAvatarBonusTier()
        {
            using (var fixture = new Fixture())
            {
                var now = 1700000000;
                var config = BuildConfig();
                for (var slot = 0; slot < AvatarItemIds.Length; slot++)
                    InsertEquippedAvatar(fixture.DatabasePath, Level85CharacterId, slot, AvatarItemIds[slot], 0);

                var expected = config.ClampAvatarTier(
                    AvatarItemIds.Min(itemId => ItemMetadataResolver.Resolve(itemId).Rarity));
                var provider = new MercenaryAvatarBonusTierProvider(
                    fixture.DatabasePath,
                    ServerPaths.SchemaFilePath);
                var completeTier = provider.ResolveTier(Level85CharacterId, now, config);

                ExecuteSql(
                    fixture.DatabasePath,
                    @"DELETE FROM character_inventory_items
WHERE character_id=@cid AND list_type=@listType AND slot_index=7;",
                    ("@listType", (int)InventoryListType.Equipment),
                    ("@cid", Level85CharacterId));
                var missingTier = provider.ResolveTier(Level85CharacterId, now, config);

                InsertEquippedAvatar(
                    fixture.DatabasePath,
                    Level85CharacterId,
                    7,
                    AvatarItemIds[7],
                    now);
                var expiredTier = provider.ResolveTier(Level85CharacterId, now, config);

                ExecuteSql(
                    fixture.DatabasePath,
                    @"UPDATE character_avatar_detail
SET expire_date=@expire
WHERE item_uid=@avatarUid;",
                    ("@avatarUid", 100000 + 7),
                    ("@expire", now + 1));
                var unexpiredTier = provider.ResolveTier(Level85CharacterId, now, config);

                Check("avatar tier requires eight unexpired slots and uses minimum grade",
                    completeTier == expected
                    && missingTier == 0
                    && expiredTier == 0
                    && unexpiredTier == expected);

                Check("official avatar grade boundary treats grade above three as no bonus",
                    MercenaryAvatarBonusTierProvider.ResolveOfficialTier(8, 3, config)
                        == config.ClampAvatarTier(3)
                    && MercenaryAvatarBonusTierProvider.ResolveOfficialTier(8, 4, config) == 0
                    && MercenaryAvatarBonusTierProvider.ResolveOfficialTier(8, 10, config) == 0);
            }
        }

        private static MercenaryService CreateService(
            Fixture fixture,
            MercenaryRepository repository,
            IMercenaryTimeProvider clock,
            MercenaryConfig config,
            int avatarTier,
            IMercenaryMailDelivery mailDelivery = null)
        {
            return new MercenaryService(
                repository,
                fixture.Characters,
                new FixedAvatarTier(avatarTier),
                rewards: new MercenaryRewardCalculator(random: FixedRandom.Instance),
                mailDelivery: mailDelivery,
                time: clock,
                getConfig: () => config);
        }

        private static MercenaryConfig BuildConfig(bool monsterReward = false)
        {
            var config = new MercenaryConfig
            {
                BaseTimeUnitSeconds = 3600,
                DefaultDropRatePerHour = 10000,
            };
            config.LevelRewards.Add(new MercenaryLevelReward
            {
                MinimumLevel = 70,
                BaseGoldPerHour = 100,
                ItemProbabilityPerHour = 10000,
            });
            config.LevelRewards.Add(new MercenaryLevelReward
            {
                MinimumLevel = 85,
                BaseGoldPerHour = 200,
                ItemProbabilityPerHour = 10000,
            });
            config.Periods.Add(new MercenaryPeriodOption { Index = 0, Hours = 2, BonusMultiplier = 1.0 });
            config.Periods.Add(new MercenaryPeriodOption { Index = 1, Hours = 6, BonusMultiplier = 1.2 });
            config.AvatarBonuses[0] = 1.0;
            config.AvatarBonuses[1] = 2.0;
            config.AvatarBonuses[2] = 2.0;
            config.AvatarBonuses[3] = 3.0;
            config.CriticalOptions.Add(new MercenaryCriticalOption { Weight = 10000, Multiplier = 1.0 });

            var area70 = CreateRewardArea(0, worldMapId: 7, minimumLevel: 70, visible: true, monsterReward);
            var area85 = CreateRewardArea(1, worldMapId: 12, minimumLevel: 85, visible: true, monsterReward);
            config.Areas.Add(area70);
            config.Areas.Add(area85);
            config.Areas.Add(new MercenaryCompetitionArea
            {
                Index = 2,
                WorldMapId = -1,
                MinimumLevel = 70,
                Visible = true,
            });
            return config;
        }

        private static MercenaryConfig BuildHiddenRandomConfig()
        {
            var config = BuildConfig();
            config.Areas.Clear();
            config.Areas.Add(CreateRewardArea(0, 7, 70, visible: false, monsterReward: false));
            config.Areas.Add(new MercenaryCompetitionArea
            {
                Index = 1,
                WorldMapId = -1,
                MinimumLevel = 70,
                Visible = true,
            });
            return config;
        }

        private static MercenaryCompetitionArea CreateRewardArea(
            byte index,
            int worldMapId,
            int minimumLevel,
            bool visible,
            bool monsterReward)
        {
            var area = new MercenaryCompetitionArea
            {
                Index = index,
                WorldMapId = worldMapId,
                MinimumLevel = minimumLevel,
                Visible = visible,
            };
            var group = new MercenaryRewardGroup { Weight = 10000, MessageKey = "selftest" };
            if (monsterReward)
                group.Monsters.Add(new MercenaryWeightedEntry { Value = 5001, Weight = 10000 });
            else
                group.Items.Add(new MercenaryWeightedEntry { Value = RewardItemId, Weight = 10000 });
            area.RewardGroups.Add(group);
            return area;
        }

        private static bool ReadMercenaryRecord(
            byte[] body,
            ref int offset,
            out MercenaryCharacterInfo record)
        {
            record = null;
            if (body == null || offset < 0 || body.Length - offset < 8)
                return false;

            var characterId = BitConverter.ToInt32(body, offset);
            offset += 4;
            var nameLength = BitConverter.ToInt32(body, offset);
            offset += 4;
            if (nameLength < 0 || nameLength > body.Length - offset - 8)
                return false;

            var name = new byte[nameLength];
            Buffer.BlockCopy(body, offset, name, 0, nameLength);
            offset += nameLength;
            record = new MercenaryCharacterInfo
            {
                CharacterId = characterId,
                Name = name,
                State = (MercenaryExpeditionState)body[offset++],
                RemainingSeconds = BitConverter.ToInt32(body, offset),
            };
            offset += 4;
            record.AreaIndex = body[offset++];
            record.PeriodIndex = body[offset++];
            record.AvatarBonusTier = body[offset++];
            return true;
        }

        private static bool Matches<T>(IEnumerable<T> actual, params T[] expected)
        {
            return actual != null && expected != null && actual.SequenceEqual(expected);
        }

        private static void RunCase(string name, Action test)
        {
            try
            {
                Console.WriteLine($"--- {name} ---");
                test();
            }
            catch (Exception ex)
            {
                Check(name + " throws no exception", false);
                Console.WriteLine(ex);
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++;
            else _fail++;
        }

        private static int CountRows(string databasePath, string table)
        {
            using (var connection = new SqliteConnection(
                SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $"SELECT COUNT(*) FROM {table};";
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static int CountMessages(string databasePath, string idempotencyKey)
        {
            using (var connection = new SqliteConnection(
                SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT COUNT(*) FROM mailbox_messages
WHERE idempotency_key = @key;";
                    command.Parameters.AddWithValue("@key", idempotencyKey);
                    return Convert.ToInt32(command.ExecuteScalar());
                }
            }
        }

        private static void ExecuteSql(string databasePath, string sql, params (string Name, object Value)[] parameters)
        {
            using (var connection = new SqliteConnection(
                SqliteDatabaseBootstrap.Initialize(databasePath, ServerPaths.SchemaFilePath)))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = sql;
                    foreach (var parameter in parameters)
                        command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void CreateInsertAbortTrigger(string databasePath, int characterId, int areaIndex)
        {
            ExecuteSql(
                databasePath,
                $@"CREATE TRIGGER mercenary_selftest_insert_abort
BEFORE INSERT ON account_mercenary_assignments
WHEN NEW.character_id = {characterId} AND NEW.area_index = {areaIndex}
BEGIN
    SELECT RAISE(ABORT, 'forced mercenary replacement failure');
END;");
        }

        private static void CreateDeleteAbortTrigger(string databasePath, int characterId)
        {
            ExecuteSql(
                databasePath,
                $@"CREATE TRIGGER mercenary_selftest_delete_abort
BEFORE DELETE ON account_mercenary_assignments
WHEN OLD.character_id = {characterId}
BEGIN
    SELECT RAISE(ABORT, 'forced mercenary settlement failure');
END;");
        }

        private static void DropTrigger(string databasePath, string triggerName)
        {
            ExecuteSql(databasePath, $"DROP TRIGGER IF EXISTS {triggerName};");
        }

        private static string TempDatabasePath(string prefix)
            => Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N") + ".db");

        private static void DeleteTempDatabase(string databasePath)
        {
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }

        private sealed class FixedTime : IMercenaryTimeProvider
        {
            public int Now { get; set; }
            public int GetUnixTimeSeconds() => Now;
        }

        private sealed class FixedAvatarTier : IMercenaryAvatarBonusTierProvider
        {
            private readonly int _tier;
            public FixedAvatarTier(int tier) => _tier = tier;
            public int ResolveTier(int characterId, int nowUnixSeconds, MercenaryConfig config) => _tier;
        }

        private sealed class FixedMonsterDrops : IMercenaryMonsterDropSource
        {
            public IReadOnlyList<MonsterDropTable.DropPoolEntry> GetDropPool(int monsterCode)
            {
                return new[]
                {
                    new MonsterDropTable.DropPoolEntry { ItemId = 777777, Weight = 10000 },
                };
            }
        }

        private static void InsertEquippedAvatar(
            string databasePath,
            int characterId,
            int slot,
            int itemId,
            int expireDate)
        {
            var avatarUid = 100000 + slot;
            var core = new ItemCore
            {
                ItemKind = ItemCore.KindAvatar,
                ItemId = itemId,
                AvatarUid = avatarUid,
            };
            ExecuteSql(
                databasePath,
                @"INSERT INTO character_inventory_items(
    character_id, list_type, slot_index, item_core)
VALUES(@cid, @listType, @slot, @itemCore);
INSERT OR REPLACE INTO character_avatar_detail(
    item_uid, owner_id, character_id, item_id, expire_date, clear_avatar_id,
    jewel_socket, color1, color2, delete_date)
VALUES(@avatarUid, @cid, @cid, @itemId, @expireDate, 0, zeroblob(30), 0, 0, 0);",
                ("@cid", characterId),
                ("@listType", (int)InventoryListType.Equipment),
                ("@slot", slot),
                ("@itemCore", core.ToBytes()),
                ("@avatarUid", avatarUid),
                ("@itemId", itemId),
                ("@expireDate", expireDate));
        }

        private sealed class FixedRandom : IMercenaryRandomSource
        {
            public static readonly FixedRandom Instance = new FixedRandom();
            public int Next(int exclusiveMax) => 0;
            public long NextLong(long exclusiveMax) => 0;
        }

        private sealed class WeightedSequenceRandom : IMercenaryRandomSource
        {
            private readonly Queue<long> _values;

            public WeightedSequenceRandom(params long[] values)
            {
                _values = new Queue<long>(values ?? Array.Empty<long>());
            }

            public int Next(int exclusiveMax) => (int)NextValue(exclusiveMax);
            public long NextLong(long exclusiveMax) => NextValue(exclusiveMax);

            private long NextValue(long exclusiveMax)
            {
                if (_values.Count == 0 || exclusiveMax <= 1)
                    return 0;
                return Math.Min(exclusiveMax - 1, Math.Max(0, _values.Dequeue()));
            }
        }

        private sealed class FailedDelivery : IMercenaryMailDelivery
        {
            public static readonly FailedDelivery Instance = new FailedDelivery();
            public MercenaryMailDeliveryResult Deliver(MercenaryRewardOutboxEntry entry)
                => new MercenaryMailDeliveryResult
                {
                    Disposition = MercenaryMailDeliveryDisposition.Failed,
                    Error = "selftest failure",
                };
        }

        private sealed class Fixture : IDisposable
        {
            public string DatabasePath { get; } = TempDatabasePath("dfo-mercenary");
            public SqliteCharacterRepository Characters { get; }

            public Fixture()
            {
                var schema = ServerPaths.SchemaFilePath;
                var connectionString = SqliteDatabaseBootstrap.Initialize(DatabasePath, schema);
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT OR IGNORE INTO accounts(account_id, m_id, password_hash)
VALUES
    (1, 'mercenary-selftest-1', ''),
    (2, 'mercenary-selftest-2', '');";
                        command.ExecuteNonQuery();
                    }
                }

                Characters = new SqliteCharacterRepository(DatabasePath, schema);
                CreateCharacter(AccountId, Level85CharacterId, 85, 0);
                CreateCharacter(AccountId, Level70CharacterId, 70, 1);
                CreateCharacter(AccountId, RandomCharacterId, 80, 2);
                CreateCharacter(AccountId, LowLevelCharacterId, 69, 3);
                CreateCharacter(OtherAccountId, OtherCharacterId, 85, 0);
            }

            private void CreateCharacter(int accountId, int characterId, byte level, byte slot)
            {
                Characters.Create(new CharacterRecord
                {
                    CharacterId = characterId,
                    AccountId = accountId,
                    Name = Encoding.UTF8.GetBytes("mercenary-" + characterId),
                    Level = level,
                    SlotIndex = slot,
                });
            }

            public void Dispose() => DeleteTempDatabase(DatabasePath);
        }
    }
}

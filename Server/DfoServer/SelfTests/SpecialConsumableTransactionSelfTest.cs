using DfoServer.Game.Currency;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DfoServer.SelfTests
{
    internal static class SpecialConsumableTransactionSelfTest
    {
        private const short SourceSlot = 40;
        private const int HappyTokenGiftBoxItemId = 0x0098AAFE;
        private const int HappyTokenGrant = 1800;
        private const int MailRewardItemId = 3619;

        public static int Run()
        {
            var failures = 0;
            try
            {
                var normalPackageItemId = ResolveNormalPackageItemId();
                using (var fixture = new Fixture(
                    "special-consumable-inventory",
                    986300,
                    986301,
                    normalPackageItemId))
                {
                    fixture.CreateInventoryInsertFailureTrigger();
                    var committed = fixture.TryCommitBooster(
                        RejectingInventoryOverflowRewardSink.Instance,
                        out var failed,
                        out var persistenceFailed);
                    Check("package reward INSERT failure rejects commit",
                        !committed && failed?.ErrorCode == 0 && persistenceFailed,
                        ref failures);
                    Check("package reward failure restores source and dirty state",
                        fixture.HasInitialSourceState() && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    committed = fixture.TryCommitBooster(
                        RejectingInventoryOverflowRewardSink.Instance,
                        out var result,
                        out persistenceFailed);
                    Check("package reward retries after persistence recovery",
                        committed && !persistenceFailed
                        && fixture.HasCommittedBoosterState(result)
                        && fixture.HasNoDirtyState(),
                        ref failures);
                }

                using (var fixture = new Fixture(
                    "special-consumable-mailbox",
                    986310,
                    986311,
                    HappyTokenGiftBoxItemId))
                {
                    fixture.CreateMailboxAttachmentFailureTrigger();
                    var committed = fixture.TryCommitMailboxOverflow();
                    Check("mailbox attachment INSERT failure rejects overflow commit",
                        !committed && fixture.HasInitialSourceState()
                        && fixture.CountMailboxRows() == 0
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    fixture.CreateSourceDeleteFailureTrigger();
                    committed = fixture.TryCommitMailboxOverflow();
                    Check("mailbox delivery rolls back when source DELETE fails",
                        !committed && fixture.HasInitialSourceState()
                        && fixture.CountMailboxRows() == 0
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    committed = fixture.TryCommitMailboxOverflow();
                    Check("mailbox overflow and source consume retry atomically",
                        committed && fixture.HasCommittedMailboxState()
                        && fixture.HasNoDirtyState(),
                        ref failures);
                }

                using (var fixture = new Fixture(
                    "special-consumable-seria",
                    986320,
                    986321,
                    SeriaLuckItemConstants.ItemTemplateId))
                {
                    fixture.SetSeriaLuckValue(7);
                    fixture.CreateSeriaLuckUpdateFailureTrigger();
                    var committed = fixture.TryCommitBooster(
                        RejectingInventoryOverflowRewardSink.Instance,
                        out var failed,
                        out var persistenceFailed);
                    Check("Seria luck UPDATE failure rejects booster commit",
                        !committed && failed?.ErrorCode == 0 && persistenceFailed,
                        ref failures);
                    Check("Seria luck failure restores source rewards and luck value",
                        fixture.HasInitialSourceState()
                        && fixture.LoadSeriaLuckValue() == 7
                        && fixture.HasNoDirtyState(),
                        ref failures);

                    fixture.DropFailureTriggers();
                    committed = fixture.TryCommitBooster(
                        RejectingInventoryOverflowRewardSink.Instance,
                        out var result,
                        out persistenceFailed);
                    Check("Seria luck booster retries after persistence recovery",
                        committed && !persistenceFailed
                        && result?.IsSeriaLuckValueSource == true
                        && result.SeriaLuckValueBefore == 7
                        && result.SeriaLuckValueAfter == 8
                        && fixture.LoadSeriaLuckValue() == 8
                        && fixture.HasCommittedBoosterState(result)
                        && fixture.HasNoDirtyState(),
                        ref failures);
                }

                using (var fixture = new Fixture(
                    "special-consumable-happy-token",
                    986330,
                    986331,
                    HappyTokenGiftBoxItemId))
                {
                    var committed = fixture.TryCommitPackage0207(
                        out var result,
                        out var persistenceFailed);
                    Check("HappyToken Cera and package source commit atomically",
                        committed && !persistenceFailed
                        && result?.Rewards.Count == 1
                        && result.Rewards[0].SpecialOutcome?.Kind == SpecialRewardKind.HappyTokenCera
                        && result.Rewards[0].GrantedCount == HappyTokenGrant
                        && fixture.LoadWallet().HappyTokenCera == HappyTokenGrant
                        && fixture.Lease.Inventory.PendingHappyTokenCeraGrant == 0
                        && fixture.HasCommittedSourceState()
                        && fixture.HasNoDirtyState(),
                        ref failures);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] special consumable transaction selftest threw: " + ex);
                failures++;
            }

            Console.WriteLine(failures == 0
                ? "SpecialConsumableTransactionSelfTest OK"
                : "SpecialConsumableTransactionSelfTest FAIL (" + failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string label, bool condition, ref int failures)
        {
            Console.WriteLine("[" + (condition ? "PASS" : "FAIL") + "] " + label);
            if (!condition)
                failures++;
        }

        private static int ResolveNormalPackageItemId()
        {
            var list = LstFile.Parse(GameWorld.PvfArchiveAccessor.ReadText("stackable/stackable.lst"));
            foreach (var entry in list.Entries)
            {
                if (entry == null || entry.Id <= 0)
                    continue;

                var stackable = StackableItemProvider.Load(entry.Id);
                if (stackable == null
                    || !InventoryPackageRewardResolver.NormalizeStackableType(stackable.StackableType)
                        .Equals("[cera package]", StringComparison.OrdinalIgnoreCase))
                    continue;

                InventoryPackageRewardResolver.ResolveNeedMaterial(
                    entry.Id,
                    stackable,
                    out var materialItemTemplateId,
                    out var materialCount);
                if (materialItemTemplateId > 0 || materialCount > 0)
                    continue;

                var inventory = new InventoryService(1, 1);
                var source = ItemCore.Create(ItemCore.KindConsumable, entry.Id);
                source.Count = 1;
                inventory.SetItem(InventoryListType.Main, SourceSlot, source);
                if (!InventorySpecialConsumableService.TryUseBoosterItem(
                        inventory,
                        new BoosterUseRequest
                        {
                            SlotIndex = SourceSlot,
                            ExpectedItemTemplateId = entry.Id,
                            RequestedCount = 1,
                        },
                        "swordman",
                        RejectingInventoryOverflowRewardSink.Instance,
                        out var result))
                    continue;

                if (result.Rewards.Any(reward =>
                    reward != null
                    && reward.SpecialOutcome == null
                    && reward.ItemTemplateId != entry.Id
                    && reward.SlotIndex >= 0))
                    return entry.Id;
            }

            throw new InvalidOperationException("unable to resolve deterministic normal package fixture");
        }

        private sealed class Fixture : IDisposable
        {
            private readonly int _accountId;
            private readonly int _characterId;
            private readonly int _sourceItemId;
            private readonly byte[] _initialSourceBytes;

            internal Fixture(
                string prefix,
                int accountId,
                int characterId,
                int sourceItemId)
            {
                _accountId = accountId;
                _characterId = characterId;
                _sourceItemId = sourceItemId;
                DatabasePath = Path.Combine(
                    Path.GetTempPath(),
                    prefix + "-" + Guid.NewGuid().ToString("N") + ".db");
                Database = new GameDatabase(DatabasePath, ServerPaths.SchemaFilePath);
                SeedCharacter(Database, accountId, characterId);

                var inventory = new InventoryService(characterId, accountId, Database);
                inventory.SetListParam16(
                    InventoryListType.Main,
                    ItemSlotBoundService.MainExpandStageFull);
                ItemMetadataResolver.TryResolveItemKind(sourceItemId, out var itemKind);
                if (itemKind == ItemCore.KindUnknown)
                    itemKind = ItemCore.KindConsumable;
                var source = ItemCore.Create(itemKind, sourceItemId);
                source.Count = 1;
                inventory.SetItem(InventoryListType.Main, SourceSlot, source);

                var seedLease = new InventoryLease(Guid.NewGuid(), characterId, inventory, 1);
                if (!InventoryPersistenceService.SaveDirty(seedLease))
                    throw new InvalidOperationException("unable to persist special consumable fixture");

                using (var connection = Database.OpenConnection())
                {
                    inventory = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        Database);
                }

                Lease = InventoryContext.Register(Guid.NewGuid(), characterId, inventory);
                _initialSourceBytes = Lease.Inventory
                    .GetItem(InventoryListType.Main, SourceSlot)
                    ?.ToBytes();
            }

            internal string DatabasePath { get; }
            internal IGameDatabase Database { get; }
            internal InventoryLease Lease { get; }

            internal bool TryCommitBooster(
                IInventoryOverflowRewardSink overflowSink,
                out BoosterUseResult result,
                out bool persistenceFailed)
                => InventorySpecialConsumableCommitService.TryCommitBoosterItem(
                    Lease,
                    new BoosterUseRequest
                    {
                        SlotIndex = SourceSlot,
                        ExpectedItemTemplateId = _sourceItemId,
                        RequestedCount = 1,
                    },
                    "swordman",
                    overflowSink,
                    out result,
                    out persistenceFailed);

            internal bool TryCommitPackage0207(
                out BoosterUseResult result,
                out bool persistenceFailed)
                => InventorySpecialConsumableCommitService.TryCommitPackage0207(
                    Lease,
                    SourceSlot,
                    Array.Empty<int>(),
                    RejectingInventoryOverflowRewardSink.Instance,
                    out result,
                    out persistenceFailed);

            internal bool TryCommitMailboxOverflow()
            {
                var sink = new MailboxInventoryOverflowRewardSink(
                    new MailboxService(new MailboxRepository(Database)));
                var rewards = new[]
                {
                    InventoryRewardGrantRequest.Create(
                        MailRewardItemId,
                        2,
                        ItemCreateReason.PackageOpen),
                };
                return OnlineInventoryMutationCommitCoordinator.TryCommit(
                    Lease,
                    "special-consumable-mailbox-selftest",
                    (connection, transaction) =>
                    {
                        var transactionSink = new TransactionBoundInventoryOverflowRewardSink(
                            connection,
                            transaction,
                            sink);
                        if (!transactionSink.TryDeliver(
                                Lease.Inventory,
                                rewards,
                                out _))
                            return false;

                        return InventoryDeleteService.TryConsumeFromSlot(
                            Lease.Inventory,
                            InventoryListType.Main,
                            SourceSlot,
                            _sourceItemId,
                            1,
                            out _);
                    });
            }

            internal bool HasInitialSourceState()
            {
                var online = Lease.Inventory.GetItem(InventoryListType.Main, SourceSlot);
                var persisted = Load().GetItem(InventoryListType.Main, SourceSlot);
                return _initialSourceBytes != null
                    && online?.ToBytes().SequenceEqual(_initialSourceBytes) == true
                    && persisted?.ToBytes().SequenceEqual(_initialSourceBytes) == true;
            }

            internal bool HasCommittedSourceState()
            {
                var online = Lease.Inventory.GetItem(InventoryListType.Main, SourceSlot);
                var persisted = Load().GetItem(InventoryListType.Main, SourceSlot);
                return (online == null || online.ItemId != _sourceItemId)
                    && (persisted == null || persisted.ItemId != _sourceItemId);
            }

            internal bool HasCommittedBoosterState(BoosterUseResult result)
            {
                if (result == null || !HasCommittedSourceState())
                    return false;

                var persisted = Load();
                return result.Rewards.Any(reward =>
                {
                    if (reward == null || reward.SpecialOutcome != null || reward.SlotIndex < 0)
                        return false;
                    var online = Lease.Inventory.GetItem(reward.ListType, reward.SlotIndex);
                    var stored = persisted.GetItem(reward.ListType, reward.SlotIndex);
                    return online?.ItemId == reward.ItemTemplateId
                        && stored?.ItemId == reward.ItemTemplateId;
                });
            }

            internal bool HasCommittedMailboxState()
                => HasCommittedSourceState() && CountMailboxRows() >= 3;

            internal bool HasNoDirtyState()
                => Lease.Inventory.DirtyListTypes.Count == 0
                    && Lease.Inventory.DirtyMainVirtualCountSlots.Count == 0
                    && Lease.Inventory.PendingHappyTokenCeraGrant == 0;

            internal WalletSnapshot LoadWallet()
            {
                using var connection = Database.OpenConnection();
                return CurrencyService.LoadWallet(connection, null, _characterId);
            }

            internal int LoadSeriaLuckValue()
            {
                using var connection = Database.OpenConnection();
                return Game.Accounts.SqliteAccountRepository.LoadSeriaLuckValue(
                    connection,
                    null,
                    _accountId);
            }

            internal void SetSeriaLuckValue(int value)
                => Execute($"UPDATE accounts SET seria_luck_value={value} WHERE account_id={_accountId};");

            internal long CountMailboxRows()
            {
                using var connection = Database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT
    (SELECT COUNT(*) FROM mailbox_messages)
  + (SELECT COUNT(*) FROM mailbox_recipients)
  + (SELECT COUNT(*) FROM mailbox_attachments)
  + (SELECT COUNT(*) FROM mailbox_system_mail_audit)
  + (SELECT COUNT(*) FROM mailbox_system_mail_audit_attachments);";
                return Convert.ToInt64(command.ExecuteScalar());
            }

            internal void CreateInventoryInsertFailureTrigger()
                => Execute($"CREATE TRIGGER fail_special_reward_insert BEFORE INSERT ON character_inventory_items WHEN NEW.character_id={_characterId} BEGIN SELECT RAISE(ABORT, 'injected special reward failure'); END;");

            internal void CreateMailboxAttachmentFailureTrigger()
                => Execute("CREATE TRIGGER fail_special_mail_attachment BEFORE INSERT ON mailbox_attachments BEGIN SELECT RAISE(ABORT, 'injected special mail attachment failure'); END;");

            internal void CreateSourceDeleteFailureTrigger()
                => Execute($"CREATE TRIGGER fail_special_source_delete BEFORE DELETE ON character_inventory_items WHEN OLD.character_id={_characterId} AND OLD.list_type=0 AND OLD.slot_index={SourceSlot} BEGIN SELECT RAISE(ABORT, 'injected special source delete failure'); END;");

            internal void CreateSeriaLuckUpdateFailureTrigger()
                => Execute($"CREATE TRIGGER fail_special_seria_luck BEFORE UPDATE OF seria_luck_value ON accounts WHEN OLD.account_id={_accountId} BEGIN SELECT RAISE(ABORT, 'injected Seria luck failure'); END;");

            internal void DropFailureTriggers()
            {
                if (!File.Exists(DatabasePath))
                    return;
                Execute(@"
DROP TRIGGER IF EXISTS fail_special_reward_insert;
DROP TRIGGER IF EXISTS fail_special_mail_attachment;
DROP TRIGGER IF EXISTS fail_special_source_delete;
DROP TRIGGER IF EXISTS fail_special_seria_luck;");
            }

            private InventoryService Load()
            {
                using var connection = Database.OpenConnection();
                return InventoryService.LoadFromDb(
                    connection,
                    _characterId,
                    _accountId,
                    Database);
            }

            private void Execute(string sql)
            {
                using var connection = Database.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            public void Dispose()
            {
                try { DropFailureTriggers(); } catch { }
                InventoryContext.Unregister(Lease.SessionId, Lease.CharacterId);
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                {
                    try
                    {
                        if (File.Exists(DatabasePath + suffix))
                            File.Delete(DatabasePath + suffix);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void SeedCharacter(
            IGameDatabase database,
            int accountId,
            int characterId)
        {
            database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO accounts(account_id,m_id,password_hash)
VALUES(@aid,'special-consumable-transaction','');
INSERT INTO characters(character_id,account_id,name,level,town_id,area_id,direction,area_state)
VALUES(@cid,@aid,@name,86,1,0,5,3);";
                command.Parameters.AddWithValue("@aid", accountId);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue(
                    "@name",
                    Encoding.UTF8.GetBytes("SpecialConsumableTransaction"));
                command.ExecuteNonQuery();
            });
        }
    }
}

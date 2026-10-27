using System;
using System.IO;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Auction.Storage;
using DfoServer.Game.Auction.Transactions;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class AuctionRollbackFenceSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool condition)
            { Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] auction rollback fence: {name}"); if (condition) passed++; else failed++; }
            bool Rejects(Func<bool> action)
            {
                try { return !action(); }
                catch (InvalidOperationException) { return true; }
            }
            bool RejectsRegistration(Func<InventoryLease> action)
            {
                try { action(); return false; }
                catch (InvalidOperationException) { return true; }
                catch (ArgumentException) { return true; }
            }
            bool RejectsReload(Func<bool> action)
            {
                try { return !action(); }
                catch (SqliteException) { return true; }
                catch (IOException) { return true; }
                catch (InvalidOperationException) { return true; }
            }
            const int character = 78401;
            var session = Guid.NewGuid(); var replacementSession = Guid.NewGuid();
            var root = Path.Combine(AppContext.BaseDirectory, ".scratch", "auction-rollback-fence-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            GameDatabase database = null;
            InventoryLease originalLease = null;
            try
            {
                database = new GameDatabase(Path.Combine(root, "inventory.db"), ServerPaths.SchemaFilePath);
                database.Write((c, t) => {
                    using var cmd = c.CreateCommand(); cmd.Transaction = t;
                    cmd.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(78401,'auction-fence','');"
                        + "INSERT INTO characters(character_id,account_id,name,level) VALUES(78401,78401,'auction-fence',85);";
                    cmd.ExecuteNonQuery();
                });
                var offlineBaseline = new InventoryLease(Guid.NewGuid(), character, new InventoryService(character, character, database), 1);
                offlineBaseline.Inventory.SetMainVirtualCount(0, 1000);
                offlineBaseline.Inventory.SetItem(InventoryListType.Main, 65, new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3001, Count = 10 });
                Check("never-managed explicit offline lease may persist without a physical online owner", InventoryPersistenceService.SaveDirty(offlineBaseline));
                // The transaction and later saves use the real database. Only
                // the coordinator's captured raw reload connection is unusable.
                // Set it before TryCommit, not in the later pre-commit fault.
                var wrapper = new ReloadFaultDatabase(database, Path.Combine(root, "missing-reload-directory", "inventory.db"));
                originalLease = InventoryContext.Register(session, new InventoryService(character, character, wrapper));
                var originalModel = originalLease.Inventory;
                originalModel.SetMainVirtualCount(0, 1000);
                originalModel.SetItem(InventoryListType.Main, 65, new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3001, Count = 10 });
                Check("healthy baseline persists through real connection", InventoryPersistenceService.SaveDirty(originalLease));
                InventoryService ReloadStored() => database.Read(c => InventoryService.LoadFromDb(c, character, character, database));
                bool StoredBaseline()
                {
                    var stored = ReloadStored();
                    return stored.GetMainVirtualCount(0).Count == 1000 && stored.GetItem(InventoryListType.Main, 65)?.Count == 10;
                }
                InventoryLease SyntheticWithChangedGold()
                {
                    var model = ReloadStored(); model.SetMainVirtualCount(0, 777);
                    return new InventoryLease(Guid.NewGuid(), character, model, 999);
                }
                Check("synthetic offline lease cannot bypass a live physical owner", Rejects(() =>
                    InventoryPersistenceService.SaveDirty(SyntheticWithChangedGold())) && StoredBaseline());
                long Scalar(string sql) => database.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); });
                const long avatarUid = 7840101;
                const int creatureUid = 7840102;
                var avatar = new AvatarDetail { AvatarUid = avatarUid, OwnerId = character, CharacterId = character,
                    ItemId = 1001, Color1 = 17, Color2 = 23 };
                var creature = new CreatureDetail { Uid = creatureUid, NameBytes = new byte[] { 65, 66 }, Field04 = 80, FieldAfterValue32 = 7 };
                var oldAvatarManager = originalModel.AvatarDetails;
                var oldCreatureManager = originalModel.CreatureDetails;
                bool StoredDetails()
                    => Scalar("SELECT count(*) FROM character_avatar_detail WHERE item_uid=7840101 AND color1=17 AND color2=23;") == 1
                        && Scalar("SELECT count(*) FROM character_creatures WHERE character_id=78401 AND creature_key=7840102 AND field04=80 AND field_after_value=7;") == 1;
                Check("healthy owned child managers persist explicit detail baselines", oldAvatarManager.Put(avatar)
                    && oldCreatureManager.Put(creature) && StoredDetails());
                var codec = new AuctionItemCodec(new NativeAuctionInstanceExtension());
                var operation = Guid.NewGuid();
                var plan = AuctionPublicAssetPlan.Create(codec, 100,
                    new[] { new AuctionItemDebit(InventoryListType.Main, 65, 3, codec.Capture(originalModel, InventoryListType.Main, 65)) },
                    new[] { new MailboxSendRequest {
                        SenderCharacterId = character, SenderAccountId = character, SenderName = "fixture system",
                        ReceiverCharacterId = character, ReceiverAccountId = character, ReceiverName = "fixture receiver",
                        Gold = 20, Text = "fixture rolled back leg", Unlimited = true,
                        IdempotencyKey = "auction:" + operation.ToString("N") + ":fence-leg" } });
                var intent = new AuctionOperationIntent(operation, "fixture-fence:" + operation.ToString("N"), AuctionOperationAction.List,
                    character, character, 0, 1792700000, JsonSerializer.Serialize(new { publicAssetPlan = plan.Serialize() }));
                bool observedDebitBeforeFailure = false;
                var rail = new AuctionPublicAssetCommit(wrapper, codec, new object(), point => {
                    if (point != AuctionAssetFaultPoint.BeforePublicCommit) return;
                    observedDebitBeforeFailure = originalModel.GetMainVirtualCount(0).Count == 900
                        && originalModel.GetItem(InventoryListType.Main, 65)?.Count == 7;
                    throw new InvalidOperationException("fixture failure after debit/mail/receipt before public commit");
                });
                var outcome = rail.Execute(intent, plan, originalLease);
                Check("actual item and wallet debit occurred before the injected rollback", observedDebitBeforeFailure);
                Check("public effects and receipt really rolled back despite mutated memory", StoredBaseline()
                    && Scalar("SELECT count(*) FROM mailbox_messages;") == 0
                    && database.Read(c => AuctionPublicReceiptRepository.Find(c, null, intent)) == null);
                Check("failed reload retains pending operation and poisons the exact model", outcome.State == AuctionAssetCommitState.Pending
                    && ReferenceEquals(originalLease.Inventory, originalModel) && !originalModel.IsPersistenceTrusted
                    && originalModel.GetMainVirtualCount(0).Count == 900 && originalModel.GetItem(InventoryListType.Main, 65)?.Count == 7);
                avatar.Color1 = 99; creature.Field04 = 1;
                Check("poisoned parent prevents child Avatar direct Put and Remove", Rejects(() => oldAvatarManager.Put(avatar))
                    && Rejects(() => oldAvatarManager.Remove(avatarUid)) && StoredDetails());
                Check("poisoned parent prevents child Creature direct Put and Remove", Rejects(() => oldCreatureManager.Put(creature))
                    && Rejects(() => oldCreatureManager.Remove(creatureUid)) && StoredDetails());
                Check("poisoned actor is unavailable through every gameplay ownership lookup",
                    !InventoryContext.IsCurrentLease(originalLease, session, character)
                    && !InventoryContext.TryGet(character, out _)
                    && !InventoryContext.TryGetLease(character, out _)
                    && !InventoryContext.TryGetOwnedLease(session, character, out _)
                    && !InventoryContext.TryGetForSession(session, character, out _));
                Check("direct SaveDirty cannot persist the rolled-back in-memory debit", Rejects(() => InventoryPersistenceService.SaveDirty(originalLease)) && StoredBaseline());
                Check("wallet save-and-load cannot launder a poisoned model", Rejects(() => InventoryPersistenceService.SaveDirtyAndLoadWallet(originalLease, out _)) && StoredBaseline());
                bool transactionSaveRejected;
                using (var c = database.OpenConnection())
                using (var t = c.BeginTransaction())
                {
                    transactionSaveRejected = Rejects(() => InventoryPersistenceService.SaveDirtyInTransaction(c, t, originalLease));
                    // Even a caller which commits after false cannot flush the model.
                    t.Commit();
                }
                Check("external public transaction cannot flush poisoned model", transactionSaveRejected && StoredBaseline());
                InventoryPersistenceService.SaveAllDirty();
                Check("minute save cannot later commit an absent-receipt debit", StoredBaseline()
                    && database.Read(c => AuctionPublicReceiptRepository.Find(c, null, intent)) == null);
                Check("hidden fenced physical owner still blocks synthetic same-character persistence", Rejects(() =>
                    InventoryPersistenceService.SaveDirty(SyntheticWithChangedGold())) && StoredBaseline());
                Check("both disconnect unregister paths fail closed without saving", !InventoryContext.Unregister(session)
                    && !InventoryContext.Unregister(session, character) && StoredBaseline());
                Check("same-session replacement cannot first save poisoned previous lease", RejectsRegistration(() =>
                    InventoryContext.Register(session, ReloadStored())) && StoredBaseline());
                Check("new-session takeover cannot first save poisoned previous lease", RejectsRegistration(() =>
                    InventoryContext.Register(replacementSession, ReloadStored())) && StoredBaseline());
                bool blockedCallbackRan = false;
                Check("ordinary inventory coordinator refuses fenced actor before callback", !OnlineInventoryMutationCommitCoordinator.TryCommit(
                    originalLease, "fixture-poisoned-owner", (c, t) => { blockedCallbackRan = true; return true; })
                    && !blockedCallbackRan && StoredBaseline());
                Check("failed explicit reload cannot silently report successful replacement", RejectsReload(() => InventoryRollbackRecoveryService.ReloadOnlineInventory(
                    wrapper.ConnectionString, originalLease)) && !originalModel.IsPersistenceTrusted && ReferenceEquals(originalLease.Inventory, originalModel));

                // This is model recovery only: it does not re-execute the pending
                // asset operation or resolve a private journal from an absent read.
                bool recovered = InventoryRollbackRecoveryService.ReloadOnlineInventory(database.ConnectionString, originalLease);
                var recoveredModel = originalLease.Inventory;
                Check("explicit full authoritative reload replaces fenced model on owned lease", recovered
                    && !ReferenceEquals(recoveredModel, originalModel) && recoveredModel.IsPersistenceTrusted
                    && InventoryContext.IsCurrentLease(originalLease, session, character)
                    && recoveredModel.GetMainVirtualCount(0).Count == 1000 && recoveredModel.GetItem(InventoryListType.Main, 65)?.Count == 10);
                originalModel.MarkPersistenceUntrusted("fixture repeated invalidation");
                Check("old model remains permanently poisoned after successful recovery", !originalModel.IsPersistenceTrusted);
                Check("old child managers remain blocked after parent reload installs a healthy model", Rejects(() => oldAvatarManager.Put(avatar))
                    && Rejects(() => oldAvatarManager.Remove(avatarUid)) && Rejects(() => oldCreatureManager.Put(creature))
                    && Rejects(() => oldCreatureManager.Remove(creatureUid)) && StoredDetails());
                Check("old model cannot be wrapped in another same-session lease", RejectsRegistration(() => InventoryContext.Register(session, originalModel))
                    && ReferenceEquals(originalLease.Inventory, recoveredModel));
                Check("old model cannot be wrapped in a new-session lease", RejectsRegistration(() => InventoryContext.Register(replacementSession, originalModel))
                    && InventoryContext.IsCurrentLease(originalLease, session, character) && StoredBaseline());
                // A fresh DB-owned model may replace healthy ownership normally.
                var fresh = ReloadStored();
                var replacement = InventoryContext.Register(replacementSession, fresh);
                Check("healthy new session replaces recovered old owner normally", InventoryContext.IsCurrentLease(replacement, replacementSession, character)
                    && !InventoryContext.IsCurrentLease(originalLease, session, character));
                var rewrappedRetired = new InventoryLease(Guid.NewGuid(), character, recoveredModel, 2001);
                Check("retired healthy model cannot regain persistence through a fresh offline lease", recoveredModel.IsPersistenceTrusted
                    && Rejects(() => InventoryPersistenceService.SaveDirty(rewrappedRetired)) && StoredBaseline());
                Check("retired healthy model cannot regain Context admission", RejectsRegistration(() => InventoryContext.Register(session, recoveredModel))
                    && InventoryContext.IsCurrentLease(replacement, replacementSession, character));
                var retiredAvatar = recoveredModel.AvatarDetails.GetDetail(avatarUid);
                var retiredCreature = recoveredModel.CreatureDetails.GetDetail(creatureUid);
                retiredAvatar.Color1 = 99; retiredCreature.Field04 = 1;
                Check("retired healthy child managers cannot write into replacement ownership", Rejects(() => recoveredModel.AvatarDetails.Put(retiredAvatar))
                    && Rejects(() => recoveredModel.AvatarDetails.Remove(avatarUid)) && Rejects(() => recoveredModel.CreatureDetails.Put(retiredCreature))
                    && Rejects(() => recoveredModel.CreatureDetails.Remove(creatureUid)) && StoredDetails());
                Check("old lease persistence cannot overwrite new-session ownership", Rejects(() => InventoryPersistenceService.SaveDirty(originalLease)) && StoredBaseline());
                Check("old session disconnect cannot unregister the replacement", !InventoryContext.Unregister(session, character)
                    && InventoryContext.IsCurrentLease(replacement, replacementSession, character));
                Check("stale recovery CAS cannot replace new-session inventory", !InventoryRollbackRecoveryService.ReloadOnlineInventory(database.ConnectionString, originalLease)
                    && ReferenceEquals(replacement.Inventory, fresh) && InventoryContext.IsCurrentLease(replacement, replacementSession, character));
                var normalCommit = OnlineInventoryMutationCommitCoordinator.TryCommit(replacement, "fixture-healthy-new-owner",
                    (c, t) => replacement.Inventory.SetMainVirtualCount(0, 1001));
                Check("healthy replacement still commits ordinary inventory normally", normalCommit
                    && ReloadStored().GetMainVirtualCount(0).Count == 1001 && ReloadStored().GetItem(InventoryListType.Main, 65)?.Count == 10);
                Check("successful model recovery never replays failed auction assets", database.Read(c => AuctionPublicReceiptRepository.Find(c, null, intent)) == null
                    && Scalar("SELECT count(*) FROM mailbox_messages;") == 0);
                Check("healthy replacement disconnect succeeds without stale writes", InventoryContext.Unregister(replacementSession, character)
                    && ReloadStored().GetMainVirtualCount(0).Count == 1001);
                Check("retired online leases stay unsavable after the physical mapping is removed", Rejects(() => InventoryPersistenceService.SaveDirty(originalLease))
                    && Rejects(() => InventoryPersistenceService.SaveDirty(replacement)) && ReloadStored().GetMainVirtualCount(0).Count == 1001);
                var detachedWrapper = new InventoryLease(Guid.NewGuid(), character, fresh, 2002);
                Check("no physical owner does not reauthorize a retired healthy model wrapper or admission", fresh.IsPersistenceTrusted
                    && Rejects(() => InventoryPersistenceService.SaveDirty(detachedWrapper))
                    && RejectsRegistration(() => InventoryContext.Register(session, fresh)));
                Check("no physical owner does not reauthorize old poisoned child managers", Rejects(() => oldAvatarManager.Put(avatar))
                    && Rejects(() => oldAvatarManager.Remove(avatarUid)) && Rejects(() => oldCreatureManager.Put(creature))
                    && Rejects(() => oldCreatureManager.Remove(creatureUid)) && !originalModel.IsPersistenceTrusted && StoredDetails());
                var detachedAvatar = fresh.AvatarDetails.GetDetail(avatarUid);
                var detachedCreature = fresh.CreatureDetails.GetDetail(creatureUid);
                detachedAvatar.Color1 = 99; detachedCreature.Field04 = 1;
                Check("no physical owner does not reauthorize retired healthy child managers", Rejects(() => fresh.AvatarDetails.Put(detachedAvatar))
                    && Rejects(() => fresh.AvatarDetails.Remove(avatarUid)) && Rejects(() => fresh.CreatureDetails.Put(detachedCreature))
                    && Rejects(() => fresh.CreatureDetails.Remove(creatureUid)) && StoredDetails());
                var finalOffline = new InventoryLease(Guid.NewGuid(), character, ReloadStored(), 1000);
                finalOffline.Inventory.SetMainVirtualCount(0, 1002);
                Check("fresh explicit offline settlement remains allowed after online owner releases", InventoryPersistenceService.SaveDirty(finalOffline)
                    && ReloadStored().GetMainVirtualCount(0).Count == 1002);
                var cleanPoison = new InventoryLease(Guid.NewGuid(), character, ReloadStored(), 1001);
                cleanPoison.Inventory.MarkPersistenceUntrusted("fixture clean poisoned offline model");
                Check("clean-data early return cannot turn an untrusted model into successful save", Rejects(() => InventoryPersistenceService.SaveDirty(cleanPoison))
                    && ReloadStored().GetMainVirtualCount(0).Count == 1002);
                Check("untrusted model cannot enter Context even with no existing physical owner", RejectsRegistration(() =>
                    InventoryContext.Register(session, cleanPoison.Inventory)) && !InventoryContext.TryGetLease(character, out _));
            }
            catch (Exception exception) { Console.WriteLine("[FAIL] unexpected auction rollback fence exception: " + exception); failed++; }
            finally
            {
                // A failed assertion must not leave a poisoned fixture registered
                // across the main runner's subsequent tests. Recover from real DB,
                // never save contaminated state just to make teardown convenient.
                if (database != null && originalLease != null && !originalLease.Inventory.IsPersistenceTrusted)
                {
                    try { InventoryRollbackRecoveryService.ReloadOnlineInventory(database.ConnectionString, originalLease); }
                    catch (Exception exception) { Console.WriteLine("[FAIL] fixture recovery cleanup: " + exception.Message); failed++; }
                }
                InventoryContext.Unregister(session); InventoryContext.Unregister(replacementSession);
                SqliteConnection.ClearAllPools();
                Directory.Delete(root, true);
            }
            Console.WriteLine($"AuctionRollbackFenceSelfTest: passed={passed} failed={failed}; real public rollback, no game server/client.");
            return failed == 0 ? 0 : 1;
        }

        private sealed class ReloadFaultDatabase : IGameDatabase
        {
            private readonly IGameDatabase inner;
            private readonly string unusableReloadConnectionString;
            internal ReloadFaultDatabase(IGameDatabase inner, string missingPath)
            {
                this.inner = inner;
                unusableReloadConnectionString = new SqliteConnectionStringBuilder { DataSource = missingPath }.ToString();
            }
            public string DatabasePath => inner.DatabasePath;
            public string SchemaFilePath => inner.SchemaFilePath;
            public string ConnectionString => unusableReloadConnectionString;
            public SqliteConnection OpenConnection() => inner.OpenConnection();
            public T Read<T>(Func<SqliteConnection, T> action) => inner.Read(action);
            public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> action, bool immediate = true) => inner.Write(action, immediate);
            public void Write(Action<SqliteConnection, SqliteTransaction> action, bool immediate = true) => inner.Write(action, immediate);
        }
    }
}

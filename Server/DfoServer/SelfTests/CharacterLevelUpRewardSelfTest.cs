using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Progression;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Peer = DfoServer.SelfTests.A21OneToOneChatSelfTest.Peer;

namespace DfoServer.SelfTests
{
    public static class CharacterLevelUpRewardSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== CHARACTER_LEVEL_UP_REWARD selftest ===");
            int failures = 0;
            void Check(string label, bool condition)
            {
                Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {label}");
                if (!condition) failures++;
            }
            void Scenario(string label, Action action)
            {
                try { action(); }
                catch (Exception ex) { Check(label + ": " + ex, false); }
            }

            Scenario("PVF reward definitions", () => VerifyCatalog(Check));
            Scenario("edited PVF rewards and configuration failure recovery", () => VerifyConfigurationChanges(Check));
            Scenario("production GBK character names", () => VerifyCharacterNames(Check));
            Scenario("shared progression entry", () => VerifyProgression(Check));
            Scenario("monster experience checkpoints survive later failures", () => VerifyMonsterExperienceRecovery(Check));
            Scenario("caller transaction rollback", () => VerifyProgressionRollback(Check));
            Scenario("mail transaction retry and restart", () => VerifyDeliveryRetry(Check));
            Scenario("mail purge and repeated levels", () => VerifyPermanentLedger(Check));
            Scenario("concurrent reward consumers", () => VerifyConcurrency(Check));
            Scenario("max-level account EXP transaction", () => VerifyMaxLevel(Check));
            Scenario("buffered monster account EXP", () => VerifyBufferedMonsterAccountExperience(Check));
            Scenario("buffered account EXP and failed level-up", () => VerifyBufferedLevelUpRollback(Check));
            Scenario("one failed character does not block others", () => VerifyFailureIsolation(Check));
            Scenario("level-up ticket production path", () => VerifyLevelUpTicket(Check));
            Scenario("schema creation and v33 migration", () => VerifyMigration(Check));
            Scenario("session generation and network projection", () => VerifyNetworkAsync(Check).GetAwaiter().GetResult());
            Scenario("common packet boundary", () => VerifyPacketBoundaryAsync(Check).GetAwaiter().GetResult());
            Scenario("EXP item preserves existing client refreshes", () => VerifyExperienceItemPacketsAsync(Check).GetAwaiter().GetResult());
            Scenario("skill books preserve unpersisted monster EXP", () => VerifySkillBookExperienceAsync(Check).GetAwaiter().GetResult());
            Scenario("startup and Clock recovery", () => VerifyClockRecovery(Check));
            Console.WriteLine(failures == 0 ? "CHARACTER_LEVEL_UP_REWARD selftest passed"
                : $"CHARACTER_LEVEL_UP_REWARD selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyCatalog(Action<string, bool> check)
        {
            var parsed = CharacterLevelUpRewardCatalog.Parse(@"
[level up reward]
70 1 2 3330 250
[/level up reward]
[level up reward]
71 1161 1162 3330 80
[/level up reward]");
            check("PVF definitions keep level/item/count and do not interpret text parameters",
                parsed.Count == 2 && parsed[70].ItemId == 3330 && parsed[70].ItemCount == 250 && parsed[71].ItemCount == 80);
            foreach (var body in new[] { "70 1 2 3330", "70 1 2 3330 1 99", "70 1 2 invalid 1", "70 1 2 3330 0", "70 1 2 -1 1" })
            {
                bool rejected = false;
                try { CharacterLevelUpRewardCatalog.Parse("[level up reward]\n" + body + "\n[/level up reward]"); }
                catch (InvalidDataException) { rejected = true; }
                check("malformed reward fails closed: " + body, rejected);
            }
            bool duplicateRejected = false;
            try { CharacterLevelUpRewardCatalog.Parse("[level up reward]\n70 1 2 3330 1\n[/level up reward]\n[level up reward]\n70 1 2 3330 2\n[/level up reward]"); }
            catch (InvalidDataException) { duplicateRejected = true; }
            check("duplicate level definitions cannot silently replace a reward", duplicateRejected);
            check("PVF without configured upgrade rewards is supported", CharacterLevelUpRewardCatalog.Parse("[other]\n1").Count == 0);

            using var fixture = new Fixture();
            var live = CharacterLevelUpRewardCatalog.Get();
            check("real PVF upgrade reward definitions load", live.Count > 0);
            var requests = live.Values.Select(reward => CharacterLevelUpRewardService.BuildMail(
                Fixture.CharacterId, Fixture.AccountId, "升级测试", reward, fixture.Now)).ToArray();
            var sent = fixture.Mailbox.SendSystemMails(requests);
            check("all actual PVF upgrade attachments can be created by the production mailbox", sent.Success && fixture.Count("mailbox_messages") == live.Count);
            check("reward mail is system mail with a 30-day UTC expiry",
                requests.All(request => request.MailType == 1 && request.Unlimited == false
                    && request.ExpireAtUtc == fixture.Now.AddDays(30) && request.SenderName == string.Empty));
        }

        private static void VerifyCharacterNames(Action<string, bool> check)
        {
            const string name = "升级测试";
            foreach (bool storedAsBlob in new[] { true, false })
            {
                using var fixture = new Fixture();
                fixture.Database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE characters SET name=@name WHERE character_id=@cid;";
                    command.Parameters.AddWithValue("@name", storedAsBlob ? (object)ClientTextEncoding.GetBytes(name) : name);
                    command.Parameters.AddWithValue("@cid", Fixture.CharacterId);
                    command.ExecuteNonQuery();
                });
                fixture.Experience.GrantToLevel(fixture.Player, 70, "character-name");
                var pending = fixture.Database.Read(connection =>
                {
                    using var transaction = connection.BeginTransaction(deferred: true);
                    long eventId = CharacterLevelUpEventRepository.LoadPendingIds(
                        connection, fixture.Now.ToUnixTimeSeconds(), 1).Single();
                    var change = CharacterLevelUpEventRepository.LoadPending(
                        connection, transaction, eventId, fixture.Now.ToUnixTimeSeconds(),
                        out int accountId, out string characterName, out bool deleted);
                    return (Event: change, AccountId: accountId, Name: characterName, Deleted: deleted);
                });
                string storage = storedAsBlob ? "GBK BLOB" : "TEXT";
                check("pending upgrade preserves the character identity and Chinese name: " + storage,
                    pending.Event?.CharacterId == Fixture.CharacterId && pending.AccountId == Fixture.AccountId
                        && pending.Name == name && !pending.Deleted);

                var delivered = fixture.Rewards.ProcessPending();
                check("production reward mail and audit preserve the receiver name: " + storage,
                    delivered.Count == 1 && fixture.Pending() == 0
                        && fixture.Scalar("SELECT COUNT(*) FROM mailbox_messages WHERE receiver_character_id=62002 AND receiver_name='升级测试';") == 1
                        && fixture.Scalar("SELECT COUNT(*) FROM mailbox_system_mail_audit WHERE receiver_character_id=62002 AND receiver_name='升级测试';") == 1);
            }
        }

        private static void VerifyProgression(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var noLevelUp = fixture.Experience.Grant(fixture.Player, Fixture.AccountId, 1,
                ExperiencePersistMode.OnLevelUpOnly, "kill");
            check("ordinary kill EXP stays in memory until its existing save boundary",
                !noLevelUp.LeveledUp && !noLevelUp.Persisted && fixture.Count("character_level_up_events") == 0);
            uint gain = checked((uint)ExpTableProvider.GetLevelThreshold(70) - fixture.Player.Exp);
            var grant = fixture.Experience.Grant(fixture.Player, Fixture.AccountId, gain,
                ExperiencePersistMode.OnLevelUpOnly, "party-kill");
            check("session EXP entry atomically records a cross-level event without a reward call",
                grant.Persisted && grant.NewLevel == 71 && fixture.Level() == 71
                    && fixture.Count("character_level_up_events") == 1 && fixture.Count("mailbox_messages") == 0);
            var delivered = fixture.Rewards.ProcessPending();
            check("one cross-level event sends every configured crossed-level reward",
                delivered.Select(d => d.Level).SequenceEqual(new byte[] { 70, 71 })
                    && fixture.Count("mailbox_messages") == 2 && fixture.Count("character_level_up_rewards") == 2);
            var inbox = fixture.Mailbox.LoadInbox(Fixture.CharacterId, 100);
            check("production inbox contains the correct grade quantities and GBK-ready Chinese text",
                inbox.Any(mail => mail.FirstAttachmentItemCount == 250 && mail.Body.Contains("70级"))
                    && inbox.Any(mail => mail.FirstAttachmentItemCount == 80 && mail.Body.Contains("71级")));
            check("session EXP save cannot produce the same level transition again",
                CharacterExperienceService.PersistSessionExp(fixture.Player, "disconnect", fixture.Database.ConnectionString)
                    && fixture.Count("character_level_up_events") == 1 && fixture.Rewards.ProcessPending().Count == 0);
            var facts = fixture.Database.Read(connection => CharacterLevelUpEventRepository.LoadCommittedAfter(connection, 0));
            check("future activities can read the committed event after base-mail consumption",
                facts.Count == 1 && facts[0].PreviousLevel == 69 && facts[0].NewLevel == 71 && facts[0].Source == "party-kill");
            fixture.Experience.GrantToLevel(fixture.Player, 72, "tutorial");
            check("tutorial/direct-level entry records and delivers through the shared consumer",
                fixture.Rewards.ProcessPending().Single().Level == 72 && fixture.Count("character_level_up_events") == 2);
            fixture.Database.Write((connection, transaction) =>
                CharacterExperienceService.CommitProgressInTransaction(connection, transaction,
                    Fixture.CharacterId, fixture.Player.Level, fixture.Player.Exp, "grow-type-refresh"));
            check("same-level combat-stat refresh does not create a level-up event", fixture.Count("character_level_up_events") == 2);
            check("historical high-level characters are not granted a retroactive sweep",
                fixture.Rewards.ProcessPending().Count == 0 && fixture.Count("mailbox_messages") == 3);
        }

        private static void VerifyMonsterExperienceRecovery(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var journal = new DungeonParticipantEffectJournal();
            var runIdentity = new DungeonRunIdentity(701, 702, 1);
            var roomIdentity = new DungeonRoomIdentity(runIdentity.InstanceIdentity, 703);
            var participant = new DungeonParticipantRosterEntry(Fixture.CharacterId, (ushort)Fixture.CharacterId,
                new DungeonRun(), runIdentity, roomIdentity, 1);
            var source = new DungeonEventEnvelope(Guid.NewGuid(), runIdentity, roomIdentity.RoomInstanceId,
                Fixture.CharacterId, Fixture.CharacterId, 1, 1, "kill-recovery-test", 0);
            journal.TryFreeze(source, DungeonParticipantEffectAudience.Room, new[] { participant }, out _);
            if (!journal.TryBegin(source.SourceEventId, DungeonParticipantEffectAudience.Room, participant,
                    DungeonParticipantEffectKinds.MonsterKill, out var first, out _))
                throw new InvalidOperationException("monster checkpoint fixture could not begin");
            uint gain = (uint)ExpTableProvider.GetLevelThreshold(69) - fixture.Player.Exp;
            fixture.Execute(@"CREATE TRIGGER reject_monster_level BEFORE INSERT ON character_level_up_events
BEGIN SELECT RAISE(ABORT, 'monster checkpoint rollback selftest'); END;");
            bool rejected = false;
            try
            {
                journal.TryApplyMonsterExperienceOnce(first,
                    () => fixture.Experience.Grant(fixture.Player, Fixture.AccountId, gain,
                        ExperiencePersistMode.OnLevelUpOnly, "checkpoint-kill"), out _);
            }
            catch (SqliteException) { rejected = true; }
            check("failed monster EXP transaction does not consume the application checkpoint",
                rejected && fixture.Level() == 69 && fixture.Count("character_level_up_events") == 0);
            fixture.Execute("DROP TRIGGER reject_monster_level;");
            check("the same monster checkpoint can retry a rolled-back grant",
                journal.TryApplyMonsterExperienceOnce(first,
                    () => fixture.Experience.Grant(fixture.Player, Fixture.AccountId, gain,
                        ExperiencePersistMode.OnLevelUpOnly, "checkpoint-kill"), out var applied)
                    && applied?.LeveledUp == true && fixture.Level() == 70);
            int statisticsApplications = 0;
            journal.TryApplyMonsterStatisticsOnce(first, () => statisticsApplications++);
            uint committedExp = fixture.Player.Exp;
            journal.TryFail(first); // 模拟经验之后的通知/任务处理失败。
            if (!journal.TryBegin(source.SourceEventId, DungeonParticipantEffectAudience.Room, participant,
                    DungeonParticipantEffectKinds.MonsterKill, out var retry, out _))
                throw new InvalidOperationException("failed monster effect could not resume");
            int repeatedGrants = 0;
            bool recovered = journal.TryApplyMonsterExperienceOnce(retry, () =>
            {
                repeatedGrants++;
                return fixture.Experience.Grant(fixture.Player, Fixture.AccountId, gain,
                    ExperiencePersistMode.OnLevelUpOnly, "should-not-regrant");
            }, out var replay);
            check("recovery after a later failure cannot award monster EXP or upgrade rewards twice",
                recovered && replay == null && repeatedGrants == 0 && fixture.Player.Exp == committedExp
                    && fixture.Count("character_level_up_events") == 1 && fixture.Rewards.ProcessPending().Count == 1);
            check("retry preserves the already counted monster statistics",
                journal.TryApplyMonsterStatisticsOnce(retry, () => statisticsApplications++) && statisticsApplications == 1);
            check("an old monster reservation cannot grant EXP after a replacement reservation",
                !journal.TryApplyMonsterExperienceOnce(first,
                    () => throw new InvalidOperationException("stale checkpoint executed"), out _));
            journal.TryCommit(retry);
        }

        private static void VerifyConfigurationChanges(Action<string, bool> check)
        {
            // 用真实 PVF 解出的正文修改测试副本，不修改归档或生产缓存。
            string liveText = PvfArchiveAccessor.ReadText(CharacterLevelUpRewardCatalog.PvfPath);
            var level70 = new PvfLib.ScriptParser().Parse(liveText).GetChildren("level up reward")
                .First(node => CharacterLevelUpRewardCatalog.Parse(liveText.Substring(node.StartIndex, node.EndIndex - node.StartIndex)).ContainsKey(70));
            string editedText = liveText.Substring(0, level70.StartIndex)
                + "[level up reward]\n70 123 456 10147584 2\n[/level up reward]\n"
                + liveText.Substring(level70.EndIndex)
                + "\n[level up reward]\n68 1 2 3330 7\n[/level up reward]\n";
            var edited = CharacterLevelUpRewardCatalog.Parse(editedText);
            check("editing actual PVF text recognizes changed item/count and an added lower reward level",
                edited[70].ItemId == 10147584 && edited[70].ItemCount == 2 && edited[68].ItemCount == 7);
            using (var fixture = new Fixture())
            {
                fixture.Experience.GrantToLevel(fixture.Player, 71, "configured-rewards");
                var rewards = new CharacterLevelUpRewardService(fixture.Database, fixture.Mailbox,
                    () => edited, () => fixture.Now);
                check("changed PVF values are used by committed mail delivery without backfilling older levels",
                    rewards.ProcessPending().Count == 2
                        && fixture.Scalar("SELECT item_count FROM mailbox_attachments WHERE item_template_id=10147584;") == 2
                        && fixture.Scalar("SELECT COUNT(*) FROM character_level_up_rewards WHERE level=68;") == 0);
                fixture.Experience.GrantToLevel(fixture.Player, 69, "reset");
                fixture.Experience.GrantToLevel(fixture.Player, 70, "repeat-level");
                var changedAgain = CharacterLevelUpRewardCatalog.Parse("[level up reward]\n70 1 2 3330 99\n[/level up reward]");
                check("changing item/count does not grant an already delivered grade again",
                    new CharacterLevelUpRewardService(fixture.Database, fixture.Mailbox, () => changedAgain,
                        () => fixture.Now).ProcessPending().Count == 0 && fixture.Count("mailbox_messages") == 2);
            }
            using (var fixture = new Fixture())
            {
                fixture.Experience.GrantToLevel(fixture.Player, 70, "bad-config");
                var invalid = new CharacterLevelUpRewardService(fixture.Database, fixture.Mailbox,
                    () => CharacterLevelUpRewardCatalog.Parse("[level up reward]\n70 1 2 3330 0\n[/level up reward]"), () => fixture.Now);
                check("invalid PVF config leaves the upgrade pending without mail or consumption",
                    invalid.ProcessPending().Count == 0 && fixture.Pending() == 1 && fixture.Count("mailbox_messages") == 0);
                fixture.Now = fixture.Now.AddMinutes(2);
                var repairedAfterRestart = new CharacterLevelUpRewardService(fixture.Database, fixture.Mailbox,
                    () => edited, () => fixture.Now);
                check("a service with corrected config recovers the pending reward exactly once",
                    repairedAfterRestart.ProcessPending().Count == 1 && fixture.Pending() == 0
                        && repairedAfterRestart.ProcessPending().Count == 0);
            }
            using (var fixture = new Fixture())
            {
                fixture.Experience.GrantToLevel(fixture.Player, 70, "invalid-item");
                var invalidItem = CharacterLevelUpRewardCatalog.Parse("[level up reward]\n70 1 2 2147483647 1\n[/level up reward]");
                check("unknown PVF item cannot consume an event or leave a partial mail",
                    new CharacterLevelUpRewardService(fixture.Database, fixture.Mailbox, () => invalidItem,
                        () => fixture.Now).ProcessPending().Count == 0 && fixture.Pending() == 1
                        && fixture.Count("mailbox_messages") == 0 && fixture.Count("character_level_up_rewards") == 0);
            }
        }

        private static void VerifyProgressionRollback(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            uint gain = checked((uint)ExpTableProvider.GetLevelThreshold(69) - fixture.Player.Exp);
            using (var connection = fixture.Database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var result = CharacterExperienceService.GrantInTransaction(connection, transaction,
                    Fixture.CharacterId, Fixture.AccountId, fixture.Player.Level, fixture.Player.Exp, gain);
                check("external-transaction XP entry records its event inside the caller transaction", result.Persisted);
                transaction.Rollback();
            }
            check("caller rollback removes both progression and its pending reward fact",
                fixture.Level() == 69 && fixture.Count("character_level_up_events") == 0 && fixture.Rewards.ProcessPending().Count == 0);
            fixture.Execute(@"CREATE TRIGGER reject_level_fact BEFORE INSERT ON character_level_up_events
BEGIN SELECT RAISE(ABORT, 'level event rollback selftest'); END;");
            bool rejected = false;
            uint originalExp = fixture.Player.Exp;
            try { fixture.Experience.Grant(fixture.Player, Fixture.AccountId, gain, ExperiencePersistMode.OnAnyChange, "rollback"); }
            catch (SqliteException) { rejected = true; }
            check("failed level-event persistence rolls back DB and preserves the session projection",
                rejected && fixture.Level() == 69 && fixture.Player.Level == 69 && fixture.Player.Exp == originalExp
                    && fixture.Count("character_level_up_events") == 0);
            fixture.Execute("DROP TRIGGER reject_level_fact; DELETE FROM character_subtype1_fields;");
            rejected = false;
            try { fixture.Experience.GrantToLevel(fixture.Player, 70, "missing-combat-state"); }
            catch (InvalidOperationException) { rejected = true; }
            check("missing combat-state row cannot leave a partial level update",
                rejected && fixture.Level() == 69 && fixture.Player.Level == 69 && fixture.Count("character_level_up_events") == 0);
            rejected = false;
            try
            {
                fixture.Database.Write((connection, transaction) =>
                    CharacterExperienceService.GrantInTransaction(connection, transaction,
                        Fixture.CharacterId, Fixture.AccountId, fixture.Player.Level, fixture.Player.Exp,
                        (uint)ExpTableProvider.GetLevelThreshold(85) - fixture.Player.Exp + 10000));
            }
            catch (InvalidOperationException) { rejected = true; }
            check("transaction entry fails closed so a caller cannot commit rewards after progression failure",
                rejected && fixture.Level() == 69 && fixture.Scalar("SELECT honor_exp FROM accounts;") == 0
                    && fixture.Scalar("SELECT growth_capsule_exp FROM accounts;") == 0
                    && fixture.Count("character_level_up_events") == 0);
            check("missing-character session save reports a failed write",
                !CharacterExperienceService.PersistSessionExp(new PlayerContext { CharacterId = 999999, Level = 70 },
                    "missing", fixture.Database.ConnectionString));
        }

        private static void VerifyDeliveryRetry(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            fixture.Experience.GrantToLevel(fixture.Player, 71, "cross-level");
            fixture.Execute(@"CREATE TRIGGER reject_reward_ledger BEFORE INSERT ON character_level_up_rewards
WHEN NEW.level = 71 BEGIN SELECT RAISE(ABORT, 'reward ledger rollback selftest'); END;");
            check("failure after a mail insert publishes no delivery", fixture.Rewards.ProcessPending().Count == 0);
            check("cross-level mail/attachments/audit/ledger roll back together and retain the pending event",
                fixture.Count("mailbox_messages") == 0 && fixture.Count("mailbox_attachments") == 0
                    && fixture.Count("mailbox_system_mail_audit") == 0 && fixture.Count("character_level_up_rewards") == 0
                    && fixture.Pending() == 1 && fixture.Level() == 71);
            fixture.Execute("DROP TRIGGER reject_reward_ledger;");
            check("failed delivery has retry backoff instead of retrying every incoming packet", fixture.Rewards.ProcessPending().Count == 0);
            fixture.Now = fixture.Now.AddMinutes(2);
            var restarted = fixture.NewRewards();
            check("a new service after restart recovers every uncommitted mail once",
                restarted.ProcessPending().Count == 2 && fixture.Pending() == 0 && fixture.Count("character_level_up_rewards") == 2);
            var inboxBefore = fixture.Mailbox.LoadInbox(Fixture.CharacterId, 100).Select(mail => mail.MessageId).ToArray();
            fixture.Now = fixture.Now.AddHours(2);
            check("later recovery neither rebuilds expiry hashes nor creates duplicate mail",
                fixture.NewRewards().ProcessPending().Count == 0
                    && fixture.Mailbox.LoadInbox(Fixture.CharacterId, 100).Select(mail => mail.MessageId).SequenceEqual(inboxBefore));
        }

        private static void VerifyPermanentLedger(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            fixture.Experience.GrantToLevel(fixture.Player, 70, "upgrade");
            fixture.Rewards.ProcessPending();
            fixture.Execute("UPDATE mailbox_messages SET expire_at = datetime('now', '-90 days');");
            fixture.Mailbox.MaintainExpiredMail();
            check("real expired-mail purge retains the reward ledger and clears only its message reference",
                fixture.Count("mailbox_messages") == 0 && fixture.Count("character_level_up_rewards") == 1
                    && fixture.Scalar("SELECT COUNT(*) FROM character_level_up_rewards WHERE message_id IS NULL;") == 1);
            fixture.Experience.GrantToLevel(fixture.Player, 69, "administrative-reset");
            fixture.Experience.GrantToLevel(fixture.Player, 70, "re-upgrade");
            check("reaching the same grade after reset and purge cannot restore the reward entitlement",
                fixture.Rewards.ProcessPending().Count == 0 && fixture.Count("mailbox_messages") == 0);
        }

        private static void VerifyConcurrency(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            fixture.Experience.GrantToLevel(fixture.Player, 70, "upgrade");
            var consumers = new[] { fixture.NewRewards(), fixture.NewRewards() };
            var results = Task.WhenAll(consumers.Select(service => Task.Run(() => service.ProcessPending()))).GetAwaiter().GetResult();
            check("two reward consumers serialize and only one announces a new delivery",
                results.Sum(result => result.Count) == 1 && fixture.Count("mailbox_messages") == 1
                    && fixture.Count("character_level_up_rewards") == 1 && fixture.Pending() == 0);
        }

        private static void VerifyLevelUpTicket(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var lease = fixture.RegisterInventory(Guid.NewGuid());
            lease.Inventory.SetItem(InventoryListType.Main, 16,
                new ItemCore { ItemKind = ItemCore.KindConsumable, ItemId = 10004024, Count = 1 });
            if (!OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "level-reward-ticket-fixture"))
                throw new InvalidOperationException("ticket fixture persistence failed");
            var result = new ExperienceItemUseService(fixture.Database, SystemRentalTimeProvider.Instance).UseLevelUpTicketBySlot(
                Fixture.CharacterId, Fixture.AccountId, 16, ExperienceItemUseLocation.Town);
            check("production level-up ticket atomically consumes the ticket and records the upgrade",
                result.Success && fixture.Level() == 70 && fixture.Count("character_level_up_events") == 1
                    && lease.Inventory.GetItem(InventoryListType.Main, 16) == null);
            check("ticket path sends its reward without a ticket-handler mail hook",
                fixture.Rewards.ProcessPending().Single().Level == 70 && fixture.Count("character_level_up_rewards") == 1);
        }

        private static void VerifyMaxLevel(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            fixture.Experience.GrantToLevel(fixture.Player, (byte)ExpTableProvider.MaxLevel, "reach-cap");
            fixture.Rewards.ProcessPending();
            long eventsBefore = fixture.Count("character_level_up_events");
            fixture.Player.Exp++;
            fixture.Execute("UPDATE characters SET exp=exp+1;");
            fixture.Execute(@"CREATE TRIGGER reject_progress BEFORE UPDATE OF level ON characters
BEGIN SELECT RAISE(ABORT, 'honor rollback selftest'); END;");
            bool rejected = false;
            try { fixture.Experience.Grant(fixture.Player, Fixture.AccountId, 10000, ExperiencePersistMode.OnAnyChange, "cap-normalization"); }
            catch (SqliteException) { rejected = true; }
            check("account honor/capsule EXP rolls back with failed character normalization",
                rejected && fixture.Scalar("SELECT honor_exp FROM accounts;") == 0
                    && fixture.Scalar("SELECT growth_capsule_exp FROM accounts;") == 0
                    && fixture.Player.Exp == (uint)ExpTableProvider.GetLevelThreshold(ExpTableProvider.MaxLevel - 1) + 1);
            fixture.Execute("DROP TRIGGER reject_progress;");
            var result = fixture.Experience.Grant(fixture.Player, Fixture.AccountId, 10000,
                ExperiencePersistMode.OnAnyChange, "cap-normalization");
            check("max-level normalization and honor gain do not create another upgrade entitlement",
                result.Persisted && result.HonorExpGain == 10000 && fixture.Player.Level == ExpTableProvider.MaxLevel
                    && fixture.Scalar("SELECT honor_exp FROM accounts;") == 10000
                    && fixture.Count("character_level_up_events") == eventsBefore && fixture.Rewards.ProcessPending().Count == 0);
        }

        private static void VerifyBufferedMonsterAccountExperience(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            fixture.Experience.GrantToLevel(fixture.Player, (byte)ExpTableProvider.MaxLevel, "buffer-cap");
            fixture.Rewards.ProcessPending();
            long eventsBefore = fixture.Count("character_level_up_events");
            long rewardsBefore = fixture.Count("character_level_up_rewards");
            fixture.Execute(@"CREATE TRIGGER reject_account_exp_write BEFORE UPDATE OF honor_exp, growth_capsule_exp ON accounts
BEGIN SELECT RAISE(ABORT, 'buffer must defer writes selftest'); END;");

            fixture.Experience.Grant(fixture.Player, Fixture.AccountId, 100,
                ExperiencePersistMode.OnLevelUpOnly, "buffer-kill-1");
            var second = fixture.Experience.Grant(fixture.Player, Fixture.AccountId, 200,
                ExperiencePersistMode.OnLevelUpOnly, "buffer-kill-2");
            uint capsuleGain = GrowthCapsuleDataProvider.CalculateExpGain(100)
                + GrowthCapsuleDataProvider.CalculateExpGain(200);
            check("max-level monster grants accumulate visible account EXP without database writes",
                !second.Persisted && !second.LeveledUp && second.Honor.TotalHonorExp == 300
                    && second.GrowthCapsule.TotalExp == capsuleGain
                    && fixture.Scalar("SELECT honor_exp FROM accounts;") == 0
                    && fixture.Scalar("SELECT growth_capsule_exp FROM accounts;") == 0);
            check("failed account flush retains pending and creates no upgrade reward",
                !AccountExperienceBuffer.Instance.Flush(Fixture.AccountId)
                    && AccountExperienceBuffer.Instance.GetEffectiveTotals(
                        Fixture.AccountId, fixture.Database.ConnectionString).TotalHonorExp == 300
                    && fixture.Scalar("SELECT honor_exp FROM accounts;") == 0
                    && fixture.Count("character_level_up_events") == eventsBefore
                    && fixture.Count("character_level_up_rewards") == rewardsBefore
                    && fixture.Rewards.ProcessPending().Count == 0);

            fixture.Execute("DROP TRIGGER reject_account_exp_write;");
            check("retry and repeated flush persist buffered monster gains exactly once",
                AccountExperienceBuffer.Instance.Flush(Fixture.AccountId)
                    && AccountExperienceBuffer.Instance.Flush(Fixture.AccountId)
                    && fixture.Scalar("SELECT honor_exp FROM accounts;") == 300
                    && fixture.Scalar("SELECT growth_capsule_exp FROM accounts;") == capsuleGain
                    && fixture.Count("character_level_up_events") == eventsBefore);
        }

        private static void VerifyBufferedLevelUpRollback(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            fixture.Player.Level = (byte)(ExpTableProvider.MaxLevel - 1);
            fixture.Player.Exp = (uint)ExpTableProvider.GetLevelThreshold(fixture.Player.Level) - 10;
            fixture.Execute($"UPDATE characters SET level={fixture.Player.Level}, exp={fixture.Player.Exp};");
            // 同一账号已由另一满级角色累计的杀怪经验。
            AccountExperienceBuffer.Instance.Add(Fixture.AccountId, 50, fixture.Database.ConnectionString);
            fixture.Execute(@"CREATE TRIGGER reject_buffered_level BEFORE INSERT ON character_level_up_events
BEGIN SELECT RAISE(ABORT, 'buffered level rollback selftest'); END;");
            byte originalLevel = fixture.Player.Level;
            uint originalExp = fixture.Player.Exp;
            bool rejected = false;
            try
            {
                fixture.Experience.Grant(fixture.Player, Fixture.AccountId, 100,
                    ExperiencePersistMode.OnLevelUpOnly, "buffer-level-failure");
            }
            catch (SqliteException) { rejected = true; }
            var pending = AccountExperienceBuffer.Instance.GetEffectiveTotals(
                Fixture.AccountId, fixture.Database.ConnectionString);
            check("failed level-up neither publishes progress nor consumes existing account pending",
                rejected && fixture.Player.Level == originalLevel && fixture.Player.Exp == originalExp
                    && fixture.Level() == originalLevel && fixture.Count("character_level_up_events") == 0
                    && fixture.Scalar("SELECT honor_exp FROM accounts;") == 0
                    && fixture.Scalar("SELECT growth_capsule_exp FROM accounts;") == 0
                    && pending.TotalHonorExp == 50
                    && pending.TotalGrowthCapsuleExp == GrowthCapsuleDataProvider.CalculateExpGain(50));

            fixture.Execute("DROP TRIGGER reject_buffered_level;");
            var grant = fixture.Experience.Grant(fixture.Player, Fixture.AccountId, 100,
                ExperiencePersistMode.OnLevelUpOnly, "buffer-level-retry");
            check("successful level-up commits its fact and flushes earlier pending before broadcasting",
                grant.Persisted && grant.LeveledUp && grant.HonorExpGain > 0
                    && fixture.Count("character_level_up_events") == 1
                    && fixture.Scalar("SELECT honor_exp FROM accounts;") == 50 + grant.HonorExpGain
                    && fixture.Scalar("SELECT growth_capsule_exp FROM accounts;")
                        == GrowthCapsuleDataProvider.CalculateExpGain(50)
                            + GrowthCapsuleDataProvider.CalculateExpGain(grant.HonorExpGain));
            int expectedMails = CharacterLevelUpRewardCatalog.Get().ContainsKey(grant.NewLevel) ? 1 : 0;
            check("level-up retry and later buffer flush cannot duplicate mail or account gains",
                fixture.Rewards.ProcessPending().Count == expectedMails
                    && fixture.Rewards.ProcessPending().Count == 0
                    && fixture.Count("character_level_up_rewards") == expectedMails
                    && AccountExperienceBuffer.Instance.Flush(Fixture.AccountId)
                    && fixture.Scalar("SELECT honor_exp FROM accounts;") == 50 + grant.HonorExpGain);
        }

        private static void VerifyFailureIsolation(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            fixture.AddCharacter(62003);
            fixture.Experience.GrantToLevel(fixture.Player, 70, "first-party-member");
            fixture.Experience.GrantToLevel(new PlayerContext { CharacterId = 62003, Level = 69 }, 70, "second-party-member");
            fixture.Execute(@"CREATE TRIGGER reject_one_character BEFORE INSERT ON character_level_up_rewards
WHEN NEW.character_id=62002 BEGIN SELECT RAISE(ABORT, 'failed member selftest'); END;");
            var result = fixture.Rewards.ProcessPending();
            check("one deferred event cannot hold up a different character's reward",
                result.Count == 1 && result[0].CharacterId == 62003 && fixture.Pending() == 1
                    && fixture.Mailbox.LoadInbox(62003, 100).Count == 1 && fixture.Mailbox.LoadInbox(Fixture.CharacterId, 100).Count == 0);
        }

        private static void VerifyMigration(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            check("fresh DB uses the current schema and creates both progression tables",
                fixture.Scalar("PRAGMA user_version;") == SqliteMigrations.CurrentVersion
                    && fixture.Count("character_level_up_events") == 0 && fixture.Count("character_level_up_rewards") == 0);
            fixture.Experience.GrantToLevel(fixture.Player, 70, "pre-migration-character");
            fixture.Execute(@"DROP TABLE character_level_up_rewards; DROP TABLE character_level_up_events;
UPDATE schema_metadata SET schema_version=33; PRAGMA user_version=33;");
            fixture.ApplyMigrations();
            check("v33 upgrade creates empty progression tables without issuing historical rewards",
                fixture.Scalar("PRAGMA user_version;") == 34
                    && fixture.Scalar("SELECT schema_version FROM schema_metadata;") == 34
                    && fixture.Count("character_level_up_rewards") == 0
                    && fixture.Count("character_level_up_events") == 0 && fixture.Count("mailbox_messages") == 0);
            fixture.ApplyMigrations();
            check("repeat initialization does not backfill an existing character's levels",
                fixture.Rewards.ProcessPending().Count == 0 && fixture.Count("mailbox_messages") == 0);
            fixture.Experience.GrantToLevel(fixture.Player, 71, "post-migration");
            var delivered = fixture.Rewards.ProcessPending();
            check("post-migration upgrades issue only newly reached configured levels",
                delivered.Count == 1 && delivered[0].Level == 71
                    && fixture.Count("character_level_up_rewards") == 1 && fixture.Count("mailbox_messages") == 1);
            fixture.Execute(@"DROP TABLE character_level_up_rewards; DROP TABLE character_level_up_events;
UPDATE schema_metadata SET schema_version=33; PRAGMA user_version=33;
CREATE TABLE idx_character_level_up_events_pending (invalid_schema INTEGER);");
            bool rejected = false;
            try { fixture.ApplyMigrations(); }
            catch (SqliteException) { rejected = true; }
            check("failed v34 migration rolls back DDL and both version markers",
                rejected && fixture.Scalar("PRAGMA user_version;") == 33
                    && fixture.Scalar("SELECT schema_version FROM schema_metadata;") == 33
                    && fixture.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name='character_level_up_events';") == 0);
            fixture.Execute("DROP TABLE idx_character_level_up_events_pending;");
            fixture.ApplyMigrations();
            fixture.Execute("UPDATE schema_metadata SET schema_version=35; PRAGMA user_version=35;");
            rejected = false;
            try { fixture.ApplyMigrations(); }
            catch (InvalidOperationException) { rejected = true; }
            check("newer unsupported schema is rejected", rejected);
        }

        private static async Task VerifyNetworkAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var sessions = new SessionDirectory();
            var transitions = new CharacterTransitionCoordinator(sessions);
            using var peer = await Peer.Create(sessions, Fixture.CharacterId, "升级测试");
            fixture.RegisterInventory(peer.Session.SessionId);
            using var coordinator = new CharacterLevelUpRewardCoordinator(fixture.Rewards, sessions, transitions);
            fixture.Experience.GrantToLevel(fixture.Player, 71, "upgrade");
            await coordinator.DrainAndNotifyAsync();
            var packets = peer.Drain();
            var alarm = packets.Single(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.MAILBOX_ALARM);
            var notice = packets.Single(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.SERVER_BROADCAST_TIME_MESSAGE);
            check("committed cross-level mail produces one mailbox alarm with the new-mail count",
                packets.Count == 2 && BitConverter.ToUInt16(alarm, 15) == 2);
            check("personal notice uses milliseconds and a GBK DSTR in the real A21 envelope",
                BitConverter.ToInt32(notice, 15) == 5000
                    && BitConverter.ToInt32(notice, 19) == notice.Length - 23
                    && ClientTextEncoding.Encoding.GetString(notice, 23, notice.Length - 23) == "恭喜你达到71级，获得升级奖励");
            await coordinator.DrainAndNotifyAsync();
            check("duplicate drain produces no duplicate client notices", peer.Drain().Count == 0);

            fixture.Experience.GrantToLevel(fixture.Player, 72, "upgrade");
            var delivered = fixture.Rewards.ProcessPending();
            using (var gate = await transitions.AcquireAsync(Fixture.CharacterId))
            {
                var notify = coordinator.NotifyAsync(delivered);
                using var replacement = await Peer.Create(sessions, Fixture.CharacterId, "新会话", register: false);
                await sessions.RegisterReplacingAsync(Fixture.CharacterId, replacement.Session);
                gate.Dispose();
                await notify;
                check("queued notification rejects a displaced session generation", peer.Drain().Count == 0 && replacement.Drain().Count == 0);
            }
            check("displaced-session notification cannot undo the already committed mail", fixture.Count("mailbox_messages") == 3);
        }

        private static async Task VerifyPacketBoundaryAsync(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var sessions = new SessionDirectory();
            using var peer = await Peer.Create(sessions, Fixture.CharacterId, "升级测试");
            fixture.RegisterInventory(peer.Session.SessionId);
            fixture.AddCharacter(62003);
            using var other = await Peer.Create(sessions, 62003, "队员");
            fixture.RegisterInventory(other.Session.SessionId, 62003);
            using var protocol = new GameProtocolHandler(sessions, database: fixture.Database);
            fixture.Experience.GrantToLevel(fixture.Player, 70, "packet-boundary");
            fixture.Experience.GrantToLevel(new PlayerContext { CharacterId = 62003, Level = 69 }, 70, "party-fanout");
            await protocol.OnPacketReceived_86JP(peer.Session,
                new GamePacketHeader { cmd = 1, type = ushort.MaxValue }, Array.Empty<byte>());
            check("central production packet boundary consumes committed events without a feature-specific hook",
                fixture.Pending() == 0 && fixture.Count("character_level_up_rewards") == 2
                    && peer.Drain().Any(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.MAILBOX_ALARM));
            check("shared packet processing also delivers to the other upgraded party member",
                other.Drain().Any(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.MAILBOX_ALARM)
                    && fixture.Mailbox.LoadInbox(62003, 100).Count == 1);
        }

        private static async Task VerifyExperienceItemPacketsAsync(Action<string, bool> check)
        {
            foreach (int itemId in new[] { 10099821, 10147584 })
                await VerifyExperienceItemPacketsAsync(check, itemId);
        }

        private static async Task VerifyExperienceItemPacketsAsync(Action<string, bool> check, int itemId)
        {
            using var fixture = new Fixture();
            var sessions = new SessionDirectory();
            using var peer = await Peer.Create(sessions, Fixture.CharacterId, "升级测试");
            peer.Session.Account = new AccountRecord { AccountId = Fixture.AccountId };
            peer.Session.Player.Level = 69;
            peer.Session.Player.Exp = (uint)ExpTableProvider.GetLevelThreshold(69) - 1001;
            fixture.Execute("UPDATE characters SET exp=" + peer.Session.Player.Exp + ";");
            fixture.Experience.Grant(peer.Session.Player, Fixture.AccountId, 1000,
                ExperiencePersistMode.OnLevelUpOnly, "kill-before-capsule");
            uint expBeforeItem = peer.Session.Player.Exp;
            peer.Session.GameSession = new GameSession(peer.Session, fixture.Database,
                new SqliteCharacterRepository(fixture.Database), sessionDirectory: sessions);
            var lease = fixture.RegisterInventory(peer.Session.SessionId);
            lease.Inventory.SetItem(InventoryListType.Main, 16,
                new ItemCore { ItemKind = ItemCore.KindConsumable, ItemId = itemId, Count = 1 });
            if (!OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "level-reward-exp-item-fixture"))
                throw new InvalidOperationException("EXP item fixture persistence failed");
            using var protocol = new GameProtocolHandler(sessions, database: fixture.Database);
            await protocol.OnPacketReceived_86JP(peer.Session,
                new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.INCREASE_STATUS }, BitConverter.GetBytes((short)16));
            var packets = peer.Drain();
            int expIndex = packets.FindIndex(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.EXP);
            int mailIndex = packets.FindIndex(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.MAILBOX_ALARM);
            check("real EXP item command preserves ACK, EXP and level persistence before its reward alarm",
                fixture.Level() == 70 && peer.Session.Player.Level == 70 && lease.Inventory.GetItem(InventoryListType.Main, 16) == null
                    && packets.Any(packet => packet[0] == 1 && BitConverter.ToUInt16(packet, 1) == (ushort)CmdPacketTypeA21.INCREASE_STATUS && packet[15] == 1)
                    && expIndex >= 0 && mailIndex > expIndex);
            check("upgrade-mail integration preserves attribute and acceptable-quest-list refreshes",
                packets.Count(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.USERINFO) >= 2
                    && packets.Any(packet => BitConverter.ToUInt16(packet, 1) == (ushort)NotiPacketTypeA21.ACCEPTABLE_QUEST_LIST));
            check("EXP item retains monster EXP that has not yet been persisted: " + itemId,
                peer.Session.Player.Exp == expBeforeItem + ExperienceItemDataProvider.Resolve(itemId).CalculateGain(69)
                    && fixture.Scalar("SELECT exp FROM characters;") == peer.Session.Player.Exp);
            await protocol.OnPacketReceived_86JP(peer.Session,
                new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.INCREASE_STATUS }, BitConverter.GetBytes((short)16));
            check("repeated EXP item packet neither consumes again nor duplicates reward mail",
                fixture.Count("character_level_up_rewards") == 1 && fixture.Count("mailbox_messages") == 1
                    && peer.Drain().All(packet => BitConverter.ToUInt16(packet, 1) != (ushort)NotiPacketTypeA21.MAILBOX_ALARM));
        }

        private static async Task VerifySkillBookExperienceAsync(Action<string, bool> check)
        {
            foreach (int itemId in new[] { ExperienceItemUseService.SkillPointBook5ItemId, ExperienceItemUseService.TpPointBook1ItemId })
            {
                using var fixture = new Fixture();
                var sessions = new SessionDirectory();
                using var peer = await Peer.Create(sessions, Fixture.CharacterId, "升级测试");
                peer.Session.Account = new AccountRecord { AccountId = Fixture.AccountId };
                peer.Session.Player.Level = fixture.Player.Level;
                peer.Session.Player.Exp = fixture.Player.Exp;
                fixture.Experience.Grant(peer.Session.Player, Fixture.AccountId, 1000,
                    ExperiencePersistMode.OnLevelUpOnly, "kill-before-skill-book");
                uint expBeforeBook = peer.Session.Player.Exp;
                peer.Session.GameSession = new GameSession(peer.Session, fixture.Database,
                    new SqliteCharacterRepository(fixture.Database), sessionDirectory: sessions);
                var lease = fixture.RegisterInventory(peer.Session.SessionId);
                lease.Inventory.SetItem(InventoryListType.Main, 16,
                    new ItemCore { ItemKind = ItemCore.KindConsumable, ItemId = itemId, Count = 1 });
                if (!OnlineInventoryMutationCommitCoordinator.TryCommit(lease, "level-reward-skill-book-fixture"))
                    throw new InvalidOperationException("skill book fixture persistence failed");
                using var protocol = new GameProtocolHandler(sessions, database: fixture.Database);
                await protocol.OnPacketReceived_86JP(peer.Session,
                    new GamePacketHeader { cmd = 1, type = (ushort)CmdPacketTypeA21.INCREASE_STATUS }, BitConverter.GetBytes((short)16));
                check("skill book preserves pending monster EXP: " + itemId,
                    lease.Inventory.GetItem(InventoryListType.Main, 16) == null
                        && peer.Session.Player.Exp == expBeforeBook && peer.Session.Player.Level == 69
                        && fixture.Count("character_level_up_events") == 0);
            }
        }

        private static void VerifyClockRecovery(Action<string, bool> check)
        {
            using var fixture = new Fixture();
            var sessions = new SessionDirectory();
            var clock = new ClockService();
            fixture.Experience.GrantToLevel(fixture.Player, 70, "offline-upgrade");
            var coordinator = new CharacterLevelUpRewardCoordinator(fixture.Rewards, sessions,
                new CharacterTransitionCoordinator(sessions));
            coordinator.StartRecovery(clock);
            check("startup drains persisted offline events before relying on a clock tick", fixture.Pending() == 0);
            fixture.Experience.GrantToLevel(fixture.Player, 71, "timer-settlement");
            fixture.Now = fixture.Now.AddMinutes(2);
            clock.CheckOnce(DateTime.UtcNow.AddMinutes(2));
            check("Clock reminder recovers an existing event without a subsequent player packet",
                fixture.Pending() == 0 && fixture.Count("character_level_up_rewards") == 2);
            coordinator.Dispose();
            check("runtime disposal cancels reward-recovery and notification timers", clock.GetDebugSnapshot().OneShotTimers == 0);
            clock.CheckOnce(DateTime.UtcNow.AddMinutes(4));
            check("clock ticks alone cannot invent upgrades or rewards",
                fixture.Count("character_level_up_events") == 2 && fixture.Count("mailbox_messages") == 2);
        }

        private sealed class Fixture : IDisposable
        {
            internal const int AccountId = 62001;
            internal const int CharacterId = 62002;
            private readonly List<(Guid Owner, int Character)> _inventoryOwners = new();
            internal GameDatabase Database { get; }
            internal MailboxService Mailbox { get; }
            internal CharacterExperienceService Experience { get; }
            internal CharacterLevelUpRewardService Rewards { get; }
            internal PlayerContext Player { get; }
            internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

            internal Fixture()
            {
                AccountExperienceBuffer.ResetForTests();
                Database = new GameDatabase(Path.Combine(Path.GetTempPath(), "s4a21-level-reward-" + Guid.NewGuid().ToString("N") + ".db"), ServerPaths.SchemaFilePath);
                Execute(@"INSERT INTO accounts(account_id,m_id,password_hash) VALUES(62001,'level-reward-test','');
INSERT INTO characters(character_id,account_id,name,job,level,exp) VALUES(62002,62001,'升级测试',0,69,0);
INSERT INTO character_subtype0_fields(character_id) VALUES(62002);
INSERT INTO character_subtype1_fields(character_id) VALUES(62002);
INSERT INTO character_init_flags(character_id) VALUES(62002);");
                Player = new PlayerContext { CharacterId = CharacterId, Level = 69, Exp = (uint)ExpTableProvider.GetLevelThreshold(68) };
                using (var connection = Database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "UPDATE characters SET exp=@exp WHERE character_id=62002;";
                    command.Parameters.AddWithValue("@exp", Player.Exp);
                    command.ExecuteNonQuery();
                }
                Mailbox = new MailboxService(new MailboxRepository(Database));
                Experience = new CharacterExperienceService(new AccountExperienceProgressService(new SqliteCharacterRepository(Database), Database), Database);
                Rewards = NewRewards();
            }

            internal CharacterLevelUpRewardService NewRewards()
                => new(Database, Mailbox, utcNow: () => Now);
            internal void AddCharacter(int characterId)
                => Execute($@"INSERT INTO characters(character_id,account_id,name,job,level,exp,slot_index)
VALUES({characterId},62001,'队员{characterId}',0,69,0,1);
INSERT INTO character_subtype0_fields(character_id) VALUES({characterId});
INSERT INTO character_subtype1_fields(character_id) VALUES({characterId});
INSERT INTO character_init_flags(character_id) VALUES({characterId});");
            internal InventoryLease RegisterInventory(Guid sessionId, int characterId = CharacterId)
            {
                using var connection = Database.OpenConnection();
                var inventory = InventoryService.LoadFromDb(connection, characterId, AccountId, Database);
                _inventoryOwners.Add((sessionId, characterId));
                return InventoryContext.Register(sessionId, characterId, inventory);
            }
            internal void Execute(string sql)
                => Database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                });
            internal long Scalar(string sql)
                => Database.Read(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    return Convert.ToInt64(command.ExecuteScalar());
                });
            internal long Count(string table) => Scalar("SELECT COUNT(*) FROM " + table + ";");
            internal void ApplyMigrations() => Database.Read(connection => { SqliteMigrations.Apply(connection); return 0; });
            internal long Pending() => Scalar("SELECT COUNT(*) FROM character_level_up_events WHERE base_rewards_processed_at_utc IS NULL;");
            internal long Level() => Scalar("SELECT level FROM characters WHERE character_id=62002;");
            public void Dispose()
            {
                foreach (var owner in _inventoryOwners) InventoryContext.Unregister(owner.Owner, owner.Character);
                AccountExperienceBuffer.ResetForTests();
                SqliteConnection.ClearAllPools();
                File.Delete(Database.DatabasePath);
                File.Delete(Database.DatabasePath + "-wal");
                File.Delete(Database.DatabasePath + "-shm");
            }
        }
    }
}

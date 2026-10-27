using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Auction.Storage;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class AuctionOperationJournalSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool condition)
            {
                Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
                if (condition) passed++; else failed++;
            }
            bool Throws(Action action)
            {
                try { action(); return false; }
                catch (InvalidOperationException) { return true; }
                catch (SqliteException) { return true; }
                catch (ArgumentException) { return true; }
            }
            var root = Path.Combine(AppContext.BaseDirectory, ".scratch", "auction-journal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var sourcePath = Path.Combine(root, "source-private.db");
                _ = new AuctionOperationJournal(sourcePath);
                var privatePath = Path.Combine(root, "upgrade-private.db");
                var journal = new AuctionOperationJournal(privatePath);
                Check("independent auction store reopens", new AuctionOperationJournal(privatePath).Pending(0, 10).Count == 0);
                var publicDatabase = new GameDatabase(Path.Combine(root, "inventory.db"), ServerPaths.SchemaFilePath);
                AuctionOperationIntent Intent(string resource, Guid? id = null, string json = "{\"quantity\":1}")
                    => new(id ?? Guid.NewGuid(), resource, AuctionOperationAction.List, 91001, 91001, 0, 1792700000, json);
                var first = Intent("listing:one");
                using (var c = new SqliteConnection("Data Source=" + privatePath))
                using (var other = new SqliteConnection("Data Source=" + sourcePath))
                {
                    c.Open(); other.Open(); using var t = other.BeginTransaction();
                    Check("journal cannot autocommit outside order transaction", Throws(() => AuctionOperationJournal.PrepareInTransaction(c, null, first)));
                    Check("journal rejects foreign private transaction", Throws(() => AuctionOperationJournal.PrepareInTransaction(c, t, first)));
                    Check("resolution cannot release reservation outside transaction", Throws(() => AuctionOperationJournal.ResolveInTransaction(c, null,
                        first, AuctionOperationState.Aborted, "{}", "Test")));
                }
                var prepared = journal.Prepare(first);
                Check("prepare survives reopen with immutable identity", new AuctionOperationJournal(privatePath).Find(first.OperationId)?.Intent.IntentHash == first.IntentHash);
                Check("replay same intent retains sequence", journal.Prepare(first).SequenceId == prepared.SequenceId);
                Check("same UUID changed quantity is rejected", Throws(() => journal.Prepare(Intent("listing:one", first.OperationId, "{\"quantity\":2}"))));
                Check("second pending operation cannot reserve same resource", Throws(() => journal.Prepare(Intent("listing:one"))));
                journal.AbortVerifiedUncommitted(first, "TestExecutorStoppedWithoutCommit");
                var next = Intent("listing:one"); journal.Prepare(next);
                Check("proven abort releases reservation", journal.Find(next.OperationId).State == AuctionOperationState.Prepared);
                journal.Quarantine(next, "TestConflict");
                Check("quarantine retains resource ownership", Throws(() => journal.Prepare(Intent("listing:one"))));
                Check("terminal state cannot be overwritten", Throws(() => journal.Complete(next, "{}")));

                var racers = Enumerable.Range(0, 8).Select(_ => Intent("listing:race")).ToArray();
                var won = new bool[racers.Length];
                Parallel.For(0, racers.Length, i =>
                {
                    try { journal.Prepare(racers[i]); won[i] = true; }
                    catch (SqliteException) { }
                });
                Check("concurrent durable reservations have exactly one winner", won.Count(x => x) == 1);

                var committed = Intent("listing:committed"); journal.Prepare(committed);
                publicDatabase.Write((c, t) => AuctionPublicReceiptRepository.Insert(c, t, committed, Guid.Empty, "{\"delivered\":true}"));
                var uncommitted = Intent("listing:rolled-back"); journal.Prepare(uncommitted);
                using (var c = publicDatabase.OpenConnection())
                using (var t = c.BeginTransaction())
                {
                    AuctionPublicReceiptRepository.Insert(c, t, uncommitted, Guid.Empty, "{\"delivered\":true}");
                    t.Rollback();
                }
                Check("rolled-back public transaction leaves no receipt", publicDatabase.Read(c => AuctionPublicReceiptRepository.Find(c, null, uncommitted)) == null);
                var gate = new object();
                var recovery = new AuctionOperationRecovery(journal, publicDatabase, gate);
                var recovered = recovery.RecoverPendingAfterExecutorsQuiesced(100);
                Check("public-commit private-finalize gap recovers committed result", recovered.Committed == 1
                    && journal.Find(committed.OperationId).State == AuctionOperationState.Committed
                    && journal.Find(committed.OperationId).ResultJson == "{\"delivered\":true}");
                Check("absent receipt after executor stops aborts without debit replay", journal.Find(uncommitted.OperationId).State == AuctionOperationState.Aborted);
                Check("repeated recovery has no effects", recovery.RecoverPendingAfterExecutorsQuiesced(100) == new AuctionRecoveryResult(0, 0, 0));
                Check("quarantine excluded from automatic recovery", journal.Find(next.OperationId).State == AuctionOperationState.Quarantined);
                Check("terminal complete repeats only identical result", journal.Complete(committed, "{\"delivered\":true}").State == AuctionOperationState.Committed
                    && Throws(() => journal.Complete(committed, "{\"delivered\":false}")));
                Check("receipt identity includes actor and frozen intent", Throws(() => publicDatabase.Read(c =>
                    AuctionPublicReceiptRepository.Find(c, null, Intent("listing:committed", committed.OperationId, "{\"quantity\":2}")))));
                Check("receipt requires asset transaction", Throws(() => publicDatabase.Read(c =>
                    AuctionPublicReceiptRepository.Insert(c, null, Intent("listing:no-tx"), Guid.Empty, "{}"))));

                // Corrupt/duplicate receipt must not release ownership or replay assets.
                var duplicate = Intent("listing:duplicate"); journal.Prepare(duplicate);
                publicDatabase.Write((c, t) => {
                    AuctionPublicReceiptRepository.Insert(c, t, duplicate, Guid.Empty, "{}");
                    using var command = c.CreateCommand(); command.Transaction = t;
                    command.CommandText = @"INSERT INTO inventory_audit_log(created_at,owner_scope,owner_id,character_id,account_id,action_name,payload_json)
SELECT created_at,owner_scope,owner_id,character_id,account_id,action_name,payload_json FROM inventory_audit_log WHERE action_name=@action;";
                    command.Parameters.AddWithValue("@action", "auction_asset:" + duplicate.OperationId.ToString("N"));
                    command.ExecuteNonQuery();
                });
                Check("duplicate permanent receipts quarantine rather than reexecute", recovery.RecoverPendingAfterExecutorsQuiesced(100).Quarantined == 1
                    && journal.Find(duplicate.OperationId).State == AuctionOperationState.Quarantined
                    && Throws(() => journal.Prepare(Intent("listing:duplicate"))));
                Check("bounded recovery rejects invalid limit", Throws(() => journal.Pending(0, 0)));
                var uncertain = Intent("listing:unavailable"); journal.Prepare(uncertain);
                var unavailable = new AuctionOperationRecovery(journal, new UnavailableDatabase(), gate);
                Check("public access failure is not absence", Throws(() => unavailable.RecoverPendingAfterExecutorsQuiesced(100))
                    && journal.Find(uncertain.OperationId).State == AuctionOperationState.Prepared
                    && Throws(() => journal.Prepare(Intent("listing:unavailable"))));
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            finally
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(root, true);
            }
            Console.WriteLine($"AuctionOperationJournalSelfTest: passed={passed} failed={failed}");
            return failed == 0 ? 0 : 1;
        }

        private sealed class UnavailableDatabase : IGameDatabase
        {
            public string DatabasePath => "unavailable-test";
            public string SchemaFilePath => "unavailable-test";
            public string ConnectionString => "unavailable-test";
            public SqliteConnection OpenConnection() => throw new InvalidOperationException("Simulated unavailable connection.");
            public T Read<T>(Func<SqliteConnection, T> action) => throw new InvalidOperationException("Simulated unavailable connection.");
            public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> action, bool immediate = true) => throw new InvalidOperationException();
            public void Write(Action<SqliteConnection, SqliteTransaction> action, bool immediate = true) => throw new InvalidOperationException();
        }
    }
}

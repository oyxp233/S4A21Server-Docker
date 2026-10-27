using System;
using System.IO;
using DfoServer.Game.Auction.Storage;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests;

public static class AuctionDatabaseSelfTest
{
    public static int Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, ".scratch", "auction-database-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int failed = 0, passed = 0;
        void Check(string name, bool good) { Console.WriteLine($"[{(good ? "PASS" : "FAIL")}] {name}"); if (good) passed++; else failed++; }
        bool Refuses(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } catch (SqliteException) { return true; } }
        try
        {
            string path = Path.Combine(root, "auction.db");
            var store = AuctionDatabase.Open(path);
            Check("auction has explicit independent baseline", store.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT baseline_id FROM feature_metadata;"; return (string)cmd.ExecuteScalar() == "a21-auction-v1"; }));
            var journal = new AuctionOperationJournal(path);
            var intent = new AuctionOperationIntent(Guid.NewGuid(), "test:durable", AuctionOperationAction.List, 1, 1, 0, 1792700000, "{}");
            journal.Prepare(intent);
            Check("reopen preserves pending immutable operation", new AuctionOperationJournal(path).Find(intent.OperationId).Intent.IntentHash == intent.IntentHash);
            Check("auction contains only its own four tables and metadata", store.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';"; return Convert.ToInt32(cmd.ExecuteScalar()) == 5; }));
            string empty = Path.Combine(root, "unmarked.db"); File.WriteAllBytes(empty, Array.Empty<byte>());
            Check("preexisting unmarked file refused without adoption", Refuses(() => AuctionDatabase.Open(empty)) && new FileInfo(empty).Length == 0);
            store.Write((c, t) => { using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "CREATE TABLE unrecognized (value TEXT);"; cmd.ExecuteNonQuery(); });
            Check("unknown same-version object refused", Refuses(() => AuctionDatabase.Open(path)));
            Check("schema refusal does not erase journal", journal.Find(intent.OperationId).Intent.IntentHash == intent.IntentHash);
        }
        catch (Exception ex) { Check(ex.ToString(), false); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine($"AUCTION_DATABASE: {passed} PASS / {failed} FAIL");
        return failed == 0 ? 0 : 1;
    }
}

using System;
using System.IO;
using DfoServer.Game.Inventory;
using DfoServer.Game.Raid;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests;

public static class RaidPersistenceSelfTest
{
	public static int Run()
	{
		int failures = 0;
		string text = Path.Combine(Path.GetTempPath(), "a21-raid-persistence-" + Guid.NewGuid().ToString("N") + ".db");
		Guid sessionId = Guid.NewGuid();
		Guid sessionId2 = Guid.NewGuid();
		try
		{
			GameDatabase db = new GameDatabase(text, ServerPaths.SchemaFilePath);
			using (SqliteConnection sqliteConnection = db.OpenConnection())
			{
				using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
				sqliteCommand.CommandText = "INSERT INTO accounts(account_id,m_id,password_hash) VALUES(987001,'raid-test-a',''),(987002,'raid-test-b','');\nINSERT INTO characters(character_id,account_id,name) VALUES(987001,987001,X'41'),(987002,987002,X'42');";
				sqliteCommand.ExecuteNonQuery();
			}
			InventoryLease lease = InventoryContext.Register(sessionId, Load(987001));
			InventoryLease lease2 = InventoryContext.Register(sessionId2, Load(987002));
			InventoryLease[] array = new InventoryLease[2] { lease, lease2 };
			foreach (InventoryLease inventoryLease in array)
			{
				ItemCore itemCore = ItemCore.Create(2, 10096296);
				itemCore.Count = 2;
				inventoryLease.Inventory.SetItem(InventoryListType.Main, 9, itemCore);
				Check("seed ticket through new persistence", InventoryPersistenceService.SaveDirty(inventoryLease));
			}
			Check("duplicate owner is rejected before deduction", !RaidEntryCostCommitService.TryConsume(new InventoryLease[2] { lease, lease }, out var mutations) && Tickets(987001) == 2);
			Check("whole squad deduction commits once per owner", RaidEntryCostCommitService.TryConsume(new InventoryLease[2] { lease2, lease }, out var mutations2) && mutations2.Count == 2 && Tickets(987001) == 1 && Tickets(987002) == 1);
			using (SqliteConnection sqliteConnection2 = db.OpenConnection())
			{
				using SqliteCommand sqliteCommand2 = sqliteConnection2.CreateCommand();
				sqliteCommand2.CommandText = "CREATE TRIGGER raid_test_delete_abort BEFORE DELETE ON character_inventory_items\nWHEN OLD.character_id=987002 BEGIN SELECT RAISE(ABORT,'injected squad commit failure'); END;";
				sqliteCommand2.ExecuteNonQuery();
			}
			Check("second owner failure rolls back both persisted deductions", !RaidEntryCostCommitService.TryConsume(new InventoryLease[2] { lease, lease2 }, out mutations) && Tickets(987001) == 1 && Tickets(987002) == 1);
			Check("rollback restores current owned inventories", InventoryContext.Get(987001).CountMainItem(10096296) == 1 && InventoryContext.Get(987002).CountMainItem(10096296) == 1);
			InventoryContext.Register(sessionId, Load(987001));
			InventoryContext.Register(sessionId2, Load(987002));
			Check("retired leases cannot deduct or grant rewards", !RaidEntryCostCommitService.TryConsume(new InventoryLease[2] { lease, lease2 }, out mutations) && !RaidRewardCommitService.TryGrantGold(lease, 100));
			using (SqliteConnection sqliteConnection3 = db.OpenConnection())
			{
				using SqliteCommand sqliteCommand3 = sqliteConnection3.CreateCommand();
				sqliteCommand3.CommandText = "DROP TRIGGER raid_test_delete_abort";
				sqliteCommand3.ExecuteNonQuery();
			}
			InventoryContext.TryGetLease(987001, out lease);
			InventoryContext.TryGetLease(987002, out lease2);
			Check("current leases may retry after rollback", RaidEntryCostCommitService.TryConsume(new InventoryLease[2] { lease, lease2 }, out mutations) && Tickets(987001) == 0 && Tickets(987002) == 0);
			Check("missing tickets do not produce mutations", !RaidEntryCostCommitService.TryConsume(new InventoryLease[2] { lease, lease2 }, out var mutations3) && mutations3.Count == 0);
			int count = Load(987001).GetMainVirtualCount(0).Count;
			Check("raid gold grant uses current commit boundary", RaidRewardCommitService.TryGrantGold(lease, 100) && Load(987001).GetMainVirtualCount(0).Count == count + 100);
			Check("raid item grant persists and reloads", RaidRewardCommitService.TryGrantItem(lease, 10096296, 1, out var changes) && changes.Length != 0 && Tickets(987001) == 1);
			int num = InventoryGoldCarryLimitLoader.Load(lease2.Inventory);
			lease2.Inventory.SetMainVirtualCount(0, 0, num - 10);
			Check("seed near-cap gold through current persistence", InventoryPersistenceService.SaveDirty(lease2));
			Check("raid gold receipt reports actual committed amount at the existing carry limit", RaidRewardCommitService.TryGrantGold(lease2, 120000, out var grantedCount) && grantedCount == 10 && Load(987002).GetMainVirtualCount(0).Count == num);
			Check("failed capped gold grant cannot report credited gold", !RaidRewardCommitService.TryGrantGold(lease2, 120000, out grantedCount) && grantedCount == 0 && Load(987002).GetMainVirtualCount(0).Count == num);
			InventoryService Load(int id)
			{
				using SqliteConnection connection = db.OpenConnection();
				return InventoryService.LoadFromDb(connection, id, id, db);
			}
			int Tickets(int id)
			{
				return Load(id).CountMainItem(10096296);
			}
		}
		catch (Exception ex)
		{
			failures++;
			Console.WriteLine("[FAIL] raid persistence: " + ex);
		}
		finally
		{
			InventoryContext.Unregister(sessionId);
			InventoryContext.Unregister(sessionId2);
			SqliteConnection.ClearAllPools();
			string[] array2 = new string[3] { "", "-wal", "-shm" };
			foreach (string text2 in array2)
			{
				if (File.Exists(text + text2))
				{
					File.Delete(text + text2);
				}
			}
		}
		Console.WriteLine($"RAID_PERSISTENCE failures={failures}");
		return (failures != 0) ? 1 : 0;
		void Check(string name, bool ok)
		{
			Console.WriteLine("[" + (ok ? "PASS" : "FAIL") + "] " + name);
			if (!ok)
			{
				failures++;
			}
		}
	}
}

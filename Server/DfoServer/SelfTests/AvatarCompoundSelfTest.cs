using System;
using System.IO;
using DfoServer.Game.Inventory;

namespace DfoServer.SelfTests
{
    public static class AvatarCompoundSelfTest
    {
        private const int AccountId = 910063;
        private const int CharacterId = 910163;
        private const short ConsumeSlot = 7;
        private const short Slot1 = 10;
        private const short Slot2 = 11;
        private const int CompoundItemId = 21;
        private const ushort AbilityNo = 2;

        private static readonly int[] AvatarCandidates =
        {
            401500167,
            40819,
            41540,
            108550662,
            108560645,
            108570739,
            108520635,
            101520586,
            101520585,
            40303,
        };

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== AVATAR_COMPOUND selftest ===");

            var previousDatabasePath = Environment.GetEnvironmentVariable("INVENTORY_DATABASE_PATH");
            var tempDb = Path.Combine(Path.GetTempPath(), "avatar_compound_selftest.db");
            DeleteTempDatabase(tempDb);
            Environment.SetEnvironmentVariable("INVENTORY_DATABASE_PATH", tempDb);

            try
            {
                var oldItemId1 = ResolveAvatarId(0);
                var oldItemId2 = ResolveAvatarId(1);
                var newItemId = ResolveAvatarId(2);
                Check("avatar metadata resolved", oldItemId1 > 0 && oldItemId2 > 0 && newItemId > 0);
                if (_fail > 0)
                {
                    PrintSummary();
                    return 1;
                }

                var inventory = new InventoryService(CharacterId, AccountId);
                for (short slot = 0; slot < Slot1; slot++)
                    inventory.SetItem(InventoryListType.Avatar, slot, CreateAvatar(oldItemId1, 20000 + slot));
                inventory.SetItem(InventoryListType.Avatar, Slot1, CreateAvatar(oldItemId1, 10001));
                inventory.SetItem(InventoryListType.Avatar, Slot2, CreateAvatar(oldItemId2, 10002));
                inventory.SetItem(InventoryListType.Main, ConsumeSlot, CreateStackable(CompoundItemId, 2));
                inventory.ClearDirtyState();

                var request = new InventoryAvatarCompoundRequest
                {
                    ConsumeSlot = ConsumeSlot,
                    Slot1 = Slot1,
                    Slot2 = Slot2,
                    RequestedItemId = newItemId,
                    AbilityNo = AbilityNo,
                };

                var ok = InventoryAvatarCompoundService.TryCompoundAvatar(
                    inventory,
                    request,
                    (old1, old2, materialId) => new[] { newItemId },
                    out var result);

                Check("compound succeeds", ok && result != null && result.Success);
                Check("new avatar inserted into first consumed slot", result != null && result.NewSlots.Count == 1 && result.NewSlots[0] == Slot1);
                Check("new avatar item id", inventory.GetItem(InventoryListType.Avatar, Slot1)?.ItemId == newItemId);
                Check("new avatar ability_no", inventory.GetItem(InventoryListType.Avatar, Slot1)?.AbilityNo == AbilityNo);
                Check("second consumed avatar slot empty", inventory.GetItem(InventoryListType.Avatar, Slot2) == null);
                Check("compound item decremented", inventory.GetItem(InventoryListType.Main, ConsumeSlot)?.Count == 1);

                var core = inventory.GetItem(InventoryListType.Avatar, Slot1);
                var detail = core != null ? inventory.AvatarDetails.GetDetail(core.AvatarUid) : null;
                Check("new avatar detail created", detail != null && detail.ItemId == newItemId);
                Check("changed avatar slots recorded",
                    result != null
                    && result.Changes.Slots.Count >= 3);
            }
            finally
            {
                Environment.SetEnvironmentVariable("INVENTORY_DATABASE_PATH", previousDatabasePath);
                DeleteTempDatabase(tempDb);
            }

            PrintSummary();
            return _fail == 0 ? 0 : 1;
        }

        private static int ResolveAvatarId(int startIndex)
        {
            for (var index = startIndex; index < AvatarCandidates.Length; index++)
            {
                var itemId = AvatarCandidates[index];
                if (ItemMetadataResolver.TryResolveItemKind(itemId, out var itemKind)
                    && itemKind == ItemCore.KindAvatar)
                    return itemId;
            }

            return 0;
        }

        private static ItemCore CreateAvatar(int itemId, int uid)
        {
            var core = ItemCore.Create(ItemCore.KindAvatar, itemId);
            core.AvatarUid = uid;
            return core;
        }

        private static ItemCore CreateStackable(int itemId, int count)
        {
            var core = ItemCore.Create(ItemCore.KindConsumable, itemId);
            core.Count = count;
            return core;
        }

        private static void DeleteTempDatabase(string path)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    var file = path + suffix;
                    if (File.Exists(file))
                        File.Delete(file);
                }
                catch
                {
                }
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok)
                _pass++;
            else
                _fail++;
        }

        private static void PrintSummary()
        {
            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
        }
    }
}

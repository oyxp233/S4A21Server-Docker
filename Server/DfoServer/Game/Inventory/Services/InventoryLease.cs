using System;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryLease
    {
        public InventoryLease(Guid sessionId, int characterId, InventoryService inventory, long version, bool contextManaged = false)
        {
            if (sessionId == Guid.Empty)
                throw new ArgumentException("背包租约必须绑定有效的 sessionId。", nameof(sessionId));
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId), "背包租约必须绑定有效的角色 ID。");

            SessionId = sessionId;
            CharacterId = characterId;
            Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            if (inventory.CharacterId != characterId)
                throw new ArgumentException("Inventory lease model owner does not match its character.", nameof(inventory));
            SyncRoot = inventory.PersistenceSyncRoot;
            Version = version;
            WasContextManaged = contextManaged;
            if (contextManaged) inventory.MarkContextManaged();
        }

        public Guid SessionId { get; }

        public int CharacterId { get; }

        public int AccountId => Inventory.AccountId;

        public InventoryService Inventory { get; private set; }

        public object SyncRoot { get; }

        public long Version { get; }
        internal bool WasContextManaged { get; }

        public bool IsOwnedBy(Guid sessionId)
        {
            return SessionId == sessionId;
        }

        internal void ReplaceInventory(InventoryService inventory)
        {
            if (inventory == null)
                throw new ArgumentNullException(nameof(inventory));
            if (inventory.CharacterId != CharacterId || inventory.AccountId != AccountId)
                throw new ArgumentException("replacement inventory owner does not match the lease", nameof(inventory));

            inventory.BindPersistenceSyncRoot(SyncRoot);
            if (WasContextManaged) inventory.MarkContextManaged();
            Inventory = inventory;
        }
    }
}

using System;

namespace DfoServer.Game.Inventory
{
    internal sealed class InventoryLease
    {
        public InventoryLease(Guid sessionId, int characterId, InventoryService inventory, long version)
        {
            if (sessionId == Guid.Empty)
                throw new ArgumentException("背包租约必须绑定有效的 sessionId。", nameof(sessionId));
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId), "背包租约必须绑定有效的角色 ID。");

            SessionId = sessionId;
            CharacterId = characterId;
            Inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            Version = version;
        }

        public Guid SessionId { get; }

        public int CharacterId { get; }

        public int AccountId => Inventory.AccountId;

        public InventoryService Inventory { get; private set; }

        public object SyncRoot { get; } = new object();

        public long Version { get; }

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

            Inventory = inventory;
        }
    }
}

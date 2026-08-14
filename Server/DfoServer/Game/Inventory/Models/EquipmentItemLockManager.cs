using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal sealed class EquipmentItemLockManager
    {
        private Dictionary<byte, EquipmentItemLock> _locks = new Dictionary<byte, EquipmentItemLock>();

        public IReadOnlyCollection<EquipmentItemLock> Locks => _locks.Values;

        internal void LoadForCharacter(SqliteConnection connection, int characterId)
        {
            _locks = EquipmentItemLockRepository.LoadForCharacter(connection, characterId);
        }

        public EquipmentItemLock Get(byte equipmentLockId)
        {
            _locks.TryGetValue(equipmentLockId, out var itemLock);
            return itemLock;
        }

        public bool TryGet(byte equipmentLockId, out EquipmentItemLock itemLock)
        {
            return _locks.TryGetValue(equipmentLockId, out itemLock);
        }

        public void Attach(EquipmentItemLock itemLock)
        {
            if (itemLock == null || itemLock.EquipmentLockId == 0)
                return;

            _locks[itemLock.EquipmentLockId] = itemLock.Copy();
        }

        public bool Remove(byte equipmentLockId)
        {
            return equipmentLockId != 0 && _locks.Remove(equipmentLockId);
        }
    }
}

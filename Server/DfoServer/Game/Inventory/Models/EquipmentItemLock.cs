namespace DfoServer.Game.Inventory
{
    internal sealed class EquipmentItemLock
    {
        public byte EquipmentLockId { get; set; }

        public byte State { get; set; } = 1;

        public int RemainingSeconds { get; set; }

        public EquipmentItemLock Copy()
        {
            return new EquipmentItemLock
            {
                EquipmentLockId = EquipmentLockId,
                State = State,
                RemainingSeconds = RemainingSeconds,
            };
        }
    }
}

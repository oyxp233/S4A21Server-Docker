namespace DfoServer.Game.Inventory
{
    public sealed class AccountCargoStateSnapshot
    {
        public ushort SelectionKey { get; set; }

        public ushort ItemCount { get; set; }

        public int Value32 { get; set; }
    }
}

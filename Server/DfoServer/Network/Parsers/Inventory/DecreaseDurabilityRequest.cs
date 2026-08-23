namespace DfoServer.Network.Parsers.Inventory
{
    internal sealed class DecreaseDurabilityRequest
    {
        internal short EquipmentSlotIndex { get; set; }

        internal static bool TryParse(byte[] body, out DecreaseDurabilityRequest request)
        {
            request = null;
            if (body == null || body.Length != 1)
                return false;

            request = new DecreaseDurabilityRequest
            {
                EquipmentSlotIndex = body[0],
            };
            return true;
        }
    }
}

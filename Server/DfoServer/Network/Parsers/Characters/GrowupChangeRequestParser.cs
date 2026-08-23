using DfoServer.Game.Characters;

namespace DfoServer.Network.Parsers.Characters
{
    internal static class GrowupChangeRequestParser
    {
        private const int TargetGrowTypeOffset = 14;

        internal static bool TryParse(
            byte[] body,
            out GrowupChangeRequest request)
        {
            request = null;
            if (body == null || body.Length <= TargetGrowTypeOffset)
                return false;

            request = new GrowupChangeRequest
            {
                TargetGrowType = body[TargetGrowTypeOffset],
            };
            return true;
        }
    }
}

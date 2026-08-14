using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Network.Parsers.Inventory
{
    public static class InvestItemAmplifyOptionRequestParser
    {
        public static bool TryParse(byte[] body, out InvestItemAmplifyOptionRequest request)
        {
            request = null;
            if (body == null || body.Length < 14)
                return false;

            var actionRaw = body[0];
            if (actionRaw != (byte)InvestItemAmplifyOptionAction.Invest
                && actionRaw != (byte)InvestItemAmplifyOptionAction.Twist
                && actionRaw != (byte)InvestItemAmplifyOptionAction.PureGold)
                return false;

            request = new InvestItemAmplifyOptionRequest
            {
                Action = (InvestItemAmplifyOptionAction)actionRaw,
                TargetSlotIndex = BitConverter.ToInt16(body, 1),
                TargetItemTemplateId = BitConverter.ToInt32(body, 3),
                MaterialSlotIndex = BitConverter.ToInt16(body, 7),
                MaterialItemTemplateId = BitConverter.ToInt32(body, 9),
                SelectedOption = body[13],
            };
            return true;
        }
    }
}

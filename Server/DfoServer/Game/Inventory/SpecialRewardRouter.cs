namespace DfoServer.Game.Inventory
{
    public sealed class SpecialRewardOutcome
    {
        public SpecialRewardKind Kind { get; set; }
        public int ItemTemplateId { get; set; }
        public int Count { get; set; }
        public short WalletSlot { get; set; }
        public int WalletNewTotal { get; set; }
    }

    public enum SpecialRewardKind
    {
        Premium,
        ReviveCoin,
        HappyTokenCera,
    }

    internal static class SpecialRewardRouter
    {
        // Current PVF: stackable/cash/chn_Trade_Gold/trade_point_event.stk.
        internal const int HappyTokenCeraVoucherItemId = 2681917;

        internal static bool TryResolveAccountCurrencyReward(
            int itemTemplateId,
            int count,
            out SpecialRewardOutcome outcome)
        {
            outcome = null;
            if (itemTemplateId != HappyTokenCeraVoucherItemId || count <= 0)
                return false;

            outcome = new SpecialRewardOutcome
            {
                Kind = SpecialRewardKind.HappyTokenCera,
                ItemTemplateId = itemTemplateId,
                Count = count,
            };
            return true;
        }
    }
}

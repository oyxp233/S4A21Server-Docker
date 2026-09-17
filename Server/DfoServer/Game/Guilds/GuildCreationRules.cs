using DfoServer.Game.Names;

namespace DfoServer.Game.Guilds
{
    internal static class GuildCreationRules
    {
        // Server name policy; wire length counts GBK bytes, not UTF-16 characters.
        internal const int MaximumNameBytes = 24;
        internal const int MaximumPromotionBytes = 80;

        internal static bool TryValidateName(byte[] raw, out string name)
            => NameInputValidator.TryValidateRawName(raw, 2, MaximumNameBytes, out name, out _);

        internal static bool IsValidPromotion(string text)
            => text != null && text.Length <= 40 && text.IndexOf('\0') < 0
                && !NameInputRuleSet.Current.HasSlang(text);
    }
}

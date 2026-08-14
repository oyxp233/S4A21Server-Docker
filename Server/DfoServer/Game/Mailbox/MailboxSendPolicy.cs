using DfoServer.Game.Inventory;
using System;

namespace DfoServer.Game.Mailbox
{
    internal static class MailboxSendPolicy
    {
        private const int MinExpirationUnixTime = 1000000000;

        public static MailboxSendError ValidateAttachment(
            MailboxSendRequest request,
            ItemCore core)
        {
            if (core == null || core.ItemId <= 0)
                return MailboxSendError.InvalidAttachment;

            // Inventory records can contain small sentinel values (for example 1).
            // Only values in the same Unix-time range accepted by inventory PVF parsing
            // represent an actual item expiration timestamp.
            if (core.ExpireTime >= MinExpirationUnixTime)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return core.ExpireTime <= now
                    ? MailboxSendError.ExpiredItem
                    : MailboxSendError.LimitedPeriodItem;
            }

            // The client stores the instance-level transfer restriction in the
            // common 84-byte item tail at offset 76 (tailData2F[29]). This is
            // independent from the PVF attach type and the attr high-bit trade
            // counter, so a tradable template can still be blocked per instance.
            if (core.TradeRestriction != 0)
                return MailboxSendError.NotTradable;

            var metadata = ItemMetadataResolver.Resolve(core.ItemId);
            var attachType = NormalizePvfToken(metadata?.AttachType);
            if (attachType == "trade limit")
            {
                return core.StackTradeCount > 0
                    ? MailboxSendError.None
                    : MailboxSendError.NotTradable;
            }
            return ValidateAttachType(request, attachType, core.SealFlag);
        }

        internal static int GetRemainingTradeCount(ItemCore core)
        {
            return core?.StackTradeCount ?? 0;
        }

        internal static byte GetTradeRestriction(ItemCore core)
        {
            return core?.TradeRestriction ?? 0;
        }

        internal static ItemCore SetRemainingTradeCount(ItemCore core, int remainingCount)
        {
            if (core == null)
                return null;

            var updated = core.Copy();
            updated.StackTradeCount = (byte)Math.Max(0, Math.Min(7, remainingCount));
            return updated;
        }

        internal static bool IsTradeLimitItem(ItemMetadata metadata)
        {
            return metadata != null
                && NormalizePvfToken(metadata.AttachType) == "trade limit";
        }

        internal static MailboxSendError ValidateAttachType(
            MailboxSendRequest request,
            string attachType,
            int sealFlag)
        {
            attachType = NormalizePvfToken(attachType);
            if (attachType == "free")
                return MailboxSendError.None;

            if (attachType.Contains("account"))
            {
                return request.SenderAccountId == request.ReceiverAccountId
                    ? MailboxSendError.None
                    : MailboxSendError.AccountBound;
            }

            // Sealing items are transferable only while their persisted instance is sealed.
            if (attachType == "sealing" || attachType == "seal")
                return sealFlag != 0 ? MailboxSendError.None : MailboxSendError.NotTradable;

            if (attachType.Length == 0
                || attachType == "trade"
                || attachType == "trade delete"
                || attachType == "sealing trade"
                || attachType.Contains("no trade")
                || attachType.Contains("not trade")
                || attachType.Contains("untrade")
                || attachType.Contains("character")
                || attachType == "bind"
                || attachType == "bound")
            {
                return MailboxSendError.NotTradable;
            }

            // Mail is an asset-transfer boundary. Unknown PVF policies fail closed so a new
            // token cannot silently turn a bound item into a tradable one.
            return MailboxSendError.NotTradable;
        }

        public static MailboxSendError ValidateDeferredPolicies(MailboxSendRequest request)
        {
            // Integration point for policies whose authoritative state does not exist yet:
            // blacklist (77/90 requires client-version confirmation), sender and receiver
            // trade restrictions (114/115), illegal text (159), and level/send count
            // limits (227). Receiver-level gold (14) and daily gold (70) are enforced by
            // MailboxRepository with authoritative database state.
            return MailboxSendError.None;
        }

        private static string NormalizePvfToken(string value)
        {
            return (value ?? string.Empty)
                .Replace("`", string.Empty)
                .Replace("[", string.Empty)
                .Replace("]", string.Empty)
                .Trim()
                .ToLowerInvariant();
        }
    }
}

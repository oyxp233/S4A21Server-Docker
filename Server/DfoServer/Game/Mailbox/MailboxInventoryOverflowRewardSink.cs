using DfoServer.Game.Inventory;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Mailbox
{
    internal sealed class MailboxInventoryOverflowRewardSink : IInventoryOverflowRewardSink
    {
        private const int MaxAttachmentsPerMail = 10;

        private readonly MailboxService _mailboxService;

        internal MailboxInventoryOverflowRewardSink(MailboxService mailboxService)
        {
            _mailboxService = mailboxService ?? throw new ArgumentNullException(nameof(mailboxService));
        }

        public bool TryDeliver(
            InventoryService inventory,
            IReadOnlyList<InventoryRewardGrantRequest> rewards,
            out InventoryOverflowDeliveryResult result)
            => TryDeliver(inventory, rewards, null, null, out result);

        internal bool TryDeliver(
            InventoryService inventory,
            IReadOnlyList<InventoryRewardGrantRequest> rewards,
            string title,
            string text,
            out InventoryOverflowDeliveryResult result)
            => TryDeliverCore(
                inventory,
                rewards,
                title,
                text,
                mails => _mailboxService.SendSystemMails(mails),
                out result);

        internal bool TryDeliver(
            SqliteConnection connection,
            SqliteTransaction transaction,
            InventoryService inventory,
            IReadOnlyList<InventoryRewardGrantRequest> rewards,
            string title,
            string text,
            out InventoryOverflowDeliveryResult result)
            => TryDeliverCore(
                inventory,
                rewards,
                title,
                text,
                mails => _mailboxService.SendSystemMails(connection, transaction, mails),
                out result);

        private static bool TryDeliverCore(
            InventoryService inventory,
            IReadOnlyList<InventoryRewardGrantRequest> rewards,
            string title,
            string text,
            Func<IReadOnlyList<MailboxSendRequest>, MailboxSendResult> sendMails,
            out InventoryOverflowDeliveryResult result)
        {
            result = new InventoryOverflowDeliveryResult();
            if (inventory == null || rewards == null || sendMails == null)
                return Fail(result);
            if (rewards.Count == 0)
                return true;

            if (!TryBuildAttachmentRequests(rewards, out var attachments))
                return Fail(result);

            if (attachments.Count == 0)
                return true;

            var mails = BuildMailRequests(inventory, attachments, title, text);
            var send = sendMails(mails);
            if (!send.Success)
            {
                FileLogger.Log($"[InventoryOverflow] mail delivery failed cid={inventory.CharacterId} reason={send.Error}");
                return Fail(result);
            }

            FileLogger.Log($"[InventoryOverflow] delivered rewards to mailbox cid={inventory.CharacterId} attachments={attachments.Count}");
            return true;
        }

        internal static bool TryBuildAttachmentRequests(
            IReadOnlyList<InventoryRewardGrantRequest> rewards,
            out List<MailboxSendAttachmentRequest> attachments)
        {
            attachments = new List<MailboxSendAttachmentRequest>();
            if (rewards == null)
                return false;

            foreach (var reward in rewards)
            {
                if (!TryAddAttachments(reward, attachments))
                    return false;
            }

            return true;
        }

        private static bool TryAddAttachments(
            InventoryRewardGrantRequest reward,
            List<MailboxSendAttachmentRequest> attachments)
        {
            if (reward == null || attachments == null)
                return false;

            var count = Math.Max(1, reward.Count);
            if (reward.UseExistingCore)
            {
                var core = reward.Core?.Copy();
                if (core == null || core.ItemId <= 0)
                    return false;

                if (InventoryStackRuleService.IsStackable(core))
                    return TryAddStackableAttachments(core, count, reward.CreateOptions, attachments);

                for (var index = 0; index < count; index++)
                    attachments.Add(CreateAttachment(core, 1, reward.CreateOptions));
                return true;
            }

            if (!TryCreateInventoryCore(reward, 1, out var sample))
                return false;

            if (InventoryStackRuleService.IsStackable(sample))
                return TryAddStackableAttachments(sample, count, reward.CreateOptions, attachments);

            attachments.Add(CreateAttachment(sample, 1, reward.CreateOptions));
            for (var index = 1; index < count; index++)
            {
                if (!TryCreateInventoryCore(reward, 1, out var core))
                    return false;

                attachments.Add(CreateAttachment(core, 1, reward.CreateOptions));
            }

            return true;
        }

        private static bool TryAddStackableAttachments(
            ItemCore sample,
            int count,
            InventoryCreateOptions options,
            List<MailboxSendAttachmentRequest> attachments)
        {
            if (sample == null || attachments == null || count <= 0)
                return false;
            if (!InventoryStackRuleService.TryGetStackLimit(sample, out var stackLimit))
                return false;

            if (stackLimit == int.MaxValue)
            {
                attachments.Add(CreateAttachment(sample, count, options));
                return true;
            }

            if (stackLimit <= 0)
                return false;

            var remaining = count;
            while (remaining > 0)
            {
                var chunk = Math.Min(remaining, stackLimit);
                attachments.Add(CreateAttachment(sample, chunk, options));
                remaining -= chunk;
            }

            return true;
        }

        private static bool TryCreateInventoryCore(
            InventoryRewardGrantRequest reward,
            int count,
            out ItemCore core)
        {
            core = null;
            if (reward == null || reward.ItemTemplateId <= 0)
                return false;

            if (!InventoryRewardGrantService.TryCreateOnly(
                    reward.ItemTemplateId,
                    reward.Reason,
                    count,
                    reward.CreateOptions,
                    out var createResult)
                || createResult.Kind != InventoryRewardGrantKind.InventoryItem
                || createResult.Core == null)
            {
                return false;
            }

            core = createResult.Core.Copy();
            return true;
        }

        private static MailboxSendAttachmentRequest CreateAttachment(
            ItemCore sourceCore,
            int count,
            InventoryCreateOptions options)
        {
            var core = sourceCore.Copy();
            count = Math.Max(1, count);
            if (InventoryStackRuleService.IsStackable(core))
                core.Count = count;
            else
                count = 1;

            core.SortLockFlag = 0;
            core.EquipmentLockId = 0;
            if (core.ItemKind == ItemCore.KindAvatar)
                core.AvatarUid = 0;
            else if (core.ItemKind == ItemCore.KindCreature)
                core.CreatureUid = 0;

            return new MailboxSendAttachmentRequest
            {
                ItemType = ResolveMailboxItemType(core),
                ItemId = core.ItemId,
                ItemCount = count,
                InstanceValue = core.Value,
                Durability = core.Durability,
                SealFlag = core.SealFlag,
                OptionValue = core.AbilityNo,
                ExpireTime = core.ExpireTime,
                Marker16 = core.Marker16,
                PetSerialOrHandle = core.CreatureUid,
                ExtraJson = "{}",
                ItemCoreData = MailboxItemCoreCodec.Encode(core),
                DetailJson = MailboxItemDetailCodec.Capture(core, options),
            };
        }

        private static IReadOnlyList<MailboxSendRequest> BuildMailRequests(
            InventoryService inventory,
            IReadOnlyList<MailboxSendAttachmentRequest> attachments,
            string title,
            string text)
        {
            var mails = new List<MailboxSendRequest>();
            var chunkIndex = 0;
            for (var offset = 0; offset < attachments.Count; offset += MaxAttachmentsPerMail)
            {
                var count = Math.Min(MaxAttachmentsPerMail, attachments.Count - offset);
                var chunk = new MailboxSendAttachmentRequest[count];
                for (var index = 0; index < count; index++)
                    chunk[index] = attachments[offset + index];

                mails.Add(new MailboxSendRequest
                {
                    SenderCharacterId = inventory.CharacterId,
                    SenderAccountId = 0,
                    SenderName = "DNF管理员",
                    ReceiverCharacterId = inventory.CharacterId,
                    ReceiverAccountId = inventory.AccountId,
                    ReceiverName = string.Empty,
                    Gold = 0,
                    Title = title ?? "Inventory overflow reward",
                    Text = string.IsNullOrWhiteSpace(text) ? "Inventory overflow reward" : text,
                    MailType = 1,
                    SourceProtocol = 0,
                    Unlimited = true,
                    AuditActor = "inventory-overflow",
                    AuditReason = "inventory reward overflow",
                    IdempotencyKey = $"inventory-overflow:{inventory.CharacterId}:{Guid.NewGuid():N}:{chunkIndex++}",
                    Attachments = chunk,
                });
            }

            return mails;
        }

        private static byte ResolveMailboxItemType(ItemCore core)
        {
            if (core == null)
                return 0;

            switch (core.ItemKind)
            {
                case ItemCore.KindAvatar:
                    return 1;
                case ItemCore.KindCreature:
                case ItemCore.KindCreatureEquipment:
                case ItemCore.KindCreatureConsumable:
                    return 3;
                default:
                    return 0;
            }
        }

        private static bool Fail(InventoryOverflowDeliveryResult result)
        {
            if (result != null)
                result.Status = InventoryOverflowDeliveryStatus.MailUnavailable;
            return false;
        }
    }
}

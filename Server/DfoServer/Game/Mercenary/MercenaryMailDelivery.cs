using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Mercenary
{
    public enum MercenaryMailDeliveryDisposition
    {
        Pending,
        Delivered,
        Failed,
    }

    public sealed class MercenaryMailDeliveryResult
    {
        public MercenaryMailDeliveryDisposition Disposition { get; set; }
        public string Error { get; set; }
        public long MailboxMessageId { get; set; }
    }

    public interface IMercenaryMailDelivery
    {
        MercenaryMailDeliveryResult Deliver(MercenaryRewardOutboxEntry entry);
    }

    public sealed class PendingMercenaryMailDelivery : IMercenaryMailDelivery
    {
        public static readonly PendingMercenaryMailDelivery Instance = new PendingMercenaryMailDelivery();

        private PendingMercenaryMailDelivery()
        {
        }

        public MercenaryMailDeliveryResult Deliver(MercenaryRewardOutboxEntry entry)
        {
            return new MercenaryMailDeliveryResult
            {
                Disposition = MercenaryMailDeliveryDisposition.Pending,
                Error = "system mail delivery is not implemented",
            };
        }
    }

    public sealed class MailboxMercenaryMailDelivery : IMercenaryMailDelivery
    {
        private const string OfficialSenderName = "DNFadmin";
        private readonly MailboxService _mailbox;

        public MailboxMercenaryMailDelivery(MailboxService mailbox)
        {
            _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
        }

        public MercenaryMailDeliveryResult Deliver(MercenaryRewardOutboxEntry entry)
        {
            if (entry == null || entry.OutboxId <= 0 || entry.AssignmentId <= 0
                || entry.AccountId <= 0 || entry.CharacterId <= 0)
            {
                return Failed(MailboxSendError.InvalidRequest);
            }

            if (!entry.HasMailReward)
            {
                return new MercenaryMailDeliveryResult
                {
                    Disposition = MercenaryMailDeliveryDisposition.Delivered,
                };
            }

            var totalGold = (long)entry.BaseGold + entry.BonusGold;
            if (totalGold < 0 || totalGold > int.MaxValue)
                return Failed(MailboxSendError.InvalidRequest);

            var attachments = new List<MailboxSendAttachmentRequest>();
            foreach (var item in entry.Items)
            {
                if (item.ItemTemplateId <= 0 || item.ItemCount <= 0)
                    return Failed(MailboxSendError.InvalidAttachment);
                attachments.Add(new MailboxSendAttachmentRequest
                {
                    ItemType = ResolveMailboxItemType(item.ItemTemplateId),
                    ItemId = item.ItemTemplateId,
                    ItemCount = item.ItemCount,
                });
            }
            if (attachments.Count == 0 && entry.ItemTemplateId > 0 && entry.ItemCount > 0)
            {
                attachments.Add(new MailboxSendAttachmentRequest
                {
                    ItemType = ResolveMailboxItemType(entry.ItemTemplateId),
                    ItemId = entry.ItemTemplateId,
                    ItemCount = entry.ItemCount,
                });
            }
            var send = _mailbox.SendSystemMail(new MailboxSendRequest
            {
                SenderCharacterId = entry.CharacterId,
                SenderAccountId = entry.AccountId,
                SenderName = OfficialSenderName,
                ReceiverCharacterId = entry.CharacterId,
                ReceiverAccountId = entry.AccountId,
                Gold = (int)totalGold,
                Text = ResolveMailText(entry.MailTitleKey, entry.MailMessageKey),
                MailType = 1,
                SourceProtocol = 0,
                IdempotencyKey = $"mercenary:{entry.AssignmentId}",
                AuditActor = "mercenary-system",
                AuditReason = $"mercenary assignment {entry.AssignmentId} reward",
                Attachments = attachments,
            });

            return send.Success
                ? new MercenaryMailDeliveryResult
                {
                    Disposition = MercenaryMailDeliveryDisposition.Delivered,
                    MailboxMessageId = send.MessageId,
                }
                : Failed(send.Error);
        }

        private static byte ResolveMailboxItemType(int itemId)
        {
            if (!ItemMetadataResolver.TryResolveItemKind(itemId, out var itemKind))
                return 0;

            switch (itemKind)
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

        private static string ResolveMailText(string titleKey, string messageKey)
        {
            var title = string.Equals(titleKey, "game_server_msg_225", StringComparison.OrdinalIgnoreCase)
                ? "佣兵归队"
                : "佣兵出战报酬";
            var message = string.Equals(messageKey, "game_server_msg_221", StringComparison.OrdinalIgnoreCase)
                ? "出战佣金已结算。"
                : "出战佣金与地区战利品已结算。";
            return title + "：" + message;
        }

        private static MercenaryMailDeliveryResult Failed(MailboxSendError error)
        {
            return new MercenaryMailDeliveryResult
            {
                Disposition = MercenaryMailDeliveryDisposition.Failed,
                Error = error.ToString(),
            };
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Auction.Transactions
{
    internal sealed record AuctionItemDebit(InventoryListType List, short Slot, int Count, AuctionItemEnvelope Source);

    // The upper order/rules layer supplies economic policy. This class freezes
    // its exact outcome; it does not calculate fees or create template items.
    internal sealed class AuctionPublicAssetPlan
    {
        private readonly AuctionItemDebit[] debits;
        private readonly string[] mails;
        private readonly string serialized;
        internal int GoldDebit { get; }
        internal IReadOnlyList<AuctionItemDebit> ItemDebits => Array.AsReadOnly(debits);

        private AuctionPublicAssetPlan(AuctionItemCodec codec, long goldDebit,
            IReadOnlyList<AuctionItemDebit> itemDebits, IReadOnlyList<MailboxSendRequest> systemMails)
        {
            if (codec == null) throw new ArgumentNullException(nameof(codec));
            if (goldDebit < 0 || goldDebit > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(goldDebit));
            if (itemDebits == null || systemMails == null) throw new ArgumentNullException();
            GoldDebit = (int)goldDebit;
            var slots = new HashSet<(InventoryListType, short)>();
            debits = itemDebits.Select(debit =>
            {
                if (debit == null || !Enum.IsDefined(debit.List) || debit.Slot < 0
                    || debit.Count <= 0 || !slots.Add((debit.List, debit.Slot)))
                    throw new ArgumentException("Invalid or duplicate auction source slot.");
                var core = codec.DecodeStrict(debit.Source);
                if (InventoryStackRuleService.IsStackable(core) ? debit.Count > core.Count : debit.Count != 1)
                    throw new ArgumentException("Invalid auction source quantity.");
                return new AuctionItemDebit(debit.List, debit.Slot, debit.Count, debit.Source);
            }).ToArray();
            var keys = new HashSet<(int, string)>();
            mails = systemMails.Select(mail =>
            {
                var frozen = FreezeMail(codec, mail);
                if (!keys.Add((frozen.SenderCharacterId, frozen.IdempotencyKey)))
                    throw new ArgumentException("Duplicate auction mail leg key.");
                return JsonSerializer.Serialize(frozen);
            }).ToArray();
            serialized = JsonSerializer.Serialize(new {
                formatVersion = 1, goldDebit = GoldDebit,
                itemDebits = debits.Select(d => new { list = (int)d.List, slot = d.Slot, count = d.Count, source = codec.Serialize(d.Source) }).ToArray(),
                systemMails = mails
            });
        }

        internal static AuctionPublicAssetPlan Create(AuctionItemCodec codec, long goldDebit,
            IReadOnlyList<AuctionItemDebit> itemDebits, IReadOnlyList<MailboxSendRequest> systemMails)
            => new(codec, goldDebit, itemDebits, systemMails);

        internal string Serialize() => serialized;
        internal IReadOnlyList<MailboxSendRequest> CopySystemMails()
            => mails.Select(json => JsonSerializer.Deserialize<MailboxSendRequest>(json)).ToArray();

        internal static AuctionPublicAssetPlan DeserializeStrict(AuctionItemCodec codec, string serialized)
        {
            using var doc = JsonDocument.Parse(serialized);
            var root = doc.RootElement;
            if (root.GetProperty("formatVersion").GetInt32() != 1) throw new ArgumentException("Unsupported asset plan.");
            var sources = root.GetProperty("itemDebits").EnumerateArray().Select(d => {
                int list = d.GetProperty("list").GetInt32();
                if (list < byte.MinValue || list > byte.MaxValue) throw new ArgumentException("Invalid source list.");
                return new AuctionItemDebit((InventoryListType)list, d.GetProperty("slot").GetInt16(),
                    d.GetProperty("count").GetInt32(), codec.DeserializeStrict(d.GetProperty("source").GetString()));
            }).ToArray();
            var messages = root.GetProperty("systemMails").EnumerateArray()
                .Select(m => JsonSerializer.Deserialize<MailboxSendRequest>(m.GetString())).ToArray();
            var plan = Create(codec, root.GetProperty("goldDebit").GetInt64(), sources, messages);
            // Rebuilding the canonical descriptor also rejects unknown, missing,
            // duplicate fields and mail fields that would otherwise be ignored.
            if (plan.Serialize() != serialized) throw new ArgumentException("Noncanonical or incomplete auction asset plan.");
            return plan;
        }

        private static MailboxSendRequest FreezeMail(AuctionItemCodec codec, MailboxSendRequest original)
        {
            if (original == null) throw new ArgumentException("Missing auction mail leg.");
            var mail = JsonSerializer.Deserialize<MailboxSendRequest>(JsonSerializer.Serialize(original));
            if (mail.SenderCharacterId <= 0 || mail.SenderAccountId <= 0 || mail.ReceiverCharacterId <= 0 || mail.ReceiverAccountId <= 0
                || mail.Gold < 0 || mail.Unlimited != true || mail.ExpireAtUtc != null
                || string.IsNullOrWhiteSpace(mail.IdempotencyKey) || mail.IdempotencyKey != mail.IdempotencyKey.Trim()
                || mail.Attachments == null || mail.Attachments.Count > 10)
                throw new ArgumentException("Auction mail must be frozen, permanent and owner-bound.");
            if (mail.Gold == 0 && mail.Attachments.Count == 0 && string.IsNullOrWhiteSpace(mail.Text))
                throw new ArgumentException("Empty auction mail leg.");
            foreach (var attachment in mail.Attachments)
            {
                if (attachment == null || attachment.ItemCoreData == null || attachment.ItemCoreData.Length != ItemCore.Size
                    || attachment.ItemId <= 0 || attachment.ItemCount <= 0)
                    throw new ArgumentException("Auction attachment requires a complete native instance.");
                var envelope = AuctionItemEnvelope.Create(attachment.ItemCoreData, attachment.DetailJson, attachment.ExtraJson);
                var core = codec.DecodeStrict(envelope);
                if (core.ItemId != attachment.ItemId || core.SortLockFlag != 0 || core.EquipmentLockId != 0
                    || (InventoryStackRuleService.IsStackable(core) ? core.Count != attachment.ItemCount : attachment.ItemCount != 1))
                    throw new ArgumentException("Auction attachment differs from its final transfer instance.");
                // Explicit native instance wins in MailboxRepository; make its
                // redundant descriptive fields exact BEFORE freezing the intent.
                attachment.InstanceValue = core.Value; attachment.Durability = core.Durability;
                attachment.SealFlag = core.SealFlag; attachment.OptionValue = core.AbilityNo;
                attachment.ExpireTime = core.ExpireTime; attachment.Marker16 = core.Marker16;
                attachment.PetSerialOrHandle = core.CreatureUid;
            }
            return mail;
        }
    }
}

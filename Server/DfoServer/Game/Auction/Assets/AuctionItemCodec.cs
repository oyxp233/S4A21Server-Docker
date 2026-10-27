using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;

namespace DfoServer.Game.Auction.Assets
{
    internal enum AuctionItemCodecError
    {
        InvalidEnvelope,
        IntegrityMismatch,
        InvalidNativeCore,
        UnsupportedDetailItem,
        UnsupportedItemKind,
        MissingSource,
        InvalidExtension,
        TransferIdentityChanged
    }

    internal sealed class AuctionItemCodecException : InvalidOperationException
    {
        internal AuctionItemCodecException(AuctionItemCodecError error, string message, Exception inner = null)
            : base(message, inner) => Error = error;

        internal AuctionItemCodecError Error { get; }
    }

    internal sealed class AuctionItemCodec
    {
        private readonly IAuctionInstanceExtension extension;

        internal AuctionItemCodec(IAuctionInstanceExtension extension)
            => this.extension = extension ?? throw new ArgumentNullException(nameof(extension));

        // Caller holds the current owned lease lock. This method only reads the
        // real inventory slot; it neither mutates inventory nor decides eligibility.
        internal AuctionItemEnvelope Capture(InventoryService inventory, InventoryListType list, short slot)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            var source = inventory.GetItem(list, slot);
            if (source == null || source.IsEmpty)
                throw new AuctionItemCodecException(AuctionItemCodecError.MissingSource, "Auction source slot is empty.");
            var copy = source.Copy();
            ValidateCore(copy);
            string detail;
            try { detail = MailboxItemDetailCodec.CaptureStrict(inventory, copy); }
            catch (Exception ex) when (IsDetailFailure(ex))
            { throw new AuctionItemCodecException(AuctionItemCodecError.UnsupportedDetailItem, "Cannot capture complete owned auction detail.", ex); }
            return CaptureCore(copy, copy, detail);
        }

        // Preserve the owned-source envelope separately. This operation applies
        // no fee, trade-count, lock, quantity or expiration policy on its own.
        internal AuctionItemEnvelope CreateTransferSnapshot(AuctionItemEnvelope source, ItemCore finalTransferCore)
        {
            var original = DecodeStrict(source);
            if (finalTransferCore == null)
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidNativeCore, "Missing final transfer core.");
            var final = finalTransferCore.Copy();
            ValidateCore(final);
            if (final.ItemId != original.ItemId || final.ItemKind != original.ItemKind)
                throw new AuctionItemCodecException(AuctionItemCodecError.TransferIdentityChanged, "Transfer changed the item template or kind.");
            return CaptureCore(original, final, source.DetailJson);
        }

        internal ItemCore DecodeStrict(AuctionItemEnvelope envelope)
        {
            if (envelope == null)
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidEnvelope, "Missing auction item envelope.");
            envelope.VerifyIntegrity();
            var bytes = envelope.NativeCore99;
            if (bytes.Length != ItemCore.Size)
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidNativeCore, "Auction item core must be exactly 99 bytes.");
            var core = ItemCore.FromBytes(bytes);
            ValidateCore(core);
            ValidateDetail(core, envelope.DetailJson);
            RestoreExtension(core, envelope.ExtensionsJson);
            if (!core.ToBytes().AsSpan().SequenceEqual(bytes))
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidExtension, "Instance extension changed the native item core.");
            return core;
        }

        internal bool MatchesSource(InventoryService inventory, InventoryListType list,
            short slot, AuctionItemEnvelope expectedSource)
        {
            DecodeStrict(expectedSource);
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            var source = inventory.GetItem(list, slot);
            if (source == null || source.IsEmpty) return false;
            var actual = Capture(inventory, list, slot);
            return CryptographicOperations.FixedTimeEquals(actual.ContentHash, expectedSource.ContentHash);
        }

        internal string Serialize(AuctionItemEnvelope envelope)
        {
            DecodeStrict(envelope);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("formatVersion", envelope.FormatVersion);
                writer.WriteBase64String("nativeCore", envelope.NativeCore99);
                writer.WriteString("detailJson", envelope.DetailJson);
                writer.WriteString("extensionsJson", envelope.ExtensionsJson);
                writer.WriteString("contentHash", envelope.ContentHashHex);
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        internal AuctionItemEnvelope DeserializeStrict(string serialized)
        {
            if (string.IsNullOrWhiteSpace(serialized))
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidEnvelope, "Missing serialized auction item.");
            try
            {
                using var document = JsonDocument.Parse(serialized);
                var root = document.RootElement;
                RequireDescriptorProperties(root);
                var version = root.GetProperty("formatVersion").GetInt32();
                var nativeCore = root.GetProperty("nativeCore").GetBytesFromBase64();
                var detail = root.GetProperty("detailJson").GetString();
                var extensions = root.GetProperty("extensionsJson").GetString();
                var hash = Convert.FromHexString(root.GetProperty("contentHash").GetString()
                    ?? throw new FormatException("Missing content hash."));
                var envelope = AuctionItemEnvelope.FromStored(version, nativeCore, detail, extensions, hash);
                DecodeStrict(envelope);
                return envelope;
            }
            catch (AuctionItemCodecException) { throw; }
            catch (Exception ex) when (ex is JsonException || ex is FormatException
                || ex is InvalidOperationException || ex is OverflowException || ex is ArgumentException)
            {
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidEnvelope, "Malformed serialized auction item.", ex);
            }
        }

        private AuctionItemEnvelope CaptureCore(ItemCore source, ItemCore final, string detail)
        {
            ValidateDetail(final, detail);
            string extensions;
            try { extensions = extension.Capture(source, final); }
            catch (AuctionItemCodecException) { throw; }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is JsonException)
            {
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidExtension, "Cannot capture auction item extensions.", ex);
            }
            var envelope = AuctionItemEnvelope.Create(final.ToBytes(), detail, extensions);
            DecodeStrict(envelope);
            return envelope;
        }

        private void RestoreExtension(ItemCore core, string json)
        {
            try { extension.Restore(core, json); }
            catch (AuctionItemCodecException) { throw; }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is JsonException)
            {
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidExtension, "Invalid auction item extensions.", ex);
            }
        }

        private static void ValidateCore(ItemCore core)
        {
            if (core == null || core.ItemId <= 0 || core.IsEmpty)
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidNativeCore, "Missing auction item instance.");
            if (core.ItemKind < ItemCore.KindEquipment || core.ItemKind > ItemCore.KindGuardianGem)
                throw new AuctionItemCodecException(AuctionItemCodecError.UnsupportedItemKind, "Unsupported auction item kind.");
            if (InventoryStackRuleService.IsStackable(core) && core.Count <= 0)
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidNativeCore, "Auction stack count must be positive.");
        }

        private static void ValidateDetail(ItemCore core, string detail)
        {
            try { MailboxItemDetailCodec.ValidateStrict(core, detail); }
            catch (Exception ex) when (IsDetailFailure(ex))
            { throw new AuctionItemCodecException(AuctionItemCodecError.UnsupportedDetailItem, "Invalid or incomplete auction transfer detail.", ex); }
        }

        private static bool IsDetailFailure(Exception ex)
            => ex is InvalidOperationException || ex is ArgumentException || ex is JsonException
                || ex is IOException || ex is FormatException || ex is OverflowException;

        private static void RequireDescriptorProperties(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object)
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidEnvelope, "Auction descriptor must be an object.");
            var required = new HashSet<string>(StringComparer.Ordinal)
                { "formatVersion", "nativeCore", "detailJson", "extensionsJson", "contentHash" };
            foreach (var property in root.EnumerateObject())
                if (!required.Remove(property.Name))
                    throw new AuctionItemCodecException(AuctionItemCodecError.InvalidEnvelope, "Unknown or duplicate auction descriptor field.");
            if (required.Count != 0)
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidEnvelope, "Incomplete auction item descriptor.");
        }
    }
}

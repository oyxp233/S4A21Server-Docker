using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.Mailbox;

namespace DfoServer.Network.Builders.Auction
{
    // Ordinary selector0 display projection ONLY. Never decode this83B payload
    // back into an asset: the envelope's full99B/detail/private state is its
    // sole persistent instance. Evidence: m2/protocol/EVIDENCE.md native crosswalk,
    // NATIVE-GAP-M2B.md producers and NATIVE-CONSUMERS-M2C.md consumer closure.
    internal static class AuctionNativeDisplayProjection
    {
        internal static bool TryProject(AuctionItemEnvelope envelope, AuctionItemCodec codec,
            out AuctionNativeItemWireSnapshot value, out string reason)
        {
            value = null;
            reason = string.Empty;
            if (envelope == null || codec == null)
                return Unavailable("missing-projection-input", out reason);

            ItemCore core;
            InventoryCreateOptions detail;
            try
            {
                core = codec.DecodeStrict(envelope);
                // The shared detail owner is the only JSON/model interpreter.
                detail = MailboxItemDetailCodec.BuildCreateOptionsStrict(core, envelope.DetailJson);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException
                || ex is JsonException || ex is IOException || ex is FormatException || ex is OverflowException)
            {
                return Unavailable("invalid-projection-instance", out reason);
            }

            if (core.ExpireTime != 0)
                return Unavailable("timed-core-display-unavailable", out reason);
            if (core.SealFlag > 1)
                return Unavailable("seal-flag-display-unavailable", out reason);
            if (core.GenuineUpgrade > 31)
                return Unavailable("forge-high-bits-display-unavailable", out reason);
            // M5 native10/u16 is AbilityNo for a valid equipment-class Avatar;
            // only the unproved other-kind/days routes retain the zero-only gate.
            // This metadata check grants display typing, never listing eligibility.
            bool typedAvatar = false;
            if (core.ItemKind == ItemCore.KindAvatar)
            {
                try
                {
                    // Same PvfArchiveAccessor/EquipmentFileCache owner as catalog.
                    // A generic /avatar/ path fallback is not a native type proof.
                    typedAvatar = ItemMetadataResolver.TryLoadEquipmentFile(core.ItemId, out var equipment)
                        && HasNativeAvatarEquipmentType(equipment.EquipmentType);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException
                    || ex is IOException || ex is FormatException || ex is OverflowException)
                {
                    return Unavailable("avatar-native-type-unavailable", out reason);
                }
                if (!typedAvatar)
                    return Unavailable("avatar-native-type-unverified", out reason);
            }
            if (core.ItemKind != ItemCore.KindEquipment && !typedAvatar && core.Durability != 0)
                return Unavailable("typed-durability-display-unavailable", out reason);
            if (core.ChronicleOptionCount != 0)
                return Unavailable("chronicle-display-unavailable", out reason);
            if (core.ItemKind == ItemCore.KindCreature)
                return Unavailable("creature-detail-display-unavailable", out reason);

            // 029165D0 defines native triple emptiness by Type==0. Native50 is
            // the whole flat inventory wire70/ChangeState, never a new packing.
            if (!core.RandomOptionChange.IsEmpty
                && (core.RandomOptionChangeState & 3) != core.RandomOptionChangedIndex)
                return Unavailable("changed-option-index-display-unavailable", out reason);

            AvatarDetail avatar = detail?.AvatarDetailTemplate;
            if (core.ItemKind == ItemCore.KindAvatar)
            {
                if (avatar == null)
                    return Unavailable("missing-avatar-display-detail", out reason);
                if (avatar.ExpireDate != 0 || avatar.DeleteDate != 0)
                    return Unavailable("timed-avatar-display-unavailable", out reason);
                if (avatar.ClearAvatarId != 0)
                    return Unavailable("avatar-appearance-display-unavailable", out reason);
            }

            var native83 = new byte[83];
            native83[0] = core.SealFlag;
            WriteInt32(native83, 1, core.ItemId);
            native83[5] = core.Attr;
            // The canonical Avatar entry writes remainingSeconds, not its UID.
            // Only permanent strict detail reaches here, so that value is0.
            // Common equipment Value0 uses the existing inventory quality seed.
            int displayValue = core.ItemKind == ItemCore.KindAvatar ? 0
                : core.ItemKind == ItemCore.KindEquipment && core.Value == 0
                    ? unchecked((int)ItemQuality.TopQualitySeed) : core.Value;
            WriteInt32(native83, 6, displayValue);
            WriteUInt16(native83, 10, core.Durability);
            WriteInt32(native83, 12, core.EnchantCardId);
            native83[16] = core.AmplifyType;
            WriteUInt16(native83, 17, core.AmplifyValue);

            // Main's explicit server-reserved display policy, NOT a recovered
            // original GUID/field definition or a proved native default.
            native83.AsSpan(19, 10).Clear();
            WriteInt32(native83, 29, core.EmblemId1);
            WriteInt32(native83, 33, core.EmblemId2);
            // Match canonical inventory's ordered nonempty-option compaction.
            var options = core.RandomOptions;
            for (int index = 0; index < 3; index++)
                WriteOption(native83, 37 + index * 3, index < options.Count ? options[index] : null);
            native83[46] = core.RandomOptionState;
            WriteOption(native83, 47, core.RandomOptionChange);
            native83[50] = core.RandomOptionChangeState;
            native83[51] = core.GenuineUpgrade;
            native83.AsSpan(52, 2).Clear(); // Same explicit server-reserved policy.
            WriteInt32(native83, 54, core.ExpireTime);
            native83.AsSpan(58, 3).Clear(); // Positive native ctor02918FD0 default.
            native83[61] = core.EnchantUpgradeCount;
            native83[62] = core.TailUnknown2;
            native83[63] = core.EmancipateEquipmentLevel;
            native83[64] = 0; // Positive native ctor02918FD0 default.
            WriteUInt16(native83, 65, (ushort)(core.A21Tail_Unknown84 & 0xFFFF));
            WriteUInt16(native83, 67, (ushort)(core.A21Tail_Unknown84 >> 16));
            native83.AsSpan(69, 14).Clear(); // Positive ctor029190C0 default.

            var additional30 = new byte[30];
            var additional4 = new byte[4];
            if (avatar != null)
            {
                var sockets = avatar.JewelSocket;
                if (sockets.Length != 30)
                    return Unavailable("avatar-socket-display-unavailable", out reason);
                // Preserve all five complete(u16 type,u32 emblem) groups.
                for (int index = 0; index < 5; index++)
                {
                    int offset = index * 6;
                    WriteUInt16(additional30, offset,
                        BinaryPrimitives.ReadUInt16LittleEndian(sockets.AsSpan(offset, 2)));
                    BinaryPrimitives.WriteUInt32LittleEndian(additional30.AsSpan(offset + 2, 4),
                        BinaryPrimitives.ReadUInt32LittleEndian(sockets.AsSpan(offset + 2, 4)));
                }
                WriteUInt16(additional4, 0, avatar.Color1);
                WriteUInt16(additional4, 2, avatar.Color2);
            }
            // Non-avatar30/4B use the positively evidenced native constructors'
            // zero defaults; never generate a missing Avatar detail template.
            if (!AuctionNativeItemWireSnapshot.TryCreate(native83, additional30, additional4, out value))
                return Unavailable("invalid-display-snapshot-shape", out reason);
            return true;
        }

        // Pure native type discriminator. No path heuristic, cache mutation,
        // synthetic source, or listing authority is introduced by this helper.
        internal static bool HasNativeAvatarEquipmentType(string rawEquipmentType)
            => EquipmentTypeInfo.TryParse(rawEquipmentType, out var equipmentType)
                && EquipmentTypeInfo.IsAvatarPart(equipmentType);

        private static bool Unavailable(string code, out string reason)
        { reason = code; return false; }

        private static void WriteOption(byte[] target, int offset, RandomOption option)
        {
            target[offset] = option?.Type ?? 0;
            target[offset + 1] = option?.Value1 ?? 0;
            target[offset + 2] = option?.Value2 ?? 0;
        }

        private static void WriteInt32(byte[] target, int offset, int value)
            => BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(offset, 4), value);
        private static void WriteUInt16(byte[] target, int offset, ushort value)
            => BinaryPrimitives.WriteUInt16LittleEndian(target.AsSpan(offset, 2), value);
    }
}

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Inventory;
using PvfLib;
using DfoServer.Network.Builders.Auction;

namespace DfoServer.SelfTests
{
    // Display fixtures use the current shared PVF cache for strict Avatar typing.
    // No database, eligibility decision,
    // runtime registration, item transfer or native client acceptance.
    public static class AuctionNativeDisplayProjectionSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool condition)
            {
                Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] auction display: {name}");
                if (condition) passed++; else failed++;
            }

            try
            {
                var extension = new NativeAuctionInstanceExtension();
                var codec = new AuctionItemCodec(extension);
                AuctionItemEnvelope Envelope(ItemCore item, string detail = "")
                    => AuctionItemEnvelope.Create(item.ToBytes(), detail, extension.Capture(item, item));
                void Reject(string name, AuctionItemEnvelope candidate, string expectedReason)
                {
                    var candidateCore = candidate.NativeCore99;
                    string candidateDetail = candidate.DetailJson, candidatePrivate = candidate.ExtensionsJson;
                    var candidateHash = candidate.ContentHash;
                    bool ok = AuctionNativeDisplayProjection.TryProject(candidate, codec, out var result, out var failureCode);
                    Check(name, !ok && result == null && failureCode == expectedReason);
                    Check(name + " preserves the complete source", candidateCore.SequenceEqual(candidate.NativeCore99)
                        && candidateDetail == candidate.DetailJson && candidatePrivate == candidate.ExtensionsJson
                        && candidateHash.SequenceEqual(candidate.ContentHash));
                }

                var equipment = EquipmentFixture();
                var envelope = Envelope(equipment);
                var before99 = envelope.NativeCore99;
                var beforeHash = envelope.ContentHash;
                string beforeSerialized = codec.Serialize(envelope);
                bool projected = AuctionNativeDisplayProjection.TryProject(envelope, codec, out var snapshot, out var reason);
                Check("permanent equipment projection is explicitly available", projected && snapshot != null && reason == "");
                if (snapshot != null)
                {
                    // Literal wire fixture from the VA crosswalk, not a99B
                    // prefix or an invocation of the implementation's writer.
                    var expected = Convert.FromHexString(
                        "01040302016544332211665554433221128877" //0..18
                        + "00000000000000000000" //19..28 server-reserved
                        + "4030201080706050" //29..36 emblem pair
                        + "010203040506070809" //37..45 ordered option triples
                        + "AB0A0B0CA61D" //46..51 state, changed triple, change-state,forge
                        + "000000000000000000" //52..60 reserved/permanent/default
                        + "BCCDDE00C4D3E2F1" //61..68 lowbyte fields and split unknown84
                        + "0000000000000000000000000000"); //69..82 native ctor default
                    Check("all83 bytes match the independently specified field fixture",
                        expected.Length == 83 && snapshot.Native83.AsSpan().SequenceEqual(expected));
                    Check("all server-reserved and positive ctor-default bytes are zero",
                        snapshot.Native83.AsSpan(19, 10).IndexOfAnyExcept((byte)0) < 0
                        && snapshot.Native83.AsSpan(52, 9).IndexOfAnyExcept((byte)0) < 0
                        && snapshot.Native83[64] == 0
                        && snapshot.Native83.AsSpan(69, 14).IndexOfAnyExcept((byte)0) < 0);
                    Check("plain item uses explicit default30 and4 only",
                        snapshot.Additional30.Length == 30 && snapshot.Additional4.Length == 4
                        && snapshot.Additional30.All(x => x == 0) && snapshot.Additional4.All(x => x == 0));
                    var exposed = snapshot.Native83.ToArray(); exposed[1] ^= 0xFF;
                    var sourceBytes = envelope.NativeCore99; sourceBytes[0] = 0;
                    Check("returned data cannot mutate the immutable projection",
                        snapshot.Native83.AsSpan().SequenceEqual(expected));
                }
                Check("full99/hash/detail/private identity are unchanged by projection",
                    before99.SequenceEqual(envelope.NativeCore99) && beforeHash.SequenceEqual(envelope.ContentHash)
                    && codec.Serialize(envelope) == beforeSerialized
                    && equipment.ToBytes().SequenceEqual(before99));
                Check("repeated projections are independent and stable",
                    AuctionNativeDisplayProjection.TryProject(envelope, codec, out var repeated, out _)
                    && snapshot != null && !ReferenceEquals(snapshot, repeated)
                    && snapshot.Native83.AsSpan().SequenceEqual(repeated.Native83.AsSpan())
                    && codec.Serialize(envelope) == beforeSerialized);

                var zeroSeed = equipment.Copy(); zeroSeed.Value = 0;
                var zeroSeedEnvelope = Envelope(zeroSeed);
                Check("equipment zero Value uses the existing inventory quality seed without rewriting source",
                    AuctionNativeDisplayProjection.TryProject(zeroSeedEnvelope, codec, out var seed, out _)
                    && BinaryPrimitives.ReadUInt32LittleEndian(seed.Native83.AsSpan(6, 4)) == ItemQuality.TopQualitySeed
                    && codec.DecodeStrict(zeroSeedEnvelope).Value == 0);

                var stack = new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3001, Count = int.MaxValue };
                Check("ordinary quantity preserves its complete32 bits",
                    AuctionNativeDisplayProjection.TryProject(Envelope(stack), codec, out var stackDisplay, out _)
                    && BinaryPrimitives.ReadInt32LittleEndian(stackDisplay.Native83.AsSpan(6, 4)) == int.MaxValue);
                stack.Count = 0;
                Reject("invalid stack cannot become an empty success projection", Envelope(stack), "invalid-projection-instance");

                var changed = equipment.Copy(); changed.RandomOptionChangedIndex = 1;
                Reject("nonempty changed triple with different independent index is unavailable",
                    Envelope(changed), "changed-option-index-display-unavailable");
                changed.RandomOptionChangedIndex = 255;
                Reject("nonempty changed triple cannot wrap a sentinel index",
                    Envelope(changed), "changed-option-index-display-unavailable");
                changed.RandomOptionChangedIndex = 3; changed.RandomOptionChangeState = 0xA7;
                Check("matching low2 value3 preserves the native bounds-check representation",
                    AuctionNativeDisplayProjection.TryProject(Envelope(changed), codec, out var index3, out _)
                    && index3.Native83[50] == 0xA7);
                for (byte index = 0; index < 4; index++)
                {
                    changed.RandomOptionChangedIndex = index;
                    changed.RandomOptionChangeState = (byte)(0x80 | index);
                    Check("matching changed index " + index + " copies the whole state byte",
                        AuctionNativeDisplayProjection.TryProject(Envelope(changed), codec, out var matching, out _)
                        && matching.Native83[50] == (byte)(0x80 | index)
                        && matching.Native83.AsSpan(47, 3).SequenceEqual(new byte[] { 10,11,12 }));
                }
                changed.RandomOptionChange.Type = 0;
                changed.RandomOptionChangedIndex = 255; changed.RandomOptionChangeState = 0xFE;
                Check("Type0 is the positively evidenced empty triple and does not consume index",
                    AuctionNativeDisplayProjection.TryProject(Envelope(changed), codec, out var emptyChanged, out _)
                    && emptyChanged.Native83[47] == 0 && emptyChanged.Native83[50] == 0xFE);

                var sparse = equipment.Copy(); sparse.RandomOption0.Clear();
                Check("ordered nonempty compaction matches the canonical inventory writer",
                    AuctionNativeDisplayProjection.TryProject(Envelope(sparse), codec, out var sparseDisplay, out _)
                    && sparseDisplay.Native83.AsSpan(37, 9).SequenceEqual(new byte[] { 4,5,6,7,8,9,0,0,0 }));
                var denied = equipment.Copy(); denied.SealFlag = 2;
                Reject("unproved seal flag is unavailable", Envelope(denied), "seal-flag-display-unavailable");
                denied = equipment.Copy(); denied.GenuineUpgrade = 32;
                Reject("unknown native forge high bits are unavailable", Envelope(denied), "forge-high-bits-display-unavailable");
                denied = equipment.Copy(); denied.ExpireTime = 1;
                Reject("timed core is unavailable", Envelope(denied), "timed-core-display-unavailable");
                denied = equipment.Copy(); denied.ChronicleOption0.OptionId = 12;
                Reject("unrepresented Chronicle container is unavailable", Envelope(denied), "chronicle-display-unavailable");
                stack = new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3001, Count = 3, Durability = 1 };
                Reject("non-equipment days or durability cannot be guessed", Envelope(stack), "typed-durability-display-unavailable");

                // Same real preferred template as the existing detail-transfer
                // fixture. Fail if this PVF changes; no path-based fallback or
                // synthetic insertion into a shared cache is permitted.
                const int actualAvatarTemplate = 101600203;
                bool realAvatar = ItemMetadataResolver.TryLoadEquipmentFile(actualAvatarTemplate, out var avatarFile)
                    && AuctionNativeDisplayProjection.HasNativeAvatarEquipmentType(avatarFile.EquipmentType);
                Check("real current PVF template has an explicit native Avatar equipment type", realAvatar);
                if (!realAvatar) throw new InvalidOperationException("Expected real typed Avatar fixture missing from current PVF.");
                var avatar = new ItemCore { ItemKind = ItemCore.KindAvatar, ItemId = actualAvatarTemplate,
                    AvatarUid = 654321, AbilityNo = 321, Attr = 1, SealFlag = 1 };
                var sockets = new JewelSocket();
                for (int index = 0; index < 5; index++)
                    sockets.Set(index, (ushort)(index + 1), 0x01020304 + index);
                string avatarDetail = JsonSerializer.Serialize(new { FormatVersion = 1, Kind = "avatar", Avatar = new {
                    ExpireDate = 0, ClearAvatarId = 0, JewelSocket = sockets.ToBytes(),
                    Color1 = (ushort)0x1234, Color2 = (ushort)0xABCD, DeleteDate = 0 } });
                var avatarEnvelope = Envelope(avatar, avatarDetail);
                string avatarBefore = codec.Serialize(avatarEnvelope);
                Check("strict permanent avatar writes all five complete sockets and both colors",
                    AuctionNativeDisplayProjection.TryProject(avatarEnvelope, codec, out var avatarDisplay, out _)
                    && avatarDisplay.Additional30.AsSpan().SequenceEqual(sockets.ToBytes())
                    && avatarDisplay.Additional4.AsSpan().SequenceEqual(new byte[] { 0x34,0x12,0xCD,0xAB }));
                var expectedAvatar83 = new byte[83];
                expectedAvatar83[0] = 1;
                BinaryPrimitives.WriteInt32LittleEndian(expectedAvatar83.AsSpan(1, 4), actualAvatarTemplate);
                expectedAvatar83[5] = 1;
                expectedAvatar83[10] = 0x41; expectedAvatar83[11] = 0x01; // AbilityNo321, never days.
                Check("real Avatar matches the entire83B fixture including AbilityNo and reserved bytes",
                    avatarDisplay != null && avatarDisplay.Native83.AsSpan().SequenceEqual(expectedAvatar83));
                Check("permanent avatar display value is remainingSeconds0 and never its UID",
                    avatarDisplay != null && BinaryPrimitives.ReadInt32LittleEndian(avatarDisplay.Native83.AsSpan(6, 4)) == 0
                    && codec.DecodeStrict(avatarEnvelope).AvatarUid == 654321
                    && codec.DecodeStrict(avatarEnvelope).AbilityNo == 321
                    && codec.Serialize(avatarEnvelope) == avatarBefore);
                Check("all99/detail/private and hash remain exact for nonzero AbilityNo",
                    avatarEnvelope.NativeCore99.SequenceEqual(avatar.ToBytes())
                    && avatarEnvelope.DetailJson == avatarDetail && codec.Serialize(avatarEnvelope) == avatarBefore);
                Reject("missing avatar detail cannot be default-generated", Envelope(avatar), "invalid-projection-instance");
                foreach (var field in new[] { "ExpireDate", "DeleteDate", "ClearAvatarId" })
                {
                    var node = JsonNode.Parse(avatarDetail).AsObject(); node["Avatar"][field] = 1;
                    Reject("avatar " + field + " is explicit unavailable", Envelope(avatar, node.ToJsonString()),
                        field == "ClearAvatarId" ? "avatar-appearance-display-unavailable" : "timed-avatar-display-unavailable");
                }
                foreach (string path in new[] { "equipment/avatar/unknown.equ", "equipment/at_avatar/unknown.equ" })
                {
                    var unknownEquipment = EquipmentFile.Parse("[equipment type]\n`[unknown native avatar type]` 0");
                    var pathFallback = new ItemMetadata { ItemKind = "equipment", PvfFilePath = path,
                        EquipmentType = unknownEquipment.EquipmentType };
                    Check("generic Avatar path fallback is insufficient: " + path,
                        ItemMetadataResolver.IsAvatarItem(pathFallback)
                        && !AuctionNativeDisplayProjection.HasNativeAvatarEquipmentType(unknownEquipment.EquipmentType));
                }
                var forgedAvatar = avatar.Copy(); forgedAvatar.ItemId = 27602;
                Reject("ordinary weapon cannot gain typed Avatar display through a forged kind",
                    Envelope(forgedAvatar, avatarDetail), "avatar-native-type-unverified");
                forgedAvatar.ItemId = int.MaxValue;
                Reject("missing actual equipment type stays unavailable despite nonzero AbilityNo",
                    Envelope(forgedAvatar, avatarDetail), "avatar-native-type-unverified");

                var creature = new ItemCore { ItemKind = ItemCore.KindCreature, ItemId = 1900000102,
                    CreatureUid = 765432, SealFlag = 1 };
                string creatureDetail = JsonSerializer.Serialize(new { FormatVersion = 1, Kind = "creature", Creature = new {
                    NameBytes = Array.Empty<byte>(), Field04 = (byte)0, ModeFlag = (byte)0,
                    Mode1Field0A = (byte)0, Mode1Field0B = (byte)0, ProgressValue32 = 0,
                    FieldAfterValue32 = (byte)0, ExpireDate = 0, TailFlag = (byte)0, ExtraJson = "{}" } });
                Reject("complete creature persistence does not imply proved display layout",
                    Envelope(creature, creatureDetail), "creature-detail-display-unavailable");

                var malformed = AuctionItemEnvelope.Create(equipment.ToBytes(), "", "not-json");
                Reject("damaged private metadata cannot be ignored by display", malformed, "invalid-projection-instance");
                Check("missing inputs fail without a manufactured snapshot",
                    !AuctionNativeDisplayProjection.TryProject(null, codec, out var missing, out var missingReason)
                    && missing == null && missingReason == "missing-projection-input"
                    && !AuctionNativeDisplayProjection.TryProject(envelope, null, out missing, out missingReason)
                    && missing == null && missingReason == "missing-projection-input");
            }
            catch (Exception ex) { Check(ex.ToString(), false); }
            Console.WriteLine($"AUCTION_NATIVE_DISPLAY_PROJECTION: {passed} PASS / {failed} FAIL; pure projection, native client untested.");
            return failed == 0 ? 0 : 1;
        }

        private static ItemCore EquipmentFixture()
        {
            var item = new ItemCore {
                ItemKind = ItemCore.KindEquipment, ItemId = 0x01020304, Value = 0x11223344,
                Attr = 0x65, Durability = 0x5566, SealFlag = 1, EnchantCardId = 0x21324354,
                AmplifyType = 0x12, AmplifyValue = 0x7788, EmblemSocketCount = 2,
                EmblemId1 = 0x10203040, EmblemId2 = 0x50607080,
                RandomOptionState = 0xAB, RandomOptionChangedIndex = 2, RandomOptionChangeState = 0xA6,
                GenuineUpgrade = 0x1D, EnchantUpgradeCount = 0xBC, TailUnknown2 = 0xCD,
                EmancipateEquipmentLevel = 0xDE, A21Tail_Unknown84 = 0xF1E2D3C4,
                // Deliberately nonzero fields absent from this display payload.
                Marker16 = 0x12345678, Rune = 0x4567, TradeRestriction = 0xA1,
                TailUnknown0 = 0xB2C3, TailUnknown1 = 0xD4, TailUnknown3 = 0xE5,
                RemainUseCount = 0xF6, SortLockFlag = 1, EquipmentLockId = 7,
                A21Tail_Unknown96 = 0x87, A21Tail_Unknown97 = 0x12345678,
            };
            item.RandomOption0.Type = 1; item.RandomOption0.Value1 = 2; item.RandomOption0.Value2 = 3;
            item.RandomOption1.Type = 4; item.RandomOption1.Value1 = 5; item.RandomOption1.Value2 = 6;
            item.RandomOption2.Type = 7; item.RandomOption2.Value1 = 8; item.RandomOption2.Value2 = 9;
            item.RandomOptionChange.Type = 10; item.RandomOptionChange.Value1 = 11; item.RandomOptionChange.Value2 = 12;
            for (int index = 0; index < ItemCore.GuardianGemSlotCount; index++)
                item.SetGuardianGemKey(index, (ushort)(0x2345 + index));
            return item;
        }
    }
}

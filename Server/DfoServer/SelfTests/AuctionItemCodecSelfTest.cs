using System;
using System.Linq;
using System.Text.Json.Nodes;
using DfoServer.Game.Auction.Assets;
using DfoServer.Game.Inventory;

namespace DfoServer.SelfTests
{
    // Pure in-memory codec checks; no database, PVF, server or client lifecycle.
    public static class AuctionItemCodecSelfTest
    {
        public static int Run()
        {
            int passed = 0, failed = 0;
            void Check(string name, bool condition)
            {
                Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] auction item: {name}");
                if (condition) passed++; else failed++;
            }
            void Reject(string name, AuctionItemCodecError expected, Action action)
            {
                try { action(); Check(name, false); }
                catch (AuctionItemCodecException ex) { Check(name, ex.Error == expected); }
            }

            try
            {
                var codec = new AuctionItemCodec(new NativeAuctionInstanceExtension());
                var inventory = new InventoryService(77001, 77001);
                var item = new ItemCore
                {
                    ItemKind = ItemCore.KindEquipment, ItemId = 27602, Value = 0x12345678,
                    Durability = 97, SealFlag = 1, EnchantCardId = 12345,
                    EnchantUpgradeCount = 2, AmplifyType = 1, AmplifyValue = 57,
                    GenuineUpgrade = 7, ExpireTime = 1900000000,
                    SortLockFlag = 1, EquipmentLockId = 15,
                    A21Tail_Unknown84 = 0x1234ABCD, A21Tail_Unknown96 = 0x7F,
                    A21Tail_Unknown97 = 0x55667788,
                };
                item.Upgrade = 10;
                item.StackTradeCount = 2;
                item.EmblemSocketCount = 2;
                item.EmblemId1 = 9001;
                item.EmblemId2 = 9002;
                item.RandomOption0.Type = 4;
                item.RandomOption0.Value1 = 12;
                item.RandomOption0.Value2 = 19;
                item.SetGuardianGemKey(0, 18);
                Check("fixture uses real inventory slot", inventory.SetItem(InventoryListType.Main, 9, item));
                var native = item.ToBytes();
                var source = codec.Capture(inventory, InventoryListType.Main, 9);
                var serialized = codec.Serialize(source);
                var restored = codec.DeserializeStrict(serialized);
                var decoded = codec.DecodeStrict(restored);
                Check("exact native99B, cultivation, tail and private state survive storage",
                    decoded.ToBytes().SequenceEqual(native));
                Check("serialization is stable after strict roundtrip", codec.Serialize(restored) == serialized);
                Check("owned source matches full snapshot", codec.MatchesSource(inventory, InventoryListType.Main, 9, source));

                var exposedCore = source.NativeCore99;
                var exposedHash = source.ContentHash;
                exposedCore[0] = 255;
                exposedHash[0] ^= 0xFF;
                Check("getter buffers cannot mutate stored snapshot",
                    source.NativeCore99.SequenceEqual(native) && codec.Serialize(source) == serialized);
                decoded.Upgrade = 1;
                Check("decoded objects cannot mutate snapshot", codec.DecodeStrict(source).ToBytes().SequenceEqual(native));

                var ordinary = source;

                var final = codec.DecodeStrict(source);
                final.SortLockFlag = 0;
                final.EquipmentLockId = 0;
                final.StackTradeCount = 1; // Caller policy outcome, never a codec policy.
                var transfer = codec.CreateTransferSnapshot(source, final);
                var finalBytes = final.ToBytes();
                final.Upgrade = 1;
                Check("transfer is recaptured against final native bytes and independent of caller mutation",
                    codec.DecodeStrict(transfer).ToBytes().SequenceEqual(finalBytes));
                Check("source remains separately available for original-instance return",
                    codec.DecodeStrict(source).ToBytes().SequenceEqual(native) && codec.Serialize(source) == serialized);
                var otherTemplate = codec.DecodeStrict(source); otherTemplate.ItemId++;
                Reject("transfer cannot replace template", AuctionItemCodecError.TransferIdentityChanged,
                    () => codec.CreateTransferSnapshot(source, otherTemplate));

                Reject("legacy82B cannot become a new template item", AuctionItemCodecError.InvalidNativeCore,
                    () => codec.DeserializeStrict(Change(serialized, "nativeCore", Convert.ToBase64String(native[..82]))));
                Reject("extended core cannot hide extra bytes", AuctionItemCodecError.InvalidNativeCore,
                    () => codec.DeserializeStrict(Change(serialized, "nativeCore", Convert.ToBase64String(native.Concat(new byte[1]).ToArray()))));
                var damaged = (byte[])native.Clone(); damaged[ItemCore.AttrOffset] ^= 1;
                Reject("changed native bytes reject stale complete hash", AuctionItemCodecError.IntegrityMismatch,
                    () => codec.DeserializeStrict(Change(serialized, "nativeCore", Convert.ToBase64String(damaged))));
                Reject("changed detail rejects stale complete hash", AuctionItemCodecError.IntegrityMismatch,
                    () => codec.DeserializeStrict(Change(serialized, "detailJson", "{}")));
                Reject("unknown format rejected", AuctionItemCodecError.InvalidEnvelope,
                    () => codec.DeserializeStrict(Change(serialized, "formatVersion", 2)));
                Reject("hash length cannot be truncated", AuctionItemCodecError.IntegrityMismatch,
                    () => codec.DeserializeStrict(Change(serialized, "contentHash", "00")));
                Reject("duplicate descriptor field rejected", AuctionItemCodecError.InvalidEnvelope,
                    () => codec.DeserializeStrict(serialized.Insert(1, "\"formatVersion\":1,")));
                Reject("unknown descriptor field rejected", AuctionItemCodecError.InvalidEnvelope,
                    () => codec.DeserializeStrict(Change(serialized, "unrecognized", true)));
                Reject("missing descriptor field rejected", AuctionItemCodecError.InvalidEnvelope, () =>
                {
                    var node = JsonNode.Parse(serialized).AsObject(); node.Remove("extensionsJson");
                    codec.DeserializeStrict(node.ToJsonString());
                });
                Reject("malformed descriptor does not fall back", AuctionItemCodecError.InvalidEnvelope,
                    () => codec.DeserializeStrict("not-json"));

                // Recompute the outer hash to exercise semantic extension checks,
                // rather than merely catching corruption with the outer checksum.
                Reject("opaque non-JSON metadata is not dropped", AuctionItemCodecError.InvalidExtension,
                    () => codec.DecodeStrict(AuctionItemEnvelope.Create(native, "", "old opaque metadata")));
                Reject("unknown extension is not dropped", AuctionItemCodecError.InvalidExtension,
                    () => codec.DecodeStrict(AuctionItemEnvelope.Create(native, "", "{\"otherState\":{}}")));
                Reject("extension adapter cannot rewrite native instance", AuctionItemCodecError.InvalidExtension,
                    () => new AuctionItemCodec(new MutatingExtension()).DecodeStrict(ordinary));
                Reject("unexpected detail on ordinary item is not discarded", AuctionItemCodecError.UnsupportedDetailItem,
                    () => codec.DecodeStrict(AuctionItemEnvelope.Create(native, "{}", source.ExtensionsJson)));

                var avatar = new ItemCore { ItemKind = ItemCore.KindAvatar, ItemId = 1001, AvatarUid = 42 };
                Reject("avatar missing detail cannot be default-created", AuctionItemCodecError.UnsupportedDetailItem,
                    () => codec.DecodeStrict(AuctionItemEnvelope.Create(avatar.ToBytes(), "", "{}")));
                var creature = new ItemCore { ItemKind = ItemCore.KindCreature, ItemId = 1002, CreatureUid = 43 };
                Reject("incomplete creature detail cannot be default-created", AuctionItemCodecError.UnsupportedDetailItem,
                    () => codec.DecodeStrict(AuctionItemEnvelope.Create(creature.ToBytes(), "{\"Kind\":\"creature\"}", "{}")));
                var stack = new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = 3001, Count = 7 };
                inventory.SetItem(InventoryListType.Main, 10, stack);
                Check("ordinary stack count is exact", codec.DecodeStrict(codec.Capture(inventory, InventoryListType.Main, 10)).Count == 7);
                var stackSource = codec.Capture(inventory, InventoryListType.Main, 10);
                var countTransfer = new AuctionTransferableItemAdapter(codec).Transform(stackSource, 3, returning: false);
                var expectedCountCore = stack.Copy(); expectedCountCore.Count = 3;
                Check("transfer adapter changes only ordinary stack quantity", countTransfer.NativeCore99.SequenceEqual(expectedCountCore.ToBytes())
                    && countTransfer.DetailJson == stackSource.DetailJson && countTransfer.ExtensionsJson == stackSource.ExtensionsJson
                    && codec.DecodeStrict(stackSource).Count == 7);
                Check("ordinary adapter return is the same exact quantity derivative", new AuctionTransferableItemAdapter(codec)
                    .Transform(stackSource, 3, returning: true).ContentHashHex == countTransfer.ContentHashHex);
                stack.Count = 0;
                Reject("zero-count stack is invalid", AuctionItemCodecError.InvalidNativeCore,
                    () => codec.Capture(inventory, InventoryListType.Main, 10));
                inventory.RemoveItem(InventoryListType.Main, 9);
                Check("removed source cannot match", !codec.MatchesSource(inventory, InventoryListType.Main, 9, source));
                Reject("empty slot cannot become a listing snapshot", AuctionItemCodecError.MissingSource,
                    () => codec.Capture(inventory, InventoryListType.Main, 9));
            }
            catch (Exception ex) { Check(ex.ToString(), false); }
            Console.WriteLine($"AUCTION_ITEM_CODEC: {passed} PASS / {failed} FAIL; codec only, no native UI exercised.");
            return failed == 0 ? 0 : 1;
        }

        private static string Change<T>(string serialized, string property, T value)
        {
            var node = JsonNode.Parse(serialized).AsObject();
            node[property] = System.Text.Json.JsonSerializer.SerializeToNode(value);
            return node.ToJsonString();
        }

        private sealed class MutatingExtension : IAuctionInstanceExtension
        {
            public string Capture(ItemCore source, ItemCore finalTransferCore) => "{}";
            public void Restore(ItemCore target, string extensionsJson) => target.Upgrade++;
        }
    }
}

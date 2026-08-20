using DfoServer.Game.Inventory;
using DfoServer.Network;
using DfoServer.Network.Builders;
using System;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class MagicBoxProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== MAGIC_BOX_PROTOCOL selftest ===");
            var failures = 0;

            const short sourceSlot = 0x56;
            const short rewardSlot0 = 0x52;
            const short rewardSlot1 = 0x53;
            const int item0 = 0x0000002A;
            const int item1 = 0x0098B3BC;
            const int expireTime = 0x6A871502;

            var result = new BoosterUseResult
            {
                MagicBoxClientType = 4,
                SourceSlotIndex = sourceSlot,
            };
            result.DisplayRewards.Add(new PackageGrantedItem
            {
                ItemTemplateId = item0,
                DisplayCount = 1,
            });
            result.DisplayRewards.Add(new PackageGrantedItem
            {
                ItemTemplateId = item1,
                DisplayCount = 0x46,
            });
            result.Rewards.Add(new BoosterRewardResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = rewardSlot0,
                ItemTemplateId = item0,
                GrantedCount = 1,
                ExpireTime = expireTime,
            });
            result.Rewards.Add(new BoosterRewardResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = rewardSlot1,
                ItemTemplateId = item1,
                GrantedCount = 0x46,
                ExpireTime = expireTime,
            });

            var body = MagicBoxOpenAckBuilder.BuildSingle(result);
            Check(
                "A21 USE_RANDOMBOX_ITEM single ACK uses captured 89-byte body",
                body.Length == 89,
                ref failures);
            Check(
                "A21 USE_RANDOMBOX_ITEM single ACK header fields",
                body[0] == 1
                && body[1] == 4
                && body[2] == 0
                && BitConverter.ToInt16(body, 3) == sourceSlot
                && BitConverter.ToInt16(body, 5) == -1
                && BitConverter.ToUInt16(body, 7) == 2,
                ref failures);
            CheckRewardRow(
                body,
                9,
                rewardSlot0,
                item0,
                1,
                expireTime,
                "A21 USE_RANDOMBOX_ITEM first reward row",
                ref failures);
            CheckRewardRow(
                body,
                49,
                rewardSlot1,
                item1,
                0x46,
                expireTime,
                "A21 USE_RANDOMBOX_ITEM second reward row",
                ref failures);

            var packet = GamePacketEnvelopeBuilder.Build(0x01, 0x00D0, body);
            Check(
                "A21 USE_RANDOMBOX_ITEM envelope size matches captured success packet",
                packet.Length == 104
                && BitConverter.ToUInt16(packet, 1) == 0x00D0
                && BitConverter.ToUInt16(packet, 3) == 104,
                ref failures);

            var reviveResult = new BoosterUseResult
            {
                MagicBoxClientType = 4,
                SourceSlotIndex = sourceSlot,
            };
            reviveResult.DisplayRewards.Add(new PackageGrantedItem
            {
                ItemTemplateId = item0,
                DisplayCount = 1,
            });
            reviveResult.Rewards.Add(new BoosterRewardResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = 1,
                ItemTemplateId = 1,
                GrantedCount = 1,
                SpecialOutcome = new SpecialRewardOutcome
                {
                    Kind = SpecialRewardKind.ReviveCoin,
                    ItemTemplateId = item0,
                    Count = 1,
                    WalletSlot = 1,
                    WalletNewTotal = 5,
                },
            });

            var reviveBody = MagicBoxOpenAckBuilder.BuildSingle(reviveResult);
            Check(
                "A21 USE_RANDOMBOX_ITEM keeps display item id for virtual revive coin reward",
                reviveBody.Length == 49
                && BitConverter.ToUInt16(reviveBody, 7) == 1
                && BitConverter.ToInt16(reviveBody, 9) == 1
                && BitConverter.ToInt32(reviveBody, 11) == item0
                && BitConverter.ToInt32(reviveBody, 15) == 1,
                ref failures);

            var batchResult = new BoosterUseResult
            {
                MagicBoxClientType = 4,
                SourceSlotIndex = 0x4B,
                ConsumedSourceCount = 10,
            };
            batchResult.DisplayRewards.Add(new PackageGrantedItem
            {
                ItemTemplateId = item0,
                DisplayCount = 1,
            });
            batchResult.DisplayRewards.Add(new PackageGrantedItem
            {
                ItemTemplateId = item0,
                DisplayCount = 3,
            });
            batchResult.DisplayRewards.Add(new PackageGrantedItem
            {
                ItemTemplateId = item1,
                DisplayCount = 5,
            });
            batchResult.Rewards.Add(new BoosterRewardResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = rewardSlot0,
                ItemTemplateId = item0,
                GrantedCount = 4,
                ExpireTime = expireTime,
            });
            batchResult.Rewards.Add(new BoosterRewardResult
            {
                ListType = InventoryListType.Main,
                SlotIndex = rewardSlot1,
                ItemTemplateId = item1,
                GrantedCount = 5,
                ExpireTime = expireTime,
            });

            var batchBody = MagicBoxOpenAckBuilder.BuildBatch(batchResult);
            Check(
                "A21 USE_RANDOMBOX_ITEM_EXPAND without double uses 95-byte two-row body",
                batchBody.Length == 95
                && batchBody[0] == 1
                && batchBody[1] == 4
                && batchBody[2] == 0
                && BitConverter.ToUInt16(batchBody, 3) == 10
                && BitConverter.ToInt16(batchBody, 5) == 0x4B
                && BitConverter.ToInt16(batchBody, 7) == -1
                && BitConverter.ToUInt16(batchBody, 9) == 2
                && BitConverter.ToUInt16(batchBody, 91) == 0
                && BitConverter.ToUInt16(batchBody, 93) == 0,
                ref failures);
            CheckRewardRow(
                batchBody,
                11,
                rewardSlot0,
                item0,
                4,
                expireTime,
                "A21 USE_RANDOMBOX_ITEM_EXPAND aggregates first reward row",
                ref failures);
            CheckRewardRow(
                batchBody,
                51,
                rewardSlot1,
                item1,
                5,
                expireTime,
                "A21 USE_RANDOMBOX_ITEM_EXPAND aggregates second reward row",
                ref failures);

            batchResult.DoubleRewards.Add(new PackageGrantedItem
            {
                ItemTemplateId = item1,
                DisplayCount = 2,
            });
            batchResult.DoubleRewards.Add(new PackageGrantedItem
            {
                ItemTemplateId = item1,
                DisplayCount = 3,
            });

            var doubleBatchBody = MagicBoxOpenAckBuilder.BuildBatch(batchResult);
            Check(
                "A21 USE_RANDOMBOX_ITEM_EXPAND double list follows reserved separator",
                doubleBatchBody.Length == 135
                && doubleBatchBody[2] == 1
                && BitConverter.ToUInt16(doubleBatchBody, 91) == 0
                && BitConverter.ToUInt16(doubleBatchBody, 93) == 1,
                ref failures);
            CheckRewardRow(
                doubleBatchBody,
                95,
                rewardSlot1,
                item1,
                5,
                expireTime,
                "A21 USE_RANDOMBOX_ITEM_EXPAND writes double reward row as 40 bytes",
                ref failures);
            Check(
                "A21 USE_RANDOMBOX_ITEM_EXPAND captured 13+3 rows account for 655-byte body",
                9 + 2 + 13 * 40 + 2 + 2 + 3 * 40 == 655,
                ref failures);

            Console.WriteLine(failures == 0
                ? "MAGIC_BOX_PROTOCOL selftest passed."
                : $"MAGIC_BOX_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void CheckRewardRow(
            byte[] body,
            int offset,
            short slot,
            int itemId,
            int count,
            int expireTime,
            string name,
            ref int failures)
        {
            Check(
                name,
                BitConverter.ToInt16(body, offset) == slot
                && BitConverter.ToInt32(body, offset + 2) == itemId
                && BitConverter.ToInt32(body, offset + 6) == count
                && BitConverter.ToUInt16(body, offset + 10) == 0
                && body[offset + 12] == 0
                && BitConverter.ToInt32(body, offset + 13) == expireTime
                && body.Skip(offset + 17).Take(23).All(value => value == 0),
                ref failures);
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            if (condition)
            {
                Console.WriteLine($"[PASS] {name}");
                return;
            }

            failures++;
            Console.WriteLine($"[FAIL] {name}");
        }
    }
}

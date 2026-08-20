using DfoServer.Game.Inventory;
using DfoServer.Game.Lottery;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Lottery;
using System;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class LotteryItemSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== LOTTERY_ITEM selftest ===");
            var failures = 0;

            var requestBody = new byte[] { 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00 };
            Check(
                "phase0 request ignores reserved tail",
                LotteryItemUseRequest.TryParse(requestBody, out var request)
                && request.Phase == 0
                && request.SlotIndex == 3,
                ref failures);

            const short sourceSlot = 3;
            const short rewardSlot = 0x7E;
            const int rewardItemId = 0x00000CEF;
            const int rewardCount = 2;
            const int expireTime = 0x6A870B0A;

            var reward = ItemCore.Create(ItemCore.KindConsumable, rewardItemId);
            reward.ExpireTime = expireTime;
            var resultBody = LotteryItemAckBuilder.BuildCommonItemResult(
                sourceSlot,
                rewardSlot,
                reward,
                rewardCount);

            Check(
                "common lottery result uses A21 50-byte body",
                resultBody.Length == 50,
                ref failures);
            Check(
                "common lottery result header fields",
                resultBody[0] == 1
                && BitConverter.ToInt16(resultBody, 1) == sourceSlot
                && BitConverter.ToInt16(resultBody, 3) == rewardSlot
                && BitConverter.ToInt32(resultBody, 5) == rewardItemId
                && BitConverter.ToInt32(resultBody, 9) == rewardCount,
                ref failures);
            Check(
                "common lottery result tail matches client read order",
                BitConverter.ToInt32(resultBody, 19) == expireTime
                && resultBody.Skip(23).All(value => value == 0),
                ref failures);

            var packet = GamePacketEnvelopeBuilder.Build(0x01, 0x001B, resultBody);
            Check(
                "common lottery result envelope size matches retail success packet",
                packet.Length == 65
                && BitConverter.ToUInt16(packet, 1) == 0x001B
                && BitConverter.ToUInt16(packet, 3) == 65,
                ref failures);

            const int grantedGold = 123456;
            var goldBody = LotteryItemAckBuilder.BuildGoldResult(sourceSlot, grantedGold);
            Check(
                "gold lottery result uses A21 50-byte body",
                goldBody.Length == 50
                && goldBody[0] == 1
                && BitConverter.ToInt16(goldBody, 1) == sourceSlot
                && BitConverter.ToInt16(goldBody, 3) == 0
                && BitConverter.ToInt32(goldBody, 5) == 0
                && BitConverter.ToInt32(goldBody, 9) == grantedGold
                && goldBody.Skip(19).All(value => value == 0),
                ref failures);

            var goldPacket = GamePacketEnvelopeBuilder.Build(0x01, 0x001B, goldBody);
            Check(
                "gold lottery result envelope size matches A21 success packet",
                goldPacket.Length == 65
                && BitConverter.ToUInt16(goldPacket, 3) == 65,
                ref failures);

            var parsedGoldStackable = PvfLib.StackableItemFile.Parse(
                "[stackable type]\n`[legacy]`\n[/stackable type]\n[int data]\n0 10000\n[/int data]");
            Check(
                "PVF legacy parser keeps gold item id zero",
                parsedGoldStackable.LegacyRewards.Count == 1
                && parsedGoldStackable.LegacyRewards[0].ItemId == 0,
                ref failures);

            var parsedUpgradableGoldStackable = PvfLib.StackableItemFile.Parse(
                "[stackable type]\n`[upgradable legacy]`\n[/stackable type]\n[int data]\n0 10000 123456\n[/int data]");
            Check(
                "PVF upgradable legacy parser keeps gold item id zero",
                parsedUpgradableGoldStackable.UpgradableLegacyRewards.Count == 1
                && parsedUpgradableGoldStackable.UpgradableLegacyRewards[0].ItemId == 0
                && parsedUpgradableGoldStackable.UpgradableLegacyRewards[0].Count == 123456,
                ref failures);

            var goldDefinitionSource = new PvfLib.StackableItemFile
            {
                StackableType = "[legacy]",
            };
            goldDefinitionSource.LegacyRewards.Add(new PvfLib.BoosterRewardEntry
            {
                RewardKind = "legacy",
                ItemId = 0,
                Weight = 10000,
                Count = grantedGold,
            });
            Check(
                "gold lottery definition keeps item id zero",
                LotteryItemDefinitionProvider.TryBuild(
                    7616,
                    goldDefinitionSource,
                    out var goldDefinition)
                && goldDefinition.RewardPool.Count == 1
                && goldDefinition.RewardPool[0].ItemId == 0
                && goldDefinition.RewardPool[0].Count == grantedGold,
                ref failures);

            Check(
                "gold lottery reward requests refresh gold",
                LotteryPresentationPolicy.ShouldSendGoldRefresh(new LotteryOpenResult
                {
                    GrantedGold = grantedGold,
                }),
                ref failures);

            Console.WriteLine(failures == 0
                ? "LOTTERY_ITEM selftest passed"
                : $"LOTTERY_ITEM selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
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

using DfoServer.Game.Lottery;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Network.Builders
{
    public static class IncreaseChanceLotteryPacketBuilder
    {
        public const int AllStateBodyLength = 204;
        private const int RecordCount = 8;
        private const int RecordLength = 24;
        private const int ClaimCapacity = 20;

        public static byte[] BuildAllState(LotteryProgressSnapshot snapshot)
        {
            return BuildAllState(snapshot == null
                ? Array.Empty<LotteryProgressSnapshot>()
                : new[] { snapshot }, snapshot);
        }

        public static byte[] BuildAllState(
            IEnumerable<LotteryProgressSnapshot> snapshots,
            LotteryProgressSnapshot current = null)
        {
            var body = new byte[AllStateBodyLength];
            var records = (snapshots ?? Array.Empty<LotteryProgressSnapshot>())
                .Where(snapshot => snapshot != null && snapshot.ItemTemplateId > 0)
                .Take(RecordCount)
                .ToList();
            if (records.Count == 0 && current == null)
                return body;

            WriteInt32(body, 0, 2);
            WriteInt32(body, 4, current?.ItemTemplateId ?? -1);
            WriteInt32(body, 8, current != null && current.NewRewardIndex >= 0
                ? current.NewRewardIndex + 1
                : -1);
            for (var recordIndex = 0; recordIndex < records.Count; recordIndex++)
            {
                var recordOffset = 12 + recordIndex * RecordLength;
                WriteInt32(body, recordOffset, records[recordIndex].ItemTemplateId);
                var claimedRewardIndexes = records[recordIndex].ClaimedRewardIndexes
                    .Where(rewardIndex => rewardIndex >= 0 && rewardIndex < ClaimCapacity)
                    .Distinct()
                    .OrderBy(rewardIndex => rewardIndex)
                    .Take(ClaimCapacity)
                    .ToList();
                for (var claimIndex = 0; claimIndex < claimedRewardIndexes.Count; claimIndex++)
                {
                    body[recordOffset + 4 + claimIndex] =
                        (byte)(claimedRewardIndexes[claimIndex] + 1);
                }
            }
            return body;
        }

        public static byte[] BuildResetResponse(int result, bool showSuccess)
        {
            if (showSuccess && result == 0)
                return new byte[] { 1 };

            var errorCode = (byte)Math.Min(byte.MaxValue, Math.Max(1, result));
            return new byte[] { 0, errorCode };
        }

        private static void WriteInt32(byte[] body, int offset, int value)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, body, offset, 4);
        }
    }
}

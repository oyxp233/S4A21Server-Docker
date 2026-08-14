using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal interface ICardRewardNotificationSender
    {
        Task SendLayoutAsync(EnhancedClientSession session);
        Task SendCardInfoAsync(
            EnhancedClientSession session,
            DungeonRun run);
        Task SendExitAsync(
            EnhancedClientSession session,
            byte state,
            byte option);
        Task SendItemUpdatesAsync(
            EnhancedClientSession session,
            IReadOnlyList<InventorySlotMutation> changes);
    }

    internal sealed class CardRewardNotificationSender
        : ICardRewardNotificationSender
    {
        public async Task SendLayoutAsync(EnhancedClientSession session)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0045,
                new byte[] { 0x01 }));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0046,
                BuildCardLayoutAck()));
        }

        public Task SendCardInfoAsync(
            EnhancedClientSession session,
            DungeonRun run)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0047,
                BuildCardInfoAck(run)));

        public Task SendExitAsync(
            EnhancedClientSession session,
            byte state,
            byte option)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0048,
                new byte[] { 0x01, state, option }));

        public async Task SendItemUpdatesAsync(
            EnhancedClientSession session,
            IReadOnlyList<InventorySlotMutation> changes)
        {
            if (changes == null || changes.Count == 0)
                return;
            foreach (var group in changes.GroupBy(change => change.ListType))
            {
                await InventoryRefreshSender.SendOnlineUpdateItemList(
                    session,
                    group.Key,
                    group.Select(change => change.SlotIndex).ToList());
            }
        }

        internal static byte[] BuildCardInfoAck(DungeonRun run)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            for (var index = 0; index < 8; index++)
            {
                if (index >= 4)
                {
                    writer.WriteByte(0xFF);
                    writer.WriteByte(0xFF);
                    writer.WriteByte(0xFF);
                    writer.WriteByte(0xFF);
                    continue;
                }

                var freeSelected = run.FreeCardSlots[index] != 0xFF;
                var paidSelected = run.PaidCardSlots[index] != 0xFF;
                if (index != 0)
                {
                    writer.WriteByte(0xFF);
                    writer.WriteByte(0xFF);
                    writer.WriteByte(0x00);
                    writer.WriteByte(0x00);
                    continue;
                }

                writer.WriteByte(freeSelected ? (byte)0x00 : (byte)0xFF);
                writer.WriteByte(paidSelected ? (byte)0x00 : (byte)0xFF);
                if (paidSelected)
                {
                    var cards = run.CardRewards;
                    var paidGold = cards != null
                        && cards.Count > 4
                        && cards[4].IsGold
                        ? cards[4].GoldAmount
                        : 0;
                    var paidItemId = cards != null
                        && cards.Count > 5
                        && !cards[5].IsGold
                        ? cards[5].ItemId
                        : 0;
                    var paidItemCount = cards != null
                        && cards.Count > 5
                        && !cards[5].IsGold
                        ? cards[5].StackCount
                        : 0;
                    writer.WriteByte(2);
                    writer.WriteUInt32(0);
                    writer.WriteInt32(paidGold);
                    writer.WriteUInt32((uint)paidItemId);
                    writer.WriteInt32(paidItemCount);
                }
                else
                {
                    writer.WriteByte(0x00);
                }
                writer.WriteByte(0x00);
            }
            return writer.ToArray();
        }

        private static byte[] BuildCardLayoutAck()
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteUInt16(0x0001);
            for (var index = 1; index < 8; index++)
                writer.WriteUInt16(0xFFFF);
            return writer.ToArray();
        }
    }
}

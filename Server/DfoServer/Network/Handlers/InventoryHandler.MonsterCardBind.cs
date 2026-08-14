using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENUM_CMDPACKET_MONSTERCARD_BIND(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length != 6 || session?.Player == null)
            {
                await SendMonsterCardBindError(session, header.type);
                return;
            }

            var binderSlot = BitConverter.ToInt16(body, 0);
            var firstSlot = BitConverter.ToInt16(body, 2);
            var secondSlot = BitConverter.ToInt16(body, 4);
            MonsterCardBindResult result = null;
            string rejection = null;
            var persistenceFailed = false;
            var ok = InventoryContext.TryGetLease(session.Player.CharacterId, out var lease)
                && lease.IsOwnedBy(session.SessionId);
            if (ok)
            {
                ok = InventoryCardEmblemCommitService.TryCommitMonsterCardBind(
                    lease,
                    _monsterCardBindService,
                    binderSlot,
                    firstSlot,
                    secondSlot,
                    out result,
                    out rejection,
                    out persistenceFailed);
            }
            else
                rejection = "inventory lease unavailable";

            if (!ok)
            {
                await SendMonsterCardBindError(session, header.type,
                    persistenceFailed
                        ? (byte)0x13
                        : string.Equals(rejection, "inventory full", StringComparison.Ordinal) ? (byte)0x04 : (byte)0x13);
                FileLogger.Log($"[{ProtocolName}] MONSTERCARD_BIND rejected: cid={session.Player.CharacterId} slots={binderSlot},{firstSlot},{secondSlot} reason={rejection} persistenceFailed={persistenceFailed}");
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type,
                MonsterCardBindAckBuilder.BuildSuccess(binderSlot, firstSlot, secondSlot, result)));
            await _refresh.SendUpdateItemList(session, InventoryListType.Main,
                new[] { binderSlot, firstSlot, secondSlot, result.Grant.SlotIndex });
            FileLogger.Log($"[{ProtocolName}] MONSTERCARD_BIND ok: cid={session.Player.CharacterId} bind={result.BindType} " +
                $"rarity={result.FirstRarity}+{result.SecondRarity}->{result.ResultRarity} success={result.SuccessRoll} " +
                $"chance={result.SuccessWeight}/{MonsterCardBindConfig.ProbabilityDenominator} item={result.ResultItemId} " +
                $"slot={result.Grant.SlotIndex} ack={MonsterCardBindAckBuilder.SuccessLength}B");
        }

        private static Task SendMonsterCardBindError(EnhancedClientSession session, ushort type, byte errorCode = 0x13)
            => session == null ? Task.CompletedTask : session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01, type, MonsterCardBindAckBuilder.BuildError(errorCode)));
    }
}

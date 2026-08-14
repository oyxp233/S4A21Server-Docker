using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_UPGRADE_CARD(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (body == null || body.Length != 7 || session?.Player == null)
            {
                await SendMonsterCardUpgradeError(session, header.type, 0x13);
                return;
            }

            var listType = (InventoryListType)body[0];
            var targetSlot = BitConverter.ToInt16(body, 1);
            var materialCount = BitConverter.ToInt16(body, 3);
            var materialSlot = BitConverter.ToInt16(body, 5);
            MonsterCardUpgradeResult result = null;
            string rejection = null;
            var persistenceFailed = false;
            var ok = InventoryContext.TryGetLease(session.Player.CharacterId, out var lease)
                && lease.IsOwnedBy(session.SessionId);
            if (ok)
            {
                ok = InventoryCardEmblemCommitService.TryCommitMonsterCardUpgrade(
                    lease,
                    _monsterCardUpgradeService,
                    listType,
                    targetSlot,
                    materialSlot,
                    materialCount,
                    out result,
                    out rejection,
                    out persistenceFailed);
            }
            else
                rejection = "inventory lease unavailable";

            if (!ok)
            {
                var errorCode = persistenceFailed
                    ? (byte)0x13
                    : string.Equals(rejection, "insufficient gold", StringComparison.Ordinal)
                    ? (byte)0x0A
                    : string.Equals(rejection, "inventory full", StringComparison.Ordinal)
                        ? (byte)0x04
                        : (byte)0x13;
                await SendMonsterCardUpgradeError(session, header.type, errorCode);
                FileLogger.Log(
                    $"[{ProtocolName}] UPGRADE_CARD rejected: cid={session.Player.CharacterId} " +
                    $"list={listType} target={targetSlot} material={materialSlot} count={materialCount} " +
                    $"reason={rejection} persistenceFailed={persistenceFailed}");
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01, header.type, MonsterCardUpgradeAckBuilder.BuildSuccess(result)));
            await _refresh.SendItemListRefresh(session, InventoryListType.Main);
            FileLogger.Log(
                $"[{ProtocolName}] UPGRADE_CARD ok: cid={session.Player.CharacterId} " +
                $"target={targetSlot} material={materialSlot} result={result.ResultSlot} success={result.Success} " +
                $"upgrade={result.UpgradeCount} chance={result.Chance}/" +
                $"{MonsterCardUpgradeConfig.ProbabilityDenominator} gold={result.GoldCost} " +
                $"remainingGold={result.UpdatedGold} ack={MonsterCardUpgradeAckBuilder.SuccessLength}B");
        }

        private static Task SendMonsterCardUpgradeError(
            EnhancedClientSession session,
            ushort type,
            byte errorCode)
            => session == null
                ? Task.CompletedTask
                : session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01, type, MonsterCardUpgradeAckBuilder.BuildError(errorCode)));
    }
}

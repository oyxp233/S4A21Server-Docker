using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        // CMD 0x0191 (wire 401) UNSEAL_RANDOM_OPTION
        // body: slot(i16) + scrollSlot(i16)
        public async Task Handle_UNSEAL_RANDOM_OPTION(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0191, new byte[] { 0x00 }));
                return;
            }

            var targetSlot = BitConverter.ToInt16(body, 0);

            var (cid, _) = ResolveOwner(session);
            RandomOptionUnsealResult result;
            bool ok;
            var persistenceFailed = false;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                ok = InventoryRandomOptionCommitService.TryCommitUnseal(
                    lease,
                    targetSlot,
                    0,
                    out result,
                    out persistenceFailed);
            }
            else
            {
                ok = false;
                result = null;
            }

            if (!ok)
            {
                FileLogger.Log($"[{ProtocolName}] UNSEAL_RANDOM_OPTION reject slot={targetSlot} persistenceFailed={persistenceFailed}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0191, new byte[] { 0x00 }));
                return;
            }

            if (!result.TargetEquipped)
                await _refresh.SendUpdateItemList(session, result.TargetListType, result.TargetSlotIndex);

            if (result.GoldCost > 0)
                await _refresh.SendGoldUpdate(session);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0191, new byte[] { 0x01 }));

            FileLogger.Log($"[{ProtocolName}] UNSEAL_RANDOM_OPTION ok slot={result.TargetSlotIndex} item=0x{result.TargetItemTemplateId:X8} goldCost={result.GoldCost} options={FormatMagicSealOptions(result.RandomOptions)}");
        }

        // CMD 0x01B6 (wire 438) CHANGE_RANDOM_OPTION
        // body: slot(i16) + materialSlot(i16) + optionIndex(u8)
        public async Task Handle_CHANGE_RANDOM_OPTION(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 5)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01B6, new byte[] { 0x00 }));
                return;
            }

            var targetSlot = BitConverter.ToInt16(body, 0);
            var optionIndex = body[4];

            var (cid, _) = ResolveOwner(session);
            RandomOptionUnsealResult result;
            bool ok;
            var persistenceFailed = false;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                ok = InventoryRandomOptionCommitService.TryCommitChange(
                    lease,
                    targetSlot,
                    0,
                    optionIndex,
                    out result,
                    out persistenceFailed);
            }
            else
            {
                ok = false;
                result = null;
            }

            if (!ok)
            {
                FileLogger.Log($"[{ProtocolName}] CHANGE_RANDOM_OPTION reject slot={targetSlot} idx={optionIndex} persistenceFailed={persistenceFailed}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01B6, new byte[] { 0x00 }));
                return;
            }

            if (!result.TargetEquipped)
                await _refresh.SendUpdateItemList(session, result.TargetListType, result.TargetSlotIndex);

            if (result.GoldCost > 0)
                await _refresh.SendGoldUpdate(session);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01B6, new byte[] { 0x01 }));

            FileLogger.Log($"[{ProtocolName}] CHANGE_RANDOM_OPTION ok slot={result.TargetSlotIndex} item=0x{result.TargetItemTemplateId:X8} replaced={result.ReplacedOptionIndex} goldCost={result.GoldCost} options={FormatMagicSealOptions(result.RandomOptions)}");
        }

        private static string FormatMagicSealOptions(System.Collections.Generic.IReadOnlyList<RandomOptionEntry> entries)
        {
            if (entries == null || entries.Count == 0) return "[]";
            var parts = new string[entries.Count];
            for (var i = 0; i < entries.Count; i++)
                parts[i] = $"{entries[i].Type:X2}/{entries[i].Value1}/{entries[i].Value2}";
            return "[" + string.Join(",", parts) + "]";
        }
    }
}

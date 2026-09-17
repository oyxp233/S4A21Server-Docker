using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    internal static class ItemTradePacketBuilder
    {
        internal static byte[] PeerAck(CmdPacketTypeA21 command, byte error = 0)
            => GamePacketEnvelopeBuilder.Build(1, (ushort)command,
                error == 0 ? new byte[] { 1 } : new byte[] { 0, error, 1 });

        internal static byte[] Invite(ushort uid, int peer)
        {
            var w = new GamePacketWriter();
            w.WriteUInt16(uid); w.WriteByte(1); w.WriteInt32(peer); w.WriteInt32(0);
            return Noti(NotiPacketTypeA21.REQUEST_PEER, w);
        }

        internal static byte[] Accepted(ushort uid, int peer, bool responder)
        {
            var w = new GamePacketWriter();
            if (responder) w.WriteByte(1);
            w.WriteUInt16(uid); w.WriteByte(1);
            if (!responder) w.WriteInt32(peer);
            w.WriteByte(1);
            if (!responder) w.WriteInt32(0);
            return GamePacketEnvelopeBuilder.Build(responder ? (byte)1 : (byte)0,
                responder ? (ushort)CmdPacketTypeA21.RESPONSE_PEER : (ushort)NotiPacketTypeA21.RESPONSE_PEER, w.ToArray());
        }

        internal static byte[] Item(short slot, ItemCore core, int gold = 0)
        {
            var w = new GamePacketWriter();
            if (slot == 0) ItemListProtocolWriter.WriteVirtualCountEntry84(w, 0, 0, gold);
            else if (core == null) ItemListProtocolWriter.WriteEmptyEntry(w, InventoryListType.Main, slot);
            else ItemListProtocolWriter.WriteCommonEntry84(w, slot, core);
            return Noti(NotiPacketTypeA21.CHANGE_ITEMTRADE_ITEM, w);
        }

        internal static byte[] State(ushort uid, byte state)
        {
            var w = new GamePacketWriter(); w.WriteUInt16(uid); w.WriteByte(state);
            return Noti(NotiPacketTypeA21.STATE_ITEMTRADE, w);
        }

        internal static byte[] RegistrationFinished() => GamePacketEnvelopeBuilder.Build(0,
            (ushort)NotiPacketTypeA21.ITEMTRADE_REG_ITEM_FINISH, System.Array.Empty<byte>());

        // The client accepts an empty mapping and clears its temporary trade
        // inventories. The handler then publishes the authoritative main list.
        internal static byte[] Closed(bool committed) => GamePacketEnvelopeBuilder.Build(0,
            committed ? (ushort)NotiPacketTypeA21.FINISH_ITEMTRADE : (ushort)NotiPacketTypeA21.CANCEL_ITEMTRADE,
            new byte[] { 0, 0 });

        private static byte[] Noti(NotiPacketTypeA21 type, GamePacketWriter w)
            => GamePacketEnvelopeBuilder.Build(0, (ushort)type, w.ToArray());
    }
}

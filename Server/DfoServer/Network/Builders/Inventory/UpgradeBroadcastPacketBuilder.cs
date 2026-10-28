using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.ItemUpgrade;

namespace DfoServer.Network.Builders
{
    internal static class UpgradeBroadcastPacketBuilder
    {
        private static byte[] Packet(NotiPacketTypeA21 type, Action<GamePacketWriter> write)
        { var w = new GamePacketWriter(); write(w); return GamePacketEnvelopeBuilder.Build(0, (ushort)type, w.ToArray()); }

        // 01232C76..01232Dxx registers the active common ACK callbacks through
        // 01637660, replacing the earlier startup readers for these commands.
        // CREATE success reaches 01230BD0 and enables the native host flag +29.
        internal static byte[] Accepted(CmdPacketTypeA21 command)
            => GamePacketEnvelopeBuilder.Build(1, (ushort)command, new byte[] { 1 });

        // Current A21 readers: 01232210, 01232360, 01232A20.
        internal static byte[] Created(UpgradeBroadcastRoom room) => Packet(NotiPacketTypeA21.CREATE_UPGRADE_ROOM_INFO, w => Address(w, room));
        internal static byte[] Destroyed(UpgradeBroadcastRoom room) => Packet(NotiPacketTypeA21.DESTROY_UPGRADE_ROOM_INFO,
            w => { w.WriteUInt16(room.Id); w.WriteUInt16(room.HostUid); });
        private static void Address(GamePacketWriter w, UpgradeBroadcastRoom room)
        { w.WriteUInt16(room.Id); w.WriteUInt16(room.HostUid); w.WriteByte(room.Town); w.WriteByte(room.Area); w.WriteInt16(room.X); w.WriteInt16(room.Y); }
        internal static byte[] List(IEnumerable<UpgradeBroadcastRoom> rooms) => Packet(NotiPacketTypeA21.UPGRADE_ROOM_LIST_INFO, w =>
        {
            var values = rooms.Take(ushort.MaxValue).ToArray(); w.WriteUInt16((ushort)values.Length);
            foreach (var room in values)
            { Address(w, room); w.WriteByte(room.State is 1 or 2 ? room.State : (byte)0); w.WriteInt32(room.Selected?.ItemId ?? -1); w.WriteByte(room.Selected?.Upgrade ?? 0); }
        });
        internal static byte[] Info(UpgradeBroadcastRoom room) => Packet(NotiPacketTypeA21.UPGRADE_ROOM_INFO, w =>
        {
            w.WriteUInt16(room.Id); w.WriteByte(0); w.WriteUInt16(room.HostUid); w.WriteDstr(room.Title);
            var viewers = room.Viewers.Select(s => s.Player.UserId).ToArray();
            w.WriteUInt16((ushort)viewers.Length); foreach (ushort uid in viewers) w.WriteUInt16(uid);
        });
        internal static byte[] Put(UpgradeBroadcastRoom room) => Packet(NotiPacketTypeA21.UPGRADE_ROOM_PUT_UP_ITEM, w =>
        { w.WriteUInt16(room.Id); w.WriteInt16(room.Slot); w.WriteInt32(room.Selected?.ItemId ?? -1); w.WriteByte(room.Selected?.Upgrade ?? 0); });
        // 0117D720 reads UPDATE_ITEM_LIST; its subtype 30 branch at 0117E778
        // creates the broadcast window's equipment preview (012333F0).
        // This is a projection of the Main item, never another inventory container.
        internal static byte[] EquipmentPreview(UpgradeBroadcastRoom room) => Packet(NotiPacketTypeA21.UPDATE_ITEM_LIST, w =>
        {
            w.WriteByte(30); w.WriteUInt16(1);
            ItemListProtocolWriter.WriteCommonEntry84(w, room.Slot, room.Selected);
        });
        internal static byte[] Result(UpgradeBroadcastRoom room, ItemUpgradeResult result) => Packet(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_ITEM_STATE, w =>
        {
            w.WriteUInt16(room.Id);
            w.WriteByte(result.UpgradeSucceeded ? (byte)0 : result.ResultCode == 3 ? (byte)2 : (byte)1);
            w.WriteInt32(result.TargetItemTemplateId); w.WriteByte(result.NewLevel);
        });
        // START/CANCEL readers operate on the current room and consume no payload.
        internal static byte[] Started() => Packet(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_START, _ => { });
        internal static byte[] Cancelled() => Packet(NotiPacketTypeA21.UPGRADE_ROOM_UPGRADE_CANCEL, _ => { });
        internal static byte[] Joined(ushort uid) => Packet(NotiPacketTypeA21.UPGRADE_JOIN_SPECTATOR_USER_INFO, w => w.WriteUInt16(uid));
        internal static byte[] Left(ushort uid) => Packet(NotiPacketTypeA21.LEAVE_SPECTATOR_USER_INFO, w => w.WriteUInt16(uid));
        internal static byte[] Title(byte[] text) => Packet(NotiPacketTypeA21.UPGRADE_ROOM_MASTER_MESSAGE, w => w.WriteDstr(text));
        // 012327D0/01231D50: totals and one actual donor event, not equipment details.
        internal static byte[] Donations(UpgradeBroadcastRoom room) => Packet(NotiPacketTypeA21.UPGRADE_ROOM_GIVE_ITEM_INFO,
            w =>
            {
                // 012365FD renders the first entry as gold, then 012366CB advances
                // unconditionally and 012366F1 asserts that the cube entry exists.
                // The native constructor 01232E8F..AA seeds exactly this order.
                w.WriteUInt16(room.Id); w.WriteByte(2);
                w.WriteInt32(0); w.WriteInt32(room.Donations.GetValueOrDefault(0));
                w.WriteInt32(3037); w.WriteInt32(room.Donations.GetValueOrDefault(3037));
            });
        internal static byte[] Donor(ushort roomId, byte[] name, int item, int count) => Packet(NotiPacketTypeA21.GIVE_USER_INFO,
            w => { w.WriteUInt16(roomId); w.WriteDstr(name); w.WriteInt32(item); w.WriteInt32(count); });
    }
}

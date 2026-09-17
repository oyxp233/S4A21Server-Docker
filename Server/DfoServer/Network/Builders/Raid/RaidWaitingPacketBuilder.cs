using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders.Raid;

public static class RaidWaitingPacketBuilder
{
    public static byte[] Build(IReadOnlyList<RaidWaitingPlayerSnapshot> players)
    {
        ArgumentNullException.ThrowIfNull(players, "players");
        GamePacketWriter gamePacketWriter = new GamePacketWriter();
        gamePacketWriter.WriteUInt32(checked((uint)players.Count));
        foreach (RaidWaitingPlayerSnapshot player in players)
        {
            ArgumentNullException.ThrowIfNull(player, "player");
            byte[] nameBytes = player.NameBytes;
            if (nameBytes == null || nameBytes.Length >= 30 || Array.IndexOf(nameBytes, (byte)0) >= 0)
            {
                throw new ArgumentException("Waiting name must fit a terminated 30-byte field.", "players");
            }
            gamePacketWriter.WriteUInt16(player.UserId);
            gamePacketWriter.WriteByte(player.ChannelId);
            gamePacketWriter.WriteByte(player.ServerIndex);
            gamePacketWriter.WriteUInt16(player.Level);
            gamePacketWriter.WriteByte(player.GrowType);
            gamePacketWriter.WriteByte(player.Job);
            gamePacketWriter.WriteByte(0);
            gamePacketWriter.WriteBytes(nameBytes);
            gamePacketWriter.WriteZeroBytes(30 - nameBytes.Length);
        }
        return GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.RAID_OTHER_CHANNEL_WAITING_LIST, gamePacketWriter.ToArray());
    }
}

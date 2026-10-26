using System;
using System.Collections.Generic;
using DfoServer.Game.Guilds;

namespace DfoServer.Network.Builders.Guilds
{
    internal static class GuildContentPacketBuilder
    {
        internal static IReadOnlyList<byte[]> Buffs(IReadOnlyList<GuildContent> contents, long now)
        {
            // Current client 00D08EF0 consumes count + (buff ID, state1..3).
            // Captured permanent guild buff is (169,1,0,0); both 167 and 169
            // use identical expire_time [0,0] definitions in variousbufflist.etc.
            // Server durable expiry owns removal. Clear only our two IDs before
            // replay, because these PVF effects allow two overlapping instances.
            var packets = new List<byte[]> { GamePacketEnvelopeBuilder.Build(0,
                (ushort)NotiPacketTypeA21.CHARACTER_DEL_BUFF,
                SpecialDungeonNotificationBuilder.BuildCharacterRemoveBuff(new[] {167,169})) };
            foreach (var content in contents)
                if (content.IsActive(now) && ((content.Type == 3 && content.Value == 167)
                    || (content.Type == 5 && content.Value == 169)))
                    packets.Add(GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.CHARACTER_ADD_BUFF,
                        SpecialDungeonNotificationBuilder.BuildCharacterAddBuff(content.Value,1,0,0)));
            return packets;
        }
    }
}

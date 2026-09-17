using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;

namespace DfoServer.Game.Guilds
{
    internal static class GuildIdentityProjection
    {
        internal static void Apply(GuildRecord guild, UserInfoMinimumTailSnapshot tail)
        {
            if (tail == null) throw new ArgumentNullException(nameof(tail));
            tail.GuildId = checked((uint)(guild?.Id ?? 0));
            tail.GuildNameBytes = guild == null ? Array.Empty<byte>() : ClientTextEncoding.GetBytes(guild.Name);
            // Guild progression is not enabled; new guilds start at level 1.
            tail.GuildLevel = guild == null ? (byte)0 : (byte)1;
        }
    }
}

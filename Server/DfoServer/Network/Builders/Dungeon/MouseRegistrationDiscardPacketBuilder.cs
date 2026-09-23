using System;

namespace DfoServer.Network.Builders
{
    internal static class MouseRegistrationDiscardPacketBuilder
    {
        internal static byte[] BuildPacket()
        {
            // A21's MOUSE_REGIST_DISCARD notification has no payload.  It
            // clears the client-side registered click action left by scripted
            // dungeon NPC interactions.
            return GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.MOUSE_REGIST_DISCARD,
                Array.Empty<byte>());
        }
    }
}

using System;

namespace DfoServer.Network.Parsers.ExpertJob
{
    internal sealed class EnterExpertJobStoreRequest
    {
        internal ushort OwnerUserId { get; private set; }

        internal static bool TryParse(byte[] body, out EnterExpertJobStoreRequest request)
        {
            request = null;
            if (body == null || body.Length != sizeof(ushort))
                return false;

            request = new EnterExpertJobStoreRequest
            {
                OwnerUserId = BitConverter.ToUInt16(body, 0),
            };
            return request.OwnerUserId != 0;
        }
    }
}

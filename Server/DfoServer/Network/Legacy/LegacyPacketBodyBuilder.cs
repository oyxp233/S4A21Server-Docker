namespace DfoServer.Network.Legacy
{
    public static class LegacyPacketBodyBuilder
    {
public static byte[] BuildVerifyPvpLagResponse(byte[] requestBody)
        {
            var resp = new byte[11];
            resp[0] = 0x01; 
            if (requestBody != null && requestBody.Length >= 4)
                System.Buffer.BlockCopy(requestBody, 0, resp, 1, 4); 
            return resp;
        }
    }
}
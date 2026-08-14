using System;

namespace DfoServer.Network.Parsers.Party
{
    // SET_PARTY_INFO (0x000C) 请求。
    // ⚠️ 86jp 实测 body 为定长 7 字节, 例: 00 XX 04 YY 00 05 00 (XX=用户在创建对话框的选择, 01~06)。
    //    与 df_game_r 早期版不同: titleIndex==0 **不**携带 int32 队名串。故这里按定长宽松解析, 不因布局失败,
    //    保证"创建队伍"能建队 + 回 PARTY_INFO。字段精确语义待真机进一步确认(byte[1]/byte[3] 随选择变)。
    public sealed class SetPartyInfoRequest
    {
        public byte TitleIndex { get; set; }
        public byte[] Title { get; set; } = Array.Empty<byte>();
        public byte UserMax { get; set; }
        public ushort DungIndex { get; set; }
        public byte DungDiffi { get; set; }
        public byte[] Raw { get; set; } = Array.Empty<byte>();

        public static bool TryParse(byte[] body, out SetPartyInfoRequest req)
        {
            req = new SetPartyInfoRequest();
            if (body == null || body.Length < 1)
                return false;

            req.Raw = body;
            req.TitleIndex = body[0];
            // 定长字段(容错读, 缺就默认): [titleIndex][userMax][u16 dungIndex][dungDiffi][尾...]
            if (body.Length > 1) req.UserMax = body[1];
            if (body.Length >= 4) req.DungIndex = BitConverter.ToUInt16(body, 2);
            if (body.Length > 4) req.DungDiffi = body[4];
            return true;
        }
    }
}

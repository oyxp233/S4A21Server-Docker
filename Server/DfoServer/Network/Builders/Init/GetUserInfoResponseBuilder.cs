namespace DfoServer.Network.Builders
{
    // GET_USERINFO 应答里的服务器配置字段(pkt0 头部 + pkt2 佣兵信息头), 存于 get_userinfo_template 表。
    // 部分字段语义未完全逆向, 列名保守。
    public sealed class GetUserInfoTemplate
    {
        public byte Pkt0RoutingByte7 { get; set; }
        public ushort GateOrCount1 { get; set; }
        public ushort GateOrCount2 { get; set; }
        public byte FlagOrManage { get; set; }
        public int KeyOrPoint { get; set; }
        public ushort Unknown16 { get; set; }
        public int Unknown32 { get; set; }

        public int SeedCharacterId { get; set; } = 1000;

        public byte Pkt2ResultCode { get; set; }
        public int Pkt2CharacterKey { get; set; }
        public byte Pkt2SlotFlag1 { get; set; }
        public byte Pkt2SlotFlag2 { get; set; }
        public byte Pkt2StateFlag { get; set; }
        public byte Pkt2Flag3 { get; set; }
        public ushort Pkt2Reserved { get; set; }
    }
}

using DfoServer.Game.Accounts;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class HonorLevelPacketBuilder
    {
        public static byte[] BuildInfoBody(HonorLevelSummary summary)
        {
            var honorLevel = summary != null ? (uint)summary.HonorLevel : 0u;
            var honorExp = summary != null ? summary.HonorExp : 0u;
            var writer = new GamePacketWriter();
            // 86JP HONOR_LEVEL_INFO 体为 8 字节：u32 荣誉等级 + u32 荣誉经验。
            // 客户端用第一个字段直接显示 Lv，不能写经验。
            writer.WriteUInt32(honorLevel);
            writer.WriteUInt32(honorExp);
            return writer.ToArray();
        }
    }
}

using DfoServer.Infrastructure;
using System;
using System.Linq;

namespace DfoServer.Network.Builders
{
    internal static class LevelUpRewardBroadcastBuilder
    {
        // 当前86客户端 VA 0x011803E0 的子类型6进入 0x01180BD0。
        // 只读取一个角色名DSTR；85由0x01180C00固定，500张由dstr.dat的48012固定。
        // 现用DNF.exe（1f8244d3…）运行态已核对，不把90版地址当作86版协议证据。
        internal const byte NativeLevel = 85;
        internal const int NativeItemId = 3330;
        internal const int NativeItemCount = 500;

        internal static bool TryBuild(byte[] characterName, out byte[] body)
        {
            body = Array.Empty<byte>();
            // VA 0x0274FC10读取字节长度，只有0 < length < 0x100才消费名字。
            if (characterName == null || characterName.Length == 0 || characterName.Length >= 0x100
                || !ClientTextEncoding.TryGetStringStrict(characterName, out var name)
                || string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
                return false;

            var writer = new GamePacketWriter();
            writer.WriteByte(6);
            writer.WriteDstr(characterName);
            body = writer.ToArray();
            return true;
        }
    }
}

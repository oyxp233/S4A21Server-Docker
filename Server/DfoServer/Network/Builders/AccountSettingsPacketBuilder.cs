using DfoServer.Game.Settings;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    /// 构造登录阶段账号级游戏选项通知包。
    /// 登录只下发账号级热键前缀用于连发运行态，完整键位布局在选择角色初始化阶段下发。
    public static class AccountSettingsPacketBuilder
    {
        public static IReadOnlyList<byte[]> BuildLoginAccountSettings(AccountSettings settings)
        {
            var main = settings?.MainGameOption ?? AccountSettings.DefaultMainGameOption;
            var quick0 = settings?.QuickchatBank0 ?? Array.Empty<byte>();
            var quick1 = settings?.QuickchatBank1 ?? Array.Empty<byte>();
            var accountHotkeys = AccountSettings.ExtractAccountScopedHotkeySlots(
                settings?.HotkeySlots ?? AccountSettings.DefaultHotkeySlots);
            var keyType = settings?.HotkeyKeyType ?? 0;

            return new[]
            {
                GamePacketEnvelopeBuilder.Build(0x00, 0x00AD, BuildGameOptionBody(main, quick0, quick1)),
                GamePacketEnvelopeBuilder.Build(0x00, 0x01C7, BuildHotkeyOptionBody((byte)keyType, accountHotkeys)),
            };
        }

        // A21 进入选角前只发送账号当前的 00AD。新号快聊为空，已有号保留两份快聊库。
        // FullAvatar(idx55) 必须开启，否则客户端会隐藏完整身体外观。
        public static byte[] BuildSelectScreenGameOption(
            AccountSettings settings,
            out byte[] persistedMain)
        {
            var source = settings?.MainGameOption
                         ?? AccountSettings.DefaultMainGameOption
                         ?? Array.Empty<byte>();
            var main = new byte[source.Length];
            Buffer.BlockCopy(source, 0, main, 0, source.Length);
            persistedMain = EnsureFullAvatarVisible(main) ? main : null;

            return BuildGameOptionBody(
                main,
                settings?.QuickchatBank0 ?? Array.Empty<byte>(),
                settings?.QuickchatBank1 ?? Array.Empty<byte>());
        }

        internal static bool EnsureFullAvatarVisible(byte[] main)
        {
            var offset = AccountSettings.FullAvatarOptionIndex * 2;
            if (main == null || main.Length < offset + 2)
                return false;
            if (main[offset] == 1 && main[offset + 1] == 0)
                return false;

            main[offset] = 1;
            main[offset + 1] = 0;
            return true;
        }

        public static byte[] BuildGameOptionBody(byte[] main, byte[] quick0, byte[] quick1)
        {
            var writer = new GamePacketWriter();
            WriteLengthPrefixed(writer, main);
            WriteLengthPrefixed(writer, quick0);
            WriteLengthPrefixed(writer, quick1);
            return writer.ToArray();
        }

        public static byte[] BuildHotkeyOptionBody(byte keyType, byte[] hotkeys)
        {
            hotkeys = hotkeys ?? Array.Empty<byte>();
            var body = new byte[1 + 4 + hotkeys.Length];
            body[0] = keyType;
            Buffer.BlockCopy(BitConverter.GetBytes(hotkeys.Length), 0, body, 1, 4);
            if (hotkeys.Length > 0)
                Buffer.BlockCopy(hotkeys, 0, body, 5, hotkeys.Length);
            return body;
        }

        public static byte[] BuildHotkeyOptionBody(byte keyType, IReadOnlyList<ushort> slots)
        {
            var slotCount = slots?.Count ?? 0;
            var hotkeys = new byte[slotCount * 2];
            for (var i = 0; i < slotCount; i++)
                Buffer.BlockCopy(BitConverter.GetBytes(slots[i]), 0, hotkeys, i * 2, 2);
            return BuildHotkeyOptionBody(keyType, hotkeys);
        }

        private static void WriteLengthPrefixed(GamePacketWriter writer, byte[] body)
        {
            body = body ?? Array.Empty<byte>();
            writer.WriteInt32(body.Length);
            writer.WriteBytes(body);
        }
    }
}

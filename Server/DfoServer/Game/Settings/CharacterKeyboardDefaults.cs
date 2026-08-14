using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;

namespace DfoServer.Game.Settings
{
    public static class CharacterKeyboardDefaults
    {
        private const byte CreatorMageJob = 10;
        private const ushort UnassignedKey = 0x86;

        private static readonly Lazy<byte[]> CreatorHotkeys =
            new Lazy<byte[]>(BuildCreatorHotkeySlots);

        public static byte[] BuildHotkeySlots(byte job)
            => Clone(IsCreatorMage(job) ? CreatorHotkeys.Value : AccountSettings.DefaultHotkeySlots);

        public static bool LooksLikeNormalDefaultHotkeySlots(byte[] hotkeys)
        {
            if (hotkeys == null || hotkeys.Length != AccountSettings.DefaultHotkeySlots.Length)
                return false;

            for (var i = AccountSettings.AccountScopedHotkeySlotCount * 2; i < hotkeys.Length; i++)
            {
                if (hotkeys[i] != AccountSettings.DefaultHotkeySlots[i])
                    return false;
            }
            return true;
        }

        public static bool IsCreatorMage(byte job)
            => job == CreatorMageJob;

        private static byte[] BuildCreatorHotkeySlots()
        {
            try
            {
                var values = ParseDefaultKeys(PvfArchiveAccessor.ReadText("clientonly/hotkeysystemforcreator.co"));
                if (values.Count == 0)
                    return Clone(AccountSettings.DefaultHotkeySlots);

                var headerSlots = 4;
                var result = new byte[(headerSlots + values.Count) * 2];
                Buffer.BlockCopy(AccountSettings.DefaultHotkeySlots, 0, result, 0, headerSlots * 2);
                for (var i = 0; i < values.Count; i++)
                    Buffer.BlockCopy(BitConverter.GetBytes(values[i]), 0, result, (headerSlots + i) * 2, 2);
                return result;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CharacterKeyboardDefaults] creator hotkey parse failed: {ex.Message}");
                return Clone(AccountSettings.DefaultHotkeySlots);
            }
        }

        private static List<ushort> ParseDefaultKeys(string text)
        {
            var result = new List<ushort>();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            foreach (Match keyBlock in Regex.Matches(text, @"\[key\]\s*`[^`]*`\s+-?\d+\s+`[^`]*`\s+`[^`]*`\s+(-?\d+)", RegexOptions.IgnoreCase))
            {
                if (!int.TryParse(keyBlock.Groups[1].Value, out var value))
                    continue;
                result.Add(value < 0 ? UnassignedKey : (ushort)Math.Min(ushort.MaxValue, value));
            }

            return result;
        }

        private static byte[] Clone(byte[] source)
        {
            if (source == null)
                return Array.Empty<byte>();
            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }
    }
}

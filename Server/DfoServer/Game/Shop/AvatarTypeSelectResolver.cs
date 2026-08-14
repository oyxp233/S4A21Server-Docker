using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.Shop
{
    // 限时时装(avatar)的时长/价格档位, 取自时装 .equ 的 [avatar type select]:
    //   每档 7 个 token: 时长(0=无限) ? ? 点券价 ? ? ?
    //   例: 7 0 0 350 0 0 0  30 0 0 770 0 0 0  0 0 0 2310 0 0 0
    //       -> 7天/350, 30天/770, 无限/2310
    // cerashop avatar 段的第3字段(CeraShopProductEntry.Count) = 此处的 1-based 档位。
    public sealed class AvatarDurationOption
    {
        public int DurationDays { get; set; } // 0 = 永久
        public int CeraPrice { get; set; }
    }

    public static class AvatarTypeSelectResolver
    {
        private const string Tag = "[avatar type select]";
        private const string EndTag = "[/avatar type select]";

        private static readonly ConcurrentDictionary<int, List<AvatarDurationOption>> Cache =
            new ConcurrentDictionary<int, List<AvatarDurationOption>>();

        private static readonly Lazy<LstFile> EquipmentList =
            new Lazy<LstFile>(() => LstFile.Parse(PvfArchiveAccessor.ReadText("equipment/equipment.lst")));

        // index1Based = cerashop avatar 段的第3字段; 返回该档的时长(天)与点券价。
        public static bool TryGetOption(int itemTemplateId, int index1Based, out int durationDays, out int ceraPrice)
        {
            durationDays = 0;
            ceraPrice = 0;
            var options = Cache.GetOrAdd(itemTemplateId, ResolveFromPvf);
            if (options == null || index1Based < 1 || index1Based > options.Count)
                return false;
            var opt = options[index1Based - 1];
            durationDays = opt.DurationDays;
            ceraPrice = opt.CeraPrice;
            return true;
        }

        public static bool TryGetOptionByPrice(int itemTemplateId, int ceraPrice, out int durationDays)
        {
            durationDays = 0;
            var options = Cache.GetOrAdd(itemTemplateId, ResolveFromPvf);
            if (options == null)
                return false;

            foreach (var opt in options)
            {
                if (opt.CeraPrice == ceraPrice)
                {
                    durationDays = opt.DurationDays;
                    return true;
                }
            }

            return false;
        }

        private static List<AvatarDurationOption> ResolveFromPvf(int itemTemplateId)
        {
            try
            {
                var entry = EquipmentList.Value.GetById(itemTemplateId);
                if (entry == null)
                    return null;
                var text = PvfArchiveAccessor.ReadText(Path.Combine("equipment", entry.FilePath));
                return Parse(text);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[AvatarTypeSelect] item {itemTemplateId} PVF read failed: {ex.Message}");
                return null;
            }
        }

        private static List<AvatarDurationOption> Parse(string text)
        {
            var result = new List<AvatarDurationOption>();
            if (string.IsNullOrEmpty(text))
                return result;

            var start = text.IndexOf(Tag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return result;
            start += Tag.Length;
            var end = text.IndexOf(EndTag, start, StringComparison.OrdinalIgnoreCase);
            var section = end > start ? text.Substring(start, end - start) : text.Substring(start);

            var nums = new List<int>();
            foreach (var tok in section.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(tok, out var v))
                    nums.Add(v);
                else
                    break; // 段内应全为数字, 遇到非数字停止
            }

            for (var i = 0; i + 6 < nums.Count; i += 7)
            {
                result.Add(new AvatarDurationOption
                {
                    DurationDays = nums[i],
                    CeraPrice = nums[i + 3],
                });
            }
            return result;
        }
    }
}

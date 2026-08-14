using DfoServer.GameWorld;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    public sealed class AvatarPackageChoice
    {
        public int ItemTemplateId { get; set; }

        public byte OptionValue { get; set; }
    }

    public sealed class AvatarPackageOpenRequest
    {
        public short SlotIndex { get; set; }

        public List<AvatarPackageChoice> Choices { get; } = new List<AvatarPackageChoice>();

        public static bool TryParse(byte[] body, out AvatarPackageOpenRequest request)
        {
            request = null;
            if (body == null || body.Length < 3)
                return false;

            var count = body[2];
            if (count == 0 || count > 32)
                return false;

            var expectedLength = 3 + count * 5;
            if (body.Length < expectedLength)
                return false;

            var parsed = new AvatarPackageOpenRequest
            {
                SlotIndex = BitConverter.ToInt16(body, 0),
            };

            var offset = 3;
            for (var i = 0; i < count; i++, offset += 5)
            {
                parsed.Choices.Add(new AvatarPackageChoice
                {
                    ItemTemplateId = BitConverter.ToInt32(body, offset),
                    OptionValue = body[offset + 4],
                });
            }

            request = parsed;
            return true;
        }
    }

    public sealed class AvatarPackageOpenResult
    {
        public short SlotIndex { get; set; }

        public int PackageItemTemplateId { get; set; }

        public int SourceRemainingStackCount { get; set; }

        public int AddedAvatarCount { get; set; }

        public int AddedMainItemCount { get; set; }

        public int AddedPetCount { get; set; }

        public List<PackageGrantedItem> GrantedItems { get; } = new List<PackageGrantedItem>();

        public List<(int itemTemplateId, int count)> ActivatedPremiums { get; } = new List<(int itemTemplateId, int count)>();
    }

    internal sealed class PackageRewardEntry
    {
        public int ItemTemplateId { get; set; }

        public int Count { get; set; }

        public bool IsAvatar { get; set; }

        public int ExpireTime { get; set; }
    }

    public sealed class PackageGrantedItem
    {
        public InventoryListType ListType { get; set; }

        public short SlotIndex { get; set; }

        public int ItemTemplateId { get; set; }

        public int DisplayCount { get; set; }

        public ushort Durability { get; set; }
    }

    internal sealed class AvatarPackageDefinition
    {
        public int PackageItemTemplateId { get; set; }

        public IReadOnlyList<PackageRewardEntry> Rewards { get; set; }

        public HashSet<int> AvatarItemIds { get; set; }
    }

    internal static class AvatarPackageDefinitionResolver
    {
        private static readonly Lazy<LstFile> StackableList =
            new Lazy<LstFile>(() => LstFile.Parse(PvfArchiveAccessor.ReadText("stackable/stackable.lst")));

        private static readonly Lazy<LstFile> EquipmentList =
            new Lazy<LstFile>(() => LstFile.Parse(PvfArchiveAccessor.ReadText("equipment/equipment.lst")));

        public static bool TryResolve(int packageItemTemplateId, out AvatarPackageDefinition definition)
        {
            definition = null;

            var stackableEntry = StackableList.Value.GetById(packageItemTemplateId);
            if (stackableEntry == null)
                return false;

            var stackableText = PvfArchiveAccessor.ReadText(Path.Combine("stackable", stackableEntry.FilePath));
            var stackable = StackableItemFile.Parse(stackableText);
            if (stackable.StackableType == null ||
                stackable.StackableType.IndexOf("[usable cera package]", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            var rewards = ParsePackageData(stackable.PackageData);
            if (rewards.Count == 0)
                return false;

            foreach (var reward in rewards)
            {
                reward.IsAvatar = IsAvatarEquipment(reward.ItemTemplateId);
                reward.ExpireTime = ResolveStackableExpirationUnixTime(reward.ItemTemplateId);
            }

            definition = new AvatarPackageDefinition
            {
                PackageItemTemplateId = packageItemTemplateId,
                Rewards = rewards,
                AvatarItemIds = new HashSet<int>(rewards.Where(x => x.IsAvatar).Select(x => x.ItemTemplateId)),
            };
            return definition.AvatarItemIds.Count > 0;
        }

        private static List<PackageRewardEntry> ParsePackageData(string packageData)
        {
            var rewards = new List<PackageRewardEntry>();
            if (string.IsNullOrWhiteSpace(packageData))
                return rewards;

            var tokens = packageData.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i + 1 < tokens.Length; i += 2)
            {
                if (!int.TryParse(tokens[i], out var itemTemplateId) || itemTemplateId <= 0)
                    continue;
                if (!int.TryParse(tokens[i + 1], out var count) || count <= 0)
                    count = 1;

                rewards.Add(new PackageRewardEntry
                {
                    ItemTemplateId = itemTemplateId,
                    Count = count,
                });
            }

            return rewards;
        }

        private static bool IsAvatarEquipment(int itemTemplateId)
        {
            var entry = EquipmentList.Value.GetById(itemTemplateId);
            if (entry == null)
                return false;

            var text = PvfArchiveAccessor.ReadText(Path.Combine("equipment", entry.FilePath));
            var equipment = EquipmentFile.Parse(text);
            return equipment.EquipmentType != null &&
                   equipment.EquipmentType.IndexOf("avatar", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int ResolveStackableExpirationUnixTime(int itemTemplateId)
        {
            var entry = StackableList.Value.GetById(itemTemplateId);
            if (entry == null)
                return 0;

            var text = PvfArchiveAccessor.ReadText(Path.Combine("stackable", entry.FilePath));
            var stackable = StackableItemFile.Parse(text);
            var expirationDate = stackable.GetStringValue("expiration date");
            if (!TryParseExpirationDate(expirationDate, out var expirationUnixTime))
                return 0;

            return expirationUnixTime;
        }

        private static bool TryParseExpirationDate(string value, out int expirationUnixTime)
        {
            expirationUnixTime = 0;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var normalized = value.Trim().Trim('`');
            var match = Regex.Match(normalized, @"\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}");
            if (!match.Success)
                return false;

            if (!DateTime.TryParseExact(
                    match.Value,
                    "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var localDateTime))
                return false;

            var offset = new DateTimeOffset(localDateTime, TimeZoneInfo.Local.GetUtcOffset(localDateTime));
            var unixTime = offset.ToUnixTimeSeconds();
            if (unixTime <= 0 || unixTime > int.MaxValue)
                return false;

            expirationUnixTime = (int)unixTime;
            return true;
        }
    }
}

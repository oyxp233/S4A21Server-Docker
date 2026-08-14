using DfoServer.GameWorld;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    public sealed class SelectablePackageOpenRequest
    {
        public short SlotIndex { get; set; }

        public short SelectionContext { get; set; }

        public int SelectedItemTemplateId { get; set; }

        public byte SelectionFlag { get; set; }

        public List<AvatarPackageChoice> AvatarChoices { get; } = new List<AvatarPackageChoice>();

        public bool HasAvatarChoices => AvatarChoices.Count > 0;

        public static bool TryParse(byte[] body, out SelectablePackageOpenRequest request)
        {
            request = null;
            if (body == null || body.Length < 9)
                return false;

            var slotIndex = BitConverter.ToInt16(body, 0);
            var selectionContext = BitConverter.ToInt16(body, 2);
            var selectedItemTemplateId = BitConverter.ToInt32(body, 4);
            if (selectedItemTemplateId <= 0)
                return false;

            request = new SelectablePackageOpenRequest
            {
                SlotIndex = slotIndex,
                SelectionContext = selectionContext,
                SelectedItemTemplateId = selectedItemTemplateId,
                SelectionFlag = body[8],
            };
            TryParseAvatarChoices(body, request);
            return true;
        }

        private static void TryParseAvatarChoices(byte[] body, SelectablePackageOpenRequest request)
        {
            for (var count = 1; count <= 32; count++)
            {
                var countOffset = 4 + count * 4;
                var expectedLength = countOffset + 1 + count * 5;
                if (expectedLength != body.Length || body[countOffset] != count)
                    continue;

                var selectedIds = new int[count];
                var idOffset = 4;
                for (var i = 0; i < count; i++, idOffset += 4)
                    selectedIds[i] = BitConverter.ToInt32(body, idOffset);

                var optionOffset = countOffset + 1;
                for (var i = 0; i < count; i++, optionOffset += 5)
                {
                    var itemTemplateId = BitConverter.ToInt32(body, optionOffset);
                    if (itemTemplateId <= 0 || itemTemplateId != selectedIds[i])
                    {
                        request.AvatarChoices.Clear();
                        return;
                    }

                    request.AvatarChoices.Add(new AvatarPackageChoice
                    {
                        ItemTemplateId = itemTemplateId,
                        OptionValue = body[optionOffset + 4],
                    });
                }

                return;
            }
        }
    }

    public sealed class SelectablePackageOpenResult
    {
        public short SlotIndex { get; set; }

        public int PackageItemTemplateId { get; set; }

        public int SourceRemainingStackCount { get; set; }

        public int RewardItemTemplateId { get; set; }

        public int AddedMainItemCount { get; set; }

        public int AddedAvatarCount { get; set; }

        public int AddedPetCount { get; set; }

        public List<PackageGrantedItem> GrantedItems { get; } = new List<PackageGrantedItem>();

        public List<(int itemTemplateId, int count)> ActivatedPremiums { get; } = new List<(int itemTemplateId, int count)>();
    }

    internal sealed class SelectablePackageDefinition
    {
        public int PackageItemTemplateId { get; set; }

        public IReadOnlyList<PackageRewardEntry> Rewards { get; set; }

        public bool TryGetReward(int itemTemplateId, out PackageRewardEntry reward)
        {
            foreach (var entry in Rewards)
            {
                if (entry.ItemTemplateId == itemTemplateId)
                {
                    reward = entry;
                    return true;
                }
            }

            reward = null;
            return false;
        }
    }

    internal static class SelectablePackageDefinitionResolver
    {
        private static readonly Lazy<LstFile> StackableList =
            new Lazy<LstFile>(() => LstFile.Parse(PvfArchiveAccessor.ReadText("stackable/stackable.lst")));

        private static readonly Lazy<LstFile> EquipmentList =
            new Lazy<LstFile>(() => LstFile.Parse(PvfArchiveAccessor.ReadText("equipment/equipment.lst")));

        public static bool TryResolve(int packageItemTemplateId, out SelectablePackageDefinition definition)
        {
            definition = null;

            var stackable = ResolveStackable(packageItemTemplateId);
            if (stackable == null)
                return false;

            var rewards = ParsePackageData(stackable.PackageData);
            if (rewards.Count == 0 && IsBoosterSelection(stackable))
                rewards = ParseBoosterSelectCategory(stackable);
            if (rewards.Count == 0)
                return false;

            foreach (var reward in rewards)
                reward.ExpireTime = ResolveItemExpirationUnixTime(reward.ItemTemplateId);

            definition = new SelectablePackageDefinition
            {
                PackageItemTemplateId = packageItemTemplateId,
                Rewards = rewards,
            };
            return true;
        }

        private static bool IsBoosterSelection(StackableItemFile stackable)
        {
            return stackable.StackableType != null &&
                   stackable.StackableType.IndexOf("[booster selection]", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static StackableItemFile ResolveStackable(int itemTemplateId)
        {
            var entry = StackableList.Value.GetById(itemTemplateId);
            if (entry == null)
                return null;

            var text = PvfArchiveAccessor.ReadText(Path.Combine("stackable", entry.FilePath));
            return StackableItemFile.Parse(text);
        }

        private static EquipmentFile ResolveEquipment(int itemTemplateId)
        {
            var entry = EquipmentList.Value.GetById(itemTemplateId);
            if (entry == null)
                return null;

            var text = PvfArchiveAccessor.ReadText(Path.Combine("equipment", entry.FilePath));
            return EquipmentFile.Parse(text);
        }

        public static bool IsAvatarEquipment(int itemTemplateId)
        {
            var equipment = ResolveEquipment(itemTemplateId);
            return equipment != null &&
                   equipment.EquipmentType != null &&
                   equipment.EquipmentType.IndexOf("avatar", StringComparison.OrdinalIgnoreCase) >= 0;
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

        private static List<PackageRewardEntry> ParseBoosterSelectCategory(StackableItemFile stackable)
        {
            var rewards = new List<PackageRewardEntry>();
            var category = stackable.Root?.GetChild("booster select category");
            if (category == null)
                return rewards;

            ParseBoosterSelectNode(category, stackable.Content, rewards);
            return rewards;
        }

        private static void ParseBoosterSelectNode(ScriptNode node, string content, List<PackageRewardEntry> rewards)
        {
            if (node == null)
                return;

            if (node.DataItems.Count > 0)
            {
                var data = "";
                foreach (var item in node.DataItems)
                    data += " " + item.GetContent(content).Trim();

                rewards.AddRange(ParsePackageData(data));
            }

            foreach (var child in node.Children)
                ParseBoosterSelectNode(child, content, rewards);
        }

        public static int ResolveItemExpirationUnixTime(int itemTemplateId)
        {
            var stackable = ResolveStackable(itemTemplateId);
            if (stackable != null && TryParseExpirationDate(stackable.GetStringValue("expiration date"), out var stackableExpire))
                return stackableExpire;

            var equipment = ResolveEquipment(itemTemplateId);
            if (equipment != null && TryParseExpirationDate(equipment.GetStringValue("expiration date"), out var equipmentExpire))
                return equipmentExpire;

            return 0;
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

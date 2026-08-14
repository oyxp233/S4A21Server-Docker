using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    public enum EquipmentEffectRuneStatus
    {
        NotApplicable,
        Applied,
        MissingSource,
        InvalidTarget,
        Locked,
    }

    public sealed class EquipmentEffectRuneUseRequest
    {
        public InventoryListType SourceListType { get; set; } = InventoryListType.Main;

        public short SourceSlotIndex { get; set; }

        public int SourceInstanceValue { get; set; }

        public int ExpectedSourceItemTemplateId { get; set; }

        public byte[] RawBody { get; set; } = Array.Empty<byte>();

        public bool HasExplicitTarget { get; set; }

        public InventoryListType TargetListType { get; set; } = InventoryListType.Main;

        public short TargetSlotIndex { get; set; }

        public int ExpectedTargetItemTemplateId { get; set; }

        public static bool TryParseAddEquipmentEffectBody(byte[] body, out EquipmentEffectRuneUseRequest request)
        {
            request = null;
            if (body == null || body.Length < 21)
                return false;

            var sourceSlot = ReadInt32Slot(body, 17);
            if (!IsPlausibleSlot(sourceSlot))
                sourceSlot = ReadInt32Slot(body, 8);
            if (!IsPlausibleSlot(sourceSlot))
                return false;

            var targetSlot = ReadInt32Slot(body, 13);
            if (!IsPlausibleSlot(targetSlot))
                return false;

            var targetListType = InventoryListType.Main;
            if (body.Length > 12)
            {
                var parsedListType = (InventoryListType)body[12];
                if (parsedListType == InventoryListType.Main
                    || parsedListType == InventoryListType.PersonalCargo
                    || parsedListType == InventoryListType.Equipment)
                    targetListType = parsedListType;
            }

            request = new EquipmentEffectRuneUseRequest
            {
                SourceListType = InventoryListType.Main,
                SourceSlotIndex = sourceSlot.Value,
                RawBody = body,
                HasExplicitTarget = true,
                TargetListType = targetListType,
                TargetSlotIndex = targetSlot.Value,
            };
            return true;
        }

        public static bool TryParseEffectId(string intData, out ushort effectId)
        {
            effectId = 0;
            if (string.IsNullOrWhiteSpace(intData))
                return false;

            var match = Regex.Match(intData, @"-?\d+");
            if (!match.Success)
                return false;

            if (!int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return false;

            if (parsed < 0 || parsed > ushort.MaxValue)
                return false;

            effectId = (ushort)parsed;
            return true;
        }

        private static short? ReadInt32Slot(byte[] body, int offset)
        {
            if (body == null || offset < 0 || offset + 4 > body.Length)
                return null;

            var value = BitConverter.ToInt32(body, offset);
            if (value < short.MinValue || value > short.MaxValue)
                return null;

            return (short)value;
        }

        private static bool IsPlausibleSlot(short? slotIndex)
        {
            return slotIndex.HasValue && slotIndex.Value >= 0 && slotIndex.Value <= 500;
        }
    }

    public sealed class EquipmentEffectRuneUseResult
    {
        public EquipmentEffectRuneStatus Status { get; set; } = EquipmentEffectRuneStatus.NotApplicable;

        public InventoryListType SourceListType { get; set; } = InventoryListType.Main;

        public short SourceSlotIndex { get; set; }

        public int SourceItemTemplateId { get; set; }

        public int SourceInstanceValue { get; set; }

        public int SourceRemainingStackCount { get; set; }

        public InventoryListType TargetListType { get; set; } = InventoryListType.Main;

        public short TargetSlotIndex { get; set; }

        public int TargetItemTemplateId { get; set; }

        public ushort AppliedEffectId { get; set; }

        public bool Handled => Status != EquipmentEffectRuneStatus.NotApplicable;

        public bool Success => Status == EquipmentEffectRuneStatus.Applied;
    }
}

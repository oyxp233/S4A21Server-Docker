using DfoServer.Game.ItemUpgrade;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    public sealed class ChronicleGrowthMaterialRequest
    {
        public short SlotIndex { get; set; }
        public int ItemTemplateId { get; set; }
    }

    public sealed class ChronicleGrowthCommand
    {
        public short TicketSlotIndex { get; set; }
        public int TicketItemTemplateId { get; set; }
        public short TargetSlotIndex { get; set; }
        public int TargetItemTemplateId { get; set; }
        public List<ChronicleGrowthMaterialRequest> Materials { get; } = new List<ChronicleGrowthMaterialRequest>();
    }

    public sealed class ChronicleGrowthConsumption
    {
        public InventoryListType ListType { get; set; }
        public short SlotIndex { get; set; }
        public int ItemTemplateId { get; set; }
        public int ConsumedCount { get; set; }
        public int RemainingCount { get; set; }
    }

    public sealed class ChronicleGrowthResult
    {
        public const byte ErrorInvalidRequest = 0x01;
        public const byte ErrorInvalidTarget = 0x04;
        public const byte ErrorRestricted = 0x07;
        public const byte ErrorInsufficientMaterial = 0x13;
        public const byte ErrorMaximumLevel = 0x1A;
        public const byte ErrorLocked = 0xD6;

        public ChronicleGrowthCommand Command { get; set; }
        public byte ErrorCode { get; set; }
        public bool GrowthSucceeded { get; set; }
        public int OldLevel { get; set; }
        public int NewLevel { get; set; }
        public int RequiredFragmentCount { get; set; }
        public int SuccessWeight { get; set; }
        public int ProbabilityRoll { get; set; }
        public List<ChronicleGrowthConsumption> Consumptions { get; } = new List<ChronicleGrowthConsumption>();

        public static ChronicleGrowthResult Error(ChronicleGrowthCommand command, byte errorCode)
            => new ChronicleGrowthResult { Command = command, ErrorCode = errorCode };
    }

    internal static class ChronicleGrowthCostCalculator
    {
        internal const int FragmentItemTemplateId = 3311;
        private const int FragmentMultiplier = 3;
        private const int MaximumCostGenuineGrade = 7;
        private static readonly double[] GenuineGradeFactors =
        {
            1.00, 1.10, 1.20, 1.35, 1.50, 2.00, 2.50, 3.00,
        };

        public static int ResolveCostGenuineGrade(int forgingLevel)
            => forgingLevel >= 0 && forgingLevel <= MaximumCostGenuineGrade ? forgingLevel : 0;

        public static int Calculate(int equipmentLevel, EquipmentType equipmentType, int reinforceLevel, int amplifyLevel, int genuineGrade)
        {
            var equipmentTypeFactor = equipmentType == EquipmentType.Weapon ? 1.20 : 1.0;
            var levelFactor = Interpolate(equipmentLevel,
                new Segment(1, 70, 1.0, 2.0),
                new Segment(70, 85, 2.0, 3.5),
                new Segment(85, 200, 3.5, 50.0));
            var reinforceFactor = Interpolate(reinforceLevel,
                new Segment(0, 3, 1.0, 1.2),
                new Segment(3, 6, 1.2, 1.5),
                new Segment(6, 10, 1.5, 2.0),
                new Segment(10, 15, 2.0, 10.0),
                new Segment(15, 20, 10.0, 40.0));
            var amplifyFactor = Interpolate(amplifyLevel,
                new Segment(0, 3, 1.0, 1.5),
                new Segment(3, 6, 1.5, 2.5),
                new Segment(6, 10, 2.5, 4.0),
                new Segment(10, 15, 4.0, 66.0),
                new Segment(15, 20, 66.0, 144.0));
            var genuineGradeFactor = genuineGrade >= 0 && genuineGrade < GenuineGradeFactors.Length
                ? GenuineGradeFactors[genuineGrade]
                : GenuineGradeFactors[0];

            var value = equipmentTypeFactor * levelFactor
                * (reinforceFactor * amplifyFactor + genuineGradeFactor - 1.0)
                * FragmentMultiplier;
            return Math.Max(1, (int)value);
        }

        private static double Interpolate(int value, params Segment[] segments)
        {
            if (segments == null || segments.Length == 0)
                return 1.0;

            foreach (var segment in segments)
            {
                if (value <= segment.Maximum)
                {
                    var clamped = Math.Max(segment.Minimum, value);
                    var span = segment.Maximum - segment.Minimum;
                    if (span <= 0)
                        return segment.MaximumFactor;
                    return segment.MinimumFactor
                        + (segment.MaximumFactor - segment.MinimumFactor) * (clamped - segment.Minimum) / span;
                }
            }
            return segments[segments.Length - 1].MaximumFactor;
        }

        private readonly struct Segment
        {
            public Segment(int minimum, int maximum, double minimumFactor, double maximumFactor)
            {
                Minimum = minimum;
                Maximum = maximum;
                MinimumFactor = minimumFactor;
                MaximumFactor = maximumFactor;
            }

            public int Minimum { get; }
            public int Maximum { get; }
            public double MinimumFactor { get; }
            public double MaximumFactor { get; }
        }
    }
}

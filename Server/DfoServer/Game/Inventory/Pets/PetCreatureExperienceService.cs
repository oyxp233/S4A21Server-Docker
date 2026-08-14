using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DfoServer.Game.Inventory
{
    internal static class PetCreatureExperienceService
    {
        private const int MaxCreatureLevel = 50;
        private static readonly Lazy<int[]> CreatureExpThresholds = new Lazy<int[]>(LoadCreatureExpThresholds);

        internal static PetCreatureExperienceUpdate ApplyDungeonClearExperience(
            InventoryService inventory,
            int consumedFatigue)
        {
            if (inventory == null || consumedFatigue <= 0)
                return PetCreatureExperienceUpdate.Noop(inventory?.CharacterId ?? 0);

            if (!PetInventoryAccessor.TryGetEquippedCreature(inventory, out _, out var detail))
                return PetCreatureExperienceUpdate.Noop(inventory.CharacterId);

            var creatureKey = detail.Uid;
            if (detail.Stomach <= 0)
                return PetCreatureExperienceUpdate.Noop(inventory.CharacterId, creatureKey);

            var beforeLevel = ClampLevel(detail.Level);
            var beforeExp = Math.Max(0, detail.Exp);
            if (beforeLevel >= MaxCreatureLevel)
                return new PetCreatureExperienceUpdate(
                    inventory.CharacterId,
                    creatureKey,
                    beforeLevel,
                    beforeLevel,
                    beforeExp,
                    beforeExp,
                    false);

            var gainedExperience = CalculateClearExperience(consumedFatigue);
            if (gainedExperience <= 0)
                return PetCreatureExperienceUpdate.Noop(inventory.CharacterId, creatureKey);

            var afterExp = AddSaturating(beforeExp, gainedExperience);
            var afterLevel = Math.Max(beforeLevel, GetCreatureLevelForExperience(afterExp));
            if (afterLevel > MaxCreatureLevel)
                afterLevel = MaxCreatureLevel;

            detail.Exp = afterExp;
            detail.Level = afterLevel;
            inventory.CreatureDetails.PutDirty(detail);

            var evolution = PetCreatureEvolutionResult.Noop;
            if (afterLevel > beforeLevel)
                evolution = PetCreatureEvolutionRuntimeService.TryEvolveEquippedPetCreature(
                    inventory,
                    creatureKey,
                    afterLevel);

            return new PetCreatureExperienceUpdate(
                inventory.CharacterId,
                creatureKey,
                beforeLevel,
                afterLevel,
                beforeExp,
                afterExp,
                afterLevel > beforeLevel || afterExp != beforeExp || evolution.Changed,
                evolution);
        }

        internal static int GetCreatureLevelForExperience(int experience)
        {
            var thresholds = CreatureExpThresholds.Value;
            var level = 1;
            for (var nextLevel = 2; nextLevel <= MaxCreatureLevel; nextLevel++)
            {
                var thresholdIndex = nextLevel - 2;
                if (thresholdIndex < 0 || thresholdIndex >= thresholds.Length)
                    break;
                if (thresholds[thresholdIndex] > experience)
                    break;

                level = nextLevel;
            }

            return ClampLevel(level);
        }

        private static int[] LoadCreatureExpThresholds()
        {
            foreach (var path in new[]
            {
                "creature/exptable.tbl",
                "Creature/ExpTable.tbl",
                "creature/ExpTable.tbl",
                "Creature/exptable.tbl",
            })
            {
                try
                {
                    var text = PvfArchiveAccessor.ReadText(path);
                    var values = ParseIntegers(text);
                    if (values.Count > 0)
                    {
                        var table = new int[Math.Min(values.Count, MaxCreatureLevel)];
                        for (var i = 0; i < table.Length; i++)
                            table[i] = values[i];

                        FileLogger.Log($"[PetCreatureExp] loaded {table.Length} thresholds from {path}");
                        return table;
                    }
                }
                catch
                {
                    // PVF 路径大小写不稳定，继续尝试下一种写法。
                }
            }

            FileLogger.Log("[PetCreatureExp] WARN: creature exp table missing; level-up disabled");
            var fallback = new int[MaxCreatureLevel];
            for (var i = 0; i < fallback.Length; i++)
                fallback[i] = int.MaxValue;
            return fallback;
        }

        private static List<int> ParseIntegers(string text)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            var token = string.Empty;
            for (var i = 0; i <= text.Length; i++)
            {
                var c = i < text.Length ? text[i] : ' ';
                if (char.IsDigit(c) || (c == '-' && token.Length == 0))
                {
                    token += c;
                    continue;
                }

                if (token.Length > 0)
                {
                    if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                        result.Add(value);
                    token = string.Empty;
                }
            }

            return result;
        }

        private static int CalculateClearExperience(int consumedFatigue)
            => Math.Max(0, consumedFatigue);

        private static int AddSaturating(int current, int add)
        {
            var value = (long)Math.Max(0, current) + Math.Max(0, add);
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private static int ClampLevel(int level)
            => Math.Max(1, Math.Min(MaxCreatureLevel, level));
    }

    internal readonly struct PetCreatureExperienceUpdate
    {
        internal PetCreatureExperienceUpdate(
            int characterId,
            int creatureKey,
            int beforeLevel,
            int afterLevel,
            int beforeExperience,
            int afterExperience,
            bool changed,
            PetCreatureEvolutionResult evolution = default(PetCreatureEvolutionResult))
        {
            CharacterId = characterId;
            CreatureKey = creatureKey;
            BeforeLevel = beforeLevel;
            AfterLevel = afterLevel;
            BeforeExperience = beforeExperience;
            AfterExperience = afterExperience;
            Changed = changed;
            Evolution = evolution;
        }

        public int CharacterId { get; }
        public int CreatureKey { get; }
        public int BeforeLevel { get; }
        public int AfterLevel { get; }
        public int BeforeExperience { get; }
        public int AfterExperience { get; }
        public bool Changed { get; }
        public PetCreatureEvolutionResult Evolution { get; }
        public int GainedExperience => Math.Max(0, AfterExperience - BeforeExperience);

        internal static PetCreatureExperienceUpdate Noop(int characterId, int creatureKey = 0)
            => new PetCreatureExperienceUpdate(characterId, creatureKey, 0, 0, 0, 0, false);
    }
}

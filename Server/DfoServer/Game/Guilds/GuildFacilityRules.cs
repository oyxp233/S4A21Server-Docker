using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Guilds
{
    internal readonly record struct GuildAgitStep(int Level, int Gold, int Contribution);
    internal readonly record struct GuildCargoStep(int Slots, int Gold, int Contribution);

    // Costs come from etc/(r)guild.etc. Packet bodies are not parsed here.
    internal static class GuildFacilityRules
    {
        internal static int ReadDonateGoldPerPoint()
            => ReadSingle("guild donate");

        internal static int ReadCargoSlotLimit()
            => ReadSingle("guild cargo size limit");

        internal static IReadOnlyList<GuildAgitStep> ReadAgitSteps()
        {
            var numbers = ReadNumbers("agit upgrade info");
            if (numbers.Count == 0 || numbers.Count % 4 != 0)
                throw new InvalidOperationException("PVF agit upgrade info is missing or not grouped by 4.");
            var steps = new List<GuildAgitStep>(numbers.Count / 4);
            for (int i = 0; i < numbers.Count; i += 4)
            {
                if (numbers[i] <= 0 || numbers[i + 1] <= 0 || numbers[i + 2] < 0)
                    throw new InvalidOperationException("PVF agit upgrade info has an invalid step.");
                steps.Add(new GuildAgitStep(numbers[i], numbers[i + 1], numbers[i + 2]));
            }
            return steps;
        }

        internal static IReadOnlyList<GuildCargoStep> ReadCargoSteps()
        {
            var numbers = ReadNumbers("guild storage upgrade info");
            if (numbers.Count == 0 || numbers.Count % 5 != 0)
                throw new InvalidOperationException("PVF guild storage upgrade info is missing or not grouped by 5.");
            var steps = new List<GuildCargoStep>(numbers.Count / 5);
            for (int i = 0; i < numbers.Count; i += 5)
            {
                if (numbers[i] <= 0 || numbers[i + 1] <= 0 || numbers[i + 2] < 0)
                    throw new InvalidOperationException("PVF guild storage upgrade info has an invalid step.");
                steps.Add(new GuildCargoStep(numbers[i], numbers[i + 1], numbers[i + 2]));
            }
            return steps;
        }

        private static int ReadSingle(string tag)
        {
            var numbers = ReadNumbers(tag);
            if (numbers.Count == 0 || numbers[0] <= 0)
                throw new InvalidOperationException($"PVF [{tag}] is missing or invalid.");
            return numbers[0];
        }

        private static List<int> ReadNumbers(string tag)
        {
            string text = PvfArchiveAccessor.ReadText("etc/(r)guild.etc");
            var match = Regex.Match(text, @"\[" + Regex.Escape(tag) + @"\](.*?)(?:\r?\n\s*\[|\z)",
                RegexOptions.CultureInvariant | RegexOptions.Singleline);
            if (!match.Success)
                throw new InvalidOperationException($"PVF [{tag}] is missing.");
            var numbers = new List<int>();
            foreach (Match number in Regex.Matches(match.Groups[1].Value, @"\d+"))
            {
                if (!int.TryParse(number.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                    throw new InvalidOperationException($"PVF [{tag}] contains an invalid number.");
                numbers.Add(value);
            }
            return numbers;
        }
    }
}

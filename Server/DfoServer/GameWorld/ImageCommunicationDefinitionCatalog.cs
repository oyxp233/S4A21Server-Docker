using PvfLib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace DfoServer.GameWorld
{
    internal readonly struct ImageCommunicationPosition
    {
        internal ImageCommunicationPosition(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        internal int X { get; }
        internal int Y { get; }
        internal int Z { get; }
    }

    internal sealed class ImageCommunicationNpcDefinition
    {
        internal ImageCommunicationNpcDefinition(
            int npcIndex,
            int positionX,
            int positionY,
            ushort requiredQuestId)
        {
            NpcIndex = npcIndex;
            PositionX = positionX;
            PositionY = positionY;
            RequiredQuestId = requiredQuestId;
        }

        internal int NpcIndex { get; }
        internal int PositionX { get; }
        internal int PositionY { get; }
        internal ushort RequiredQuestId { get; }
    }

    internal sealed class ImageCommunicationDefinition
    {
        internal static readonly ImageCommunicationDefinition Empty =
            new ImageCommunicationDefinition(
                0,
                0,
                default,
                default,
                Array.Empty<ImageCommunicationNpcDefinition>());

        internal ImageCommunicationDefinition(
            int chargeMilliseconds,
            int summonDurationMilliseconds,
            ImageCommunicationPosition summonPosition,
            ImageCommunicationPosition effectPosition,
            IReadOnlyList<ImageCommunicationNpcDefinition> npcs)
        {
            ChargeMilliseconds = chargeMilliseconds;
            SummonDurationMilliseconds = summonDurationMilliseconds;
            SummonPosition = summonPosition;
            EffectPosition = effectPosition;
            Npcs = npcs ?? Array.Empty<ImageCommunicationNpcDefinition>();
        }

        internal int ChargeMilliseconds { get; }
        internal int SummonDurationMilliseconds { get; }
        internal ImageCommunicationPosition SummonPosition { get; }
        internal ImageCommunicationPosition EffectPosition { get; }
        internal IReadOnlyList<ImageCommunicationNpcDefinition> Npcs { get; }
    }

    internal static class ImageCommunicationDefinitionCatalog
    {
        private const string ConfigPath = "etc/imagecommunication.etc";
        private static readonly Lazy<ImageCommunicationDefinition> Cached =
            new Lazy<ImageCommunicationDefinition>(Load);

        internal static ImageCommunicationDefinition Current => Cached.Value;

        internal static ImageCommunicationDefinition Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new FormatException($"{ConfigPath} is empty");

            var root = new ScriptParser().Parse(text);
            var section = root.GetChild("image communication");
            if (section == null)
                throw new FormatException($"{ConfigPath} is missing [image communication]");

            var entries = new List<ImageCommunicationNpcDefinition>();
            var questIds = new HashSet<ushort>();
            var npcIndexes = new HashSet<int>();
            foreach (var node in section.GetChildren("npc"))
            {
                var npcIndex = ReadInt(node, text, "npc index");
                var npcPosition = ReadInts(node, text, "npc pos", 2);
                var requiredQuest = ReadInt(node, text, "require quest");
                if (npcIndex <= 0
                    || requiredQuest <= 0
                    || requiredQuest > ushort.MaxValue)
                {
                    throw new FormatException(
                        $"{ConfigPath} contains an invalid [npc] entry");
                }

                var questId = (ushort)requiredQuest;
                if (!npcIndexes.Add(npcIndex))
                {
                    throw new FormatException(
                        $"{ConfigPath} contains duplicate NPC {npcIndex}");
                }
                if (!questIds.Add(questId))
                {
                    throw new FormatException(
                        $"{ConfigPath} contains duplicate quest {questId}");
                }

                entries.Add(new ImageCommunicationNpcDefinition(
                    npcIndex,
                    npcPosition[0],
                    npcPosition[1],
                    questId));
            }

            if (entries.Count == 0)
                throw new FormatException($"{ConfigPath} contains no NPC entries");

            var chargeMilliseconds = ReadInt(section, text, "charge");
            var summonDurationMilliseconds = ReadInt(
                section,
                text,
                "summon time");
            if (chargeMilliseconds < 0 || summonDurationMilliseconds < 0)
            {
                throw new FormatException(
                    $"{ConfigPath} contains a negative duration");
            }

            return new ImageCommunicationDefinition(
                chargeMilliseconds,
                summonDurationMilliseconds,
                ReadPosition(section, text, "summon pos"),
                ReadPosition(section, text, "effect pos"),
                new ReadOnlyCollection<ImageCommunicationNpcDefinition>(entries));
        }

        private static ImageCommunicationDefinition Load()
        {
            try
            {
                var definition = Parse(PvfArchiveAccessor.ReadText(ConfigPath));
                FileLogger.Log(
                    $"[ImageCommunication] loaded npcs={definition.Npcs.Count} "
                    + $"chargeMs={definition.ChargeMilliseconds} "
                    + $"summonMs={definition.SummonDurationMilliseconds}");
                return definition;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[ImageCommunication] failed to load {ConfigPath}: {ex.Message}");
                return ImageCommunicationDefinition.Empty;
            }
        }

        private static int ReadInt(
            ScriptNode node,
            string text,
            string tag)
        {
            return ReadInts(node, text, tag, 1)[0];
        }

        private static ImageCommunicationPosition ReadPosition(
            ScriptNode node,
            string text,
            string tag)
        {
            var values = ReadInts(node, text, tag, 3);
            return new ImageCommunicationPosition(
                values[0],
                values[1],
                values[2]);
        }

        private static List<int> ReadInts(
            ScriptNode node,
            string text,
            string tag,
            int expectedCount)
        {
            var result = new List<int>();
            var content = node?.GetChild(tag)?.GetFirstDataContent(text);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new FormatException(
                    $"{ConfigPath} is missing [{tag}]");
            }

            foreach (var token in content.Split(
                new[] { ' ', '\t', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(
                        token,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var value))
                {
                    throw new FormatException(
                        $"{ConfigPath} [{tag}] contains '{token}'");
                }
                result.Add(value);
            }

            if (result.Count != expectedCount)
            {
                throw new FormatException(
                    $"{ConfigPath} [{tag}] expected {expectedCount} values, "
                    + $"got {result.Count}");
            }

            return result;
        }
    }
}

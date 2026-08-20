using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    internal sealed class EpicPieceCatalogEntry
    {
        public int Index { get; set; }
        public int OutputEquipmentId { get; set; }
        public int EpicPieceId { get; set; }
    }

    internal sealed class EpicPieceRecipeEntry
    {
        public int ItemId { get; set; }
        public int Count { get; set; }
    }

    internal sealed class EpicPieceRecipe
    {
        public int OutputEquipmentId { get; set; }
        public int EpicPieceId { get; set; }
        public int EpicPieceCount { get; set; }
        public IReadOnlyList<EpicPieceRecipeEntry> Materials { get; set; } = Array.Empty<EpicPieceRecipeEntry>();
    }

    internal static class EpicPieceCatalogService
    {
        private const string EpicPieceInfoPath = "etc/epicpieceinfo.etc";
        private static readonly Lazy<CatalogData> Catalog =
            new Lazy<CatalogData>(LoadCatalog);

        internal static int Count => Catalog.Value.Entries.Count;

        internal static IReadOnlyList<EpicPieceCatalogEntry> Entries => Catalog.Value.Entries;

        internal static bool IsEpicPieceId(int itemId)
            => itemId > 0 && Catalog.Value.IndexByPieceId.ContainsKey(itemId);

        internal static bool TryGetIndexByPieceId(int itemId, out int index)
            => Catalog.Value.IndexByPieceId.TryGetValue(itemId, out index);

        internal static bool TryGetEntryByPieceId(int itemId, out EpicPieceCatalogEntry entry)
        {
            entry = null;
            if (!TryGetIndexByPieceId(itemId, out var index))
                return false;

            entry = Catalog.Value.Entries[index];
            return true;
        }

        internal static bool TryGetEntryByOutputId(int outputEquipmentId, out EpicPieceCatalogEntry entry)
            => Catalog.Value.EntryByOutputId.TryGetValue(outputEquipmentId, out entry);

        internal static bool TryGetRecipeByOutputId(int outputEquipmentId, out EpicPieceRecipe recipe)
            => Catalog.Value.RecipeByOutputId.TryGetValue(outputEquipmentId, out recipe);

        internal static void Warmup()
        {
            _ = Catalog.Value;
        }

        private static CatalogData LoadCatalog()
        {
            try
            {
                return Parse(PvfArchiveAccessor.ReadText(EpicPieceInfoPath));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[EpicPieceCatalog] load failed: {ex.Message}");
                return CatalogData.Empty;
            }
        }

        private static CatalogData Parse(string text)
        {
            var dropBlock = ExtractTaggedBlock(text, "equipment piece drop info");
            var pieceValues = ParseIntList(ExtractTaggedBlock(dropBlock, "piece list"));
            if (pieceValues.Count == 0 || pieceValues.Count % 2 != 0)
                return CatalogData.Empty;

            var entries = new List<EpicPieceCatalogEntry>();
            var indexByPieceId = new Dictionary<int, int>();
            var entryByOutputId = new Dictionary<int, EpicPieceCatalogEntry>();
            for (var offset = 0; offset + 1 < pieceValues.Count; offset += 2)
            {
                var outputId = pieceValues[offset];
                var pieceId = pieceValues[offset + 1];
                if (outputId <= 0 || pieceId <= 0)
                    continue;
                if (indexByPieceId.ContainsKey(pieceId)
                    || entryByOutputId.ContainsKey(outputId))
                    continue;

                var entry = new EpicPieceCatalogEntry
                {
                    Index = entries.Count,
                    OutputEquipmentId = outputId,
                    EpicPieceId = pieceId,
                };
                entries.Add(entry);
                indexByPieceId[pieceId] = entry.Index;
                entryByOutputId[outputId] = entry;
            }

            var recipeByOutputId = ParseRecipes(text, entryByOutputId);
            return new CatalogData(entries, indexByPieceId, entryByOutputId, recipeByOutputId);
        }

        private static Dictionary<int, EpicPieceRecipe> ParseRecipes(
            string text,
            IReadOnlyDictionary<int, EpicPieceCatalogEntry> entryByOutputId)
        {
            var result = new Dictionary<int, EpicPieceRecipe>();
            var recipeBlock = ExtractTaggedBlock(text, "equipment recipe info");
            if (string.IsNullOrWhiteSpace(recipeBlock))
                return result;

            var pattern = new Regex(
                @"\[info\](.*?)\[/info\]",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
            foreach (Match match in pattern.Matches(recipeBlock))
            {
                var info = match.Groups[1].Value;
                var outputValues = ParseIntList(ExtractTaggedBlock(info, "output"));
                var recipeValues = ParseIntList(ExtractTaggedBlock(info, "recipe"));
                if (outputValues.Count == 0 || recipeValues.Count < 2 || recipeValues.Count % 2 != 0)
                    continue;

                var outputId = outputValues[0];
                if (!entryByOutputId.TryGetValue(outputId, out var catalogEntry))
                    continue;

                var pieceId = recipeValues[0];
                var pieceCount = recipeValues[1];
                if (pieceId != catalogEntry.EpicPieceId || pieceCount <= 0)
                    continue;

                var materials = new List<EpicPieceRecipeEntry>();
                for (var offset = 2; offset + 1 < recipeValues.Count; offset += 2)
                {
                    var materialId = recipeValues[offset];
                    var materialCount = recipeValues[offset + 1];
                    if (materialId <= 0 || materialCount <= 0)
                        continue;

                    materials.Add(new EpicPieceRecipeEntry
                    {
                        ItemId = materialId,
                        Count = materialCount,
                    });
                }

                result[outputId] = new EpicPieceRecipe
                {
                    OutputEquipmentId = outputId,
                    EpicPieceId = pieceId,
                    EpicPieceCount = pieceCount,
                    Materials = materials,
                };
            }

            return result;
        }

        private static string ExtractTaggedBlock(string text, string tagName)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(tagName))
                return string.Empty;

            var open = "[" + tagName + "]";
            var close = "[/" + tagName + "]";
            var openIndex = text.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (openIndex < 0)
                return string.Empty;

            var start = openIndex + open.Length;
            var closeIndex = text.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
            return closeIndex < 0
                ? text.Substring(start)
                : text.Substring(start, closeIndex - start);
        }

        private static List<int> ParseIntList(string text)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            foreach (Match match in Regex.Matches(text, @"-?\d+"))
            {
                if (int.TryParse(match.Value, out var value))
                    result.Add(value);
            }
            return result;
        }

        private sealed class CatalogData
        {
            internal static readonly CatalogData Empty =
                new CatalogData(
                    new List<EpicPieceCatalogEntry>(),
                    new Dictionary<int, int>(),
                    new Dictionary<int, EpicPieceCatalogEntry>(),
                    new Dictionary<int, EpicPieceRecipe>());

            internal CatalogData(
                List<EpicPieceCatalogEntry> entries,
                Dictionary<int, int> indexByPieceId,
                Dictionary<int, EpicPieceCatalogEntry> entryByOutputId,
                Dictionary<int, EpicPieceRecipe> recipeByOutputId)
            {
                Entries = entries;
                IndexByPieceId = indexByPieceId;
                EntryByOutputId = entryByOutputId;
                RecipeByOutputId = recipeByOutputId;
            }

            internal List<EpicPieceCatalogEntry> Entries { get; }
            internal Dictionary<int, int> IndexByPieceId { get; }
            internal Dictionary<int, EpicPieceCatalogEntry> EntryByOutputId { get; }
            internal Dictionary<int, EpicPieceRecipe> RecipeByOutputId { get; }
        }
    }
}

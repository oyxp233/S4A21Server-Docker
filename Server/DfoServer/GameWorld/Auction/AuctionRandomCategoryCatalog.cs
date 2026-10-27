using PvfLib;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace DfoServer.GameWorld.Auction
{
    public sealed class AuctionRandomCategory
    {
        internal AuctionRandomCategory(int id, string group, string label, byte[] optionTypes)
        {
            CategoryId = id;
            Group = group;
            Label = label;
            OptionTypes = Array.AsReadOnly((byte[])optionTypes.Clone());
        }
        public int CategoryId { get; }
        public string Group { get; }
        public string Label { get; }
        public IReadOnlyList<byte> OptionTypes { get; }
    }

    // Resource mapping only: this class does not decide AND/OR or option visibility.
    public sealed class AuctionRandomCategoryCatalog
    {
        private readonly IReadOnlyDictionary<int, AuctionRandomCategory> _categories;
        private AuctionRandomCategoryCatalog(string hash, Dictionary<int, AuctionRandomCategory> categories)
        {
            SourcePvfSha256 = hash;
            _categories = new ReadOnlyDictionary<int, AuctionRandomCategory>(categories);
        }
        public string SourcePvfSha256 { get; }
        public IReadOnlyDictionary<int, AuctionRandomCategory> Categories => _categories;
        public bool TryGet(int categoryId, out AuctionRandomCategory category)
            => _categories.TryGetValue(categoryId, out category);

        public static AuctionRandomCategoryCatalog Load(Func<string, string> readText, string sourcePvfSha256)
        {
            if (readText == null) throw new ArgumentNullException(nameof(readText));
            return Parse(readText("etc/randomoption/auctionrandomcategory.etc"), sourcePvfSha256);
        }

        public static AuctionRandomCategoryCatalog Parse(string text, string sourcePvfSha256)
        {
            var hash = AuctionTemplateCatalog.ValidateFingerprint(sourcePvfSha256);
            var sections = Regex.Matches(text ?? "", @"\[random category\](?<body>.*?)\[/random category\]", RegexOptions.Singleline);
            if (sections.Count != 1) throw new InvalidDataException("Expected one auction random category section.");
            var tokens = ScriptValueTokenizer.Tokenize(sections[0].Groups["body"].Value);
            var categories = new Dictionary<int, AuctionRandomCategory>();
            for (var position = 0; position < tokens.Count;)
            {
                if (tokens.Count - position < 4) throw new InvalidDataException("Truncated random category header.");
                var id = ParseInt(tokens[position++]);
                var group = tokens[position++];
                var label = tokens[position++];
                var count = ParseInt(tokens[position++]);
                if (count < 0 || count > 255 || count > tokens.Count - position
                    || id < -1 || (id == -1 && count != 0) || (id >= 0 && count == 0))
                    throw new InvalidDataException("Invalid random category count or id.");
                var types = new byte[count];
                var seen = new HashSet<byte>();
                for (var i = 0; i < count; i++)
                {
                    var type = ParseInt(tokens[position++]);
                    if (type <= 0 || type > byte.MaxValue || !seen.Add((byte)type))
                        throw new InvalidDataException("Invalid or duplicate random option type.");
                    types[i] = (byte)type;
                }
                if (id == -1) continue;
                if (!categories.TryAdd(id, new AuctionRandomCategory(id, group, label, types)))
                    throw new InvalidDataException("Duplicate auction random category id.");
            }
            if (categories.Count == 0) throw new InvalidDataException("Empty auction random category catalog.");
            return new AuctionRandomCategoryCatalog(hash, categories);
        }

        private static int ParseInt(string token)
        {
            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new InvalidDataException("Invalid random category integer.");
            return value;
        }
    }
}

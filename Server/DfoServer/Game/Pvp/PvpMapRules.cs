using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using PvfLib;

namespace DfoServer.Game.Pvp
{
    internal sealed class PvpMapRules
    {
        private readonly HashSet<short> _selectable;
        private readonly byte[] _randomMaps;
        private static readonly Lazy<PvpMapRules> Rules = new Lazy<PvpMapRules>(
            () => Parse(PvfArchiveAccessor.ReadText("etc/pvpmapbasicdata.etc")));

        private PvpMapRules(IEnumerable<short> selectable)
        {
            _selectable = new HashSet<short>(selectable);
            _randomMaps = _selectable.Where(index => index > 0).OrderBy(index => index)
                .Select(index => checked((byte)index)).ToArray();
            if (!_selectable.Contains(0) || _randomMaps.Length == 0)
                throw new InvalidDataException("PvP map list has no random-map candidates.");
        }

        internal static PvpMapRules Current => Rules.Value;
        internal bool CanSelect(short index) => _selectable.Contains(index);

        internal byte SelectStartMap(short index)
        {
            if (!CanSelect(index))
                throw new ArgumentOutOfRangeException(nameof(index));
            return index == 0 ? _randomMaps[ServerRandom.Next(_randomMaps.Length)] : (byte)index;
        }

        internal static PvpMapRules Parse(string text)
        {
            // 29E77B0 loads [pvp map order] and availability flags; 13B4E00
            // preserves the zero-based order index on the wire. Index 0 is random.
            var root = new ScriptParser().Parse(text);
            var orderNode = root.GetChild("pvp map order");
            var order = orderNode?.DataItems.SelectMany(item => Regex.Matches(
                    item.GetContent(text) ?? string.Empty, @"\d+").Cast<Match>())
                .Select(match => int.Parse(match.Value)).ToArray() ?? Array.Empty<int>();
            if (order.Length < 2 || order.Length > byte.MaxValue)
                throw new InvalidDataException("Invalid PvP map order.");

            var flags = new Dictionary<int, bool>();
            foreach (Match row in Regex.Matches(text,
                @"\[pvp map basic data\]\s*([^\[]+)\[/pvp map basic data\]", RegexOptions.IgnoreCase))
            {
                var values = Regex.Matches(row.Groups[1].Value, @"-?\d+")
                    .Cast<Match>().Select(match => int.Parse(match.Value)).ToArray();
                if (values.Length != 7)
                    throw new InvalidDataException("Invalid PvP map availability row.");
                flags.Add(values[0], values[4] != 0);
            }
            return new PvpMapRules(Enumerable.Range(0, order.Length)
                .Where(index => flags.TryGetValue(order[index], out var enabled) && enabled)
                .Select(index => (short)index));
        }
    }
}

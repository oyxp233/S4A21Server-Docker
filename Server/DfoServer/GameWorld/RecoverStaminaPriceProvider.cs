using DfoServer.Infrastructure;
using System;

namespace DfoServer.GameWorld
{
    internal static class RecoverStaminaPriceProvider
    {
        private const string StartTag = "[stamina recovery cost]";
        private const string EndTag = "[/stamina recovery cost]";
        private static readonly object LoadLock = new object();
        private static int[] _basePrices;

        public static int GetBasePrice(byte level)
        {
            var prices = EnsureLoaded();
            if (prices.Length == 0)
                throw new InvalidOperationException("PVF stamina recovery cost table is empty.");

            var normalizedLevel = Math.Max(1, (int)level);
            var index = Math.Min(prices.Length, normalizedLevel) - 1;
            return Math.Max(0, prices[index]);
        }

        private static int[] EnsureLoaded()
        {
            if (_basePrices != null)
                return _basePrices;

            lock (LoadLock)
            {
                if (_basePrices != null)
                    return _basePrices;

                try
                {
                    var text = PvfArchiveAccessor.ReadText("etc/serverparameter.etc");
                    _basePrices = Parse(text);
                    FileLogger.Log($"[RecoverStaminaPriceProvider] Loaded {_basePrices.Length} PVF price entries");
                }
                catch (Exception ex)
                {
                    _basePrices = Array.Empty<int>();
                    FileLogger.Log($"[RecoverStaminaPriceProvider] Failed to load PVF price table: {ex.Message}");
                    throw;
                }

                return _basePrices;
            }
        }

        private static int[] Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<int>();

            var start = text.IndexOf(StartTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return Array.Empty<int>();

            start += StartTag.Length;
            var end = text.IndexOf(EndTag, start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                end = text.Length;

            var data = text.Substring(start, end - start);
            var tokens = data.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var prices = new int[tokens.Length];
            var count = 0;
            for (var i = 0; i < tokens.Length; i++)
            {
                int value;
                if (int.TryParse(tokens[i], out value))
                    prices[count++] = value;
            }

            if (count == prices.Length)
                return prices;

            var compact = new int[count];
            Array.Copy(prices, compact, count);
            return compact;
        }
    }
}

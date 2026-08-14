using PvfLib;
using System;
using System.Globalization;

namespace DfoServer.Game.Inventory
{
    internal readonly struct StackableExpirationPolicy
    {
        internal StackableExpirationPolicy(int absoluteExpirationUnixTime, int usablePeriodDays)
        {
            AbsoluteExpirationUnixTime = absoluteExpirationUnixTime;
            UsablePeriodDays = usablePeriodDays;
        }

        internal int AbsoluteExpirationUnixTime { get; }
        internal int UsablePeriodDays { get; }
    }

    internal static class StackableExpirationPolicyResolver
    {
        internal static bool TryResolve(
            StackableItemFile stackable,
            out StackableExpirationPolicy policy)
        {
            policy = default;
            if (stackable?.Root == null)
                return false;

            if (!StackablePvfValueReader.TryReadOptionalSingleValue(
                    stackable,
                    "usable period",
                    out var hasUsablePeriod,
                    out var usablePeriodValue))
            {
                return false;
            }

            var usablePeriodDays = 0;
            if (hasUsablePeriod
                && (!int.TryParse(usablePeriodValue, out usablePeriodDays)
                    || usablePeriodDays < 0))
            {
                return false;
            }

            if (!StackablePvfValueReader.TryReadOptionalSingleValue(
                    stackable,
                    "expiration date",
                    out var hasAbsoluteExpiration,
                    out var absoluteExpirationValue))
            {
                return false;
            }

            var absoluteExpiration = 0;
            if (hasAbsoluteExpiration
                && !PvfExpirationMetadata.TryParseUnixTime(
                    absoluteExpirationValue,
                    out absoluteExpiration))
            {
                return false;
            }

            policy = new StackableExpirationPolicy(absoluteExpiration, usablePeriodDays);
            return true;
        }
    }

    internal readonly struct EquipmentExpirationPolicy
    {
        internal EquipmentExpirationPolicy(int absoluteExpirationUnixTime, int usablePeriodDays)
        {
            AbsoluteExpirationUnixTime = absoluteExpirationUnixTime;
            UsablePeriodDays = usablePeriodDays;
        }

        internal int AbsoluteExpirationUnixTime { get; }
        internal int UsablePeriodDays { get; }
    }

    internal static class EquipmentExpirationPolicyResolver
    {
        internal static bool TryResolve(
            EquipmentFile equipment,
            out EquipmentExpirationPolicy policy)
        {
            policy = default;
            if (equipment?.Root == null)
                return false;

            var usablePeriodDays = 0;
            var usablePeriodValue = equipment.GetStringValue("usable period");
            if (!string.IsNullOrWhiteSpace(usablePeriodValue)
                && (!int.TryParse(
                        NormalizeValue(usablePeriodValue),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out usablePeriodDays)
                    || usablePeriodDays < 0))
            {
                return false;
            }

            var absoluteExpiration = 0;
            var absoluteExpirationValue = equipment.GetStringValue("expiration date");
            if (!string.IsNullOrWhiteSpace(absoluteExpirationValue)
                && !PvfExpirationMetadata.TryParseUnixTime(
                    absoluteExpirationValue,
                    out absoluteExpiration))
            {
                return false;
            }

            policy = new EquipmentExpirationPolicy(absoluteExpiration, usablePeriodDays);
            return true;
        }

        private static string NormalizeValue(string value)
        {
            return (value ?? string.Empty).Trim().Trim('`').Trim();
        }
    }

    internal static class PvfExpirationMetadata
    {
        private static readonly TimeSpan ServerUtcOffset = TimeSpan.FromHours(8);
        private static readonly string[] DateFormats =
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd",
        };

        internal static bool TryParseUnixTime(
            string rawValue,
            out int expirationUnixTime)
        {
            expirationUnixTime = 0;
            var normalized = (rawValue ?? string.Empty).Trim().Trim('`').Trim();
            if (normalized.Length == 0)
                return false;

            if (DateTime.TryParseExact(
                normalized,
                DateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localDateTime))
            {
                return TryConvertServerLocalTime(
                    localDateTime,
                    out expirationUnixTime);
            }

            if (!int.TryParse(
                    normalized,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var numericValue))
                return false;

            if (numericValue == 0)
                return true;

            if (numericValue >= 1_000_000_000)
            {
                expirationUnixTime = numericValue;
                return true;
            }

            return numericValue > 0
                && DateTime.TryParseExact(
                    numericValue.ToString(CultureInfo.InvariantCulture),
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var numericLocalDate)
                && TryConvertServerLocalTime(numericLocalDate, out expirationUnixTime);
        }

        internal static int AddDaysFromNow(int days)
        {
            var expire = DateTimeOffset.Now.ToUnixTimeSeconds() + (long)days * 86400L;
            return (int)Math.Min(int.MaxValue, Math.Max(0, expire));
        }

        private static bool TryConvertServerLocalTime(
            DateTime localDateTime,
            out int unixTime)
        {
            var unspecified = DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified);
            var seconds = new DateTimeOffset(unspecified, ServerUtcOffset).ToUnixTimeSeconds();
            if (seconds > 0 && seconds <= int.MaxValue)
            {
                unixTime = (int)seconds;
                return true;
            }

            unixTime = 0;
            return false;
        }
    }
}

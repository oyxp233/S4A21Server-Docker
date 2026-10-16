using System;
using DfoServer.Game.Accounts;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class BoosterGageBodyBuilder : IInitPacketBuilder
    {
        internal const int GaugeUnitsPerLuckPoint = 5;

        public ushort NotiType => (ushort)NotiPacketTypeA21.BOOSTER_GAGE;

        public bool TryBuild(
            SelectCharacterDataSnapshot snapshot,
            int occurrenceIndex,
            out byte[] body)
        {
            body = Build(
                snapshot?.InitializationSnapshot?.SeriaLuckValue ?? 0);
            return true;
        }

        internal static byte[] Build(int seriaLuckValue)
        {
            var normalized = SqliteAccountRepository.NormalizeSeriaLuckValue(
                seriaLuckValue);
            var gaugeValue = checked((byte)(
                normalized * GaugeUnitsPerLuckPoint));
            return new[]
            {
                gaugeValue,
                gaugeValue,
            };
        }
    }
}

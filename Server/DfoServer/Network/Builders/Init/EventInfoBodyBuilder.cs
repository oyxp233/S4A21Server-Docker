using System;
using DfoServer.Game.Events;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Builders
{
    public sealed class EventInfoBodyBuilder : IInitPacketBuilder
    {
        internal const ushort RaidChannelEventId = 181;

        private readonly GameEventRepository _repository;

        public EventInfoBodyBuilder()
            : this(GameDatabase.CreateDefault())
        {
        }

        internal EventInfoBodyBuilder(IGameDatabase database)
        {
            _repository = new GameEventRepository(
                database ?? throw new ArgumentNullException(nameof(database)));
        }

        public ushort NotiType => (ushort)NotiPacketTypeA21.EVENT_INFO;

        public bool TryBuild(
            SelectCharacterDataSnapshot snapshot,
            int occurrenceIndex,
            out byte[] body)
        {
            body = Build(_repository.LoadEventInfoSnapshot());
            return true;
        }

        internal static byte[] Build(GameEventInfoSnapshot snapshot)
        {
            var writer = new GamePacketWriter();
            var entries = snapshot?.Events ?? Array.Empty<GameEventInfoEntry>();
            var extraEntries = snapshot?.ExtraEntries
                ?? Array.Empty<GameEventExtraInfoEntry>();

            var entryCount = Math.Min(ushort.MaxValue, entries.Count);
            var hasRaidEvent = false;
            for (var index = 0; index < entryCount; index++)
                hasRaidEvent |= entries[index].EventId == RaidChannelEventId;
            if (!hasRaidEvent && entryCount == ushort.MaxValue)
                entryCount--;

            writer.WriteUInt16((ushort)(entryCount + (hasRaidEvent ? 0 : 1)));
            for (var index = 0; index < entryCount; index++)
            {
                var entry = entries[index];
                writer.WriteUInt16(entry.EventId);
                writer.WriteUInt32(entry.Unknown0);
                WriteDstr(writer, entry.StartNotice);
                WriteDstr(writer, entry.EndNotice);
                writer.WriteByte(entry.HasDetail ? (byte)1 : (byte)0);
                if (!entry.HasDetail)
                    continue;

                writer.WriteByte(entry.FlagA);
                writer.WriteByte(entry.FlagB);
                WriteDstr(writer, entry.Title);
                WriteDstr(writer, entry.ShortName);
                WriteDstr(writer, entry.ReservedOrIcon);
                writer.WriteUInt32(entry.StartUnixTime);
                writer.WriteUInt32(entry.EndUnixTime);
                WriteDstr(writer, entry.LinkKey);
                WriteDstr(writer, entry.Description);
                writer.WriteByte(entry.DetailEnabled ? (byte)1 : (byte)0);
            }

            if (!hasRaidEvent)
            {
                writer.WriteUInt16(RaidChannelEventId);
                writer.WriteUInt32(0);
                WriteDstr(writer, string.Empty);
                WriteDstr(writer, string.Empty);
                writer.WriteByte(0);
            }

            writer.WriteByte((byte)Math.Min(byte.MaxValue, extraEntries.Count));
            for (var index = 0; index < extraEntries.Count && index < byte.MaxValue; index++)
            {
                var extra = extraEntries[index];
                writer.WriteUInt16(extra.EventId);
                var parameters = extra.Parameters ?? Array.Empty<uint>();
                for (var parameterIndex = 0; parameterIndex < 12; parameterIndex++)
                {
                    writer.WriteUInt32(parameterIndex < parameters.Count
                        ? parameters[parameterIndex]
                        : 0);
                }
            }

            return writer.ToArray();
        }

        private static void WriteDstr(GamePacketWriter writer, string value)
        {
            writer.WriteDstr(ClientTextEncoding.GetBytes(value ?? string.Empty));
        }
    }
}

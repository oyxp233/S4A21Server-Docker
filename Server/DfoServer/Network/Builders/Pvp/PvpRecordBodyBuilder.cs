using System;
using DfoServer.Game.Pvp;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Builders.Pvp
{
    internal sealed class PvpRecordBodyBuilder : IInitPacketBuilder
    {
        private readonly SqlitePvpRecordRepository _records;

        internal PvpRecordBodyBuilder(IGameDatabase database)
        {
            _records = new SqlitePvpRecordRepository(database);
        }

        public ushort NotiType => (ushort)NotiPacketTypeA21.PVP_RECORD;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var record = snapshot?.CharacterRecord == null
                ? null : _records.Load(snapshot.CharacterRecord.CharacterId);
            body = record == null ? null : BuildBody(record);
            return body != null;
        }

        // A21 1165CB0: initialization and ordinary-room settlement use 51 bytes.
        // Total-match rooms require an additional three-member tail and must
        // not reuse this basic body.
        internal static byte[] BuildBody(PvpRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));
            var writer = new GamePacketWriter();
            writer.WriteInt32(record.Wins);
            writer.WriteInt32(record.Losses);
            writer.WriteInt32(record.Experience);
            writer.WriteInt32(record.ExperienceFloor);
            writer.WriteInt32(record.ExperienceCeiling);
            writer.WriteByte(record.Grade);
            writer.WriteByte(record.RatingGrade);
            writer.WriteInt32(-1); // synchronize progress without a separate grade-change animation
            writer.WriteInt32(record.RankPoint);
            writer.WriteInt32(record.PeakRankPoint);
            writer.WriteInt32(record.RankWarmupGames);
            writer.WriteByte(0); // grade-change status
            writer.WriteInt32(0); // unnamed 183A450 field; no supported source
            writer.WriteInt32(record.TotalMatchPoint);
            writer.WriteInt32(record.TotalMatchWarmupGames);
            return writer.ToArray();
        }
    }
}

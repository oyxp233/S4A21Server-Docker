using System;
using DfoServer.Game.Pvp;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Network.Builders.Pvp
{
    internal sealed class PvpTotalMatchTeamBodyBuilder : IInitPacketBuilder
    {
        private readonly SqlitePvpTotalMatchTeamRepository _teams;

        internal PvpTotalMatchTeamBodyBuilder(IGameDatabase database)
        {
            _teams = new SqlitePvpTotalMatchTeamRepository(database);
        }

        public ushort NotiType => (ushort)NotiPacketTypeA21.PVP_TOTAL_MATCH_TEAM_INFO;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var team = snapshot?.CharacterRecord == null ? null : _teams.Load(snapshot.CharacterRecord.AccountId);
            body = team == null ? null : BuildBody(team);
            return body != null;
        }

        // 1164630: Dstr + three u8 character-list indices.
        internal static byte[] BuildBody(PvpTotalMatchTeam team)
        {
            if (team == null)
                throw new ArgumentNullException(nameof(team));
            var writer = new GamePacketWriter();
            var name = ClientTextEncoding.GetBytes(team.Name);
            writer.WriteInt32(name.Length);
            writer.WriteBytes(name);
            foreach (var slot in team.Slots)
                writer.WriteByte(slot);
            return writer.ToArray();
        }

        // 110D890: dispatcher success + the same three indices.
        internal static byte[] BuildSuccessBody(PvpTotalMatchTeam team)
            => new[] { (byte)1, team.Slots[0], team.Slots[1], team.Slots[2] };
    }
}

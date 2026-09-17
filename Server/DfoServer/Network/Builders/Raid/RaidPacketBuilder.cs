using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace DfoServer.Network.Builders.Raid
{
    public sealed class RaidMemberSnapshot
    {
        public ushort UserId { get; set; }
        public uint CharacterId { get; set; }
        public byte[] NameBytes { get; set; } = Array.Empty<byte>();
        public byte Job { get; set; }
        public byte GrowType { get; set; }
        public ushort PartyIndex { get; set; }
        public uint PhaseClearCount { get; set; }
        public byte PhaseIndex { get; set; }
    }

    public sealed class RaidRewardEntry
    {
        public ushort UserId { get; set; }
        public byte CardType { get; set; }
        public uint Quantity { get; set; }
        public uint ItemId { get; set; }
        public uint Flags { get; set; }
    }

    public sealed class RaidEntryCostStatus
    {
        public ushort UserId { get; set; }
        public bool Ready { get; set; }
        public uint OwnedCount { get; set; }
    }

    public sealed class RaidBuffStatusEntry
    {
        public ushort PartyIndex { get; set; }
        public ushort UserId { get; set; }
        public uint ActiveUntilTimestamp { get; set; }
        public uint CooldownUntilTimestamp { get; set; }
    }

    public sealed class RaidBuffStatusGroup
    {
        public byte BuffType { get; set; }
        public IReadOnlyList<RaidBuffStatusEntry> Entries { get; set; } = Array.Empty<RaidBuffStatusEntry>();
    }

    public sealed class RaidMonsterStatusEntry
    {
        public ushort SituationIndex { get; set; }
        public IReadOnlyList<ushort> MemberIds { get; set; } = Array.Empty<ushort>();
        public uint UsedCoinCount { get; set; }
        public IReadOnlyList<uint> RuntimeValues { get; set; } = Array.Empty<uint>();
    }

    public static class RaidPacketBuilder
    {
        internal const uint MemberColumnRequestMagic = 827343698u;

        internal const uint MemberColumnProtocolVersion = 1u;

        internal const int MemberColumnRequestSize = 12;

        internal static bool TryReadMemberColumnRequest(byte[] body, out uint raidId)
        {
            raidId = 0u;
            if (body == null || body.Length != 12 || BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4, 4)) != 827343698 || BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8, 4)) != 1)
            {
                return false;
            }
            raidId = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(0, 4));
            return raidId != 0;
        }

        public static byte[] BuildPeerInvite(ushort inviterUserId, int peerId)
        {
            GamePacketWriter gamePacketWriter = new GamePacketWriter();
            gamePacketWriter.WriteUInt16(inviterUserId);
            gamePacketWriter.WriteByte(10);
            gamePacketWriter.WriteInt32(peerId);
            gamePacketWriter.WriteUInt16(0);
            gamePacketWriter.WriteUInt16(0);
            gamePacketWriter.WriteUInt16(0);
            return gamePacketWriter.ToArray();
        }

        public static byte[] BuildCreateAck(uint raidKey)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteUInt32(raidKey);
            return writer.ToArray();
        }

        public static byte[] BuildRaidModify(
            uint raidId,
            byte[] titleBytes,
            RaidMemberSnapshot leader)
        {
            return BuildRaidModify(raidId, titleBytes, 0, 0, leader, new[] { leader });
        }

        public static byte[] BuildRaidModify(uint raidId, byte[] titleBytes, uint state, uint stateArgument, RaidMemberSnapshot leader, IReadOnlyList<RaidMemberSnapshot> members)
        {
            return BuildRaidModify(raidId, titleBytes, state, stateArgument, leader, members, columnV1: false);
        }

        public static byte[] BuildRaidModify(uint raidId, byte[] titleBytes, uint state, uint stateArgument, RaidMemberSnapshot leader, IReadOnlyList<RaidMemberSnapshot> members, bool columnV1)
        {
            GamePacketWriter gamePacketWriter = new GamePacketWriter();
            gamePacketWriter.WriteUInt32(raidId);
            gamePacketWriter.WriteUInt32(0u);
            WriteRaidObject(gamePacketWriter, raidId, titleBytes, state, stateArgument, leader);
            WriteMemberList(gamePacketWriter, members);
            if (columnV1)
            {
                WriteMemberColumnFooter(gamePacketWriter, members, stateArgument);
            }
            return gamePacketWriter.ToArray();
        }

        public static byte[] BuildRaidCreate(uint raidId, byte[] titleBytes, uint state, uint stateArgument, RaidMemberSnapshot leader, IReadOnlyList<RaidMemberSnapshot> members)
        {
            return BuildRaidCreate(raidId, titleBytes, state, stateArgument, leader, members, columnV1: false);
        }

        public static byte[] BuildRaidCreate(uint raidId, byte[] titleBytes, uint state, uint stateArgument, RaidMemberSnapshot leader, IReadOnlyList<RaidMemberSnapshot> members, bool columnV1)
        {
            return BuildRaidModify(raidId, titleBytes, state, stateArgument, leader, members, columnV1);
        }

        // RAID_LIST is a list of complete RAID objects, without the
        // operation field used by RAID_MODIFY.
        public static byte[] BuildRaidList(
            uint raidId,
            byte[] titleBytes,
            uint state,
            uint stateArgument,
            RaidMemberSnapshot leader,
            IReadOnlyList<RaidMemberSnapshot> members)
        {
            if (members == null)
                throw new ArgumentNullException(nameof(members));

            var writer = new GamePacketWriter();
            writer.WriteUInt32(1);
            WriteRaidObject(writer, raidId, titleBytes, state, stateArgument, leader);
            WriteMemberList(writer, members);
            return writer.ToArray();
        }

        public static byte[] BuildRaidInfoUpdate(
            uint raidId,
            byte[] titleBytes,
            uint state,
            uint stateArgument,
            RaidMemberSnapshot leader)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(raidId);
            writer.WriteUInt32(2); // raid object update operation
            WriteRaidObject(writer, raidId, titleBytes, state, stateArgument, leader);
            return writer.ToArray();

        }

        public static byte[] BuildRaidMembersUpdate(uint raidId, IReadOnlyList<RaidMemberSnapshot> members)
        {
            return BuildRaidMembersUpdate(raidId, members, columnV1: false);
        }

        public static byte[] BuildRaidMembersUpdate(uint raidId, IReadOnlyList<RaidMemberSnapshot> members, bool columnV1)
        {
            GamePacketWriter gamePacketWriter = new GamePacketWriter();
            gamePacketWriter.WriteUInt32(raidId);
            gamePacketWriter.WriteUInt32(3u);
            WriteMemberList(gamePacketWriter, members);
            if (columnV1)
            {
                WriteMemberColumnFooter(gamePacketWriter, members, (uint)((members.Count != 0) ? members[0].PhaseIndex : 0));
            }
            return gamePacketWriter.ToArray();
        }

        private static void WriteMemberColumnFooter(GamePacketWriter gamePacketWriter, IReadOnlyList<RaidMemberSnapshot> members, uint phase)
        {
            if (phase > 1)
            {
                throw new ArgumentOutOfRangeException("phase");
            }
            if (members.Count > 20)
            {
                throw new ArgumentOutOfRangeException("members");
            }
            gamePacketWriter.WriteUInt32(826491730u);
            gamePacketWriter.WriteByte((byte)phase);
            gamePacketWriter.WriteByte((byte)members.Count);
            foreach (RaidMemberSnapshot member in members)
            {
                if (member.PhaseIndex != phase)
                {
                    throw new ArgumentException("Member phase differs from footer/object phase", "members");
                }
                gamePacketWriter.WriteUInt16(member.UserId);
                gamePacketWriter.WriteUInt32(member.CharacterId);
                gamePacketWriter.WriteUInt32(member.PhaseClearCount);
            }
        }

        private static void WriteRaidObject(GamePacketWriter writer, uint raidId, byte[] titleBytes, uint state, uint stateArgument, RaidMemberSnapshot leader)
        {
            if (state > 255)
            {
                throw new ArgumentOutOfRangeException("state");
            }
            if (stateArgument > 255)
            {
                throw new ArgumentOutOfRangeException("stateArgument");
            }
            writer.WriteUInt32(raidId);
            writer.WriteRawDstr(titleBytes);
            writer.WriteByte(0);
            writer.WriteByte((byte)state);
            writer.WriteByte((byte)stateArgument);
            writer.WriteUInt32(0u);
            WriteMember(writer, leader);
        }

        private static void WriteMemberList(
            GamePacketWriter writer,
            IReadOnlyList<RaidMemberSnapshot> members)
        {
            writer.WriteByte((byte)members.Count);
            foreach (var member in members)
                WriteMember(writer, member);
        }

        public static byte[] BuildRaidRemove(uint raidId)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(raidId);
            writer.WriteUInt32(1); // remove operation
            return writer.ToArray();

        }

        public static byte[] BuildRaidDirectory(IReadOnlyList<RaidDirectoryEntry> raids)
        {
            if (raids == null)
            {
                throw new ArgumentNullException("raids");
            }
            GamePacketWriter gamePacketWriter = new GamePacketWriter();
            gamePacketWriter.WriteUInt32((uint)raids.Count);
            foreach (RaidDirectoryEntry raid in raids)
            {
                if (raid?.Leader == null)
                {
                    throw new ArgumentException("Raid directory entries need a leader.", "raids");
                }
                WriteRaidObject(gamePacketWriter, raid.RaidId, raid.TitleBytes, raid.State, raid.StateArgument, raid.Leader);
                gamePacketWriter.WriteByte((byte)raid.MemberCount);
            }
            return gamePacketWriter.ToArray();
        }

        public static byte[] BuildWaitingList(RaidMemberSnapshot member)
        {
            if (member == null)
                throw new ArgumentNullException(nameof(member));

            return BuildWaitingList(new[] { member });
        }

        public static byte[] BuildWaitingList(IReadOnlyList<RaidMemberSnapshot> members)
        {
            if (members == null)
                throw new ArgumentNullException(nameof(members));

            var writer = new GamePacketWriter();
            writer.WriteUInt32((uint)members.Count);
            foreach (var member in members)
            {
                // IDA sub_D0F3B0 reads u16 user id followed by u32 value.
                writer.WriteUInt16(member.UserId);
                writer.WriteUInt32(member.PartyIndex);
            }
            return writer.ToArray();
        }

        public static byte[] BuildRaidState(uint state, uint arg)
        {
            if (state > 255)
            {
                throw new ArgumentOutOfRangeException("state");
            }
            if (arg > 255)
            {
                throw new ArgumentOutOfRangeException("arg");
            }
            GamePacketWriter gamePacketWriter = new GamePacketWriter();
            gamePacketWriter.WriteByte((byte)state);
            gamePacketWriter.WriteByte((byte)arg);
            return gamePacketWriter.ToArray();
        }

        public static byte[] BuildRaidWaitingAck()
        {
            GamePacketWriter gamePacketWriter = new GamePacketWriter();
            gamePacketWriter.WriteByte(1);
            for (int i = 0; i < 5; i++)
            {
                gamePacketWriter.WriteUInt32(0u);
            }
            return gamePacketWriter.ToArray();
        }

        public static byte[] BuildSetTimer(uint key0, uint key1, uint durationSeconds)
        {
            var endTimestamp = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + durationSeconds);
            return BuildSetTimer(key0, key1, durationSeconds, endTimestamp);
        }

        internal static byte[] BuildSetTimer(uint key0, uint key1, uint durationSeconds, uint endTimestamp)
        {
            if (key0 > 255)
            {
                throw new ArgumentOutOfRangeException("key0");
            }
            GamePacketWriter gamePacketWriter = new GamePacketWriter();
            gamePacketWriter.WriteByte((byte)key0);
            gamePacketWriter.WriteUInt32(key1);
            gamePacketWriter.WriteUInt32(endTimestamp);
            gamePacketWriter.WriteUInt32(durationSeconds);
            return gamePacketWriter.ToArray();
        }

        public static byte[] BuildRemainTime(byte timerType, uint remainSeconds)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(timerType);
            writer.WriteUInt32(remainSeconds);
            return writer.ToArray();
        }

        public static byte[] BuildRaidResult(uint resultType, uint phaseIndex, uint clearTimeSeconds, uint deadCount, uint rank, byte rewardOption)
        {
            GamePacketWriter gamePacketWriter = new GamePacketWriter();
            checked
            {
                gamePacketWriter.WriteByte((byte)resultType);
                gamePacketWriter.WriteByte((byte)phaseIndex);
                gamePacketWriter.WriteUInt32(clearTimeSeconds);
                gamePacketWriter.WriteUInt16(unchecked((ushort)Math.Min(deadCount, 65535u)));
                gamePacketWriter.WriteByte((byte)rank);
                gamePacketWriter.WriteByte(rewardOption);
                return gamePacketWriter.ToArray();
            }
        }

        public static byte[] BuildRaidMovieSkip(uint movieId, uint option)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(movieId);
            writer.WriteUInt32(option);
            return writer.ToArray();
        }

        public static byte[] BuildRaidRewardList(uint rewardType, IReadOnlyList<RaidRewardEntry> rewards)
        {
            if (rewards == null)
            {
                throw new ArgumentNullException("rewards");
            }
            GamePacketWriter gamePacketWriter = new GamePacketWriter();
            checked
            {
                gamePacketWriter.WriteByte((byte)rewardType);
                gamePacketWriter.WriteByte((byte)rewards.Count);
                foreach (RaidRewardEntry reward in rewards)
                {
                    if (reward == null)
                    {
                        throw new ArgumentException("Reward entries cannot contain null.", "rewards");
                    }
                    gamePacketWriter.WriteUInt16(reward.UserId);
                    gamePacketWriter.WriteByte(reward.CardType);
                    gamePacketWriter.WriteByte((byte)reward.Flags);
                    gamePacketWriter.WriteUInt32(reward.ItemId);
                    gamePacketWriter.WriteUInt16((ushort)reward.Quantity);
                }
                return gamePacketWriter.ToArray();
            }
        }

        public static byte[] BuildSetSymbols(IReadOnlyList<KeyValuePair<uint, uint>> symbols)
        {
            if (symbols == null)
            {
                throw new ArgumentNullException("symbols");
            }
            GamePacketWriter gamePacketWriter = new GamePacketWriter();
            gamePacketWriter.WriteByte(checked((byte)symbols.Count));
            foreach (KeyValuePair<uint, uint> symbol in symbols)
            {
                gamePacketWriter.WriteUInt32(symbol.Key);
                gamePacketWriter.WriteUInt32(symbol.Value);
            }
            return gamePacketWriter.ToArray();
        }

        public static byte[] BuildSetSymbol(uint symbolId, uint value)
        {
            return BuildSetSymbols(new[] { new KeyValuePair<uint, uint>(symbolId, value) });
        }

        public static byte[] BuildDungeonState(
            uint dungeonId,
            uint state,
            uint infectionDungeonId = 0)
        {
            return BuildDungeonState(
                new[] { new KeyValuePair<uint, uint>(dungeonId, state) },
                infectionDungeonId);
        }

        public static byte[] BuildDungeonState(IReadOnlyList<KeyValuePair<uint, uint>> dungeonStates, uint infectionDungeonId = 0u)
        {
            if (dungeonStates == null)
            {
                throw new ArgumentNullException("dungeonStates");
            }
            if (dungeonStates.Count > 255)
            {
                throw new ArgumentOutOfRangeException("dungeonStates");
            }
            GamePacketWriter gamePacketWriter = new GamePacketWriter();
            gamePacketWriter.WriteByte(0);
            gamePacketWriter.WriteByte((byte)dungeonStates.Count);
            foreach (KeyValuePair<uint, uint> dungeonState in dungeonStates)
            {
                if (dungeonState.Value > 255)
                {
                    throw new ArgumentOutOfRangeException("dungeonStates");
                }
                gamePacketWriter.WriteUInt32(dungeonState.Key);
                gamePacketWriter.WriteByte((byte)dungeonState.Value);
            }
            gamePacketWriter.WriteUInt32(infectionDungeonId);
            return gamePacketWriter.ToArray();
        }

        public static byte[] BuildChangeDungeonState(uint dungeonId, uint state)
        {
            if (state > 255)
            {
                throw new ArgumentOutOfRangeException("state");
            }
            GamePacketWriter gamePacketWriter = new GamePacketWriter();
            gamePacketWriter.WriteUInt32(dungeonId);
            gamePacketWriter.WriteByte(0);
            gamePacketWriter.WriteByte((byte)state);
            return gamePacketWriter.ToArray();
        }

        public static byte[] BuildRaidDungeonParticipationInfo(uint targetId, uint op, IReadOnlyList<uint> memberUserIds)
        {
            if (memberUserIds == null)
            {
                throw new ArgumentNullException("memberUserIds");
            }
            GamePacketWriter gamePacketWriter = new GamePacketWriter();
            gamePacketWriter.WriteByte(1);
            gamePacketWriter.WriteUInt32(targetId);
            checked
            {
                gamePacketWriter.WriteByte((byte)op);
                gamePacketWriter.WriteByte((byte)memberUserIds.Count);
                foreach (uint memberUserId in memberUserIds)
                {
                    gamePacketWriter.WriteUInt16((ushort)memberUserId);
                }
                return gamePacketWriter.ToArray();
            }
        }

        public static byte[] BuildRaidMemberState(ushort userId, byte state)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(userId);
            writer.WriteByte(state);
            return writer.ToArray();
        }

        public static byte[] BuildEntryCostInfo(IReadOnlyList<RaidEntryCostStatus> statuses)
        {
            if (statuses == null)
            {
                throw new ArgumentNullException("statuses");
            }
            GamePacketWriter gamePacketWriter = new GamePacketWriter();
            gamePacketWriter.WriteUInt32((uint)statuses.Count);
            foreach (RaidEntryCostStatus status in statuses)
            {
                if (status == null)
                {
                    throw new ArgumentException("Entry cost statuses cannot contain null.", "statuses");
                }
                gamePacketWriter.WriteUInt16(status.UserId);
                gamePacketWriter.WriteByte(status.Ready ? ((byte)1) : ((byte)0));
                gamePacketWriter.WriteUInt16((ushort)Math.Min(status.OwnedCount, 65535u));
            }
            return gamePacketWriter.ToArray();
        }

        public static byte[] BuildRaidBuffSystem(IReadOnlyList<RaidBuffStatusGroup> groups)
        {
            if (groups == null)
                throw new ArgumentNullException(nameof(groups));
            if (groups.Count > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(groups));

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)groups.Count);
            foreach (var group in groups)
            {
                if (group == null || group.Entries == null || group.Entries.Count > byte.MaxValue)
                    throw new ArgumentException("Invalid raid buff group.", nameof(groups));
                writer.WriteByte(group.BuffType);
                writer.WriteByte((byte)group.Entries.Count);
                foreach (var entry in group.Entries)
                {
                    if (entry == null)
                        throw new ArgumentException("Raid buff entries cannot contain null.", nameof(groups));
                    writer.WriteUInt16(entry.PartyIndex);
                    writer.WriteUInt16(entry.UserId);
                    writer.WriteUInt32(entry.ActiveUntilTimestamp);
                    writer.WriteUInt32(entry.CooldownUntilTimestamp);
                }
            }
            return writer.ToArray();
        }

        public static byte[] BuildRaidMonsterHp(IReadOnlyList<RaidMonsterStatusEntry> dungeons)
        {
            if (dungeons == null)
                throw new ArgumentNullException(nameof(dungeons));
            if (dungeons.Count > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(dungeons));

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)dungeons.Count);
            foreach (var dungeon in dungeons)
            {
                if (dungeon == null || dungeon.MemberIds == null || dungeon.RuntimeValues == null
                    || dungeon.MemberIds.Count > byte.MaxValue || dungeon.RuntimeValues.Count > byte.MaxValue)
                    throw new ArgumentException("Invalid raid monster entry.", nameof(dungeons));
                writer.WriteUInt16(dungeon.SituationIndex);
                writer.WriteByte((byte)dungeon.MemberIds.Count);
                foreach (var memberId in dungeon.MemberIds)
                    writer.WriteUInt16(memberId);
                writer.WriteUInt32(dungeon.UsedCoinCount);
                writer.WriteByte((byte)dungeon.RuntimeValues.Count);
                foreach (var runtimeValue in dungeon.RuntimeValues)
                    writer.WriteUInt32(runtimeValue);
            }
            return writer.ToArray();
        }

        private static void WriteMember(GamePacketWriter writer, RaidMemberSnapshot member)
        {
            writer.WriteUInt16(member.UserId);
            writer.WriteByte(1);
            writer.WriteRawDstr(member.NameBytes);
            writer.WriteByte(member.Job);
            writer.WriteByte(member.GrowType);
            writer.WriteByte((byte)member.PartyIndex);
            writer.WriteByte(0);
            writer.WriteUInt32(member.CharacterId);
            writer.WriteByte(0);
            writer.WriteByte(0);
        }
    }
}

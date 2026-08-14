using System;
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

        public static byte[] BuildRaidModify(
            uint raidId,
            byte[] titleBytes,
            uint state,
            uint stateArgument,
            RaidMemberSnapshot leader,
            IReadOnlyList<RaidMemberSnapshot> members)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(raidId);
            writer.WriteUInt32(0); // create/full refresh operation
            WriteRaidObject(writer, raidId, titleBytes, state, stateArgument, leader);
            WriteMemberList(writer, members);
            return writer.ToArray();

        }

        public static byte[] BuildRaidCreate(
            uint raidId,
            byte[] titleBytes,
            uint state,
            uint stateArgument,
            RaidMemberSnapshot leader,
            IReadOnlyList<RaidMemberSnapshot> members)
        {
            return BuildRaidModify(raidId, titleBytes, state, stateArgument, leader, members);

        }
        // RAID_LIST (0x024F) is a list of complete RAID objects, without the
        // operation field used by RAID_MODIFY (0x0250).
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

        public static byte[] BuildRaidMembersUpdate(
            uint raidId,
            IReadOnlyList<RaidMemberSnapshot> members)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(raidId);
            writer.WriteUInt32(3); // member list update operation
            WriteMemberList(writer, members);
            return writer.ToArray();

        }

        private static void WriteRaidObject(
            GamePacketWriter writer,
            uint raidId,
            byte[] titleBytes,
            uint state,
            uint stateArgument,
            RaidMemberSnapshot leader)
        {
            writer.WriteUInt32(raidId);
            writer.WriteRawDstr(titleBytes);
            writer.WriteUInt32(0); // object+36
            writer.WriteUInt32(state); // object+40
            writer.WriteUInt32(stateArgument); // object+48
            writer.WriteUInt32(0); // object+52
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
            var writer = new GamePacketWriter();
            writer.WriteUInt32(state);
            writer.WriteUInt32(arg);
            return writer.ToArray();
        }

        public static byte[] BuildSetTimer(uint key0, uint key1, uint durationSeconds)
        {
            var endTimestamp = checked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + durationSeconds);
            return BuildSetTimer(key0, key1, durationSeconds, endTimestamp);
        }

        internal static byte[] BuildSetTimer(uint key0, uint key1, uint durationSeconds, uint endTimestamp)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(key0);
            writer.WriteUInt32(key1);
            writer.WriteUInt32(endTimestamp);
            writer.WriteUInt32(durationSeconds);
            return writer.ToArray();
        }

        public static byte[] BuildRemainTime(byte timerType, uint remainSeconds)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(timerType);
            writer.WriteUInt32(remainSeconds);
            return writer.ToArray();
        }

        public static byte[] BuildRaidResult(
            uint resultType,
            uint phaseIndex,
            uint clearTimeSeconds,
            uint deadCount,
            uint rank,
            byte rewardOption)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(resultType);
            writer.WriteUInt32(phaseIndex);
            writer.WriteUInt32(clearTimeSeconds);
            writer.WriteUInt32(deadCount);
            writer.WriteUInt32(rank);
            writer.WriteByte(rewardOption);
            return writer.ToArray();
        }

        public static byte[] BuildRaidMovieSkip(uint movieId, uint option)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(movieId);
            writer.WriteUInt32(option);
            return writer.ToArray();
        }

        public static byte[] BuildRaidRewardList(
            uint rewardType,
            IReadOnlyList<RaidRewardEntry> rewards)
        {
            if (rewards == null)
                throw new ArgumentNullException(nameof(rewards));

            var writer = new GamePacketWriter();
            writer.WriteUInt32(rewardType);
            writer.WriteUInt32((uint)rewards.Count);
            foreach (var reward in rewards)
            {
                if (reward == null)
                    throw new ArgumentException("Reward entries cannot contain null.", nameof(rewards));
                writer.WriteUInt16(reward.UserId);
                writer.WriteByte(reward.CardType);
                writer.WriteUInt32(reward.Flags);
                writer.WriteUInt32(reward.ItemId);
                writer.WriteUInt32(reward.Quantity);
            }
            return writer.ToArray();
        }

        public static byte[] BuildSetSymbols(IReadOnlyList<KeyValuePair<uint, uint>> symbols)
        {
            if (symbols == null)
                throw new ArgumentNullException(nameof(symbols));

            var writer = new GamePacketWriter();
            writer.WriteUInt32((uint)symbols.Count);
            foreach (var symbol in symbols)
            {
                writer.WriteUInt32(symbol.Key);
                writer.WriteUInt32(symbol.Value);
            }
            return writer.ToArray();
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

        public static byte[] BuildDungeonState(
            IReadOnlyList<KeyValuePair<uint, uint>> dungeonStates,
            uint infectionDungeonId = 0)
        {
            if (dungeonStates == null)
                throw new ArgumentNullException(nameof(dungeonStates));

            var writer = new GamePacketWriter();
            writer.WriteUInt32(0);
            writer.WriteUInt32((uint)dungeonStates.Count);
            foreach (var dungeonState in dungeonStates)
            {
                writer.WriteUInt32(dungeonState.Key);
                writer.WriteUInt32(dungeonState.Value);
            }
            writer.WriteUInt32(infectionDungeonId);
            return writer.ToArray();
        }

        public static byte[] BuildChangeDungeonState(uint dungeonId, uint state)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(dungeonId);
            writer.WriteUInt32(0); // read by the client but unused
            writer.WriteUInt32(state);
            return writer.ToArray();
        }

        public static byte[] BuildRaidDungeonParticipationInfo(
            uint targetId,
            uint op,
			IReadOnlyList<uint> memberUserIds)
        {
			if (memberUserIds == null)
				throw new ArgumentNullException(nameof(memberUserIds));

            var writer = new GamePacketWriter();
            writer.WriteUInt32(1);
            writer.WriteUInt32(targetId);
            writer.WriteUInt32(op);
			writer.WriteUInt32((uint)memberUserIds.Count);
			foreach (var memberUserId in memberUserIds)
				writer.WriteUInt32(memberUserId);
            return writer.ToArray();
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
                throw new ArgumentNullException(nameof(statuses));

            var writer = new GamePacketWriter();
            writer.WriteUInt32((uint)statuses.Count);
            foreach (var status in statuses)
            {
                if (status == null)
                    throw new ArgumentException("Entry cost statuses cannot contain null.", nameof(statuses));
                writer.WriteUInt16(status.UserId);
                writer.WriteUInt32(status.Ready ? 1u : 0u);
                writer.WriteUInt32(status.OwnedCount);
            }
            return writer.ToArray();
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
            writer.WriteUInt32(member.CharacterId);
            writer.WriteRawDstr(member.NameBytes);
            writer.WriteUInt32(member.Job);
            writer.WriteByte(member.GrowType);
            writer.WriteUInt16(member.PartyIndex);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteByte(0);
        }
    }
}

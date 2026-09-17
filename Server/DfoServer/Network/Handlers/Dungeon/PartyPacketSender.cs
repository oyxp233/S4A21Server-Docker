using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Session;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class PartyPacketSendResult
    {
        internal PartyPacketSendResult(
            IReadOnlyList<DungeonParticipantRosterEntry> succeeded,
            IReadOnlyList<DungeonParticipantRosterEntry> failed)
        {
            Succeeded = Freeze(succeeded);
            Failed = Freeze(failed);
        }

        internal IReadOnlyList<DungeonParticipantRosterEntry> Succeeded
        {
            get;
        }

        internal IReadOnlyList<DungeonParticipantRosterEntry> Failed
        {
            get;
        }

        private static IReadOnlyList<DungeonParticipantRosterEntry> Freeze(
            IReadOnlyList<DungeonParticipantRosterEntry> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<DungeonParticipantRosterEntry>();

            return source.ToList().AsReadOnly();
        }
    }

    internal sealed class PartyPacketSender
    {
        private readonly ISessionDirectory _sessions;
        private readonly TimeSpan _sendLockTimeout;

        internal PartyPacketSender(
            ISessionDirectory sessions,
            TimeSpan? sendLockTimeout = null)
        {
            _sessions = sessions;
            _sendLockTimeout = sendLockTimeout
                ?? SessionDirectory.BestEffortSendTimeout;
            if (_sendLockTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sendLockTimeout));
            }
        }

        internal async Task<PartyPacketSendResult> SendToPartyAsync(
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            IReadOnlyList<byte[]> packets)
        {
            var orderedRoster = OrderRoster(roster);
            var succeeded = new List<DungeonParticipantRosterEntry>(
                orderedRoster.Count);
            var failed = new List<DungeonParticipantRosterEntry>();

            if (!TryBuildFrozenWireBatch(
                    packets,
                    out var frozenPackets,
                    out var wireBatch))
            {
                failed.AddRange(orderedRoster);
                return new PartyPacketSendResult(
                    succeeded.AsReadOnly(),
                    failed.AsReadOnly());
            }

            var sends = new Task<bool>[orderedRoster.Count];
            for (var index = 0; index < orderedRoster.Count; index++)
            {
                sends[index] = SendParticipantAsync(
                    orderedRoster[index],
                    frozenPackets,
                    wireBatch);
            }

            var results = await Task.WhenAll(sends);
            for (var index = 0; index < orderedRoster.Count; index++)
            {
                var participant = orderedRoster[index];
                if (results[index])
                    succeeded.Add(participant);
                else
                    failed.Add(participant);
            }

            return new PartyPacketSendResult(
                succeeded.AsReadOnly(),
                failed.AsReadOnly());
        }

        private async Task<bool> SendParticipantAsync(
            DungeonParticipantRosterEntry participant,
            IReadOnlyList<byte[]> frozenPackets,
            byte[] wireBatch)
        {
            if (!TryResolveCurrentSession(participant, out var session))
                return false;

            try
            {
                using var timeout = new CancellationTokenSource(
                    _sendLockTimeout);
                // One transport write gives the ordered envelope batch one
                // send-lock linearization point. The receiver still parses
                // each envelope by its own A21 frame length. A directory
                // replacement observed after canSend is ordered after this
                // accepted batch; replacement before it fails the predicate.
                return await session.TrySendPacketBatchAsync(
                    frozenPackets,
                    wireBatch,
                    timeout.Token,
                    () => IsCurrentSession(participant, session));
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[PartyPacketSender] packet batch failed: "
                    + $"cid={participant?.CharacterId ?? 0} "
                    + $"userId={participant?.ParticipantUserId ?? 0} "
                    + $"error={ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private bool TryResolveCurrentSession(
            DungeonParticipantRosterEntry participant,
            out EnhancedClientSession session)
        {
            session = null;
            return participant != null
                && _sessions != null
                && _sessions.TryGet(participant.CharacterId, out session)
                && IsCurrentSession(participant, session);
        }

        private bool IsCurrentSession(
            DungeonParticipantRosterEntry participant,
            EnhancedClientSession expectedSession)
        {
            if (participant == null
                || expectedSession == null
                || _sessions == null
                || !_sessions.TryGet(
                    participant.CharacterId,
                    out var currentSession)
                || !ReferenceEquals(currentSession, expectedSession))
            {
                return false;
            }

            var player = expectedSession.Player;
            return expectedSession.TcpClient != null
                && expectedSession.TcpClient.Connected
                && player != null
                && player.CharacterId == participant.CharacterId
                && player.UserId == participant.ParticipantUserId
                && ReferenceEquals(player.CurrentRun, participant.Run)
                && player.IsCurrentDungeonRun(participant.RunIdentity)
                && participant.Run.Matches(participant.RunIdentity);
        }

        private static bool TryBuildFrozenWireBatch(
            IReadOnlyList<byte[]> packets,
            out IReadOnlyList<byte[]> frozenPackets,
            out byte[] wireBatch)
        {
            frozenPackets = null;
            wireBatch = null;
            if (packets == null || packets.Count == 0)
                return false;

            try
            {
                var packetCount = packets.Count;
                var copiedPackets = new byte[packetCount][];
                var totalLength = 0;
                for (var index = 0; index < packetCount; index++)
                {
                    var packet = packets[index];
                    if (packet == null || packet.Length == 0)
                        return false;

                    var frozenPacket = packet.ToArray();
                    copiedPackets[index] = frozenPacket;
                    totalLength = checked(totalLength + frozenPacket.Length);
                }

                if (totalLength <= 0)
                    return false;

                wireBatch = new byte[totalLength];
                var offset = 0;
                foreach (var packet in copiedPackets)
                {
                    Buffer.BlockCopy(
                        packet,
                        0,
                        wireBatch,
                        offset,
                        packet.Length);
                    offset += packet.Length;
                }
                frozenPackets = Array.AsReadOnly(copiedPackets);
                return true;
            }
            catch (Exception ex)
                when (ex is OverflowException
                      || ex is ArgumentException
                      || ex is InvalidOperationException
                      || ex is IndexOutOfRangeException)
            {
                frozenPackets = null;
                wireBatch = null;
                return false;
            }
        }

        private static IReadOnlyList<DungeonParticipantRosterEntry>
            OrderRoster(IReadOnlyList<DungeonParticipantRosterEntry> roster)
        {
            if (roster == null || roster.Count == 0)
                return Array.Empty<DungeonParticipantRosterEntry>();

            return roster
                .OrderBy(value => value?.PartySlot ?? byte.MaxValue)
                .ThenBy(value =>
                    value?.ParticipantUserId ?? ushort.MaxValue)
                .ThenBy(value => value?.CharacterId ?? int.MaxValue)
                .ToList()
                .AsReadOnly();
        }
    }
}

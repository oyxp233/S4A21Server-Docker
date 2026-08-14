using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Pets;
using DfoServer.Network.Parsers.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonRejoinCoordinator
    {
        private sealed class Offer
        {
            internal int AccountId;
            internal int CharacterId;
            internal ushort ParticipantUserId;
            internal int PartyId;
            internal long AttachmentGeneration;
            internal DungeonRunIdentity RunIdentity;
            internal SemaphoreSlim OperationGate = new SemaphoreSlim(1, 1);
        }

        private const byte GenericRejectErrorCode = 0x04;
        private const int ReservedInt32 = 0;
        private const byte InitialRejoinUiState = 0;

        private readonly object _syncRoot = new object();
        private readonly Dictionary<Guid, Offer> _offersBySession =
            new Dictionary<Guid, Offer>();
        private readonly DungeonInstanceRegistry _registry;
        private readonly CharacterTransitionCoordinator
            _characterTransitions;
        private readonly Func<EnhancedClientSession, int, Task<bool>>
            _restoreParty;
        private readonly Func<EnhancedClientSession, int, Task>
            _rollbackParty;
        private readonly Func<EnhancedClientSession, Task> _leaveTown;
        private readonly Func<EnhancedClientSession, byte[], Task> _sendPacket;
        private readonly Func<EnhancedClientSession, Task>
            _recoverParticipantEffects;

        internal DungeonRejoinCoordinator(
            DungeonInstanceRegistry registry,
            CharacterTransitionCoordinator characterTransitions,
            Func<EnhancedClientSession, int, Task<bool>> restoreParty,
            Func<EnhancedClientSession, int, Task> rollbackParty,
            Func<EnhancedClientSession, Task> leaveTown,
            Func<EnhancedClientSession, byte[], Task> sendPacket = null,
            Func<EnhancedClientSession, Task> recoverParticipantEffects = null)
        {
            _registry = registry
                ?? throw new ArgumentNullException(nameof(registry));
            _characterTransitions = characterTransitions
                ?? throw new ArgumentNullException(nameof(characterTransitions));
            _restoreParty = restoreParty
                ?? throw new ArgumentNullException(nameof(restoreParty));
            _rollbackParty = rollbackParty
                ?? throw new ArgumentNullException(nameof(rollbackParty));
            _leaveTown = leaveTown
                ?? throw new ArgumentNullException(nameof(leaveTown));
            _sendPacket = sendPacket
                ?? ((session, packet) => session.SendPacketAsync(packet));
            _recoverParticipantEffects = recoverParticipantEffects
                ?? (_ => Task.CompletedTask);
        }

        internal async Task ProjectCandidateAsync(
            EnhancedClientSession session)
        {
            var player = session?.Player;
            var accountId = session?.Account?.AccountId ?? 0;
            if (player == null
                || accountId <= 0
                || player.CharacterId <= 0
                || player.UserId == 0)
            {
                ClearSession(session?.SessionId ?? Guid.Empty);
                return;
            }

            var status = _registry.TryGetCandidate(
                accountId,
                player.CharacterId,
                player.UserId,
                out var candidate);
            if (status != DungeonAttachmentOperationStatus.Success)
            {
                ClearSession(session.SessionId);
                if (status != DungeonAttachmentOperationStatus.NotFound
                    && status != DungeonAttachmentOperationStatus.InvalidState)
                {
                    FileLogger.Log(
                        $"[DungeonRejoin] candidate not projected " +
                        $"cid={player.CharacterId} status={status}");
                }
                return;
            }

            StoreOffer(session.SessionId, candidate);
            await SendNotiAsync(
                session,
                NotiPacketType.DISCONN_DUNGEON_INFO,
                DungeonRejoinNotificationBuilder
                    .BuildDisconnectedDungeonInfo(
                        candidate.PartyId,
                        ReservedInt32,
                        InitialRejoinUiState));
            foreach (var participantUserId in candidate.ParticipantUserIds)
            {
                await SendNotiAsync(
                    session,
                    NotiPacketType.REJOIN_DUNGEON,
                    DungeonRejoinNotificationBuilder.BuildParticipant(
                        participantUserId));
            }

            FileLogger.Log(
                $"[DungeonRejoin] candidate projected " +
                $"cid={candidate.CharacterId} party={candidate.PartyId} " +
                $"instance={candidate.RunIdentity.PartyDungeonInstanceId} " +
                $"run={candidate.RunIdentity.RunId}/" +
                $"{candidate.RunIdentity.RunGeneration} " +
                $"attachmentGeneration={candidate.AttachmentGeneration} " +
                $"participants={candidate.ParticipantUserIds.Count}");
        }

        internal async Task HandleRejoinAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!DungeonRejoinRequestParser.TryParseRejoin(
                    body,
                    out var request,
                    out var parseError))
            {
                await SendRejectAsync(session, header.type, parseError);
                return;
            }

            using (var transition =
                   await _characterTransitions.AcquireIfCurrentAsync(session))
            {
                if (transition == null)
                {
                    await SendRejectAsync(
                        session,
                        header.type,
                        "stale_session_generation");
                    return;
                }

                if (!TryGetOffer(session, request.PartyId, out var offer))
                {
                    await SendRejectAsync(session, header.type, "offer_missing");
                    return;
                }

                var operationGate = offer.OperationGate;
                await operationGate.WaitAsync();
                try
                {
                    if (!TryGetOffer(session, request.PartyId, out offer))
                    {
                        await SendRejectAsync(
                            session,
                            header.type,
                            "offer_missing_after_wait");
                        return;
                    }

                    var status = _registry.TryResume(
                        offer.AccountId,
                        offer.CharacterId,
                        offer.ParticipantUserId,
                        request.PartyId,
                        request.TargetParticipantUserId,
                        offer.AttachmentGeneration,
                        session.SessionId,
                        out var attachment,
                        out var didTransition);
                    if (status != DungeonAttachmentOperationStatus.Success)
                    {
                        await SendRejectAsync(
                            session,
                            header.type,
                            "registry_" + status);
                        return;
                    }

                    if (!didTransition)
                    {
                        await SendSuccessAsync(session, header.type);
                        await SendNotiAsync(
                            session,
                            NotiPacketType.REJOINABLE_DUNGEON,
                            DungeonRejoinNotificationBuilder
                                .BuildRejoinableDungeon(request.PartyId));
                        FileLogger.Log(
                            $"[DungeonRejoin] accepted request replayed " +
                            $"cid={offer.CharacterId} party={request.PartyId} " +
                            $"attachmentGeneration=" +
                            $"{attachment.AttachmentGeneration}");
                        return;
                    }

                    if (!await _restoreParty(session, request.PartyId))
                    {
                        RollbackRegistryResume(session, attachment);
                        await SendRejectAsync(
                            session,
                            header.type,
                            "party_restore_failed");
                        return;
                    }

                    try
                    {
                        await _leaveTown(session);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log(
                            $"[DungeonRejoin] town leave projection failed: " +
                            $"cid={offer.CharacterId} error={ex.Message}");
                    }

                    if (!DungeonRunLifecycle.AttachResumedRun(session, attachment))
                    {
                        await _rollbackParty(session, request.PartyId);
                        RollbackRegistryResume(session, attachment);
                        await SendRejectAsync(
                            session,
                            header.type,
                            "run_attach_failed");
                        return;
                    }

                    PetCreatureRuntimeService.BeginDungeon(
                        session,
                        attachment.RunIdentity,
                        "dungeon_rejoin");
                    try
                    {
                        await _recoverParticipantEffects(session);
                    }
                    catch (Exception ex)
                    {
                        // A failed projector remains journaled and is retried by the
                        // next valid rejoin; it must not roll back a valid attachment.
                        FileLogger.Log(
                            $"[DungeonRejoin] participant effect recovery failed: " +
                            $"cid={offer.CharacterId} error={ex.Message}");
                    }
                    await SendSuccessAsync(session, header.type);
                    await SendNotiAsync(
                        session,
                        NotiPacketType.REJOINABLE_DUNGEON,
                        DungeonRejoinNotificationBuilder.BuildRejoinableDungeon(
                            request.PartyId));
                    FileLogger.Log(
                        $"[DungeonRejoin] accepted " +
                        $"cid={offer.CharacterId} party={request.PartyId} " +
                        $"targetUid={request.TargetParticipantUserId} " +
                        $"instance={attachment.RunIdentity.PartyDungeonInstanceId} " +
                        $"run={attachment.RunIdentity.RunId}/" +
                        $"{attachment.RunIdentity.RunGeneration} " +
                        $"attachmentGeneration={attachment.AttachmentGeneration}");
                }
                finally
                {
                    operationGate.Release();
                }
            }
        }

        internal async Task HandleCancelAsync(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!DungeonRejoinRequestParser.TryParseCancel(
                    body,
                    out var request,
                    out var parseError))
            {
                await SendRejectAsync(session, header.type, parseError);
                return;
            }
            using (var transition =
                   await _characterTransitions.AcquireIfCurrentAsync(session))
            {
                if (transition == null)
                {
                    await SendRejectAsync(
                        session,
                        header.type,
                        "stale_session_generation");
                    return;
                }
                if (!TryGetOffer(session, request.PartyId, out var offer))
                {
                    await SendRejectAsync(session, header.type, "offer_missing");
                    return;
                }

                var operationGate = offer.OperationGate;
                await operationGate.WaitAsync();
                try
                {
                    if (!TryGetOffer(session, request.PartyId, out offer))
                    {
                        await SendRejectAsync(
                            session,
                            header.type,
                            "offer_missing_after_wait");
                        return;
                    }

                    var status = _registry.TryCancel(
                        offer.AccountId,
                        offer.CharacterId,
                        offer.ParticipantUserId,
                        request.PartyId,
                        offer.AttachmentGeneration,
                        out var cancelled);
                    if (status != DungeonAttachmentOperationStatus.Success)
                    {
                        await SendRejectAsync(
                            session,
                            header.type,
                            "registry_" + status);
                        return;
                    }

                    await SendSuccessAsync(session, header.type);
                    await SendNotiAsync(
                        session,
                        NotiPacketType.CANCEL_REJOIN_DUNGEON,
                        DungeonRejoinNotificationBuilder.BuildParticipant(
                            cancelled.ParticipantUserId));
                    FileLogger.Log(
                        $"[DungeonRejoin] cancelled " +
                        $"cid={offer.CharacterId} party={request.PartyId} " +
                        $"instance={cancelled.RunIdentity.PartyDungeonInstanceId} " +
                        $"run={cancelled.RunIdentity.RunId}/" +
                        $"{cancelled.RunIdentity.RunGeneration}");
                }
                finally
                {
                    operationGate.Release();
                }
            }
        }

        internal void ClearSession(Guid sessionId)
        {
            if (sessionId == Guid.Empty)
                return;
            lock (_syncRoot)
                _offersBySession.Remove(sessionId);
        }

        private void RollbackRegistryResume(
            EnhancedClientSession session,
            DungeonParticipantAttachmentSnapshot attachment)
        {
            var status = _registry.TryDetach(
                attachment.AccountId,
                attachment.CharacterId,
                attachment.ParticipantUserId,
                session.SessionId,
                attachment.RunIdentity,
                out var rollbackOffer);
            if (status == DungeonAttachmentOperationStatus.Success)
                StoreOffer(session.SessionId, rollbackOffer);
        }

        private void StoreOffer(
            Guid sessionId,
            DungeonParticipantAttachmentSnapshot attachment)
        {
            lock (_syncRoot)
            {
                _offersBySession.TryGetValue(sessionId, out var previous);
                _offersBySession[sessionId] = new Offer
                {
                    AccountId = attachment.AccountId,
                    CharacterId = attachment.CharacterId,
                    ParticipantUserId = attachment.ParticipantUserId,
                    PartyId = attachment.PartyId,
                    AttachmentGeneration =
                        attachment.AttachmentGeneration,
                    RunIdentity = attachment.RunIdentity,
                    OperationGate = previous?.OperationGate
                        ?? new SemaphoreSlim(1, 1),
                };
            }
        }

        private bool TryGetOffer(
            EnhancedClientSession session,
            int partyId,
            out Offer offer)
        {
            offer = null;
            if (session?.Player == null)
                return false;

            lock (_syncRoot)
            {
                if (!_offersBySession.TryGetValue(
                        session.SessionId,
                        out var candidate)
                    || candidate.PartyId != partyId
                    || candidate.AccountId
                        != (session.Account?.AccountId ?? 0)
                    || candidate.CharacterId
                        != session.Player.CharacterId
                    || candidate.ParticipantUserId
                        != session.Player.UserId)
                {
                    return false;
                }

                offer = candidate;
                return true;
            }
        }

        private Task SendSuccessAsync(
            EnhancedClientSession session,
            ushort type)
        {
            return _sendPacket(
                session,
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    type,
                    CommonPacketBodyBuilder.BuildSuccessAck()));
        }

        private async Task SendRejectAsync(
            EnhancedClientSession session,
            ushort type,
            string reason)
        {
            FileLogger.Log(
                $"[DungeonRejoin] request rejected " +
                $"cid={session?.Player?.CharacterId ?? 0} " +
                $"type=0x{type:X4} reason={reason} " +
                $"wireError=0x{GenericRejectErrorCode:X2}");
            await _sendPacket(
                session,
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    type,
                    CommonPacketBodyBuilder.BuildCmdError(
                        GenericRejectErrorCode)));
        }

        private Task SendNotiAsync(
            EnhancedClientSession session,
            NotiPacketType type,
            byte[] body)
        {
            return _sendPacket(
                session,
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)type,
                    body));
        }
    }
}

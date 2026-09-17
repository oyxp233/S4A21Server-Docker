using DfoServer.Game.Party;
using DfoServer.Game.Raid;
using DfoServer.Game.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    /// A21 SEND_MESSAGE：mode:u8 + targetUid:u16 + targetCharacterId:u32 + message:dstr；
    /// 私聊还可跟 targetName:dstr。
    public sealed partial class ChatHandler : IDisposable
    {
        private const int MaximumMessageBytes = 256;
        private const byte DirectMessageMode = 1;
        private const byte PartyMessageMode = 2;
        private const byte AreaMessageMode = 3;
        internal const byte GuildMessageMode = 6; // 01A63239 jump table -> 01A63312 guild identity check.
        private const byte AlternateDirectMessageMode = 7;
        private const byte OneToOneConversationMode = 43;

        private readonly ISessionDirectory _sessions;
        private readonly PartyManager _parties;
        private readonly RaidManager _raids;
        private readonly object _conversationLock = new object();
        private readonly Dictionary<ulong, Conversation> _activeConversations =
            new Dictionary<ulong, Conversation>();
        private uint _nextConversationId = 1;
        private readonly CharacterTransitionCoordinator _transitions;
        private Game.Guilds.GuildRepository _guilds;
        private CharacterTransitionCoordinator _guildTransitions;
        private Game.Friends.BlacklistRepository _blacklist;

        internal void ConfigureBlacklist(Game.Friends.BlacklistRepository blacklist) => _blacklist = blacklist;
        private bool IsBlocked(int recipient, int sender) => _blacklist?.IsBlocked(recipient, sender) == true;

        internal void ConfigureGuilds(Game.Guilds.GuildRepository guilds, CharacterTransitionCoordinator transitions)
        { _guilds = guilds; _guildTransitions = transitions; }

        public ChatHandler(
            ISessionDirectory sessions,
            PartyManager parties,
            CharacterTransitionCoordinator transitions,
            RaidManager raids = null)
        {
            _sessions = sessions
                ?? throw new ArgumentNullException(nameof(sessions));
            _parties = parties
                ?? throw new ArgumentNullException(nameof(parties));
            _transitions = transitions
                ?? throw new ArgumentNullException(nameof(transitions));
            _raids = raids;
            _sessions.SessionEnding += OnSessionEndingAsync;
        }

        public void Dispose()
        {
            _sessions.SessionEnding -= OnSessionEndingAsync;
        }

        public async Task Handle_SEND_MESSAGE(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (session?.Player == null
                || session.Player.CharacterId <= 0
                || !TryParseRequest(body, out var request))
            {
                FileLogger.Log(
                    $"[GameProtocol] SEND_MESSAGE invalid " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"bodyBytes={body?.Length ?? 0}");
                return;
            }

            if (request.Mode == GuildMessageMode)
            {
                await SendGuildMessageAsync(session, request);
                return;
            }
            if (request.Mode == OneToOneConversationMode)
            {
                await SendConversationMessageAsync(session, request);
                return;
            }
            var recipients = ResolveRecipients(session, request);
            var sendTasks = new List<Task>(recipients.Count);
            foreach (var recipient in recipients)
            {
                if (IsRaidMessageMode(request.Mode))
                {
                    sendTasks.Add(SendRaidMessageAsync(session, recipient, request));
                    continue;
                }
                int senderId = session.Player.CharacterId;
                int recipientId = recipient.Player.CharacterId;
                var packet = GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.MESSAGE,
                    BuildNotificationBody(
                        request.Mode,
                        session.Player.UserId,
                        serverGroup: 0,
                        request.MessageBytes));
                sendTasks.Add(recipient.TrySendPacketAsync(packet, default, () =>
                    session.Player.CharacterId == senderId && recipient.Player.CharacterId == recipientId
                    && _transitions.IsCurrent(session) && _transitions.IsCurrent(recipient)
                    && !IsBlocked(recipientId, senderId)));
            }

            if (sendTasks.Count > 0)
                await Task.WhenAll(sendTasks);

            FileLogger.Log(
                $"[GameProtocol] SEND_MESSAGE cid={session.Player.CharacterId} " +
                $"uid={session.Player.UserId} mode={request.Mode} " +
                $"targetUid={request.TargetUniqueId} " +
                $"targetCid={request.TargetCharacterId} " +
                $"messageBytes={request.MessageBytes.Length} " +
                $"recipients={sendTasks.Count}");
        }

        internal IReadOnlyList<EnhancedClientSession> ResolveRecipients(
            EnhancedClientSession sender,
            ChatMessageRequest request)
        {
            if (IsRaidMessageMode(request.Mode))
            {
                if (!IsOnline(sender) || _raids == null
                    || !_raids.TryGetByUser(sender.Player.UserId, out var raid)
                    || !CanSendRaidMessage(request.Mode, sender.Player.UserId, raid.LeaderUserId))
                    return Array.Empty<EnhancedClientSession>();
                return ResolveRaidRecipients(sender, raid);
            }
            var result = new Dictionary<Guid, EnhancedClientSession>();
            if (request.Mode == OneToOneConversationMode) return result.Values.ToList();
            if (request.Mode == GuildMessageMode) return result.Values.ToList(); // Dedicated durable membership path, including in dungeons.
            AddIfCurrentChannel(result, sender, sender);

            if (IsDirectMessageMode(request.Mode))
            {
                AddIfOnline(result, FindDirectTarget(request));
                return result.Values.ToList();
            }

            if (request.Mode == PartyMessageMode
                || sender.Player.CurrentRun != null)
            {
                var party = _parties.GetPartyByUser(sender.Player.UserId);
                if (party != null)
                {
                    foreach (var member in party.MembersBySlot())
                    {
                        if (_sessions.TryGet(
                                member.CharacterId,
                                out var memberSession))
                        {
                            AddIfCurrentChannel(
                                result,
                                sender,
                                memberSession);
                        }
                    }
                }
                return result.Values.ToList();
            }

            if (request.Mode == AreaMessageMode)
            {
                foreach (var areaSession in _sessions.GetSessionsInArea(
                             sender.Player.CurTownId,
                             sender.Player.CurAreaId,
                             sender.Player.CharacterId,
                             sender.ListenerPort))
                {
                    AddIfCurrentChannel(result, sender, areaSession);
                }
            }

            // Unknown modes deliberately remain sender-only.  Several values
            // are backed by guild/megaphone services and must not become a
            // free cross-channel broadcast merely because their wire shape is
            // shared with ordinary chat.
            return result.Values.ToList();
        }

        private async Task SendGuildMessageAsync(EnhancedClientSession sender, ChatMessageRequest request)
        {
            if (_guilds == null || _guildTransitions == null
                || !Infrastructure.ClientTextEncoding.TryGetStringStrict(request.MessageBytes, out var text)
                || text.IndexOf('\0') >= 0 || string.IsNullOrWhiteSpace(text)) return;
            int actor = sender.Player.CharacterId;
            var guild = _guilds.GetForMember(actor);
            if (guild == null) return;
            foreach (int id in _guilds.GetMemberIds(guild.Id))
            {
                if (!_sessions.TryGet(id, out var recipient)) continue;
                async Task SendCurrent()
                {
                    if (sender.Player.CharacterId != actor || recipient.Player.CharacterId != id
                        || IsBlocked(id, actor)) return;
                    if (!Game.Inventory.InventoryContext.TryGetOwnedLease(sender.SessionId, actor, out _)
                        || !Game.Inventory.InventoryContext.TryGetOwnedLease(recipient.SessionId, id, out _)
                        || _guilds.GetForMember(actor)?.Id != guild.Id || _guilds.GetForMember(id)?.Id != guild.Id) return;
                    // 01173D70 reads mode, status, sender DSTR, server byte, message DSTR.
                    // Use the name-bearing form on every channel; UIDs are channel-local.
                    await recipient.TrySendPacketAsync(GamePacketEnvelopeBuilder.Build(0,
                        (ushort)NotiPacketTypeA21.MESSAGE_OTHER_CHANNEL,
                        BuildGuildNotificationBody(sender.Player.Name, request.MessageBytes)), default,
                        () => !IsBlocked(id, actor));
                }
                if (id == actor) await _guildTransitions.RunIfCurrentAsync(sender, SendCurrent);
                else await _guildTransitions.RunIfBothCurrentAsync(sender, recipient, SendCurrent);
            }
        }

        internal static byte[] BuildGuildNotificationBody(byte[] senderName, byte[] message)
        {
            var w = new GamePacketWriter(); w.WriteByte(GuildMessageMode); w.WriteByte(0);
            w.WriteDstr(senderName); w.WriteByte(GameNetworkConfig.ChannelServerIndex); w.WriteDstr(message);
            return w.ToArray();
        }

        internal static bool IsRaidMessageMode(byte mode) => mode == 52 || mode == 53;

        internal static bool CanSendRaidMessage(byte mode, ushort senderId, ushort leaderId)
            => senderId != 0 && (mode == 52 || (mode == 53 && senderId == leaderId));

        private async Task SendRaidMessageAsync(
            EnhancedClientSession sender,
            EnhancedClientSession recipient,
            ChatMessageRequest request)
        {
            var packet = GamePacketEnvelopeBuilder.Build(0,
                (ushort)NotiPacketTypeA21.MESSAGE,
                BuildNotificationBody(request.Mode, sender.Player.UserId, 0, request.MessageBytes));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (sender.SessionId != recipient.SessionId)
                {
                    var context = RaidHandler.BuildRaidFormationUserContextPacket(sender);
                    if (context == null
                        || !await recipient.TrySendPacketAsync(context, timeout.Token, CanSend))
                    {
                        FileLogger.Log($"[RaidChat] context failed from={sender.Player.CharacterId} to={recipient.Player.CharacterId}");
                        return;
                    }
                }
                var sent = await recipient.TrySendPacketAsync(packet, timeout.Token, CanSend);
                FileLogger.Log($"[RaidChat] from={sender.Player.CharacterId} to={recipient.Player.CharacterId} sent={sent}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[RaidChat] failed from={sender.Player.CharacterId} to={recipient.Player.CharacterId} error={ex.GetType().Name}");
            }
            bool CanSend() => _transitions.IsCurrent(sender) && _transitions.IsCurrent(recipient)
                && !IsBlocked(recipient.Player.CharacterId, sender.Player.CharacterId)
                && ResolveRecipients(sender, request)
                .Any(current => current.SessionId == recipient.SessionId);
        }

        internal IReadOnlyList<EnhancedClientSession> ResolveRaidRecipients(
            EnhancedClientSession sender, RaidSnapshot raid)
        {
            var result = new Dictionary<Guid, EnhancedClientSession>();
            if (!IsOnline(sender) || raid == null
                || !_sessions.TryGet(sender.Player.CharacterId, out var current)
                || current.SessionId != sender.SessionId
                || !raid.Members.Any(m => m.UserId == sender.Player.UserId
                    && m.CharacterId == (uint)sender.Player.CharacterId && m.SessionId == sender.SessionId))
                return result.Values.ToList();
            foreach (var member in raid.Members)
            {
                if (member.CharacterId <= int.MaxValue
                    && _sessions.TryGet((int)member.CharacterId, out var memberSession)
                    && memberSession?.Player != null
                    && memberSession.SessionId == member.SessionId
                    && memberSession.Player.UserId == member.UserId
                    && memberSession.Player.CharacterId == (int)member.CharacterId)
                    AddIfCurrentChannel(result, sender, memberSession);
            }
            return result.Values.ToList();
        }

        private EnhancedClientSession FindDirectTarget(
            ChatMessageRequest request)
        {
            if (request.TargetCharacterId > 0
                && request.TargetCharacterId <= int.MaxValue
                && _sessions.TryGet(
                    (int)request.TargetCharacterId,
                    out var byCharacterId))
            {
                return byCharacterId;
            }

            foreach (var candidate in _sessions.GetAllGameSessions())
            {
                if (candidate?.Player == null)
                    continue;
                if (request.TargetUniqueId != 0
                    && candidate.Player.UserId == request.TargetUniqueId)
                {
                    return candidate;
                }
                if (request.TargetNameBytes.Length > 0
                    && candidate.Player.Name != null
                    && candidate.Player.Name.SequenceEqual(
                        request.TargetNameBytes))
                {
                    return candidate;
                }
            }
            return null;
        }

        public async Task Handle_CREATE_GROUP(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (session?.Player == null
                || session.Player.CharacterId <= 0
                || !TryParseNameArgument(body, out var targetName))
            {
                FileLogger.Log(
                    $"[GameProtocol] CREATE_GROUP invalid " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"bodyBytes={body?.Length ?? 0}");
                return;
            }

            var target = FindSessionByName(targetName);
            if (!IsOnline(target) || target.SessionId == session.SessionId)
            {
                FileLogger.Log(
                    $"[GameProtocol] CREATE_GROUP target offline " +
                    $"cid={session.Player.CharacterId} " +
                    $"targetBytes={targetName.Length}");
                return;
            }

            var actor = new ConversationMember(session);
            var peer = new ConversationMember(target);
            await _transitions.RunIfBothCurrentAsync(session, target, async () =>
            {
                if (!actor.IsCurrent(_sessions) || !peer.IsCurrent(_sessions)) return;
                if (IsBlocked(peer.CharacterId, actor.CharacterId) || IsBlocked(actor.CharacterId, peer.CharacterId))
                {
                    await session.TrySendPacketAsync(GamePacketEnvelopeBuilder.Build(0,
                        (ushort)NotiPacketTypeA21.CREATE_GROUP, new byte[] { 77 }), default,
                        () => actor.IsCurrent(_sessions));
                    return;
                }
                var key = MakeConversationKey(actor.CharacterId, peer.CharacterId);
                Conversation conversation;
                lock (_conversationLock)
                {
                    if (!_activeConversations.TryGetValue(key, out conversation)
                        || !conversation.IsCurrent(_sessions))
                    {
                        conversation = new Conversation(AllocateConversationIdLocked(), actor, peer);
                        _activeConversations[key] = conversation;
                    }
                }
                await SendCreateGroupNotificationAsync(conversation, conversation.First, conversation.Second);
                await SendCreateGroupNotificationAsync(conversation, conversation.Second, conversation.First);
            });
        }

        public Task Handle_ONE_TO_ONE_CHAT_STATE(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            return Task.CompletedTask;
        }

        private Task SendCreateGroupNotificationAsync(
            Conversation conversation,
            ConversationMember recipient,
            ConversationMember peer)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0); // success
            writer.WriteUInt32(conversation.Id);
            writer.WriteByte(2);
            writer.WriteDstr(recipient.Name);
            writer.WriteDstr(peer.Name);
            return SessionDirectory.TrySendBestEffortAsync(token => recipient.Session.TrySendPacketAsync(
                GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.CREATE_GROUP, writer.ToArray()),
                token,
                () => IsActiveConversation(conversation) && !recipient.Published,
                () => recipient.Published = true), $"chat create cid={recipient.CharacterId}");
        }

        private Conversation FindConversation(EnhancedClientSession sender, uint id)
        {
            lock (_conversationLock)
            {
                return _activeConversations.Values.FirstOrDefault(c => c.Id == id
                    && c.Member(sender)?.IsCurrent(_sessions) == true);
            }
        }

        private async Task OnSessionEndingAsync(
            int characterId,
            EnhancedClientSession session)
        {
            Conversation[] removed;
            lock (_conversationLock)
            {
                removed = _activeConversations.Values.Where(c =>
                    c.Member(session)?.CharacterId == characterId).ToArray();
                foreach (var conversation in removed) _activeConversations.Remove(conversation.Key);
            }
            foreach (var conversation in removed)
                await SendConversationLeftAsync(conversation, conversation.Member(session));
        }

        private uint AllocateConversationIdLocked()
        {
            while (_nextConversationId == 0
                || _nextConversationId == uint.MaxValue
                || _activeConversations.Values.Any(c => c.Id == _nextConversationId))
            {
                _nextConversationId++;
            }

            return _nextConversationId++;
        }

        private static ulong MakeConversationKey(int first, int second)
        {
            var low = (uint)Math.Min(first, second);
            var high = (uint)Math.Max(first, second);
            return ((ulong)low << 32) | high;
        }

        private EnhancedClientSession FindSessionByName(byte[] nameBytes)
        {
            if (nameBytes == null || nameBytes.Length == 0)
                return null;

            foreach (var candidate in _sessions.GetAllGameSessions())
            {
                if (candidate?.Player?.Name != null
                    && candidate.Player.Name.SequenceEqual(nameBytes))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static bool TryParseNameArgument(
            byte[] body,
            out byte[] nameBytes)
        {
            nameBytes = Array.Empty<byte>();
            if (body == null || body.Length < 5)
                return false;

            var length = BitConverter.ToInt32(body, 0);
            if (length <= 0 || length > 255 || body.Length != 4 + length)
                return false;

            nameBytes = new byte[length];
            Buffer.BlockCopy(body, 4, nameBytes, 0, length);
            return Infrastructure.ClientTextEncoding.TryGetStringStrict(nameBytes, out var name)
                && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);
        }

        private static bool IsDirectMessageMode(byte mode)
            => mode == DirectMessageMode
                || mode == AlternateDirectMessageMode
                || mode == OneToOneConversationMode;

        private static void AddIfCurrentChannel(
            IDictionary<Guid, EnhancedClientSession> recipients,
            EnhancedClientSession sender,
            EnhancedClientSession candidate)
        {
            if (candidate?.Player == null
                || candidate.Player.CharacterId <= 0
                || candidate.TcpClient == null
                || !candidate.TcpClient.Connected)
            {
                return;
            }
            if (sender.ListenerPort > 0
                && candidate.ListenerPort != sender.ListenerPort)
            {
                return;
            }
            recipients[candidate.SessionId] = candidate;
        }

        private static void AddIfOnline(
            IDictionary<Guid, EnhancedClientSession> recipients,
            EnhancedClientSession candidate)
        {
            if (IsOnline(candidate))
                recipients[candidate.SessionId] = candidate;
        }

        private static bool IsOnline(EnhancedClientSession candidate)
            => candidate?.Player != null
                && candidate.Player.CharacterId > 0
                && candidate.TcpClient != null
                && candidate.TcpClient.Connected;

        internal static bool TryParseRequest(
            byte[] body,
            out ChatMessageRequest request)
        {
            request = null;
            if (body == null || body.Length < 11)
                return false;

            var mode = body[0];
            var targetUniqueId = BitConverter.ToUInt16(body, 1);
            var targetCharacterId = BitConverter.ToUInt32(body, 3);
            var messageLength = BitConverter.ToInt32(body, 7);
            if (messageLength <= 0
                || messageLength > MaximumMessageBytes
                || body.Length < 11 + messageLength)
            {
                return false;
            }

            var messageBytes = new byte[messageLength];
            Buffer.BlockCopy(body, 11, messageBytes, 0, messageLength);
            if (Array.IndexOf(messageBytes, (byte)0) >= 0)
                return false;

            var offset = 11 + messageLength;
            var targetNameBytes = Array.Empty<byte>();
            if (IsDirectMessageMode(mode))
            {
                if (body.Length > offset)
                {
                    if (body.Length < offset + 4)
                        return false;
                    var nameLength = BitConverter.ToInt32(body, offset);
                    if (nameLength < 0
                        || nameLength > (mode == OneToOneConversationMode ? 255 : 30)
                        || (body.Length != offset + 4 + nameLength
                            && body.Length != offset + 5 + nameLength))
                    {
                        return false;
                    }
                    targetNameBytes = new byte[nameLength];
                    if (nameLength > 0)
                    {
                        Buffer.BlockCopy(
                            body,
                            offset + 4,
                            targetNameBytes,
                            0,
                            nameLength);
                    }
                }
            }
            else if (body.Length != offset)
            {
                return false;
            }

            // A21 conversation messages end with a name DSTR and server byte.
            if (mode == OneToOneConversationMode
                && (targetCharacterId == 0 || targetCharacterId == uint.MaxValue
                    || targetNameBytes.Length == 0 || body.Length != offset + 5 + targetNameBytes.Length
                    || !Infrastructure.ClientTextEncoding.TryGetStringStrict(targetNameBytes, out var name)
                    || name.Any(char.IsControl)
                    || !Infrastructure.ClientTextEncoding.TryGetStringStrict(messageBytes, out _)))
                return false;

            request = new ChatMessageRequest(
                mode,
                targetUniqueId,
                targetCharacterId,
                mode == OneToOneConversationMode
                    // Mode 43 uses the target-character field as its
                    // server-assigned conversation id.
                    ? targetCharacterId
                    : 0,
                messageBytes,
                targetNameBytes);
            return true;
        }

        internal static byte[] BuildNotificationBody(
            byte mode,
            ushort senderUniqueId,
            byte serverGroup,
            byte[] messageBytes)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(mode);
            writer.WriteUInt16(senderUniqueId);
            writer.WriteByte(serverGroup);
            writer.WriteDstr(messageBytes ?? Array.Empty<byte>());
            return writer.ToArray();
        }

        internal static byte[] BuildGroupChatNotificationBody(
            uint conversationId,
            byte[] senderNameBytes,
            byte[] messageBytes)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32(conversationId);
            writer.WriteDstr(senderNameBytes ?? Array.Empty<byte>());
            writer.WriteDstr(messageBytes ?? Array.Empty<byte>());
            return writer.ToArray();
        }
    }

    internal sealed class ChatMessageRequest
    {
        internal ChatMessageRequest(
            byte mode,
            ushort targetUniqueId,
            uint targetCharacterId,
            uint conversationId,
            byte[] messageBytes,
            byte[] targetNameBytes)
        {
            Mode = mode;
            TargetUniqueId = targetUniqueId;
            TargetCharacterId = targetCharacterId;
            ConversationId = conversationId;
            MessageBytes = messageBytes ?? Array.Empty<byte>();
            TargetNameBytes = targetNameBytes ?? Array.Empty<byte>();
        }

        internal byte Mode { get; }
        internal ushort TargetUniqueId { get; }
        internal uint TargetCharacterId { get; }
        internal uint ConversationId { get; }
        internal byte[] MessageBytes { get; }
        internal byte[] TargetNameBytes { get; }
    }
}

using System;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Session;

namespace DfoServer.Network.Handlers
{
    public sealed partial class ChatHandler
    {
        internal void RegisterHandlers(GameCommandRegistry.GameCommandRegistrationGroup group)
        {
            group[(ushort)CmdPacketTypeA21.SEND_MESSAGE] = Handle_SEND_MESSAGE;
            group[(ushort)CmdPacketTypeA21.CREATE_GROUP] = Handle_CREATE_GROUP;
            group[(ushort)CmdPacketTypeA21.LEAVE_FROM_GROUP] = Handle_LEAVE_FROM_GROUP;
            group[(ushort)CmdPacketTypeA21.ONE_TO_ONE_CHAT_STATE] = Handle_ONE_TO_ONE_CHAT_STATE;
        }

        private async Task SendConversationMessageAsync(EnhancedClientSession sender, ChatMessageRequest request)
        {
            var conversation = FindConversation(sender, request.ConversationId);
            if (conversation == null) return;
            var actor = conversation.Member(sender);
            var peer = conversation.Other(actor);
            await _transitions.RunIfBothCurrentAsync(sender, peer.Session, async () =>
            {
                if (!IsActiveConversation(conversation)) return;
                // The sender's window already displays its own message.
                await SessionDirectory.TrySendBestEffortAsync(token => peer.Session.TrySendPacketAsync(
                    GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.MESSAGE_GROUP_CHAT,
                        BuildGroupChatNotificationBody(conversation.Id, actor.Name, request.MessageBytes)),
                    token, () => IsActiveConversation(conversation) && actor.Published && peer.Published),
                    $"chat message cid={peer.CharacterId}");
            });
        }

        public async Task Handle_LEAVE_FROM_GROUP(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length != sizeof(uint)) return;
            var conversation = FindConversation(session, BitConverter.ToUInt32(body, 0));
            if (conversation == null) return;
            var actor = conversation.Member(session);
            await _transitions.RunIfCurrentAsync(session, async () =>
            {
                lock (_conversationLock)
                {
                    if (!actor.IsCurrent(_sessions)
                        || !_activeConversations.TryGetValue(conversation.Key, out var current)
                        || !ReferenceEquals(current, conversation)) return;
                    _activeConversations.Remove(conversation.Key);
                }
                // The leaving client has already removed its local window.
                await SendConversationLeftAsync(conversation, actor);
            });
        }

        private Task SendConversationLeftAsync(Conversation conversation, ConversationMember actor)
        {
            var peer = conversation.Other(actor);
            var writer = new GamePacketWriter();
            writer.WriteUInt32(conversation.Id);
            writer.WriteDstr(actor.Name);
            return SessionDirectory.TrySendBestEffortAsync(token => peer.Session.TrySendPacketAsync(
                GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.LEAVE_USER_FROM_GROUP, writer.ToArray()),
                token, () => peer.Published && peer.IsCurrent(_sessions)), $"chat leave cid={peer.CharacterId}");
        }

        private bool IsActiveConversation(Conversation conversation)
        {
            lock (_conversationLock)
                return _activeConversations.TryGetValue(conversation.Key, out var current)
                    && ReferenceEquals(current, conversation) && conversation.IsCurrent(_sessions)
                    && !IsBlocked(conversation.First.CharacterId, conversation.Second.CharacterId)
                    && !IsBlocked(conversation.Second.CharacterId, conversation.First.CharacterId);
        }

        private sealed class ConversationMember
        {
            internal readonly EnhancedClientSession Session;
            internal readonly int CharacterId;
            private readonly ushort _userId;
            internal readonly byte[] Name;
            internal bool Published;

            internal ConversationMember(EnhancedClientSession session)
            {
                Session = session; CharacterId = session.Player.CharacterId;
                _userId = session.Player.UserId; Name = session.Player.Name?.ToArray() ?? Array.Empty<byte>();
            }

            internal bool IsCurrent(ISessionDirectory sessions)
                => CharacterId > 0 && _userId != 0 && Name.Length > 0
                    && IsOnline(Session) && Session.Player.CharacterId == CharacterId
                    && Session.Player.UserId == _userId && Session.Player.Name != null
                    && Session.Player.Name.SequenceEqual(Name)
                    && sessions.TryGet(CharacterId, out var current) && ReferenceEquals(current, Session);
        }

        private sealed class Conversation
        {
            internal readonly uint Id;
            internal readonly ulong Key;
            internal readonly ConversationMember First;
            internal readonly ConversationMember Second;

            internal Conversation(uint id, ConversationMember first, ConversationMember second)
            { Id = id; First = first; Second = second; Key = MakeConversationKey(first.CharacterId, second.CharacterId); }

            internal ConversationMember Member(EnhancedClientSession session)
                => ReferenceEquals(First.Session, session) ? First : ReferenceEquals(Second.Session, session) ? Second : null;
            internal ConversationMember Other(ConversationMember member) => ReferenceEquals(member, First) ? Second : First;
            internal bool IsCurrent(ISessionDirectory sessions) => First.IsCurrent(sessions) && Second.IsCurrent(sessions);
        }
    }
}

using DfoServer.Game.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class ChatHandler
    {
        // 整个 01A9 body 上限：实机样本 169B，给装备 tooltip 数据块留足余量。
        private const int MaximumHyperlinkBodyBytes = 1024;

        // A21 ITEM_HYPERLINK_MESSAGE：头部与 SEND_MESSAGE 同构（mode:u8 +
        // targetUid:u16 + targetCid:u32 + message:dstr），文本之后到包尾为
        // 装备超链接二进制数据块（tooltip 数据，原样转发，不解析）。
        // 私聊 mode 1/7：2026-09-24 packet capture 实机样本（188B）证实 86JP
        // 在文本后追加 targetName:dstr + 1B 私聊标志（与 SEND_MESSAGE 私聊尾部
        // 同构，见 .analysis/86JP-ref ChatHandler 注释），其后才是装备数据块
        // （已观察样本均为 141B，以 01 FF 00 FF FF 开头）。名字与标志必须剥掉，
        // 不得进入下行数据块。
        public async Task Handle_ITEM_HYPERLINK_MESSAGE(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (session?.Player == null
                || session.Player.CharacterId <= 0
                || !TryParseHyperlinkRequest(body, out var request, out var linkBytes))
            {
                FileLogger.Log(
                    $"[GameProtocol] ITEM_HYPERLINK_MESSAGE invalid " +
                    $"cid={session?.Player?.CharacterId ?? 0} " +
                    $"bodyBytes={body?.Length ?? 0}");
                return;
            }

            if (request.Mode == GuildMessageMode)
            {
                int members = await SendGuildHyperlinkMessageAsync(session, request, linkBytes);
                FileLogger.Log(
                    $"[GameProtocol] ITEM_HYPERLINK_MESSAGE cid={session.Player.CharacterId} " +
                    $"uid={session.Player.UserId} mode={request.Mode} " +
                    $"textBytes={request.MessageBytes.Length} blobBytes={linkBytes.Length} " +
                    $"recipients={members}");
                return;
            }

            var recipients = ResolveRecipients(session, request);
            var sendTasks = new List<Task>(recipients.Count);
            foreach (var recipient in recipients)
            {
                if (IsRaidMessageMode(request.Mode))
                {
                    sendTasks.Add(SendRaidHyperlinkMessageAsync(session, recipient, request, linkBytes));
                    continue;
                }
                int senderId = session.Player.CharacterId;
                int recipientId = recipient.Player.CharacterId;
                var packet = GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.MESSAGE_HYPER_LINK,
                    BuildHyperlinkNotificationBody(
                        request.Mode,
                        session.Player.UserId,
                        serverGroup: 0,
                        request.MessageBytes,
                        linkBytes));
                sendTasks.Add(recipient.TrySendPacketAsync(packet, default, () =>
                    session.Player.CharacterId == senderId && recipient.Player.CharacterId == recipientId
                    && _transitions.IsCurrent(session) && _transitions.IsCurrent(recipient)
                    && !IsBlocked(recipientId, senderId)));
            }

            if (sendTasks.Count > 0)
                await Task.WhenAll(sendTasks);

            FileLogger.Log(
                $"[GameProtocol] ITEM_HYPERLINK_MESSAGE cid={session.Player.CharacterId} " +
                $"uid={session.Player.UserId} mode={request.Mode} " +
                $"textBytes={request.MessageBytes.Length} blobBytes={linkBytes.Length} " +
                $"recipients={sendTasks.Count}");
        }

        internal static bool TryParseHyperlinkRequest(
            byte[] body,
            out ChatMessageRequest request,
            out byte[] linkBytes)
        {
            request = null;
            linkBytes = Array.Empty<byte>();
            if (body == null || body.Length < 11 || body.Length > MaximumHyperlinkBodyBytes)
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

            var tailOffset = 11 + messageLength;
            var targetNameBytes = Array.Empty<byte>();
            // 私聊 mode 1/7 才解析尾部 targetName:dstr（长度上限 30，与
            // SEND_MESSAGE TryParseRequest 对齐）。数据块首 4 字节
            // 01 FF 00 FF 作为 i32 是负数，不可能被误读为名字长度；
            // 名字不自洽（如旧客户端不带名字）时回退为"文本后全部是数据块"。
            if (IsDirectMessageMode(mode) && mode != OneToOneConversationMode
                && body.Length > tailOffset)
            {
                var nameLength = BitConverter.ToInt32(body, tailOffset);
                if (nameLength >= 0 && nameLength <= 30
                    && body.Length >= tailOffset + 4 + nameLength)
                {
                    var afterName = tailOffset + 4 + nameLength;
                    // 已观察样本中数据块固定以 01 FF 00 FF FF 开头（5 件装备
                    // 均 141B），私聊标志固定为 0x01。名字后紧跟 01 01 即
                    // "标志 + 数据块"；01 开头但第 2 字节非 01 即"无标志、
                    // 直接数据块"。剥完后数据块必须非空且以 0x01 开头。
                    bool StripName(int newTailOffset)
                    {
                        targetNameBytes = new byte[nameLength];
                        Buffer.BlockCopy(body, tailOffset + 4, targetNameBytes, 0, nameLength);
                        tailOffset = newTailOffset;
                        return true;
                    }
                    if (body.Length - afterName >= 2
                        && body[afterName] == 0x01 && body[afterName + 1] == 0x01)
                        StripName(afterName + 1); // 剥掉 1B 私聊标志
                    else if (body.Length - afterName >= 1 && body[afterName] == 0x01)
                        StripName(afterName); // 无标志，名字后直接是数据块
                }
            }

            linkBytes = new byte[body.Length - tailOffset];
            Buffer.BlockCopy(body, tailOffset, linkBytes, 0, linkBytes.Length);

            request = new ChatMessageRequest(
                mode,
                targetUniqueId,
                targetCharacterId,
                conversationId: 0,
                messageBytes,
                targetNameBytes);
            return true;
        }

        // 公会超链接聊天复用 SEND_MESSAGE mode 6 的持久化成员遍历、黑名单与
        // owned lease 校验；通知包布局按 MESSAGE_OTHER_CHANNEL 同构追加数据块
        // 推断，待实机验证。返回遍历到的成员数（仅用于日志）。
        private async Task<int> SendGuildHyperlinkMessageAsync(
            EnhancedClientSession sender,
            ChatMessageRequest request,
            byte[] linkBytes)
        {
            if (_guilds == null || _guildTransitions == null
                || !Infrastructure.ClientTextEncoding.TryGetStringStrict(request.MessageBytes, out var text)
                || text.IndexOf('\0') >= 0 || string.IsNullOrWhiteSpace(text)) return 0;
            int actor = sender.Player.CharacterId;
            var guild = _guilds.GetForMember(actor);
            if (guild == null) return 0;
            int attempted = 0;
            foreach (int id in _guilds.GetMemberIds(guild.Id))
            {
                attempted++;
                if (!_sessions.TryGet(id, out var recipient)) continue;
                async Task SendCurrent()
                {
                    if (sender.Player.CharacterId != actor || recipient.Player.CharacterId != id
                        || IsBlocked(id, actor)) return;
                    if (!Game.Inventory.InventoryContext.TryGetOwnedLease(sender.SessionId, actor, out _)
                        || !Game.Inventory.InventoryContext.TryGetOwnedLease(recipient.SessionId, id, out _)
                        || _guilds.GetForMember(actor)?.Id != guild.Id || _guilds.GetForMember(id)?.Id != guild.Id) return;
                    await recipient.TrySendPacketAsync(GamePacketEnvelopeBuilder.Build(0,
                        (ushort)NotiPacketTypeA21.MESSAGE_OTHER_CHANNEL_HYPER_LINK,
                        BuildGuildHyperlinkNotificationBody(sender.Player.Name, request.MessageBytes, linkBytes)), default,
                        () => !IsBlocked(id, actor));
                }
                if (id == actor) await _guildTransitions.RunIfCurrentAsync(sender, SendCurrent);
                else await _guildTransitions.RunIfBothCurrentAsync(sender, recipient, SendCurrent);
            }
            return attempted;
        }

        private async Task SendRaidHyperlinkMessageAsync(
            EnhancedClientSession sender,
            EnhancedClientSession recipient,
            ChatMessageRequest request,
            byte[] linkBytes)
        {
            var packet = GamePacketEnvelopeBuilder.Build(0,
                (ushort)NotiPacketTypeA21.MESSAGE_HYPER_LINK,
                BuildHyperlinkNotificationBody(request.Mode, sender.Player.UserId, 0, request.MessageBytes, linkBytes));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (sender.SessionId != recipient.SessionId)
                {
                    var context = RaidHandler.BuildRaidFormationUserContextPacket(sender);
                    if (context == null
                        || !await recipient.TrySendPacketAsync(context, timeout.Token, CanSend))
                    {
                        FileLogger.Log($"[RaidChat] hyperlink context failed from={sender.Player.CharacterId} to={recipient.Player.CharacterId}");
                        return;
                    }
                }
                var sent = await recipient.TrySendPacketAsync(packet, timeout.Token, CanSend);
                FileLogger.Log($"[RaidChat] hyperlink from={sender.Player.CharacterId} to={recipient.Player.CharacterId} sent={sent}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[RaidChat] hyperlink failed from={sender.Player.CharacterId} to={recipient.Player.CharacterId} error={ex.GetType().Name}");
            }
            bool CanSend() => _transitions.IsCurrent(sender) && _transitions.IsCurrent(recipient)
                && !IsBlocked(recipient.Player.CharacterId, sender.Player.CharacterId)
                && ResolveRecipients(sender, request)
                .Any(current => current.SessionId == recipient.SessionId);
        }

        // 布局按 SEND_MESSAGE→MESSAGE 同构推断（mode + uid + serverGroup +
        // dstr(文本)），数据块原文追加在尾部，待实机验证。
        internal static byte[] BuildHyperlinkNotificationBody(
            byte mode,
            ushort senderUniqueId,
            byte serverGroup,
            byte[] messageBytes,
            byte[] linkBytes)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(mode);
            writer.WriteUInt16(senderUniqueId);
            writer.WriteByte(serverGroup);
            writer.WriteDstr(messageBytes ?? Array.Empty<byte>());
            writer.WriteBytes(linkBytes ?? Array.Empty<byte>());
            return writer.ToArray();
        }

        // 布局按 SEND_MESSAGE→MESSAGE_OTHER_CHANNEL 同构推断（mode + 0x00 +
        // dstr(发送者名) + server 字节 + dstr(文本)），数据块原文追加在尾部，
        // 待实机验证。
        internal static byte[] BuildGuildHyperlinkNotificationBody(
            byte[] senderName,
            byte[] message,
            byte[] linkBytes)
        {
            var w = new GamePacketWriter(); w.WriteByte(GuildMessageMode); w.WriteByte(0);
            w.WriteDstr(senderName); w.WriteByte(GameNetworkConfig.ChannelServerIndex); w.WriteDstr(message);
            w.WriteBytes(linkBytes ?? Array.Empty<byte>());
            return w.ToArray();
        }
    }
}

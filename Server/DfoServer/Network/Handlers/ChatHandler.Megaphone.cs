using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers
{
    public sealed partial class ChatHandler
    {
        // Current A21 capture: SEND_MESSAGE mode 11, targetCid=65 selects Main
        // slot 65 containing item 36. PVF stackable.lst maps 36 to
        // cash/megaphone_heartbeat.stk (服务器喇叭, one use, entire server).
        internal const byte ServerMegaphoneMode = 11;
        internal const int ServerMegaphoneItemId = 36;

        private async Task SendServerMegaphoneAsync(EnhancedClientSession sender,
            ChatMessageRequest request, byte[] linkBytes = null)
        {
            if (request.TargetCharacterId > short.MaxValue
                || !ClientTextEncoding.TryGetStringStrict(request.MessageBytes, out var text)
                || string.IsNullOrWhiteSpace(text)
                // A21 equipment links delimit their labels with 0x7F.
                || text.Any(ch => char.IsControl(ch) && !(linkBytes != null && ch == '\u007F'))) return;
            int actor = sender.Player.CharacterId;
            short slot = (short)request.TargetCharacterId;
            bool consumed = false;
            byte[] name = null;
            await _transitions.RunIfCurrentAsync(sender, async () =>
            {
                if (sender.Player.CharacterId != actor
                    || !InventoryContext.TryGetOwnedLease(sender.SessionId, actor, out var lease)) return;
                // Keep resource semantics explicit when a different PVF is installed.
                var entry = ItemMetadataResolver.GetStackableEntry(ServerMegaphoneItemId);
                var definition = StackableItemProvider.Load(ServerMegaphoneItemId);
                if (!string.Equals(entry?.FilePath?.Replace('\\', '/'), "cash/megaphone_heartbeat.stk", StringComparison.OrdinalIgnoreCase)
                    || definition == null || sender.Player.Level < definition.MinimumLevel) return;
                var used = InventoryDeleteCommitService.TryCommitStackableUseDetailed(
                    lease, InventoryListType.Main, slot, ServerMegaphoneItemId);
                consumed = used?.Consumed == true;
                if (consumed) name = sender.Player.Name.ToArray();
                if (used?.Committed == true)
                    await SessionDirectory.TrySendBestEffortAsync(_ =>
                        InventoryRefreshSender.TrySendOwnedItemListRefresh(sender, lease,
                            InventoryListType.Main, () => _transitions.IsCurrent(sender)), "megaphone inventory");
                if (!consumed)
                    await SessionDirectory.TrySendBestEffortAsync(ct => sender.SendPacketAsync(
                        GamePacketEnvelopeBuilder.Build(0, (ushort)NotiPacketTypeA21.SERVER_NOTICE_MESSAGE,
                            ServerNoticeMessageBuilder.Build("服务器喇叭使用失败，请确认道具数量、期限和冷却状态。")), ct), "megaphone rejected");
            });
            if (!consumed) return;

            // The name-bearing reader 01173D70 consumes mode/status/name/server/text;
            // UIDs alone cannot identify a sender in another area or channel.
            var w = new GamePacketWriter(); w.WriteByte(ServerMegaphoneMode); w.WriteByte(0);
            w.WriteDstr(name); w.WriteByte(GameNetworkConfig.ChannelServerIndex); w.WriteDstr(request.MessageBytes);
            if (linkBytes != null) w.WriteBytes(linkBytes);
            var packet = GamePacketEnvelopeBuilder.Build(0,
                (ushort)(linkBytes == null ? NotiPacketTypeA21.MESSAGE_OTHER_CHANNEL
                    : NotiPacketTypeA21.MESSAGE_OTHER_CHANNEL_HYPER_LINK), w.ToArray());
            int delivered = 0;
            await Task.WhenAll(_sessions.GetAllGameSessions().Select(async recipient =>
            {
                int target = recipient.Player.CharacterId;
                await SessionDirectory.TrySendBestEffortAsync(async ct =>
                {
                    if (await recipient.TrySendPacketAsync(packet, ct, () =>
                        sender.Player.CharacterId == actor && recipient.Player.CharacterId == target
                        && _transitions.IsCurrent(sender) && _transitions.IsCurrent(recipient)
                        && InventoryContext.TryGetOwnedLease(sender.SessionId, actor, out _)
                        && InventoryContext.TryGetOwnedLease(recipient.SessionId, target, out _)
                        && !IsBlocked(target, actor))) Interlocked.Increment(ref delivered);
                }, $"megaphone to={target}");
            }));
            FileLogger.Log($"[Megaphone] cid={actor} slot={slot} item={ServerMegaphoneItemId} consumed=1 recipients={delivered} hyperlink={linkBytes != null}");
        }
    }
}

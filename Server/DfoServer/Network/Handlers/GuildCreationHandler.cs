using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Guilds;
using DfoServer.Network.Parsers.Guilds;
using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    internal sealed class GuildCreationHandler
    {
        private readonly GuildRepository _repository;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly InventoryRefreshSender _refresh;
        private readonly Lazy<GuildCreationService> _creation;
        private readonly ConditionalWeakTable<InventoryLease, CreationDraft> _drafts = new();

        internal GuildCreationHandler(GuildRepository repository, CharacterTransitionCoordinator transitions,
            InventoryRefreshSender refresh)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _transitions = transitions ?? throw new ArgumentNullException(nameof(transitions));
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
            _creation = new Lazy<GuildCreationService>(() => new GuildCreationService(_repository, GuildCreationService.ReadCreationCost()));
        }

        internal Task Handle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _transitions.RunIfCurrentAsync(session, async () =>
            {
                if (!InventoryContext.TryGetOwnedLease(session.SessionId, session.Player.CharacterId, out var lease))
                    return;
                var command = (CmdPacketTypeA21)header.type;
                var draft = _drafts.GetOrCreateValue(lease);
                switch (command)
                {
                    case CmdPacketTypeA21.CHECK_GUILD_NAME_DOUBLE:
                        draft.Name = null;
                        draft.Promotion = null;
                        draft.Approved = false;
                        if (!GuildTextRequest.TryParse(body, GuildCreationRules.MaximumNameBytes, out var raw, out _)
                            || !GuildCreationRules.TryValidateName(raw, out var name))
                        {
                            await Reply(session, command, 0x6C);
                            return;
                        }
                        if (_repository.NameExists(name))
                        {
                            await Reply(session, command, 0x6B);
                            return;
                        }
                        if (_repository.IsMember(lease.CharacterId))
                        {
                            await Reply(session, command, 0x6A);
                            return;
                        }
                        draft.Name = name;
                        await Reply(session, command);
                        // The client uploads promotion text only through its permit flow.
                        // Approving here would silently discard the typed promotion.
                        FileLogger.Log($"[Guild] name approved cid={lease.CharacterId}");
                        break;
                    case CmdPacketTypeA21.CHECK_GUILD_CREATE_PROMOTE_MSG:
                        draft.Approved = false;
                        draft.Promotion = null;
                        if (draft.Name == null
                            || !GuildTextRequest.TryParse(body, GuildCreationRules.MaximumPromotionBytes, out _, out var promotion)
                            || !GuildCreationRules.IsValidPromotion(promotion))
                        {
                            await Reply(session, command, 0x6C);
                            return;
                        }
                        draft.Promotion = promotion;
                        await Reply(session, command);
                        break;
                    case CmdPacketTypeA21.REQUEST_GUILD_CREATE_PERMIT:
                        // Client sender 02493890 emits one target-name DSTR. Solo mode
                        // deliberately does not resolve or contact that player.
                        if (draft.Name == null || draft.Promotion == null
                            || !GuildTextRequest.TryParse(body, 64, out _, out _))
                        {
                            await Reply(session, command, 3);
                            return;
                        }
                        draft.Approved = true;
                        await Reply(session, command);
                        await session.SendPacketAsync(GuildCreationPacketBuilder.SinglePlayerPermit());
                        break;
                    case CmdPacketTypeA21.CANCEL_GUILD_CREATE:
                        if (body == null || body.Length == 0) _drafts.Remove(lease);
                        break;
                    case CmdPacketTypeA21.CALL_GUILD_CREATE_RIGHT:
                        // Current client capture: exactly 01; callback 0111A3A0
                        // opens GuildMakeComplete.xui on a successful ACK.
                        if (body == null || body.Length != 1 || body[0] != 1
                            || draft.Name == null || draft.Promotion == null || !draft.Approved)
                        {
                            await session.SendPacketAsync(GuildCreationPacketBuilder.Failure(command));
                            return;
                        }
                        var result = _creation.Value.Create(lease, ClientTextEncoding.GetBytes(draft.Name), draft.Promotion);
                        FileLogger.Log($"[Guild] create cid={lease.CharacterId} status={result.Status} guild={result.Guild?.Id ?? 0}");
                        if (result.Status != GuildResult.Success)
                        {
                            await session.SendPacketAsync(GuildCreationPacketBuilder.Failure(command));
                            return;
                        }
                        _drafts.Remove(lease);
                        session.Player.Subtype0Tail ??= new Game.SelectCharacter.UserInfoMinimumTailSnapshot();
                        GuildIdentityProjection.Apply(result.Guild, session.Player.Subtype0Tail);
                        // Commit has completed; never send while holding the inventory lock.
                        await _refresh.SendGoldUpdate(session);
                        await _refresh.SendNoti2AppearanceUpdate(session);
                        await session.SendPacketAsync(GuildCreationPacketBuilder.Created(result.Guild));
                        await Reply(session, command);
                        break;
                }
            });

        private static Task Reply(EnhancedClientSession session, CmdPacketTypeA21 command, byte error = 0)
            => session.SendPacketAsync(GuildCreationPacketBuilder.Ack(command, error));

        private sealed class CreationDraft
        {
            public string Name;
            public string Promotion;
            public bool Approved;
        }
    }
}

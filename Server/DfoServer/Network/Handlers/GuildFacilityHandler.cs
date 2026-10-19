using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Guilds;

namespace DfoServer.Network.Handlers
{
    internal sealed class GuildFacilityHandler
    {
        private readonly GuildRepository _repository;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly GuildStatePublisher _publisher;
        private readonly InventoryRefreshSender _refresh;
        private readonly Lazy<GuildFacilityService> _facilities;

        internal GuildFacilityHandler(GuildRepository repository, CharacterTransitionCoordinator transitions,
            GuildStatePublisher publisher, InventoryRefreshSender refresh)
        {
            _repository = repository;
            _transitions = transitions;
            _publisher = publisher;
            _refresh = refresh;
            _facilities = new Lazy<GuildFacilityService>(() => GuildFacilityService.FromPvf(repository.Database));
        }

        internal async Task Handle(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var command = (CmdPacketTypeA21)header.type;
            int actor = session.Player.CharacterId;
            if (actor <= 0) return;
            if (IsCargoItemCommand(command))
            {
                FileLogger.Log($"[Guild] cargo item command has no captured body cid={actor} command={command} len={body?.Length ?? -1} hex={Hex(body)}");
                await Ack(session, command, actor, false);
                return;
            }
            var status = GuildResult.NotFound;
            bool current = await _transitions.RunIfCurrentAsync(session, async () =>
            {
                if (!InventoryContext.TryGetOwnedLease(session.SessionId, actor, out var lease)) return;
                var result = Apply(command, lease, body);
                status = result.Status;
                if (result.Status != GuildResult.Success)
                    FileLogger.Log($"[Guild] facility rejected cid={actor} command={command} result={result.Status} len={body?.Length ?? -1} hex={Hex(body)}");
                var ack = result.Status == GuildResult.Success && command == CmdPacketTypeA21.DONATE_GUILD_FUND
                    ? GuildInfoPacketBuilder.Donated(result.Contribution, result.MemberContribution)
                    : GuildCreationPacketBuilder.Ack(command, result.Status == GuildResult.Success ? (byte)0 : (byte)0x22);
                if (result.Status == GuildResult.Success && command == CmdPacketTypeA21.DONATE_GUILD_FUND)
                    FileLogger.Log($"[Guild] donate cid={actor} guild={result.Contribution} member={result.MemberContribution} gold={result.GoldAfter}");
                await SessionDirectory.TrySendBestEffortAsync(ct => session.SendPacketAsync(ack, ct),
                    $"guild facility ack cid={actor} command={command}");
                if (result.Status == GuildResult.Success && SpentGold(command))
                    await _refresh.SendGoldUpdate(session);
            });
            if (current && status == GuildResult.Success && SpentGold(command))
                await _publisher.RefreshAsync(new[] { actor });
        }

        private GuildFacilityResult Apply(CmdPacketTypeA21 command, InventoryLease lease, byte[] body)
        {
            switch (command)
            {
                case CmdPacketTypeA21.DONATE_GUILD_FUND:
                    if (body == null || body.Length != 4) return new(GuildResult.InvalidAmount);
                    int gold = BinaryPrimitives.ReadInt32LittleEndian(body);
                    return _facilities.Value.Donate(lease, gold);
                case CmdPacketTypeA21.CHECK_CREATE_GUILD_AGIT:
                    return CheckAgit(lease);
                case CmdPacketTypeA21.CREATE_GUILD_AGIT:
                case CmdPacketTypeA21.UPGRADE_GUILD_AGIT:
                    if (!EmptyOrLevel(body)) return new(GuildResult.InvalidAmount);
                    return _facilities.Value.CreateOrUpgradeAgit(lease);
                case CmdPacketTypeA21.UPGRADE_GUILD_CARGO:
                    if (!EmptyOrLevel(body)) return new(GuildResult.InvalidAmount);
                    return _facilities.Value.UpgradeCargo(lease);
                default:
                    return new(GuildResult.NotFound);
            }
        }

        private GuildFacilityResult CheckAgit(InventoryLease lease)
        {
            var guild = _repository.GetRosterForMember(lease.CharacterId)?.Guild;
            if (guild == null) return new(GuildResult.NotFound);
            if (guild.LeaderId != lease.CharacterId) return new(GuildResult.NotLeader);
            _ = _facilities.Value;
            var preview = _repository.Database.Read(c => GuildRepository.ReadFacility(c, null, guild.Id));
            var steps = GuildFacilityRules.ReadAgitSteps();
            var next = System.Linq.Enumerable.FirstOrDefault(steps, step => step.Level == preview.AgitLevel + 1);
            if (next.Level == 0) return new(GuildResult.Success, Level: preview.AgitLevel);
            int gold = lease.Inventory.GetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart)?.Count ?? 0;
            if (preview.Contribution < next.Contribution) return new(GuildResult.InsufficientContribution, gold, preview.Contribution, preview.AgitLevel);
            if (gold < next.Gold) return new(GuildResult.InsufficientGold, gold, preview.Contribution, preview.AgitLevel);
            return new(GuildResult.Success, gold, preview.Contribution, preview.AgitLevel);
        }

        private static bool EmptyOrLevel(byte[] body)
            => body == null || body.Length == 0 || body.Length == 1 || body.Length == 4;

        private static bool SpentGold(CmdPacketTypeA21 command)
            => command is CmdPacketTypeA21.DONATE_GUILD_FUND
                or CmdPacketTypeA21.CREATE_GUILD_AGIT
                or CmdPacketTypeA21.UPGRADE_GUILD_AGIT
                or CmdPacketTypeA21.UPGRADE_GUILD_CARGO;

        private static bool IsCargoItemCommand(CmdPacketTypeA21 command)
            => command is CmdPacketTypeA21.GUILD_CARGO
                or CmdPacketTypeA21.GUILD_CARGO_PUSH_ITEM
                or CmdPacketTypeA21.GUILD_CARGO_POP_ITEM
                or CmdPacketTypeA21.GUILD_CARGO_MOVE_ITEM
                or CmdPacketTypeA21.GUILD_CARGO_HISTORY;

        private static Task Ack(EnhancedClientSession session, CmdPacketTypeA21 command, int actor, bool success)
            => SessionDirectory.TrySendBestEffortAsync(ct => session.SendPacketAsync(
                GuildCreationPacketBuilder.Ack(command, success ? (byte)0 : (byte)0x22), ct),
                $"guild facility ack cid={actor} command={command}");

        private static string Hex(byte[] body)
        {
            if (body == null || body.Length == 0) return "";
            int length = Math.Min(body.Length, 64);
            return Convert.ToHexString(body.AsSpan(0, length));
        }
    }
}

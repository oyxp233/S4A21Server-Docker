using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders.Guilds;

namespace DfoServer.Network.Handlers
{
    // Read durable state again under each current character gate. Call only AFTER
    // releasing mutation gates; queued refreshes never replay an old guild snapshot.
    internal sealed class GuildStatePublisher
    {
        private readonly GuildRepository _repository;
        private readonly CharacterTransitionCoordinator _transitions;
        private readonly ISessionDirectory _sessions;
        private readonly InventoryRefreshSender _appearance;
        internal GuildStatePublisher(GuildRepository repository, CharacterTransitionCoordinator transitions,
            ISessionDirectory sessions, InventoryRefreshSender appearance)
        { _repository = repository; _transitions = transitions; _sessions = sessions; _appearance = appearance; }

        // 选角进号在第一条 USERINFO0 之后、城镇状态之前发送。GUILD_INFO 插入公会节点，
        // 紧随的 GUILD_AGIT_INFO 在节点已存在时创建据点子对象并写入等级字节。
        internal static IReadOnlyList<byte[]> BuildLoginNotices(GuildRepository repository, int characterId)
        {
            if (repository == null)
                throw new ArgumentNullException(nameof(repository));

            var roster = repository.GetRosterForMember(characterId);
            if (roster?.Guild == null)
                return Array.Empty<byte[]>();

            return new[]
            {
                GuildInfoPacketBuilder.Build(roster, characterId),
                GuildAgitPacketBuilder.Entrance(repository.GetAgitLevel(roster.Guild.Id)),
            };
        }

        internal async Task RefreshAsync(IEnumerable<int> characters, bool disbanded = false)
        {
            foreach (int id in characters.Distinct().OrderBy(x => x))
                if (_sessions.TryGet(id, out var session))
                    await _transitions.RunIfCurrentAsync(session, async () =>
                    {
                        if (!InventoryContext.TryGetOwnedLease(session.SessionId, id, out _)) return;
                        var roster = _repository.GetRosterForMember(id);
                        session.Player.Subtype0Tail ??= new Game.SelectCharacter.UserInfoMinimumTailSnapshot();
                        uint oldId = session.Player.Subtype0Tail.GuildId;
                        GuildIdentityProjection.Apply(roster?.Guild, session.Player.Subtype0Tail);
                        var pending = roster == null ? _repository.GetPendingApplication(id) : null;
                        await SessionDirectory.TrySendBestEffortAsync(async ct =>
                        {
                        if (oldId != session.Player.Subtype0Tail.GuildId)
                            await _appearance.SendNoti2AppearanceUpdate(session);
                        if (roster == null)
                        {
                            if (oldId != 0) await session.SendPacketAsync(GuildManagementPacketBuilder.Cleared(disbanded), ct);
                            await session.SendPacketAsync(GuildManagementPacketBuilder.Pending(pending), ct);
                            return;
                        }
                        await session.SendPacketAsync(GuildJoinPacketBuilder.Rank(roster.Members.Single(m => m.CharacterId == id).Rank), ct);
                        await session.SendPacketAsync(GuildInfoPacketBuilder.Build(roster, id), ct);
                        await session.SendPacketAsync(GuildAgitPacketBuilder.Entrance(
                            _repository.GetAgitLevel(roster.Guild.Id)), ct);
                        await session.SendPacketAsync(GuildMemberPacketBuilder.Build(roster, FindOnlineChannel), ct);
                        }, $"guild refresh cid={id}");
                    });
        }

        internal byte? FindOnlineChannel(int id)
            => _sessions.TryGet(id, out var session) && _transitions.IsCurrent(session)
                && InventoryContext.TryGetOwnedLease(session.SessionId, id, out _)
                && GameNetworkConfig.TryResolveGameChannel(session.ListenerPort, out var channel)
                ? checked((byte)channel.ChannelId) : null;
    }
}

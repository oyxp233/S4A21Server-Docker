using DfoServer.Game.Dungeon;
using DfoServer.Game.Mercenary;
using DfoServer.Game.Party;
using DfoServer.Game.Raid;
using DfoServer.Game.Session;
using System;

namespace DfoServer.Infrastructure
{
    // 与一个在线会话目录绑定的世界状态。不同运行时不得交叉复用。
    internal sealed class GameProtocolWorldDependencies : IDisposable
    {
        internal GameProtocolWorldDependencies(
            ISessionDirectory sessions,
            CharacterTransitionCoordinator characterTransitions,
            DungeonInstanceRegistry dungeonInstances,
            PartyManager partyManager,
            RaidManager raidManager,
            MercenaryRepository mercenaryRepository,
            IMercenaryRestrictionService mercenaryRestrictions)
        {
            Sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            CharacterTransitions = characterTransitions
                ?? throw new ArgumentNullException(nameof(characterTransitions));
            DungeonInstances = dungeonInstances
                ?? throw new ArgumentNullException(nameof(dungeonInstances));
            PartyManager = partyManager
                ?? throw new ArgumentNullException(nameof(partyManager));
            RaidManager = raidManager
                ?? throw new ArgumentNullException(nameof(raidManager));
            MercenaryRepository = mercenaryRepository
                ?? throw new ArgumentNullException(nameof(mercenaryRepository));
            MercenaryRestrictions = mercenaryRestrictions
                ?? throw new ArgumentNullException(nameof(mercenaryRestrictions));
        }

        internal ISessionDirectory Sessions { get; }

        internal CharacterTransitionCoordinator CharacterTransitions { get; }

        internal DungeonInstanceRegistry DungeonInstances { get; }

        internal PartyManager PartyManager { get; }

        internal RaidManager RaidManager { get; }

        internal MercenaryRepository MercenaryRepository { get; }

        internal IMercenaryRestrictionService MercenaryRestrictions { get; }

        public void Dispose()
        {
            DungeonInstances.Dispose();
        }
    }
}

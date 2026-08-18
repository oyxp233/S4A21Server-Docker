using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Game.Accounts
{
    public sealed class GrowthCapsuleSyncService
    {
        private readonly ICharacterRepository _characterRepository;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly GrowthCapsuleProgressRepository _growthCapsuleRepository;
        private readonly SqliteCharacterProgressRepository _progressRepository;

        public GrowthCapsuleSyncService(ICharacterRepository characterRepository)
            : this(characterRepository, ServerPaths.DatabasePath, ServerPaths.SchemaFilePath)
        {
        }

        public GrowthCapsuleSyncService(
            ICharacterRepository characterRepository,
            string databasePath,
            string schemaFilePath)
            : this(
                characterRepository,
                new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public GrowthCapsuleSyncService(
            ICharacterRepository characterRepository,
            IGameDatabase database)
        {
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            if (database == null) throw new ArgumentNullException(nameof(database));
            _honorLevel = new HonorLevelSyncService(characterRepository, database);
            _growthCapsuleRepository = new GrowthCapsuleProgressRepository(database);
            _progressRepository = new SqliteCharacterProgressRepository(database);
        }

        public async Task SendExpProgressAsync(
            EnhancedClientSession session,
            string reason,
            GrowthCapsuleSummary growthCapsule = null,
            HonorLevelSummary honor = null)
        {
            if (session?.Player == null)
                return;

            if (session.Player.Level < ExpTableProvider.MaxLevel)
                return;

            var accountId = session.Account?.AccountId ?? 0;
            if (accountId <= 0 || session.Player.CharacterId <= 0)
                return;

            honor = honor ?? _honorLevel.LoadSummary(accountId);
            growthCapsule = growthCapsule ?? _growthCapsuleRepository.LoadSummary(accountId);
            if (!TryResolveSkillPointProtocolState(session, out var skillPoints))
                return;

            var displayExp = GrowthCapsuleDataProvider.GetDisplayProgress(
                session.Player.Level, growthCapsule);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.EXP,
                ExpNotificationBuilder.Build(
                    session.Player.Level,
                    session.Player.Exp,
                    skillPoints,
                    honor,
                    growthCapsuleExp: displayExp)));
            FileLogger.Log($"[GameProtocol] GROWTH_CAPSULE_SYNC {reason}: account={accountId} cid={session.Player.CharacterId} level={session.Player.Level} total={growthCapsule.TotalExp} display={displayExp} claimable={growthCapsule.TotalExp >= growthCapsule.RequiredExp} honorLevel={honor.HonorLevel} honorExp={honor.HonorExp}");
        }

        private bool TryResolveSkillPointProtocolState(
            EnhancedClientSession session,
            out SkillPointProtocolState skillPoints)
        {
            skillPoints = default;
            try
            {
                var record = _characterRepository.GetById(session.Player.CharacterId);
                if (record == null)
                {
                    FileLogger.Log($"[GameProtocol] GROWTH_CAPSULE_SYNC skill points failed: character {session.Player.CharacterId} not found");
                }
                else
                {
                    Characters.CharacterStatComputer.DecodeGrowType(record.GrowType, out var capFirstGrow, out var capSecondGrow);
                    skillPoints = SkillStateService.LoadProtocolState(
                        _progressRepository,
                        record.CharacterId,
                        record.Job,
                        session.Player.Level,
                        record.BonusSp,
                        record.BonusTp,
                        persist: false,
                        growType: capFirstGrow,
                        secondGrowType: capSecondGrow);
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[GameProtocol] GROWTH_CAPSULE_SYNC skill points failed: {ex.Message}");
            }

            return false;
        }
    }
}

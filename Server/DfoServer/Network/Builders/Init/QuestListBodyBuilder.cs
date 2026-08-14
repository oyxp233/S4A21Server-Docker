using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Quests;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    public sealed class QuestListBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x0015;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            var character = snapshot.CharacterRecord;
            int level = (character != null) ? character.Level : 1;
            int job = (character != null) ? character.Job : 0;
            int growType = (character != null) ? character.GrowType : -1;

            var clearedFlags = new Dictionary<int, int>();
            foreach (var entry in init.CharacInvisibleFalgs)
            {
                if (entry.FlagValue != 0)
                    clearedFlags[entry.SlotIndex] = entry.FlagValue;
            }

            var allowedCreatureKinds = character != null
                && character.CharacterId > 0
                && InventoryContext.TryGetLease(character.CharacterId, out var lease)
                    ? PetCreatureEvolutionRuntimeService.LoadEligiblePetCreatureEvolutionQuestKinds(lease.Inventory)
                    : null;

            body = BuildBody(level, job, growType, clearedFlags, allowedCreatureKinds);
            return true;
        }

        // 可接任务列表(NOTI 0x0015)包体的唯一构建点 --
        // 选角初始化、交任务后的刷新、副本返城后的刷新三条路径共用。
        public static byte[] BuildBody(int level, int job, int growType, Dictionary<int, int> clearedFlags)
            => BuildBody(level, job, growType, clearedFlags, null);

        public static byte[] BuildBody(
            int level,
            int job,
            int growType,
            Dictionary<int, int> clearedFlags,
            ISet<int> allowedCreatureKinds)
        {
            var clearedSet = new HashSet<int>(clearedFlags.Keys);
            var questIds = GameWorld.QuestData.ComputeAcceptableQuests(
                level,
                job,
                growType,
                clearedSet,
                clearedFlags,
                allowedCreatureKinds);

            var writer = new GamePacketWriter();
            writer.WriteByte((byte)level);
            writer.WriteUInt16((ushort)questIds.Count);
            foreach (var questId in questIds)
                writer.WriteUInt16(questId);
            return writer.ToArray();
        }
    }
}

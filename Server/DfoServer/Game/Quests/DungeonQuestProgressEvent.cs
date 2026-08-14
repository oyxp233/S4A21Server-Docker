using System;
using DfoServer.Game.Dungeon;

namespace DfoServer.Game.Quests
{
    public enum DungeonQuestProgressKind
    {
        HuntMonster = 0,
        ClearMap = 1,
        ClearDungeon = 2,
        HuntEnemy = 3,
    }

    public sealed class DungeonQuestProgressEvent
    {
        private DungeonQuestProgressEvent(
            DungeonEventEnvelope envelope,
            DungeonQuestProgressKind kind,
            int dungeonId,
            int difficulty,
            int mapId,
            int actorCode,
            byte monsterType,
            int enemyType)
        {
            Envelope = envelope ?? throw new ArgumentNullException(nameof(envelope));
            Kind = kind;
            DungeonId = dungeonId;
            Difficulty = difficulty;
            MapId = mapId;
            ActorCode = actorCode;
            MonsterType = monsterType;
            EnemyType = enemyType;
        }

        public DungeonEventEnvelope Envelope { get; }
        public Guid SourceEventId => Envelope.SourceEventId;
        public DungeonQuestProgressKind Kind { get; }
        public int DungeonId { get; }
        public int Difficulty { get; }
        public int MapId { get; }
        public int ActorCode { get; }
        public int MonsterCode => ActorCode;
        public byte MonsterType { get; }
        public int EnemyType { get; }

        public static DungeonQuestProgressEvent HuntMonster(
            DungeonEventEnvelope envelope,
            int dungeonId,
            int difficulty,
            int monsterCode,
            byte monsterType) =>
            new DungeonQuestProgressEvent(
                envelope,
                DungeonQuestProgressKind.HuntMonster,
                dungeonId,
                difficulty,
                mapId: 0,
                actorCode: monsterCode,
                monsterType: monsterType,
                enemyType: 0);

        public static DungeonQuestProgressEvent HuntEnemy(
            DungeonEventEnvelope envelope,
            int dungeonId,
            int difficulty,
            int enemyCode,
            int enemyType) =>
            new DungeonQuestProgressEvent(
                envelope,
                DungeonQuestProgressKind.HuntEnemy,
                dungeonId,
                difficulty,
                mapId: 0,
                actorCode: enemyCode,
                monsterType: 0,
                enemyType: enemyType);

        public static DungeonQuestProgressEvent ClearMap(
            DungeonEventEnvelope envelope,
            int dungeonId,
            int mapId) =>
            new DungeonQuestProgressEvent(
                envelope,
                dungeonId > 0
                    ? DungeonQuestProgressKind.ClearDungeon
                    : DungeonQuestProgressKind.ClearMap,
                dungeonId,
                difficulty: 0,
                mapId,
                actorCode: 0,
                monsterType: 0,
                enemyType: 0);
    }
}

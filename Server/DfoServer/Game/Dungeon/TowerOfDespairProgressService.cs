using System;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Game.Dungeon
{
    public sealed class TowerOfDespairProgressService
    {
        private readonly TowerOfDespairProgressRepository _repository;

        public TowerOfDespairProgressService(TowerOfDespairProgressRepository repository)
        {
            _repository = repository ?? throw new System.ArgumentNullException(nameof(repository));
        }

        public int ResolveEntryDungeonId(int characterId, int requestedDungeonId)
        {
            if (!DungeonData.TryGetTowerOfDespairFloor(requestedDungeonId, out _))
                return requestedDungeonId;

            if (!TryGetNextFloor(characterId, out var nextFloor, out _))
                return requestedDungeonId;

            return DungeonData.TryGetTowerOfDespairDungeonId(nextFloor, out var dungeonId)
                ? dungeonId
                : requestedDungeonId;
        }

        internal bool TryGetNextFloor(
            int characterId,
            out int nextFloor,
            out Exception error)
        {
            nextFloor = 1;
            error = null;
            try
            {
                nextFloor = _repository.GetNextFloor(characterId);
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }

        internal bool TryRecordClear(
            int characterId,
            int clearedDungeonId,
            out int nextFloor,
            out Exception error)
        {
            nextFloor = 0;
            error = null;
            if (!DungeonData.TryGetTowerOfDespairFloor(clearedDungeonId, out var clearedFloor))
                return false;

            try
            {
                nextFloor = _repository.RecordClear(characterId, clearedFloor);
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }
    }
}

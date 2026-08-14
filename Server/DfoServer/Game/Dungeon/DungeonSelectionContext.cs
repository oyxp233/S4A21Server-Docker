using System.Threading;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct DungeonTownReturnAnchor
    {
        internal DungeonTownReturnAnchor(
            byte townId,
            byte areaId,
            short x,
            short y,
            byte direction,
            byte areaState)
        {
            TownId = townId;
            AreaId = areaId;
            X = x;
            Y = y;
            Direction = direction;
            AreaState = areaState;
        }

        internal byte TownId { get; }
        internal byte AreaId { get; }
        internal short X { get; }
        internal short Y { get; }
        internal byte Direction { get; }
        internal byte AreaState { get; }
        internal bool IsValid => TownId > 0;
    }

    // The selection screen has no DungeonRun. Give it an identity so stale
    // asynchronous return work cannot project into a later run.
    internal sealed class DungeonSelectionContext
    {
        private int _returnState;

        internal DungeonSelectionContext(
            long selectionId,
            long runGeneration,
            DungeonTownReturnAnchor returnAnchor)
        {
            SelectionId = selectionId;
            RunGeneration = runGeneration;
            ReturnAnchor = returnAnchor;
        }

        internal long SelectionId { get; }
        internal long RunGeneration { get; }
        internal DungeonTownReturnAnchor ReturnAnchor { get; }
        internal bool IsReturning => Volatile.Read(ref _returnState) == 1;

        internal bool TryBeginReturn() =>
            Interlocked.CompareExchange(ref _returnState, 1, 0) == 0;

        internal void CancelReturn() =>
            Interlocked.CompareExchange(ref _returnState, 0, 1);

        internal bool TryCompleteReturn() =>
            Interlocked.CompareExchange(ref _returnState, 2, 1) == 1;
    }
}

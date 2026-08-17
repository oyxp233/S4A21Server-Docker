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
        private readonly object _circleEntrySyncRoot = new object();
        private int _circleDungeonId;
        private ushort _circleQuestId;

        internal DungeonSelectionContext(
            long selectionId,
            long runGeneration,
            DungeonTownReturnAnchor returnAnchor,
            bool isA21TutorialEntry)
        {
            SelectionId = selectionId;
            RunGeneration = runGeneration;
            ReturnAnchor = returnAnchor;
            IsA21TutorialEntry = isA21TutorialEntry;
        }

        internal long SelectionId { get; }
        internal long RunGeneration { get; }
        internal DungeonTownReturnAnchor ReturnAnchor { get; }
        internal bool IsA21TutorialEntry { get; }
        internal bool IsReturning => Volatile.Read(ref _returnState) == 1;

        internal bool TryBeginReturn() =>
            Interlocked.CompareExchange(ref _returnState, 1, 0) == 0;

        internal void CancelReturn() =>
            Interlocked.CompareExchange(ref _returnState, 0, 1);

        internal bool TryCompleteReturn() =>
            Interlocked.CompareExchange(ref _returnState, 2, 1) == 1;

        internal bool TryBindCircleEntry(int dungeonId, ushort circleQuestId)
        {
            if (dungeonId <= 0 || circleQuestId == 0 || IsReturning)
                return false;

            lock (_circleEntrySyncRoot)
            {
                if (IsReturning)
                    return false;

                _circleDungeonId = dungeonId;
                _circleQuestId = circleQuestId;
                return true;
            }
        }

        internal bool TryConsumeCircleEntry(
            int dungeonId,
            out ushort circleQuestId)
        {
            lock (_circleEntrySyncRoot)
            {
                var pendingDungeonId = _circleDungeonId;
                var pendingQuestId = _circleQuestId;
                _circleDungeonId = 0;
                _circleQuestId = 0;

                circleQuestId = pendingDungeonId == dungeonId
                    ? pendingQuestId
                    : (ushort)0;
                return circleQuestId != 0;
            }
        }
    }
}

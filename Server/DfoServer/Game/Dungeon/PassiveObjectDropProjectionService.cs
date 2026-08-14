using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal sealed class PassiveObjectDropProjectionResult
    {
        internal static readonly PassiveObjectDropProjectionResult Empty =
            new PassiveObjectDropProjectionResult(
                Array.Empty<PassiveObjectDropEntry>(),
                invalidIntentCount: 0,
                staleRoom: false,
                sceneSlotsExhausted: false);

        internal PassiveObjectDropProjectionResult(
            IReadOnlyList<PassiveObjectDropEntry> entries,
            int invalidIntentCount,
            bool staleRoom,
            bool sceneSlotsExhausted)
        {
            Entries = entries ?? Array.Empty<PassiveObjectDropEntry>();
            InvalidIntentCount = Math.Max(0, invalidIntentCount);
            StaleRoom = staleRoom;
            SceneSlotsExhausted = sceneSlotsExhausted;
        }

        internal IReadOnlyList<PassiveObjectDropEntry> Entries { get; }
        internal int InvalidIntentCount { get; }
        internal bool StaleRoom { get; }
        internal bool SceneSlotsExhausted { get; }
    }

    internal static class PassiveObjectDropProjectionService
    {
        internal static PassiveObjectDropProjectionResult ProjectAndRegister(
            DungeonRun run,
            DungeonInstanceRoom room,
            PassiveObjectDropPlan plan)
        {
            if (run == null || room == null || plan == null)
                return PassiveObjectDropProjectionResult.Empty;

            lock (run.SyncRoot)
            {
                if (!TryGetProjectionState(run, room, out var roomState))
                {
                    return new PassiveObjectDropProjectionResult(
                        Array.Empty<PassiveObjectDropEntry>(),
                        invalidIntentCount: 0,
                        staleRoom: true,
                        sceneSlotsExhausted: false);
                }
                if (roomState.PassiveObjectDropEntries != null)
                {
                    return new PassiveObjectDropProjectionResult(
                        roomState.PassiveObjectDropEntries,
                        invalidIntentCount: 0,
                        staleRoom: false,
                        sceneSlotsExhausted: false);
                }
            }

            var prepared = new List<PreparedDrop>(plan.Intents.Count);
            var invalidIntentCount = 0;
            for (var index = 0; index < plan.Intents.Count; index++)
            {
                var intent = plan.Intents[index];
                if (!TryPrepare(intent, out var drop))
                {
                    invalidIntentCount++;
                    continue;
                }

                prepared.Add(new PreparedDrop(intent.ObjectIndex, drop));
            }

            lock (run.SyncRoot)
            {
                if (!TryGetProjectionState(run, room, out var roomState))
                {
                    return new PassiveObjectDropProjectionResult(
                        Array.Empty<PassiveObjectDropEntry>(),
                        invalidIntentCount,
                        staleRoom: true,
                        sceneSlotsExhausted: false);
                }
                if (roomState.PassiveObjectDropEntries != null)
                {
                    return new PassiveObjectDropProjectionResult(
                        roomState.PassiveObjectDropEntries,
                        invalidIntentCount: 0,
                        staleRoom: false,
                        sceneSlotsExhausted: false);
                }

                if (prepared.Count == 0)
                {
                    roomState.PassiveObjectDropEntries =
                        Array.Empty<PassiveObjectDropEntry>();
                    return new PassiveObjectDropProjectionResult(
                        roomState.PassiveObjectDropEntries,
                        invalidIntentCount,
                        staleRoom: false,
                        sceneSlotsExhausted: false);
                }

                if (!TryReserveSceneSlots(
                        run,
                        prepared.Count,
                        out var slots,
                        out var finalCounter))
                {
                    roomState.PassiveObjectDropEntries =
                        Array.Empty<PassiveObjectDropEntry>();
                    return new PassiveObjectDropProjectionResult(
                        roomState.PassiveObjectDropEntries,
                        invalidIntentCount,
                        staleRoom: false,
                        sceneSlotsExhausted: true);
                }

                var entries = new PassiveObjectDropEntry[prepared.Count];
                for (var index = 0; index < prepared.Count; index++)
                {
                    var current = prepared[index];
                    var drop = current.Drop;
                    drop.SceneSlot = slots[index];
                    run.Drops.Add(drop.SceneSlot, drop);
                    entries[index] = new PassiveObjectDropEntry
                    {
                        ObjectIndex = current.ObjectIndex,
                        GlobalSeq = drop.SceneSlot,
                        ItemId = drop.TemplateId,
                        StackCount = drop.StackCount,
                        Endurance = drop.Endurance,
                        Core = drop.Core?.Copy(),
                    };
                }
                run.SceneSlotCounter = finalCounter;
                roomState.PassiveObjectDropEntries = Array.AsReadOnly(entries);

                return new PassiveObjectDropProjectionResult(
                    roomState.PassiveObjectDropEntries,
                    invalidIntentCount,
                    staleRoom: false,
                    sceneSlotsExhausted: false);
            }
        }

        private static bool TryGetProjectionState(
            DungeonRun run,
            DungeonInstanceRoom room,
            out RoomState roomState)
        {
            roomState = null;
            return run.PartyDungeonInstanceId == room.PartyDungeonInstanceId
                && run.CurrentRoomInstanceId == room.RoomInstanceId
                && run.RoomStates.TryGetValue(room.Key, out roomState)
                && roomState != null
                && ReferenceEquals(roomState.InstanceRoom, room);
        }

        private static bool TryPrepare(
            PassiveObjectDropIntent intent,
            out DropInfo drop)
        {
            drop = default;
            if (intent.Amount <= 0)
                return false;

            switch (intent.Kind)
            {
                case PassiveObjectDropIntentKind.Gold:
                    if (intent.ItemId != 0)
                        return false;
                    drop = DropInfo.CreateGold(sceneSlot: 0, intent.Amount);
                    return drop.StackCount > 0;

                case PassiveObjectDropIntentKind.Item:
                    if (intent.ItemId <= 0)
                        return false;
                    drop = DropInfo.CreateItem(
                        sceneSlot: 0,
                        intent.ItemId,
                        intent.Amount);
                    return drop.TemplateId > 0 && drop.Core != null;

                default:
                    return false;
            }
        }

        private static bool TryReserveSceneSlots(
            DungeonRun run,
            int count,
            out ushort[] slots,
            out ushort finalCounter)
        {
            slots = Array.Empty<ushort>();
            finalCounter = run.SceneSlotCounter;
            if (count <= 0)
                return true;

            var reserved = new HashSet<ushort>();
            var candidate = run.SceneSlotCounter;
            var result = new ushort[count];
            for (var index = 0; index < count; index++)
            {
                var found = false;
                for (var attempt = 0; attempt < ushort.MaxValue; attempt++)
                {
                    candidate++;
                    if (candidate == 0)
                        candidate++;
                    if (run.Drops.ContainsKey(candidate)
                        || !reserved.Add(candidate))
                    {
                        continue;
                    }

                    result[index] = candidate;
                    found = true;
                    break;
                }

                if (!found)
                    return false;
            }

            slots = result;
            finalCounter = candidate;
            return true;
        }

        private readonly struct PreparedDrop
        {
            internal PreparedDrop(byte objectIndex, DropInfo drop)
            {
                ObjectIndex = objectIndex;
                Drop = drop;
            }

            internal byte ObjectIndex { get; }
            internal DropInfo Drop { get; }
        }
    }
}

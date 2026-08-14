using System;
using DfoServer.Game.Characters;
using DfoServer.GameWorld;

namespace DfoServer.Network
{
    public sealed class GameChannelSpawn
    {
        public GameChannelSpawn(
            byte townId,
            byte areaId,
            short x,
            short y,
            byte direction,
            byte areaState,
            bool isTransient)
        {
            TownId = townId;
            AreaId = areaId;
            X = x;
            Y = y;
            Direction = direction;
            AreaState = areaState;
            IsTransient = isTransient;
        }

        public byte TownId { get; }

        public byte AreaId { get; }

        public short X { get; }

        public short Y { get; }

        public byte Direction { get; }

        public byte AreaState { get; }

        public bool IsTransient { get; }

        public void ApplyTo(CharacterRecord record)
        {
            if (record == null)
                throw new ArgumentNullException(nameof(record));

            record.TownId = TownId;
            record.AreaId = AreaId;
            record.PosX = X;
            record.PosY = Y;
            record.Direction = Direction;
            record.AreaState = AreaState;
        }
    }

    public static class GameChannelSpawnPolicy
    {
        public const byte Channel100TownId = 17;
        public const byte Channel100SpawnAreaId = 4;
        public const byte RaidTownId = 19;
        public const byte NormalTownFallbackId = 20;

        public static GameChannelSpawn Resolve(
            int listenerGamePort,
            int persistedTownId)
        {
            if (TryResolveTransientSpawn(listenerGamePort, out var transientSpawn))
                return transientSpawn;

            var townId = persistedTownId > 0
                ? persistedTownId
                : 1;
            if (townId == RaidTownId)
                townId = NormalTownFallbackId;
            var gate = Town.GetCeraRoomInfo(townId);
            if (gate.Town <= 0)
                throw new InvalidOperationException(
                    $"Town {townId} has no Seria-room gate.");

            return new GameChannelSpawn(
                gate.Town,
                gate.Area,
                gate.X,
                gate.Y,
                direction: 5,
                areaState: 3,
                isTransient: false);
        }

        public static bool TryResolveTransientSpawn(
            int listenerGamePort,
            out GameChannelSpawn spawn)
        {
            spawn = null;
            if (GameNetworkConfig.IsRaidListener(listenerGamePort))
            {
                var raidGate = Town.GetCeraRoomInfo(RaidTownId);
                if (raidGate.Town <= 0)
                    throw new InvalidOperationException(
                        $"Town {RaidTownId} has no Seria-room gate.");

                spawn = new GameChannelSpawn(
                    raidGate.Town,
                    raidGate.Area,
                    raidGate.X,
                    raidGate.Y,
                    direction: 5,
                    areaState: 3,
                    isTransient: true);
                return true;
            }

            if (!GameNetworkConfig.IsChannel100Listener(listenerGamePort))
                return false;

            if (!Town.TryGetDungeonGateReturnInfo(
                    Channel100TownId,
                    Channel100SpawnAreaId,
                    out var gate))
            {
                throw new InvalidOperationException(
                    $"Town {Channel100TownId} area " +
                    $"{Channel100SpawnAreaId} has no dungeon-gate anchor.");
            }

            spawn = new GameChannelSpawn(
                gate.Town,
                gate.Area,
                gate.X,
                gate.Y,
                direction: 5,
                areaState: 3,
                isTransient: true);
            return true;
        }

        public static bool CanEnterTown(
            int listenerGamePort,
            int targetTownId)
        {
            if (GameNetworkConfig.IsRaidListener(listenerGamePort))
                return targetTownId == RaidTownId;
            return !GameNetworkConfig.IsChannel100Listener(listenerGamePort)
                   || targetTownId == Channel100TownId;
        }

        public static bool ShouldPersistPosition(int listenerGamePort)
        {
            return !GameNetworkConfig.IsChannel100Listener(listenerGamePort)
                   && !GameNetworkConfig.IsRaidListener(listenerGamePort);
        }
    }
}

using DfoServer.Game.Dungeon;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Inventory
{
    internal sealed class EpicPieceDropRollRequest
    {
        internal int DungeonId { get; set; }
        internal int MonsterId { get; set; }
        internal byte DungeonDifficulty { get; set; }
        internal byte HellDifficulty { get; set; }
        internal int DungeonMinimumLevel { get; set; }
        internal int DungeonBasisLevel { get; set; }
        internal DnfLcg Random { get; set; }
    }

    internal sealed class EpicPieceDropRollResult
    {
        internal int EpicEquipmentId { get; set; }
        internal int EpicPieceId { get; set; }
        internal int Count { get; set; }
    }

    internal interface IEpicPieceDropService
    {
        IReadOnlyList<EpicPieceDropRollResult> Roll(
            EpicPieceDropRollRequest request);

        bool TryRoll(
            EpicPieceDropRollRequest request,
            out EpicPieceDropRollResult result);
    }

    internal sealed class EpicPieceDropService : IEpicPieceDropService
    {
        private const int EpicEquipmentRarity = 4;

        internal static readonly EpicPieceDropService Instance =
            new EpicPieceDropService();

        private EpicPieceDropService()
        {
        }

        public IReadOnlyList<EpicPieceDropRollResult> Roll(
            EpicPieceDropRollRequest request)
        {
            var dungeonDifficulty = request != null
                ? request.DungeonDifficulty
                : (byte)0;
            var hellDifficulty = request != null
                ? request.HellDifficulty
                : (byte)0;
            var rollCount = ResolveRollCount(
                dungeonDifficulty,
                hellDifficulty);
            if (rollCount <= 0)
                return Array.Empty<EpicPieceDropRollResult>();

            var results = new List<EpicPieceDropRollResult>(rollCount);
            for (var i = 0; i < rollCount; i++)
            {
                if (TryRoll(request, out var result))
                    results.Add(result);
            }

            return results;
        }

        public bool TryRoll(
            EpicPieceDropRollRequest request,
            out EpicPieceDropRollResult result)
        {
            result = null;
            if (request == null || request.Random == null)
                return false;

            if (!HellMonsterDropConfig.TryChooseSpecificEquipment(
                    request.Random,
                    request.DungeonMinimumLevel,
                    request.DungeonBasisLevel,
                    EpicEquipmentRarity,
                    allowFallback: false,
                    predicate: HasEpicPiece,
                    out var equipmentId,
                    out var candidateCount,
                    out var fallbackUsed,
                    out var gradeMin,
                    out var gradeMax))
            {
                FileLogger.Log(
                    $"[EpicPieceDrop] no mapped epic equipment " +
                    $"dungeon={request.DungeonId} monster={request.MonsterId} " +
                    $"levelRange={request.DungeonMinimumLevel}-{request.DungeonBasisLevel} " +
                    $"gradeRange={gradeMin}-{gradeMax} candidates={candidateCount} " +
                    $"fallback={fallbackUsed}");
                return false;
            }

            if (!EpicPieceCatalogService.TryGetEntryByOutputId(
                    equipmentId,
                    out var entry))
            {
                FileLogger.Log(
                    $"[EpicPieceDrop] mapped equipment missing catalog entry " +
                    $"equipment={equipmentId}");
                return false;
            }

            result = new EpicPieceDropRollResult
            {
                EpicEquipmentId = equipmentId,
                EpicPieceId = entry.EpicPieceId,
                Count = 1,
            };
            return true;
        }

        internal static int ResolveRollCount(
            byte dungeonDifficulty,
            byte hellDifficulty)
        {
            var hellCount = hellDifficulty switch
            {
                1 => 1,
                2 => 0,
                _ => 0,
            };

            var dungeonCount = dungeonDifficulty switch
            {
                0 => 5,
                1 => 6,
                2 => 8,
                3 => 9,
                4 => 10,
                _ => 0,
            };
            return hellCount + dungeonCount;
        }

        private static bool HasEpicPiece(int equipmentId)
            => EpicPieceCatalogService.TryGetEntryByOutputId(
                equipmentId,
                out _);
    }
}

using DfoServer.Game.CharacterData;
using System.Collections.Generic;

namespace DfoServer.Game.Skills
{
    public sealed class DarkKnightComboSkillSaveResult
    {
        public bool Saved { get; set; }

        public int Page { get; set; }

        public int QuickSlotsCleaned { get; set; }
    }

    public sealed class DarkKnightComboSkillService
    {
        private readonly SqliteCharacterProgressRepository _skillRepository;
        private readonly SqliteDarkKnightComboSkillRepository _comboRepository;

        public DarkKnightComboSkillService(
            SqliteCharacterProgressRepository skillRepository,
            SqliteDarkKnightComboSkillRepository comboRepository)
        {
            _skillRepository = skillRepository;
            _comboRepository = comboRepository;
        }

        public int SaveComboSkillInfo(int characterId, byte[] body)
            => SaveComboSkillInfoCore(characterId, body, cleanSavedChildren: false).Saved ? 1 : 0;

        public DarkKnightComboSkillSaveResult SaveAutoComboSkillInfo(int characterId, byte[] body)
            => SaveComboSkillInfoCore(characterId, body, cleanSavedChildren: true);

        public DarkKnightComboSkillSaveResult CleanDuplicateQuickSlots(int characterId, int page)
        {
            page = page == 1 ? 1 : 0;
            var comboBodies = _comboRepository.LoadPageBodies(characterId);
            var rootSkillIds = DarkKnightComboSkillInfoCodec.GetRootSkillIds(comboBodies);
            var savedChildSkillIds = GetChildSkillIdsForPage(comboBodies, page);
            var moves = BuildAutoComboCandidateMoves(characterId, page, rootSkillIds, savedChildSkillIds);
            var moved = _skillRepository.MoveSkillsToSlots(characterId, page, moves);
            return new DarkKnightComboSkillSaveResult
            {
                Saved = true,
                Page = page,
                QuickSlotsCleaned = moved,
            };
        }

        public bool SwapDarkKnightSkillSlot(int characterId, int page, int slot1, int slot2)
        {
            if (slot1 == slot2)
                return false;

            page = page == 1 ? 1 : 0;
            var rows = _skillRepository.LoadSkillSlots(characterId, page);
            var beforeSlot1 = SkillIdAtSlot(rows, slot1);
            if (beforeSlot1.HasValue)
            {
                _skillRepository.SwapSkillSlot(characterId, page, slot1, slot2);
                return true;
            }

            var beforeSlot2 = SkillIdAtSlot(rows, slot2);
            var comboChildSkillIds = GetChildSkillIdsForPage(_comboRepository.LoadPageBodies(characterId), page);
            var inferred = InferMovedComboChildSkillId(slot1, slot2, beforeSlot1, beforeSlot2, comboChildSkillIds);
            if (!inferred.HasValue)
                return false;

            return _skillRepository.MoveSkillToSlot(characterId, page, inferred.Value, slot2);
        }

        private DarkKnightComboSkillSaveResult SaveComboSkillInfoCore(
            int characterId,
            byte[] body,
            bool cleanSavedChildren)
        {
            if (characterId <= 0 || body == null || body.Length == 0)
                return new DarkKnightComboSkillSaveResult();

            var storage = DarkKnightComboSkillInfoCodec.NormalizePageBlock(body);
            if (!DarkKnightComboSkillInfoCodec.IsValidPageBlock(storage))
                return new DarkKnightComboSkillSaveResult();

            var page = storage[0] == 1 ? 1 : 0;
            _comboRepository.SavePageBody(characterId, storage);
            var moves = cleanSavedChildren
                ? BuildSavedChildMoves(characterId, page, DarkKnightComboSkillInfoCodec.GetChildSkillIds(storage))
                : new List<SkillSlotMove>();
            var moved = _skillRepository.MoveSkillsToSlots(characterId, page, moves);

            return new DarkKnightComboSkillSaveResult
            {
                Saved = true,
                Page = page,
                QuickSlotsCleaned = moved,
            };
        }

        private List<SkillSlotMove> BuildSavedChildMoves(
            int characterId,
            int page,
            HashSet<ushort> childSkillIds)
        {
            if (childSkillIds == null || childSkillIds.Count == 0)
                return new List<SkillSlotMove>();

            return BuildMoves(characterId, page, skillId => childSkillIds.Contains(skillId));
        }

        private List<SkillSlotMove> BuildAutoComboCandidateMoves(
            int characterId,
            int page,
            HashSet<ushort> rootSkillIds,
            HashSet<ushort> savedChildSkillIds)
        {
            return BuildMoves(
                characterId,
                page,
                skillId => IsDarkKnightAutoComboCandidate(skillId, rootSkillIds, savedChildSkillIds));
        }

        private List<SkillSlotMove> BuildMoves(
            int characterId,
            int page,
            System.Predicate<ushort> shouldMove)
        {
            var rows = _skillRepository.LoadSkillSlots(characterId, page);
            var occupiedSlots = new HashSet<int>();
            foreach (var row in rows)
            {
                if (row.Slot >= 0)
                    occupiedSlots.Add(row.Slot);
            }

            var moves = new List<SkillSlotMove>();
            foreach (var row in rows)
            {
                if (!DarkKnightComboSkillInfoCodec.IsShortcutSlot(row.Slot)
                    || !shouldMove(row.SkillId))
                {
                    continue;
                }

                var targetSlot = FindFreeNonShortcutSlot(occupiedSlots);
                if (!targetSlot.HasValue)
                    continue;

                occupiedSlots.Remove(row.Slot);
                occupiedSlots.Add(targetSlot.Value);
                moves.Add(new SkillSlotMove
                {
                    SkillId = row.SkillId,
                    ToSlot = targetSlot.Value,
                });
            }

            return moves;
        }

        private static HashSet<ushort> GetChildSkillIdsForPage(IEnumerable<byte[]> bodies, int page)
        {
            var ids = new HashSet<ushort>();
            if (bodies == null)
                return ids;

            foreach (var body in bodies)
            {
                if (body == null || body.Length == 0 || (body[0] == 1 ? 1 : 0) != page)
                    continue;

                foreach (var skillId in DarkKnightComboSkillInfoCodec.GetChildSkillIds(body))
                    ids.Add(skillId);
            }

            return ids;
        }

        private static ushort? SkillIdAtSlot(IEnumerable<SkillSlotRecord> rows, int slot)
        {
            if (rows == null)
                return null;

            foreach (var row in rows)
            {
                if (row.Slot == slot)
                    return row.SkillId;
            }

            return null;
        }

        private static ushort? InferMovedComboChildSkillId(
            int fromSlot,
            int toSlot,
            ushort? fromSkillId,
            ushort? toSkillId,
            HashSet<ushort> comboChildSkillIds)
        {
            if (fromSkillId.HasValue)
                return fromSkillId;

            if (DarkKnightComboSkillInfoCodec.IsShortcutSlot(fromSlot)
                || !DarkKnightComboSkillInfoCodec.IsOrdinaryQuickSlot(toSlot)
                || comboChildSkillIds == null
                || comboChildSkillIds.Count == 0)
            {
                return null;
            }

            var candidates = new List<ushort>();
            foreach (var skillId in comboChildSkillIds)
            {
                if (!toSkillId.HasValue || skillId != toSkillId.Value)
                    candidates.Add(skillId);
            }

            return candidates.Count == 1 ? candidates[0] : (ushort?)null;
        }

        internal static bool IsDarkKnightAutoComboCandidate(
            ushort skillId,
            HashSet<ushort> rootSkillIds,
            HashSet<ushort> savedChildSkillIds)
        {
            if (rootSkillIds != null && rootSkillIds.Contains(skillId))
                return false;

            if (savedChildSkillIds != null && savedChildSkillIds.Contains(skillId))
                return true;

            var data = SkillDataProvider.GetSkill(9, skillId);
            if (data == null)
                return false;

            return data.IsActive
                && !data.IsSpecial
                && !data.IsTpSkill
                && data.RawGroup >= 0
                && data.RawGroup <= 3
                && IsDarkKnightNativeComboSkillPath(data.PvfPath);
        }

        private static bool IsDarkKnightNativeComboSkillPath(string pvfPath)
        {
            if (string.IsNullOrWhiteSpace(pvfPath))
                return false;

            var normalized = pvfPath.Replace('\\', '/');
            return normalized.StartsWith("DemonicSwordman/", System.StringComparison.OrdinalIgnoreCase)
                && normalized.IndexOf("/NotApplicable/", System.StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static int? FindFreeNonShortcutSlot(HashSet<int> occupiedSlots)
        {
            for (var slot = 150; slot <= 197; slot++)
            {
                if (!occupiedSlots.Contains(slot))
                    return slot;
            }

            for (var slot = 12; slot <= 197; slot++)
            {
                if (!occupiedSlots.Contains(slot))
                    return slot;
            }

            return null;
        }
    }
}

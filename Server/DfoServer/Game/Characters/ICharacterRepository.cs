using System.Collections.Generic;

namespace DfoServer.Game.Characters
{
    public interface ICharacterRepository
    {
        CharacterRecord GetById(int characterId);
        IReadOnlyList<CharacterRecord> ListByAccount(int accountId);
        int Create(CharacterRecord record);
        void UpdatePosition(int characterId, byte townId, byte areaId, short posX, short posY, byte direction, byte areaState);
        void UpdateSeedFields(int characterId, byte[] name, byte job, byte growType, byte level, byte pvpGrade, byte pvpRatingGrade, byte userState, CharacterAppearanceEntry[] appearance, System.DateTime? createdAt = null);
        void UpdateAppearance(int characterId, CharacterAppearanceEntry[] appearance);
        void SoftDelete(int characterId);
        CharacterRecord GetByName(string name);
        CharacterRecord GetByNameIncludingDeleted(string name);
        int CountByAccount(int accountId);
        void SwapSlotIndexes(int accountId, byte slotA, byte slotB);
    }
}

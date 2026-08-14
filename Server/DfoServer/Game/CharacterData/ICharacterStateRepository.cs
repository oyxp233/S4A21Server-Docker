using DfoServer.Game.SelectCharacter;

namespace DfoServer.Game.CharacterData
{
    public interface ICharacterStateRepository
    {
        void LoadFlags(int characterId, SelectCharacterInitializationSnapshot snapshot);

        void SaveFlags(int characterId, SelectCharacterInitializationSnapshot snapshot);

        void SaveCharacterOption(int characterId, byte[] body);

        void SaveMoodValue(int characterId, ushort moodValue);

        void SaveHotkeyConfig(int characterId, byte[] hotkeys);

        bool HasFlags(int characterId);

        void InitializeEmptyState(int characterId);

        void LoadAll(int characterId, SelectCharacterInitializationSnapshot snapshot);
    }
}

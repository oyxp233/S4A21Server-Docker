using DfoServer.Game.Characters;
using DfoServer.Game.KnightShield;
using System.Collections.Generic;

namespace DfoServer.Game.SelectCharacter
{
    public interface ISelectCharacterDataSource
    {
        
        
        
        
        
        SelectCharacterDataSnapshot Load(int characterId, int accountId);

        int GetSeedCharacterId();

        void InitializeNewCharacter(int characterId, int accountId, byte job);
    }

    public sealed class SelectCharacterDataSnapshot
    {
        public SelectCharacterInitializationSnapshot InitializationSnapshot { get; set; } = new SelectCharacterInitializationSnapshot();

        public KnightShieldDeckSnapshot KnightShieldDeck { get; set; }

        public CharacterRecord CharacterRecord { get; set; }
    }
}

using System;

namespace DfoServer.Game.Mercenary
{
    public interface IMercenaryRestrictionService
    {
        bool IsAssigned(int characterId);
        bool CanDelete(int characterId);
        bool CanMutateAppearance(int characterId);
        bool CanEnterContent(int characterId);
    }

    public sealed class MercenaryRestrictionService : IMercenaryRestrictionService
    {
        private readonly MercenaryRepository _repository;

        public MercenaryRestrictionService(MercenaryRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        public bool IsAssigned(int characterId) => _repository.IsAssigned(characterId);
        public bool CanDelete(int characterId) => !IsAssigned(characterId);
        public bool CanMutateAppearance(int characterId) => !IsAssigned(characterId);
        public bool CanEnterContent(int characterId) => !IsAssigned(characterId);
    }
}

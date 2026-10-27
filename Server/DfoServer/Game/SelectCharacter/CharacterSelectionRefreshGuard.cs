using System;
using DfoServer.Game.Characters;

namespace DfoServer.Game.SelectCharacter
{
    internal static class CharacterSelectionRefreshGuard
    {
        internal static bool TryGetFresh(
            Func<int, CharacterRecord> load,
            int accountId,
            int characterId,
            out CharacterRecord current)
        {
            if (load == null) throw new ArgumentNullException(nameof(load));
            current = null;
            if (accountId <= 0 || characterId <= 0) return false;
            var candidate = load(characterId);
            if (candidate == null || candidate.Deleted
                || candidate.AccountId != accountId || candidate.CharacterId != characterId)
                return false;
            current = candidate;
            return true;
        }
    }
}

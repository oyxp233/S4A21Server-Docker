using System.Collections.Concurrent;
using System.Threading;

namespace DfoServer.Game.ExpertJob
{
    internal sealed class ExpertJobOperationCoordinator
    {
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _gates =
            new ConcurrentDictionary<int, SemaphoreSlim>();

        internal SemaphoreSlim GetGate(int characterId)
            => _gates.GetOrAdd(characterId, _ => new SemaphoreSlim(1, 1));
    }
}

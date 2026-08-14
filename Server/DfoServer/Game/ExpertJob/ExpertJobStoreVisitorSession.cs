using System;

namespace DfoServer.Game.ExpertJob
{
    public sealed class ExpertJobStoreVisitorSession
    {
        public Guid VisitorSessionId { get; set; }

        public int VisitorCharacterId { get; set; }

        public int OwnerCharacterId { get; set; }

        public ExpertJobStoreKind Kind { get; set; }
    }
}

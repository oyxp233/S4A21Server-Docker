using System;
using System.Collections.Generic;

namespace DfoServer.Game.ExpertJob
{
    public sealed class ExpertJobStoreRuntimeService
    {
        public const byte ErrorInvalidRequest = 10;
        public const byte ErrorInvalidState = 19;
        public const byte ErrorStoreBusy = 190;

        private readonly object _syncRoot = new object();
        private readonly Dictionary<int, ExpertJobStoreSession> _storesByOwnerCharacterId =
            new Dictionary<int, ExpertJobStoreSession>();
        private readonly Dictionary<Guid, ExpertJobStoreVisitorSession> _visitorsBySessionId =
            new Dictionary<Guid, ExpertJobStoreVisitorSession>();

        public bool TryCreate(
            Guid ownerSessionId,
            int ownerCharacterId,
            ushort ownerUserId,
            byte expertJobType,
            byte townId,
            byte areaId,
            bool isInDungeon,
            bool isInParty,
            ExpertJobStoreCreateCommand command,
            DisjointMachineState disjointMachineState,
            out ExpertJobStoreSession store,
            out byte errorCode)
            => TryCreate(
                ownerSessionId,
                ownerCharacterId,
                ownerUserId,
                expertJobType,
                townId,
                areaId,
                isInDungeon,
                isInParty,
                command,
                disjointMachineState,
                null,
                out store,
                out errorCode);

        public bool TryCreate(
            Guid ownerSessionId,
            int ownerCharacterId,
            ushort ownerUserId,
            byte expertJobType,
            byte townId,
            byte areaId,
            bool isInDungeon,
            bool isInParty,
            ExpertJobStoreCreateCommand command,
            DisjointMachineState disjointMachineState,
            EnchanterStoreState enchanterState,
            out ExpertJobStoreSession store,
            out byte errorCode)
        {
            store = null;
            if (!TryValidateCreateRequest(
                    ownerCharacterId,
                    ownerUserId,
                    expertJobType,
                    townId,
                    isInDungeon,
                    isInParty,
                    command,
                    disjointMachineState,
                    enchanterState,
                    out errorCode))
                return false;

            lock (_syncRoot)
            {
                if (_storesByOwnerCharacterId.ContainsKey(ownerCharacterId))
                {
                    errorCode = ErrorStoreBusy;
                    return false;
                }

                store = new ExpertJobStoreSession
                {
                    OwnerSessionId = ownerSessionId,
                    OwnerCharacterId = ownerCharacterId,
                    OwnerUserId = ownerUserId,
                    ExpertJobType = expertJobType,
                    Kind = command.Kind,
                    NameBytes = (byte[])command.NameBytes.Clone(),
                    Cost = command.Cost,
                    DisjointMachine = command.Kind == ExpertJobStoreKind.DisjointMachine
                        ? new DisjointMachineState
                        {
                            MachineGrade = disjointMachineState.MachineGrade,
                            Endurance = disjointMachineState.Endurance,
                        }
                        : null,
                    Enchanter = command.Kind == ExpertJobStoreKind.EnchantShop
                        ? new EnchanterStoreState
                        {
                            Endurance = enchanterState.Endurance,
                            CardQualificationLevels = new List<byte>(
                                enchanterState.CardQualificationLevels),
                        }
                        : null,
                    TownId = townId,
                    AreaId = areaId,
                    PositionX = command.PositionX,
                    PositionY = command.PositionY,
                    Direction = command.Direction,
                };
                _storesByOwnerCharacterId.Add(ownerCharacterId, store);
                errorCode = 0;
                return true;
            }
        }

        internal bool TryValidateCreate(
            int ownerCharacterId,
            ushort ownerUserId,
            byte expertJobType,
            byte townId,
            bool isInDungeon,
            bool isInParty,
            ExpertJobStoreCreateCommand command,
            DisjointMachineState disjointMachineState,
            EnchanterStoreState enchanterState,
            out byte errorCode)
        {
            if (!TryValidateCreateRequest(
                    ownerCharacterId,
                    ownerUserId,
                    expertJobType,
                    townId,
                    isInDungeon,
                    isInParty,
                    command,
                    disjointMachineState,
                    enchanterState,
                    out errorCode))
            {
                return false;
            }

            lock (_syncRoot)
            {
                if (_storesByOwnerCharacterId.ContainsKey(ownerCharacterId))
                {
                    errorCode = ErrorStoreBusy;
                    return false;
                }
            }

            errorCode = 0;
            return true;
        }

        public bool TryGetStoreInArea(
            byte townId,
            byte areaId,
            ushort ownerUserId,
            out ExpertJobStoreSession store)
        {
            store = null;
            if (townId == 0 || ownerUserId == 0)
                return false;

            lock (_syncRoot)
            {
                foreach (var candidate in _storesByOwnerCharacterId.Values)
                {
                    if (candidate.OwnerUserId == ownerUserId
                        && candidate.TownId == townId
                        && candidate.AreaId == areaId)
                    {
                        store = candidate;
                        return true;
                    }
                }
            }

            return false;
        }

        public bool TryGetOwnedStore(
            Guid ownerSessionId,
            int ownerCharacterId,
            out ExpertJobStoreSession store)
        {
            lock (_syncRoot)
            {
                return _storesByOwnerCharacterId.TryGetValue(ownerCharacterId, out store)
                    && store.OwnerSessionId == ownerSessionId;
            }
        }

        public bool HasStore(int ownerCharacterId)
        {
            lock (_syncRoot)
                return _storesByOwnerCharacterId.ContainsKey(ownerCharacterId);
        }

        public bool TryEnter(
            Guid visitorSessionId,
            int visitorCharacterId,
            byte townId,
            byte areaId,
            ushort ownerUserId,
            out ExpertJobStoreSession store)
        {
            store = null;
            if (visitorSessionId == Guid.Empty || visitorCharacterId <= 0)
                return false;

            lock (_syncRoot)
            {
                foreach (var candidate in _storesByOwnerCharacterId.Values)
                {
                    if (candidate.OwnerUserId != ownerUserId
                        || candidate.TownId != townId
                        || candidate.AreaId != areaId)
                        continue;

                    store = candidate;
                    _visitorsBySessionId[visitorSessionId] = new ExpertJobStoreVisitorSession
                    {
                        VisitorSessionId = visitorSessionId,
                        VisitorCharacterId = visitorCharacterId,
                        OwnerCharacterId = candidate.OwnerCharacterId,
                        Kind = candidate.Kind,
                    };
                    return true;
                }
            }

            return false;
        }

        public bool TryGetEnteredStore(
            Guid visitorSessionId,
            int visitorCharacterId,
            out ExpertJobStoreSession store)
        {
            store = null;
            lock (_syncRoot)
            {
                if (!_visitorsBySessionId.TryGetValue(visitorSessionId, out var visitor)
                    || visitor.VisitorCharacterId != visitorCharacterId
                    || !_storesByOwnerCharacterId.TryGetValue(visitor.OwnerCharacterId, out store)
                    || store.Kind != visitor.Kind)
                {
                    _visitorsBySessionId.Remove(visitorSessionId);
                    store = null;
                    return false;
                }

                return true;
            }
        }

        public bool Leave(Guid visitorSessionId)
        {
            lock (_syncRoot)
                return _visitorsBySessionId.Remove(visitorSessionId);
        }

        public bool TryClose(Guid ownerSessionId, int ownerCharacterId, out ExpertJobStoreSession store)
        {
            lock (_syncRoot)
            {
                if (!_storesByOwnerCharacterId.TryGetValue(ownerCharacterId, out store)
                    || store.OwnerSessionId != ownerSessionId)
                {
                    store = null;
                    return false;
                }

                _storesByOwnerCharacterId.Remove(ownerCharacterId);
                RemoveVisitorsForOwner(ownerCharacterId);
                return true;
            }
        }

        public bool TryCloseSession(Guid ownerSessionId, out ExpertJobStoreSession store)
        {
            lock (_syncRoot)
            {
                _visitorsBySessionId.Remove(ownerSessionId);
                store = null;
                var ownerCharacterId = 0;
                foreach (var entry in _storesByOwnerCharacterId)
                {
                    if (entry.Value.OwnerSessionId != ownerSessionId)
                        continue;

                    store = entry.Value;
                    ownerCharacterId = entry.Key;
                    break;
                }

                if (store == null)
                    return false;

                _storesByOwnerCharacterId.Remove(ownerCharacterId);
                RemoveVisitorsForOwner(ownerCharacterId);
                return true;
            }
        }

        public IReadOnlyList<ExpertJobStoreSession> GetStoresInArea(byte townId, byte areaId)
        {
            var result = new List<ExpertJobStoreSession>();
            lock (_syncRoot)
            {
                foreach (var store in _storesByOwnerCharacterId.Values)
                {
                    if (store.TownId == townId && store.AreaId == areaId)
                        result.Add(store);
                }
            }

            return result;
        }

        public int Count
        {
            get
            {
                lock (_syncRoot)
                    return _storesByOwnerCharacterId.Count;
            }
        }

        private static bool TryValidateKind(
            byte expertJobType,
            ExpertJobStoreCreateCommand command,
            DisjointMachineState disjointMachineState,
            EnchanterStoreState enchanterState)
        {
            switch (command.Kind)
            {
                case ExpertJobStoreKind.DisjointMachine:
                    return expertJobType == ExpertJobStateCodec.DisjointerType
                        && command.Cost <= DisjointMachineConfigProvider.Config.MaximumStoreCharge
                        && disjointMachineState != null
                        && disjointMachineState.MachineGrade > 0
                        && disjointMachineState.MachineGrade
                            <= DisjointMachineConfigProvider.Config.RepairRules.Count
                        && disjointMachineState.Endurance > 0;
                case ExpertJobStoreKind.EnchantShop:
                    return expertJobType == ExpertJobStateCodec.EnchanterType
                        && command.Cost <= EnchanterConfigProvider.Config.MaximumStoreCharge
                        && enchanterState != null
                        && enchanterState.Endurance > 0
                        && enchanterState.CardQualificationLevels != null
                        && enchanterState.CardQualificationLevels.Count > 0;
                default:
                    return false;
            }
        }

        private static bool TryValidateCreateRequest(
            int ownerCharacterId,
            ushort ownerUserId,
            byte expertJobType,
            byte townId,
            bool isInDungeon,
            bool isInParty,
            ExpertJobStoreCreateCommand command,
            DisjointMachineState disjointMachineState,
            EnchanterStoreState enchanterState,
            out byte errorCode)
        {
            errorCode = ErrorInvalidRequest;
            if (ownerCharacterId <= 0
                || ownerUserId == 0
                || townId == 0
                || command == null
                || command.NameBytes == null
                || command.NameBytes.Length > 255
                || command.Cost < 0)
            {
                return false;
            }

            if (isInDungeon || isInParty
                || !TryValidateKind(expertJobType, command, disjointMachineState, enchanterState))
            {
                errorCode = ErrorInvalidState;
                return false;
            }

            return true;
        }

        private void RemoveVisitorsForOwner(int ownerCharacterId)
        {
            var sessionIds = new List<Guid>();
            foreach (var pair in _visitorsBySessionId)
            {
                if (pair.Value.OwnerCharacterId == ownerCharacterId)
                    sessionIds.Add(pair.Key);
            }

            foreach (var sessionId in sessionIds)
                _visitorsBySessionId.Remove(sessionId);
        }
    }
}

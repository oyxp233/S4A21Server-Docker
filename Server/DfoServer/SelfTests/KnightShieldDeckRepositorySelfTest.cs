using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.KnightShield;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class KnightShieldDeckRepositorySelfTest
    {
        private const int AccountId = 975001;
        private const int CharacterId = 975002;

        private static int _pass;
        private static int _fail;

        public static int Run()
        {
            _pass = 0;
            _fail = 0;
            Console.WriteLine("=== KNIGHT_SHIELD_DECK repository selftest ===");

            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "dfo-knight-shield-deck-selftest-" + Guid.NewGuid().ToString("N") + ".db");

            try
            {
                var connectionString = SqliteDatabaseBootstrap.Initialize(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                SeedCharacter(connectionString);

                var repository = new KnightShieldDeckRepository(databasePath, ServerPaths.SchemaFilePath);

                var protocolDeck = new KnightShieldDeckSnapshot(new[]
                {
                    113370003,
                    113370004,
                    0,
                    113370006,
                    113370007,
                });
                var deckBody = KnightShieldDeckBodyBuilder.BuildDeck(protocolDeck);
                Check("0x0245 body is exactly five int32 values",
                    deckBody.Length == KnightShieldDeckBodyBuilder.DeckBodyLength
                    && BitConverter.ToInt32(deckBody, 0) == 113370003
                    && BitConverter.ToInt32(deckBody, 4) == 113370004
                    && BitConverter.ToInt32(deckBody, 8) == 0
                    && BitConverter.ToInt32(deckBody, 12) == 113370006
                    && BitConverter.ToInt32(deckBody, 16) == 113370007);

                var guardianReturnReset = CharacterSelectHandler.BuildKnightShieldReturnSelectReset(
                    new Game.Session.PlayerContext
                    {
                        CharacterId = CharacterId,
                        Job = 12
                    });
                Check("guardian return-select clears only the client deck before teardown",
                    guardianReturnReset != null
                    && guardianReturnReset.Length == KnightShieldDeckBodyBuilder.DeckBodyLength
                    && Array.TrueForAll(guardianReturnReset, value => value == 0));
                Check("non-guardian return-select does not send a shield reset",
                    CharacterSelectHandler.BuildKnightShieldReturnSelectReset(
                        new Game.Session.PlayerContext
                        {
                            CharacterId = CharacterId,
                            Job = 9
                        }) == null);

                var changeDeckAck = KnightShieldDeckBodyBuilder.BuildChangeDeckAck(protocolDeck);
                Check("0x0292 ACK is status, reserved byte, and five authoritative slots",
                    changeDeckAck.Length == KnightShieldDeckBodyBuilder.ChangeDeckAckLength
                    && changeDeckAck[KnightShieldDeckBodyBuilder.ChangeDeckAckStatusOffset] == 1
                    && changeDeckAck[KnightShieldDeckBodyBuilder.ChangeDeckAckReservedOffset] == 0
                    && BitConverter.ToInt32(changeDeckAck, KnightShieldDeckBodyBuilder.ChangeDeckAckSlotsOffset) == 113370003
                    && BitConverter.ToInt32(changeDeckAck, KnightShieldDeckBodyBuilder.ChangeDeckAckSlotsOffset + 4) == 113370004
                    && BitConverter.ToInt32(changeDeckAck, KnightShieldDeckBodyBuilder.ChangeDeckAckSlotsOffset + 8) == 0
                    && BitConverter.ToInt32(changeDeckAck, KnightShieldDeckBodyBuilder.ChangeDeckAckSlotsOffset + 12) == 113370006
                    && BitConverter.ToInt32(changeDeckAck, KnightShieldDeckBodyBuilder.ChangeDeckAckSlotsOffset + 16) == 113370007);
                var empty = repository.Load(CharacterId);
                Check("empty load always exposes five slots", empty.ShieldItemIds.Count == 5);
                Check("empty load fills every slot with zero", Matches(empty, 0, 0, 0, 0, 0));

                var initial = new KnightShieldDeckSnapshot(new[]
                {
                    113370003,
                    113370004,
                    0,
                    113370006,
                    113370007,
                });
                repository.Save(CharacterId, initial);
                Check("group save round-trips all five logical slots",
                    Matches(repository.Load(CharacterId), 113370003, 113370004, 0, 113370006, 113370007));
                Check("empty logical slot is not persisted as a row", CountRows(connectionString) == 4);

                var beforeFailedSave = repository.Load(CharacterId);
                CreateAbortTrigger(connectionString, 113379999);
                var forcedFailure = Throws<SqliteException>(() => repository.Save(
                    CharacterId,
                    new KnightShieldDeckSnapshot(new[]
                    {
                        113370101,
                        113370102,
                        113370103,
                        113379999,
                        113370105,
                    })));
                DropAbortTrigger(connectionString);
                Check("forced mid-save failure reaches SQLite", forcedFailure);
                Check("failed group save rolls back delete and partial inserts",
                    Matches(repository.Load(CharacterId), beforeFailedSave.ToArray()));

                repository.Save(CharacterId, new KnightShieldDeckSnapshot(new[]
                {
                    113370201,
                    0,
                    0,
                    0,
                    113370205,
                }));
                Check("later group save removes stale rows",
                    Matches(repository.Load(CharacterId), 113370201, 0, 0, 0, 113370205)
                    && CountRows(connectionString) == 2);

                var character = new CharacterRecord
                {
                    CharacterId = CharacterId,
                    AccountId = AccountId,
                    Name = System.Text.Encoding.UTF8.GetBytes("knight-shield-deck-selftest"),
                    Job = KnightShieldDataProvider.GuardianJob,
                    GrowType = 1,
                    Level = 1,
                    CreatedAt = DateTime.UtcNow,
                };
                var service = new KnightShieldService(repository);
                repository.Save(CharacterId, new KnightShieldDeckSnapshot());

                Check("guardian shield eligibility depends only on the main job",
                    KnightShieldDataProvider.IsEligibleCharacter(KnightShieldDataProvider.GuardianJob)
                    && !KnightShieldDataProvider.IsEligibleCharacter(11));
                Check("shield catalogs are loaded from PVF for both existing guardian grow types",
                    Matches(KnightShieldDataProvider.GetCatalogItems(1),
                        113370003, 113370004, 113370005, 113370006, 113370007,
                        113370008, 113370009, 113370010, 113370011, 113370012, 113370035)
                    && Matches(KnightShieldDataProvider.GetCatalogItems(2),
                        113370025, 113370026, 113370027, 113370028, 113370030,
                        113370029, 113370031, 113370032, 113370033, 113370034, 113370036));
                Check("known support weapon is recognized as a knight shield",
                    KnightShieldDataProvider.IsKnightShield(113370003));
                Check("female slayer support weapon is not a guardian shield",
                    !KnightShieldDataProvider.IsKnightShield(100330200));
                var femaleSlayer = new CharacterRecord
                {
                    CharacterId = CharacterId,
                    Job = 11,
                    GrowType = 4,
                };
                Check("female slayer cannot enter the guardian shield deck",
                    !service.TryEquipMain(femaleSlayer, 113370003, out _, out _)
                    && repository.Load(CharacterId).MainShieldItemId == 0);
                var chaosGuardian = new CharacterRecord
                {
                    CharacterId = CharacterId,
                    Job = KnightShieldDataProvider.GuardianJob,
                    GrowType = 2,
                };
                Check("another guardian grow type uses its own PVF shield catalog",
                    service.TryEquipMain(chaosGuardian, 113370025, out var chaosDeck, out _)
                    && chaosDeck.MainShieldItemId == 113370025
                    && !service.TryEquipMain(chaosGuardian, 113370003, out _, out _));
                Check("subtype1 rejects a persisted shield from another grow-type catalog",
                    !new Game.CharacterData.SqliteSubtype1Repository(
                        databasePath,
                        ServerPaths.SchemaFilePath).Load(CharacterId).EquippedEntries.Exists(
                            entry => entry.Slot == KnightShieldEquipmentSnapshotSynchronizer.SupportWeaponSlot));
                repository.Save(CharacterId, new KnightShieldDeckSnapshot());
                Check("catalog to main equips and persists the selected shield",
                    service.TryEquipMain(character, 113370003, out var equipped, out _)
                    && equipped.MainShieldItemId == 113370003
                    && repository.Load(CharacterId).MainShieldItemId == 113370003);
                Check("five-slot deck save persists main and spare shields",
                    service.TrySaveDeck(
                        character,
                        new[] { 113370003, 113370004, 113370006, 0, 113370007 },
                        out var savedDeck,
                        out _)
                    && Matches(savedDeck, 113370003, 113370004, 113370006, 0, 113370007)
                    && Matches(repository.Load(CharacterId), 113370003, 113370004, 113370006, 0, 113370007));
                Check("spare-to-main move swaps persisted deck slots",
                    service.TryMoveDeckSlot(character, 1, 0, out var swappedDeck, out _)
                    && Matches(swappedDeck, 113370004, 113370003, 113370006, 0, 113370007)
                    && Matches(repository.Load(CharacterId), 113370004, 113370003, 113370006, 0, 113370007));
                Check("main-to-spare move swaps the deck back",
                    service.TryMoveDeckSlot(character, 0, 1, out var restoredDeck, out _)
                    && Matches(restoredDeck, 113370003, 113370004, 113370006, 0, 113370007));
                var beforeRejectedSlotMove = repository.Load(CharacterId);
                Check("empty deck source slot is rejected without mutation",
                    !service.TryMoveDeckSlot(character, 3, 0, out _, out _)
                    && Matches(repository.Load(CharacterId), beforeRejectedSlotMove.ToArray()));
                Check("out-of-range deck slot move is rejected without mutation",
                    !service.TryMoveDeckSlot(character, 5, 0, out _, out _)
                    && Matches(repository.Load(CharacterId), beforeRejectedSlotMove.ToArray()));

                var subtype1 = new Game.CharacterData.SqliteSubtype1Repository(
                    databasePath,
                    ServerPaths.SchemaFilePath).Load(CharacterId);
                var supportWeapon = subtype1.EquippedEntries.Find(
                    entry => entry.Slot == KnightShieldEquipmentSnapshotSynchronizer.SupportWeaponSlot);
                Check("subtype1 dynamically restores the deck main shield in support-weapon slot 23",
                    supportWeapon != null
                    && supportWeapon.Core != null
                    && supportWeapon.Core.ItemId == 113370003);
                Check("dynamic shield ItemCore keeps the native default list-33 state",
                    supportWeapon?.Core != null
                    && supportWeapon.Core.Value == 0
                    && supportWeapon.Core.Attr == 0
                    && supportWeapon.Core.Durability == 0);
                var appearanceEntries = AppearanceService.BuildFromEquippedEntries(subtype1.EquippedEntries);
                Check("appearance projection carries the standard support-weapon slot 23",
                    Array.Exists(
                        appearanceEntries,
                        entry => entry.Slot == KnightShieldEquipmentSnapshotSynchronizer.SupportWeaponSlot
                            && entry.DisplayItemId == 113370003));
                var unifiedInventoryAppearance = KnightShieldAppearanceSynchronizer.Apply(
                    Array.Empty<CharacterAppearanceEntry>(),
                    character.Job,
                    character.GrowType,
                    repository.Load(CharacterId));
                Check("unified inventory appearance merges the deck main shield into slot 23",
                    Array.Exists(
                        unifiedInventoryAppearance,
                        entry => entry.Slot == KnightShieldEquipmentSnapshotSynchronizer.SupportWeaponSlot
                            && entry.DisplayItemId == 113370003));
                var subtype1Body = UserInfoSubtype1Builder.BuildFromSnapshot(
                    subtype1,
                    new SkillInfoSnapshot());
                Check("subtype1 wire body carries one complete slot-23 ItemCore entry",
                    subtype1Body.Length > 136
                    && subtype1Body[92] == 1
                    && subtype1Body[93] == 23
                    && BitConverter.ToInt32(subtype1Body, 94) == 113370003);
                Check("dynamic shield restore does not persist into ordinary equipment items",
                    CountEquipmentSlot23(connectionString) == 0);
                var initSequence = NewCharacterInitSequence.Build();
                var subtype1TemplateIndex = FindTemplateIndex(
                    initSequence,
                    0x00,
                    0x0002,
                    occurrenceIndex: 1);
                var subtype0TemplateIndex = FindTemplateIndex(
                    initSequence,
                    0x00,
                    0x0002,
                    occurrenceIndex: 0);
                var deckTemplateIndex = FindTemplateIndex(
                    initSequence,
                    0x00,
                    KnightShieldDeckBodyBuilder.DeckNotificationType,
                    occurrenceIndex: null);
                Check("select-character still initializes subtype1 slot-23 equipment",
                    subtype1TemplateIndex >= 0);
                Check("select-character orders one 0x0245 after subtype1 slot-23 restore",
                    CountTemplates(
                        initSequence,
                        0x00,
                        KnightShieldDeckBodyBuilder.DeckNotificationType) == 1
                    && subtype0TemplateIndex >= 0
                    && subtype0TemplateIndex < subtype1TemplateIndex
                    && subtype1TemplateIndex < deckTemplateIndex);
                Check("select-character sequence contains no synthetic MOVE_ITEMSPACE ACK",
                    CountTemplates(initSequence, 0x01, 0x0013) == 0);

                var streamSnapshot = new SelectCharacterDataSnapshot
                {
                    CharacterRecord = new CharacterRecord
                    {
                        CharacterId = 0,
                        AccountId = AccountId,
                        Name = character.Name,
                        Job = character.Job,
                        GrowType = character.GrowType,
                        Level = character.Level,
                        CreatedAt = character.CreatedAt,
                    },
                    InitializationSnapshot = new SelectCharacterInitializationSnapshot
                    {
                        UserInfoAddition = subtype1,
                    },
                    KnightShieldDeck = repository.Load(CharacterId),
                };
                var fullInitPackets = BuildPacketStreamWithOnlineInventory(
                    new FixedSelectCharacterDataSource(streamSnapshot));
                var fullDeckCount = 0;
                var fullDeckIndex = -1;
                var fullSubtype0Index = -1;
                var fullSubtype1Index = -1;
                for (var packetIndex = 0; packetIndex < fullInitPackets.Count; packetIndex++)
                {
                    var packet = fullInitPackets[packetIndex];
                    if (IsEnvelope(packet, 0x00, KnightShieldDeckBodyBuilder.DeckNotificationType))
                    {
                        fullDeckCount++;
                        fullDeckIndex = packetIndex;
                    }
                    else if (IsEnvelope(packet, 0x00, 0x0002)
                        && packet.Length > 15
                        && packet[15] == 1)
                    {
                        fullSubtype1Index = packetIndex;
                    }
                    else if (IsEnvelope(packet, 0x00, 0x0002)
                        && packet.Length > 15
                        && packet[15] == 0
                        && fullSubtype0Index < 0)
                    {
                        fullSubtype0Index = packetIndex;
                    }
                }
                Check("guardian init sends the authoritative deck exactly once after slot-23 restore",
                    fullDeckCount == 1
                    && fullSubtype0Index >= 0
                    && fullSubtype0Index < fullSubtype1Index
                    && fullSubtype1Index < fullDeckIndex
                    && MatchesEnvelopeBody(
                        fullInitPackets[fullDeckIndex],
                        KnightShieldDeckBodyBuilder.BuildDeck(streamSnapshot.KnightShieldDeck)));

                var crossGrowTypeDeck = new KnightShieldDeckSnapshot(
                    new[] { 113370025, 113370026, 113370029, 0, 113370036 });
                streamSnapshot.CharacterRecord.GrowType = 2;
                streamSnapshot.KnightShieldDeck = crossGrowTypeDeck;
                var crossGrowTypePackets = BuildPacketStreamWithOnlineInventory(
                    new FixedSelectCharacterDataSource(streamSnapshot),
                    new List<SelectCharacterPacketTemplate>
                    {
                        new SelectCharacterPacketTemplate
                        {
                            Kind = SelectCharacterPacketTemplateKind.Raw,
                            Command = 0x00,
                            Type = KnightShieldDeckBodyBuilder.DeckNotificationType,
                        },
                    });
                Check("cross-grow-type init keeps authoritative shield ids without aliases",
                    CountPackets(
                        crossGrowTypePackets,
                        0x00,
                        KnightShieldDeckBodyBuilder.DeckNotificationType) == 1
                    && MatchesEnvelopeBody(
                        crossGrowTypePackets.Find(packet =>
                            IsEnvelope(
                                packet,
                                0x00,
                                KnightShieldDeckBodyBuilder.DeckNotificationType)),
                        KnightShieldDeckBodyBuilder.BuildDeck(crossGrowTypeDeck)));

                streamSnapshot.CharacterRecord.Job = 0;
                var nonGuardianPackets = BuildPacketStreamWithOnlineInventory(
                    new FixedSelectCharacterDataSource(streamSnapshot));
                Check("non-guardian init does not send 0x0245",
                    CountPackets(
                        nonGuardianPackets,
                        0x00,
                        KnightShieldDeckBodyBuilder.DeckNotificationType) == 0);

                RunHandlerProtocolChecks(
                    databasePath,
                    repository,
                    service,
                    character);

                var beforeDuplicate = repository.Load(CharacterId);
                Check("duplicate shield is rejected without mutating persisted deck",
                    !service.TrySaveDeck(
                        character,
                        new[] { 113370003, 113370004, 113370004, 0, 0 },
                        out _,
                        out _)
                    && Matches(repository.Load(CharacterId), beforeDuplicate.ToArray()));
                Check("non-shield item is rejected without mutating persisted deck",
                    !service.TrySaveDeck(
                        character,
                        new[] { 101030475, 0, 0, 0, 0 },
                        out _,
                        out _)
                    && Matches(repository.Load(CharacterId), beforeDuplicate.ToArray()));
                Check("main to catalog clears only current shield",
                    service.TryUnequipMain(character, out var unequipped, out _)
                    && Matches(unequipped, 0, 113370004, 113370006, 0, 113370007)
                    && Matches(repository.Load(CharacterId), 0, 113370004, 113370006, 0, 113370007));
                Check("empty main deck removes support-weapon slot from later subtype1 snapshots",
                    !new Game.CharacterData.SqliteSubtype1Repository(
                        databasePath,
                        ServerPaths.SchemaFilePath).Load(CharacterId).EquippedEntries.Exists(
                            entry => entry.Slot == KnightShieldEquipmentSnapshotSynchronizer.SupportWeaponSlot));

                Check("snapshot rejects a non-five-slot deck",
                    Throws<ArgumentException>(() => new KnightShieldDeckSnapshot(new[] { 1, 2, 3, 4 })));

                DeleteCharacter(connectionString);
                Check("character deletion cascades shield deck rows", CountRows(connectionString) == 0);
            }
            finally
            {
                DeleteTempDatabase(databasePath);
            }

            Console.WriteLine($"=== result: {_pass} PASS, {_fail} FAIL ===");
            return _fail == 0 ? 0 : 1;
        }

        private static void SeedCharacter(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(@aid, 'knight-shield-deck-selftest', '');
INSERT INTO characters(character_id, account_id, name, job, grow_type)
VALUES(@cid, @aid, 'knight-shield-deck-selftest', 12, 1);
INSERT INTO character_subtype1_fields(character_id)
VALUES(@cid);";
                    command.Parameters.AddWithValue("@aid", AccountId);
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static long CountRows(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT COUNT(*)
FROM character_knight_shield_deck
WHERE character_id = @cid;";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    return Convert.ToInt64(command.ExecuteScalar());
                }
            }
        }

        private static long CountEquipmentSlot23(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT COUNT(*)
FROM character_inventory_items
WHERE character_id = @cid AND list_type = @listType AND slot_index = 23;";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.Parameters.AddWithValue("@listType", (int)InventoryListType.Equipment);
                    return Convert.ToInt64(command.ExecuteScalar());
                }
            }
        }

        private static bool IsZeroRange(byte[] value, int offset)
        {
            if (value == null || offset < 0 || offset > value.Length)
                return false;

            for (var index = offset; index < value.Length; index++)
            {
                if (value[index] != 0)
                    return false;
            }

            return true;
        }

        private static int FindTemplateIndex(
            System.Collections.Generic.IReadOnlyList<SelectCharacterPacketTemplate> templates,
            byte command,
            ushort type,
            int? occurrenceIndex)
        {
            for (var index = 0; index < templates.Count; index++)
            {
                var template = templates[index];
                if (template.Command == command
                    && template.Type == type
                    && (!occurrenceIndex.HasValue || template.OccurrenceIndex == occurrenceIndex.Value))
                {
                    return index;
                }
            }

            return -1;
        }

        private static int CountTemplates(
            System.Collections.Generic.IEnumerable<SelectCharacterPacketTemplate> templates,
            byte command,
            ushort type)
        {
            var count = 0;
            foreach (var template in templates)
            {
                if (template.Command == command && template.Type == type)
                    count++;
            }

            return count;
        }

        private static int CountPackets(
            System.Collections.Generic.IEnumerable<byte[]> packets,
            byte command,
            ushort type)
        {
            var count = 0;
            foreach (var packet in packets)
            {
                if (IsEnvelope(packet, command, type))
                    count++;
            }

            return count;
        }

        private static void RunHandlerProtocolChecks(
            string databasePath,
            KnightShieldDeckRepository repository,
            KnightShieldService service,
            CharacterRecord character)
        {
            var characterRepository = new SqliteCharacterRepository(
                databasePath,
                ServerPaths.SchemaFilePath);
            var handler = new KnightShieldHandler(service, characterRepository);
            var expectedDeck = repository.Load(CharacterId).ToArray();

            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            using (var receiver = new TcpClient())
            {
                listener.Start();
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                receiver.Connect(IPAddress.Loopback, endpoint.Port);
                using (var sender = listener.AcceptTcpClient())
                {
                    receiver.ReceiveTimeout = 3_000;
                    var session = new EnhancedClientSession(sender, new GamePacketHeader());
                    session.Player.HydrateFrom(characterRepository.GetById(CharacterId));
                    session.Player.GrowType = 1;

                    var ordinaryMoveHandled = handler.TryHandleMoveItemSpace(
                            session,
                            new GamePacketHeader(),
                            BuildMoveBody(0, 1, 0, 0, 0, 2))
                        .GetAwaiter()
                        .GetResult();
                    Check("ordinary list move falls through the shield handler",
                        !ordinaryMoveHandled && !receiver.GetStream().DataAvailable);

                    var rejectedHandled = handler.TryHandleMoveItemSpace(
                            session,
                            new GamePacketHeader(),
                            BuildMoveBody(34, 1, 101030475, 0, 33, 0))
                        .GetAwaiter()
                        .GetResult();
                    var rejectedAck = ReadPacket(receiver.GetStream());
                    Check("invalid shield move sends only the generic invalid-operation ACK",
                        rejectedHandled
                        && IsEnvelope(rejectedAck, 0x01, 0x0013)
                        && rejectedAck.Length == 15 + MoveItemSpaceAckBuilder.ErrorBodyLength
                        && rejectedAck[15] == 0
                        && rejectedAck[16] == 0x02
                        && rejectedAck[17] == 34
                        && rejectedAck[18] == 33
                        && Matches(repository.Load(CharacterId), expectedDeck));

                    var equippedHandled = handler.TryHandleMoveItemSpace(
                            session,
                            new GamePacketHeader(),
                            BuildMoveBody(34, 1, 113370004, 0, 33, 0))
                        .GetAwaiter()
                        .GetResult();
                    var moveAck = ReadPacket(receiver.GetStream());
                    var deckNoti = ReadPacket(receiver.GetStream());
                    var moveAppearanceNoti = ReadPacket(receiver.GetStream());
                    Check("valid main-shield move sends ACK, one deck notification, and one appearance refresh",
                        equippedHandled
                        && IsEnvelope(moveAck, 0x01, 0x0013)
                        && moveAck.Length == 15 + MoveItemSpaceAckBuilder.SuccessBodyLength
                        && moveAck[15] == 1
                        && moveAck[16] == 34
                        && moveAck[23] == 33
                        && IsEnvelope(deckNoti, 0x00, KnightShieldDeckBodyBuilder.DeckNotificationType)
                        && BitConverter.ToInt32(deckNoti, 15) == 113370004
                        && IsEnvelope(moveAppearanceNoti, 0x00, 0x0002)
                        && !receiver.GetStream().DataAvailable);
                    Check("handler move persists main and removes its duplicate spare entry",
                        Matches(repository.Load(CharacterId), 113370004, 0, 113370006, 0, 113370007));

                    handler.HandleChangeDeckInfo(
                            session,
                            new GamePacketHeader(),
                            BuildDeckBody(113370004, 113370006, 113370006, 0, 113370007))
                        .GetAwaiter()
                        .GetResult();
                    var rejectedDeckAck = ReadPacket(receiver.GetStream());
                    Check("rejected 0x0292 echoes the authoritative deck in one ACK",
                        IsEnvelope(rejectedDeckAck, 0x01, KnightShieldDeckBodyBuilder.ChangeDeckCommandType)
                        && rejectedDeckAck.Length == 15 + KnightShieldDeckBodyBuilder.ChangeDeckAckLength
                        && MatchesEnvelopeBody(
                            rejectedDeckAck,
                            KnightShieldDeckBodyBuilder.BuildChangeDeckAck(
                                new KnightShieldDeckSnapshot(new[] { 113370004, 0, 113370006, 0, 113370007 })))
                        && Matches(repository.Load(CharacterId), 113370004, 0, 113370006, 0, 113370007)
                        && !receiver.GetStream().DataAvailable);

                    handler.HandleChangeDeckInfo(
                            session,
                            new GamePacketHeader(),
                            BuildDeckBody(expectedDeck))
                        .GetAwaiter()
                        .GetResult();
                    var savedDeckAck = ReadPacket(receiver.GetStream());
                    var savedDeckAppearanceNoti = ReadPacket(receiver.GetStream());
                    Check("valid 0x0292 echoes five slots and refreshes a changed main-shield appearance",
                        IsEnvelope(savedDeckAck, 0x01, KnightShieldDeckBodyBuilder.ChangeDeckCommandType)
                        && savedDeckAck.Length == 15 + KnightShieldDeckBodyBuilder.ChangeDeckAckLength
                        && MatchesEnvelopeBody(
                            savedDeckAck,
                            KnightShieldDeckBodyBuilder.BuildChangeDeckAck(new KnightShieldDeckSnapshot(expectedDeck)))
                        && IsEnvelope(savedDeckAppearanceNoti, 0x00, 0x0002)
                        && Matches(repository.Load(CharacterId), expectedDeck)
                        && !receiver.GetStream().DataAvailable);

                    session.Close();
                }
            }

            Check("handler protocol test restores its original deck fixture",
                service.TrySaveDeck(character, expectedDeck, out var restored, out _)
                && Matches(restored, expectedDeck));
        }

        private static byte[] BuildMoveBody(
            byte sourceList,
            short sourceSlot,
            int sourceItemId,
            int moveValue,
            byte destinationList,
            short destinationSlot)
        {
            var body = new byte[24];
            body[0] = sourceList;
            BitConverter.GetBytes(sourceSlot).CopyTo(body, 1);
            BitConverter.GetBytes(sourceItemId).CopyTo(body, 3);
            BitConverter.GetBytes(moveValue).CopyTo(body, 7);
            body[11] = destinationList;
            BitConverter.GetBytes(destinationSlot).CopyTo(body, 12);
            return body;
        }

        private static byte[] BuildDeckBody(params int[] shieldItemIds)
        {
            if (shieldItemIds == null || shieldItemIds.Length != KnightShieldDeckSnapshot.SlotCount)
                throw new ArgumentException("deck body requires five item ids", nameof(shieldItemIds));

            var body = new byte[KnightShieldDeckBodyBuilder.DeckBodyLength];
            for (var slotIndex = 0; slotIndex < shieldItemIds.Length; slotIndex++)
            {
                BitConverter.GetBytes(shieldItemIds[slotIndex])
                    .CopyTo(body, slotIndex * sizeof(int));
            }
            return body;
        }

        private static byte[] ReadPacket(NetworkStream stream)
        {
            var header = ReadExactly(stream, 15);
            var packetLength = BitConverter.ToInt32(header, 3);
            if (packetLength < header.Length)
                throw new InvalidDataException($"invalid packet length {packetLength}");

            var packet = new byte[packetLength];
            Buffer.BlockCopy(header, 0, packet, 0, header.Length);
            if (packetLength > header.Length)
            {
                var body = ReadExactly(stream, packetLength - header.Length);
                Buffer.BlockCopy(body, 0, packet, header.Length, body.Length);
            }
            return packet;
        }

        private static byte[] ReadExactly(NetworkStream stream, int length)
        {
            var result = new byte[length];
            var offset = 0;
            while (offset < result.Length)
            {
                var read = stream.Read(result, offset, result.Length - offset);
                if (read <= 0)
                    throw new EndOfStreamException("connection closed while reading packet");
                offset += read;
            }
            return result;
        }

        private static bool IsEnvelope(byte[] packet, byte command, ushort type)
        {
            return packet != null
                && packet.Length >= 15
                && packet[0] == command
                && BitConverter.ToUInt16(packet, 1) == type;
        }

        private static bool MatchesEnvelopeBody(byte[] packet, byte[] expectedBody)
        {
            if (packet == null
                || expectedBody == null
                || packet.Length != expectedBody.Length + 15)
                return false;

            for (var index = 0; index < expectedBody.Length; index++)
            {
                if (packet[index + 15] != expectedBody[index])
                    return false;
            }

            return true;
        }

        private static void CreateAbortTrigger(string connectionString, int rejectedItemId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $@"
CREATE TRIGGER knight_shield_deck_selftest_abort
BEFORE INSERT ON character_knight_shield_deck
WHEN NEW.shield_item_id = {rejectedItemId}
BEGIN
    SELECT RAISE(ABORT, 'forced shield deck save failure');
END;";
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void DropAbortTrigger(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "DROP TRIGGER IF EXISTS knight_shield_deck_selftest_abort;";
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void DeleteCharacter(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "DELETE FROM characters WHERE character_id = @cid;";
                    command.Parameters.AddWithValue("@cid", CharacterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static bool Matches(KnightShieldDeckSnapshot snapshot, params int[] expected)
        {
            if (snapshot == null || expected == null || expected.Length != KnightShieldDeckSnapshot.SlotCount)
                return false;

            for (var slotIndex = 0; slotIndex < expected.Length; slotIndex++)
            {
                if (snapshot.GetShieldItemId(slotIndex) != expected[slotIndex])
                    return false;
            }

            return true;
        }

        private static bool Matches(IReadOnlyList<int> actual, params int[] expected)
        {
            if (actual == null || expected == null || actual.Count != expected.Length)
                return false;

            for (var index = 0; index < expected.Length; index++)
            {
                if (actual[index] != expected[index])
                    return false;
            }

            return true;
        }

        private static bool Throws<TException>(Action action)
            where TException : Exception
        {
            try
            {
                action();
                return false;
            }
            catch (TException)
            {
                return true;
            }
        }

        private static void Check(string label, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}");
            if (ok) _pass++;
            else _fail++;
        }

        private static void DeleteTempDatabase(string databasePath)
        {
            TryDelete(databasePath);
            TryDelete(databasePath + "-wal");
            TryDelete(databasePath + "-shm");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static List<byte[]> BuildPacketStreamWithOnlineInventory(
            ISelectCharacterDataSource dataSource,
            List<SelectCharacterPacketTemplate> templates = null)
        {
            var sessionId = Guid.NewGuid();
            InventoryContext.Register(
                sessionId,
                CharacterId,
                new InventoryService(CharacterId, AccountId));
            try
            {
                var packets = templates == null
                    ? SelectCharacterPacketBuilder.BuildPacketStream(
                        dataSource,
                        CharacterId,
                        AccountId)
                    : SelectCharacterPacketBuilder.BuildPacketStream(
                        dataSource,
                        CharacterId,
                        AccountId,
                        templates);
                return new List<byte[]>(packets);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, CharacterId);
            }
        }

        private sealed class FixedSelectCharacterDataSource : ISelectCharacterDataSource
        {
            private readonly SelectCharacterDataSnapshot _snapshot;

            public FixedSelectCharacterDataSource(SelectCharacterDataSnapshot snapshot)
            {
                _snapshot = snapshot;
            }

            public SelectCharacterDataSnapshot Load(int characterId, int accountId)
            {
                return _snapshot;
            }

            public int GetSeedCharacterId()
            {
                return CharacterId;
            }

            public void InitializeNewCharacter(int characterId, int accountId, byte job)
            {
            }
        }
    }
}

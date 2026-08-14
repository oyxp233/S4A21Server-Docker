using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    /// <summary>
    /// Free-duel regression for the production SELECT_CHARACTER dispatch.
    /// The handler must finish the CH.68 lobby handshake before accepting
    /// MAKE_PVP_ROOM; testing the room registry alone cannot catch a missing
    /// HandleLobbyReadyAsync call in GameProtocolHandler.
    /// </summary>
    public static class FreeDuelSelectionWiringSelfTest
    {
        private const int CharacterId = 62001;
        private const int SecondCharacterId = 62002;

        public static int Run()
        {
            var failures = 0;
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                "free_duel_selection_wiring_" +
                Guid.NewGuid().ToString("N") + ".db");
            var previousDatabasePath =
                Environment.GetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH");
            var previousFreeDuelEnvironment =
                Environment.GetEnvironmentVariable(
                    GameNetworkConfig
                        .FreeDuelListenerEnvironmentVariable);
            var previousFreeDuelEnabled =
                GameNetworkConfig.FreeDuelListenerEnabled;
            SessionDirectory sessions = null;
            ConnectedSession connection = null;
            GameProtocolHandler protocol = null;

            try
            {
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    databasePath);
                Environment.SetEnvironmentVariable(
                    GameNetworkConfig
                        .FreeDuelListenerEnvironmentVariable,
                    "1");
                GameNetworkConfig.Configure(Array.Empty<string>());

                var accounts = new SqliteAccountRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var characters = new SqliteCharacterRepository(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var accountId = accounts.Create(
                    "free-duel-selection-wiring",
                    string.Empty);
                characters.Create(
                    new CharacterRecord
                    {
                        CharacterId = CharacterId,
                        AccountId = accountId,
                        Name = Encoding.UTF8.GetBytes("PvpWiring"),
                        Job = 0,
                        GrowType = 0,
                        Level = 1,
                        TownId = 1,
                        AreaId = 0,
                        Direction = 5,
                        AreaState = 3,
                        Appearance = Array.Empty<
                            CharacterAppearanceEntry>()
                    });
                characters.Create(
                    new CharacterRecord
                    {
                        CharacterId = SecondCharacterId,
                        AccountId = accountId,
                        Name = Encoding.UTF8.GetBytes("PvpWiring2"),
                        Job = 0,
                        GrowType = 0,
                        Level = 1,
                        TownId = 1,
                        AreaId = 0,
                        Direction = 5,
                        AreaState = 3,
                        Appearance = Array.Empty<
                            CharacterAppearanceEntry>()
                    });

                sessions = new SessionDirectory();
                protocol = new GameProtocolHandler(sessions);
                connection = ConnectedSession.Create(
                    GameNetworkConfig.FreeDuelGamePort);
                connection.Session.Account =
                    accounts.GetById(accountId);

                protocol.OnPacketReceived_86JP(
                        connection.Session,
                        CommandHeader(0x0004),
                        new byte[] { 0x00, 0x00 })
                    .GetAwaiter()
                    .GetResult();
                var selectionPackets = connection.DrainPackets();
                var lobbySnapshotSent = selectionPackets.Any(
                    packet =>
                        packet.Command == 0x00 &&
                        packet.Type ==
                            PvpRoomHandler.RoomInfoNotificationType);
                Check(
                    "CH.68 SELECT_CHARACTER production dispatch sends " +
                    "the initial PVP_ROOM_INFO snapshot",
                    lobbySnapshotSent,
                    ref failures);

                if (!InventoryContext.TryGetOwnedLease(
                        connection.Session.SessionId,
                        CharacterId,
                        out var oldLease))
                {
                    throw new InvalidOperationException(
                        "first selected inventory lease is unavailable");
                }
                oldLease.Inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    10);
                if (!InventoryPersistenceService.SaveDirty(oldLease))
                    throw new InvalidOperationException(
                        "first selected inventory fixture save failed");
                CreateInventorySaveAbortTrigger(
                    databasePath,
                    CharacterId,
                    InventoryService.MainVirtualCurrencySlotStart);
                oldLease.Inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    11);

                protocol.OnPacketReceived_86JP(
                        connection.Session,
                        CommandHeader(0x0004),
                        new byte[] { 0x01, 0x00 })
                    .GetAwaiter()
                    .GetResult();
                var failedSwitchPackets = connection.DrainPackets();
                Check(
                    "failed old inventory save rejects character switch",
                    failedSwitchPackets.Any(
                        packet =>
                            packet.Command == 0x01
                            && packet.Type == 0x0004)
                    && connection.Session.Player.CharacterId == CharacterId
                    && sessions.TryGet(CharacterId, out var currentSession)
                    && ReferenceEquals(currentSession, connection.Session)
                    && !sessions.TryGet(SecondCharacterId, out _)
                    && InventoryContext.TryGetOwnedLease(
                        connection.Session.SessionId,
                        CharacterId,
                        out var retainedLease)
                    && ReferenceEquals(retainedLease, oldLease)
                    && retainedLease.Inventory.CountMainItem(0) == 11
                    && LoadPersistedGold(databasePath, CharacterId) == 10,
                    ref failures);
                DropInventorySaveAbortTrigger(databasePath);
                if (!InventoryPersistenceService.SaveDirty(oldLease))
                    throw new InvalidOperationException(
                        "retained inventory retry save failed");

                CreateInventorySaveAbortTrigger(
                    databasePath,
                    CharacterId,
                    InventoryService.MainVirtualCurrencySlotStart);
                oldLease.Inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    12);
                protocol.OnPacketReceived_86JP(
                        connection.Session,
                        CommandHeader(0x0007),
                        Array.Empty<byte>())
                    .GetAwaiter()
                    .GetResult();
                var failedReturnPackets = connection.DrainPackets();
                Check(
                    "failed old inventory save rejects return to selection",
                    failedReturnPackets.Any(
                        packet =>
                            packet.Command == 0x01
                            && packet.Type == 0x0007)
                    && connection.Session.Player.CharacterId == CharacterId
                    && sessions.TryGet(CharacterId, out var returnSession)
                    && ReferenceEquals(returnSession, connection.Session)
                    && InventoryContext.TryGetOwnedLease(
                        connection.Session.SessionId,
                        CharacterId,
                        out var returnLease)
                    && ReferenceEquals(returnLease, oldLease)
                    && returnLease.Inventory.CountMainItem(0) == 12
                    && LoadPersistedGold(databasePath, CharacterId) == 11,
                    ref failures);
                DropInventorySaveAbortTrigger(databasePath);
                if (!InventoryPersistenceService.SaveDirty(oldLease))
                    throw new InvalidOperationException(
                        "return selection retained inventory retry save failed");

                protocol.OnPacketReceived_86JP(
                        connection.Session,
                        CommandHeader(
                            PvpRoomHandler.MakeRoomCommandType),
                        new byte[]
                        {
                            0x06, 0x00, 0x00, 0x00, 0x00
                        })
                    .GetAwaiter()
                    .GetResult();
                var makeRoomPackets = connection.DrainPackets();
                var roomCreated =
                    connection.Session.Player.UserState ==
                        PvpRoomHandler.PvpUserState &&
                    makeRoomPackets.Any(
                        packet =>
                            packet.Command == 0x00 &&
                            packet.Type ==
                                PvpRoomHandler
                                    .RoomInfoNotificationType) &&
                    makeRoomPackets.Any(
                        packet =>
                            packet.Command == 0x00 &&
                            packet.Type ==
                                PvpRoomHandler
                                    .UserAreaNotificationType) &&
                    !makeRoomPackets.Any(
                        packet =>
                            packet.Command == 0x01 &&
                            packet.Type ==
                                PvpRoomHandler.MakeRoomCommandType);
                Check(
                    "MAKE_PVP_ROOM succeeds after the production " +
                    "selection-to-lobby handshake",
                    roomCreated,
                    ref failures);

                oldLease.Inventory.SetMainVirtualCount(
                    InventoryService.MainVirtualCurrencySlotStart,
                    13);
                protocol.OnClientDisconnected(connection.Session)
                    .GetAwaiter()
                    .GetResult();
                Check(
                    "PvP disconnect persists dirty inventory before releasing " +
                    "the owned lease",
                    LoadPersistedGold(databasePath, CharacterId) == 13
                    && !InventoryContext.TryGetLease(
                        CharacterId,
                        out _),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    "[FAIL] free-duel selection wiring threw: " + ex);
                failures++;
            }
            finally
            {
                if (sessions != null && connection != null)
                {
                    try
                    {
                        sessions.UnregisterAsync(
                                CharacterId,
                                connection.Session)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch
                    {
                    }
                    InventoryContext.Unregister(
                        connection.Session.SessionId,
                        CharacterId);
                }

                protocol?.Dispose();
                connection?.Dispose();
                Environment.SetEnvironmentVariable(
                    "INVENTORY_DATABASE_PATH",
                    previousDatabasePath);

                // Restore the in-process gate exactly, without letting a
                // pre-existing environment value change the saved state.
                Environment.SetEnvironmentVariable(
                    GameNetworkConfig
                        .FreeDuelListenerEnvironmentVariable,
                    "0");
                GameNetworkConfig.Configure(
                    previousFreeDuelEnabled
                        ? new[]
                        {
                            "--free-duel-channel-listener"
                        }
                        : Array.Empty<string>());
                Environment.SetEnvironmentVariable(
                    GameNetworkConfig
                        .FreeDuelListenerEnvironmentVariable,
                    previousFreeDuelEnvironment);
                DeleteDatabaseFiles(databasePath);
            }

            Console.WriteLine(
                failures == 0
                    ? "FreeDuelSelectionWiringSelfTest OK"
                    : "FreeDuelSelectionWiringSelfTest FAIL (" +
                      failures + ")");
            return failures == 0 ? 0 : 1;
        }

        private static GamePacketHeader CommandHeader(ushort type)
        {
            return new GamePacketHeader
            {
                cmd = 0x01,
                type = type,
                length = 15
            };
        }

        private static void DeleteDatabaseFiles(string databasePath)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    var path = databasePath + suffix;
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }

        private static void CreateInventorySaveAbortTrigger(
            string databasePath,
            int characterId,
            short slotIndex)
        {
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    ForeignKeys = true,
                }.ToString()))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $@"
CREATE TRIGGER fail_selection_inventory_save
BEFORE UPDATE OF item_core ON character_inventory_items
WHEN OLD.character_id = {characterId}
 AND OLD.list_type = 0
 AND OLD.slot_index = {slotIndex}
BEGIN
    SELECT RAISE(ABORT, 'injected selection inventory failure');
END;";
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void DropInventorySaveAbortTrigger(
            string databasePath)
        {
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    ForeignKeys = true,
                }.ToString()))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "DROP TRIGGER IF EXISTS fail_selection_inventory_save;";
                    command.ExecuteNonQuery();
                }
            }
        }

        private static int LoadPersistedGold(
            string databasePath,
            int characterId)
        {
            using (var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    ForeignKeys = true,
                }.ToString()))
            {
                connection.Open();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
SELECT item_core
FROM character_inventory_items
WHERE character_id = @cid
  AND list_type = 0
  AND slot_index = 0
LIMIT 1;";
                    command.Parameters.AddWithValue("@cid", characterId);
                    var value = command.ExecuteScalar();
                    return value is byte[] data && data.Length >= ItemCore.Size
                        ? ItemCore.FromBytes(data).Count
                        : 0;
                }
            }
        }

        private static void Check(
            string label,
            bool condition,
            ref int failures)
        {
            Console.WriteLine(
                "[" + (condition ? "PASS" : "FAIL") + "] " +
                label);
            if (!condition)
                failures++;
        }

        private sealed class CapturedPacket
        {
            internal byte Command { get; set; }
            internal ushort Type { get; set; }
        }

        private sealed class ConnectedSession : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly TcpClient _peer;
            private readonly TcpClient _server;

            private ConnectedSession(
                TcpListener listener,
                TcpClient peer,
                TcpClient server,
                EnhancedClientSession session)
            {
                _listener = listener;
                _peer = peer;
                _server = server;
                Session = session;
            }

            internal EnhancedClientSession Session { get; }

            internal static ConnectedSession Create(int listenerPort)
            {
                var listener = new TcpListener(
                    IPAddress.Loopback,
                    0);
                listener.Start();
                var port =
                    ((IPEndPoint)listener.LocalEndpoint).Port;
                var peer = new TcpClient
                {
                    ReceiveBufferSize = 1024 * 1024
                };
                var connect = peer.ConnectAsync(
                    IPAddress.Loopback,
                    port);
                var server = listener.AcceptTcpClient();
                connect.GetAwaiter().GetResult();
                server.SendBufferSize = 1024 * 1024;
                return new ConnectedSession(
                    listener,
                    peer,
                    server,
                    new EnhancedClientSession(
                        server,
                        new GamePacketHeader(),
                        listenerPort));
            }

            internal IReadOnlyList<CapturedPacket> DrainPackets()
            {
                var bytes = new List<byte>();
                var quiet = Stopwatch.StartNew();
                while (quiet.Elapsed < TimeSpan.FromMilliseconds(50))
                {
                    var available = _peer.Available;
                    if (available <= 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    var chunk = new byte[available];
                    var offset = 0;
                    while (offset < chunk.Length)
                    {
                        var read = _peer.GetStream().Read(
                            chunk,
                            offset,
                            chunk.Length - offset);
                        if (read <= 0)
                            throw new EndOfStreamException(
                                "free-duel self-test socket closed");
                        offset += read;
                    }
                    bytes.AddRange(chunk);
                    quiet.Restart();
                }

                var packets = new List<CapturedPacket>();
                var packetOffset = 0;
                while (packetOffset + 15 <= bytes.Count)
                {
                    var data = bytes.ToArray();
                    var length = BitConverter.ToInt32(
                        data,
                        packetOffset + 3);
                    if (length < 15 ||
                        packetOffset + length > data.Length)
                    {
                        throw new InvalidDataException(
                            "truncated game packet in free-duel " +
                            "selection self-test");
                    }
                    packets.Add(
                        new CapturedPacket
                        {
                            Command = data[packetOffset],
                            Type = BitConverter.ToUInt16(
                                data,
                                packetOffset + 1)
                        });
                    packetOffset += length;
                }

                if (packetOffset != bytes.Count)
                {
                    throw new InvalidDataException(
                        "trailing game packet bytes in free-duel " +
                        "selection self-test");
                }
                return packets;
            }

            public void Dispose()
            {
                _server.Dispose();
                _peer.Dispose();
                _listener.Stop();
            }
        }
    }
}

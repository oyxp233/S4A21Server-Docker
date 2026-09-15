using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Pvp;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders.Pvp;
using DfoServer.Network.Parsers.Pvp;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class A21FairPvpScoreSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_FAIR_PVP_SCORE selftest ===");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try
            {
                CheckRequest();
                CheckResponse();
                CheckDetailedResponse();
                CheckFirstSeasonRequest();
                CheckFirstSeasonResponse();
                CheckDispatchAsync(CmdPacketTypeA21.FAIR_PVP_SCORE).GetAwaiter().GetResult();
                CheckDispatchAsync(CmdPacketTypeA21.INTEGRATE_MATCH_PVP_SCORE).GetAwaiter().GetResult();
                Console.WriteLine("A21_FAIR_PVP_SCORE: PASS");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"A21_FAIR_PVP_SCORE: FAIL {ex}");
                return 1;
            }
        }

        private static void CheckRequest()
        {
            var captured = new byte[] { 0xEC, 0x03, 1, 3, 0xFF, 0xFF, 0xFF, 0xFF };
            Require(FairPvpScoreRequest.TryParse(captured, out var request)
                && request.TargetUserId == 1004 && request.IsSelf
                && request.ViewMode == 3 && request.ActorIndex == -1,
                "personal-information button request matches native 2741810");
            for (var length = 0; length <= 10; length++)
            {
                if (length == 8)
                    continue;
                var invalid = new byte[length];
                Array.Copy(captured, invalid, Math.Min(captured.Length, length));
                Require(!FairPvpScoreRequest.TryParse(invalid, out _),
                    $"reject request length {length}");
            }
            foreach (var (offset, value) in new[] { (2, 2), (3, 0), (3, 4) })
            {
                var invalid = (byte[])captured.Clone();
                invalid[offset] = (byte)value;
                Require(!FairPvpScoreRequest.TryParse(invalid, out _),
                    $"reject request byte {offset}={value}");
            }
            foreach (var actor in new[] { -2, 3, int.MaxValue })
            {
                var invalid = Request(1004, true, 3, actor);
                Require(!FairPvpScoreRequest.TryParse(invalid, out _),
                    $"reject actor index {actor}");
            }
            foreach (var uid in new ushort[] { 0, ushort.MaxValue })
                Require(!FairPvpScoreRequest.TryParse(Request(uid, true), out _),
                    $"reject reserved uid {uid}");
            Require(!FairPvpScoreRequest.TryParse(null, out _), "reject null request");
            foreach (var actor in new[] { -1, 0, 1, 2 })
                Require(FairPvpScoreRequest.TryParse(Request(1004, false, 1, actor), out _),
                    $"accept native actor index {actor}");
        }

        private static void CheckResponse()
        {
            var body = FairPvpScoreResponseBuilder.BuildBasicBody(0x1234, 3, 17, 4);
            // Independent field walk from 2743B80, including dispatcher success.
            using var stream = new MemoryStream(body);
            using var reader = new BinaryReader(stream);
            Require(reader.ReadByte() == 1 && reader.ReadUInt16() == 0x1234
                && reader.ReadByte() == 3 && reader.ReadByte() == 0
                && reader.ReadByte() == 17 && reader.ReadByte() == 4,
                "basic score echoes target/view and preserves both grade bytes");
            var empty = reader.ReadUInt32() == 0 && reader.ReadUInt32() == 0;
            for (var i = 0; i < 3; i++)
                empty &= reader.ReadByte() == 0;
            for (var i = 0; i < 4; i++)
                empty &= reader.ReadUInt32() == 0;
            Require(empty && stream.Position == stream.Length && body.Length == 34,
                "native no-detail field walk consumes exactly 34 bytes");
        }

        private static void CheckDetailedResponse()
        {
            var score = new PvpSeasonScore
            {
                Individual = new PvpModeScore { Wins = 11, Losses = 12, Draws = 13 },
                Relay = new PvpModeScore { Wins = 21, Losses = 22, Draws = 23 },
                Team = new PvpModeScore { Wins = 31, Losses = 32, Draws = 33 },
                RecentWins = 4, RecentLosses = 5, RecentDraws = 1, WinStreak = 2, PeakWinStreak = 7,
                Jobs = new[] { new PvpJobScore { Job = 3, GrowType = 1,
                    Score = new PvpModeScore { Wins = 8, Losses = 9, Draws = 10 } } }
            };
            var body = FairPvpScoreResponseBuilder.BuildBody(0x1234, 3, 5, 2, score);
            using var stream = new MemoryStream(body);
            using var reader = new BinaryReader(stream);
            Require(reader.ReadByte() == 1 && reader.ReadUInt16() == 0x1234 && reader.ReadByte() == 3
                && reader.ReadByte() == 1 && reader.ReadByte() == 5 && reader.ReadByte() == 2,
                "detailed score enables the native record branch and preserves grades");
            Require(reader.ReadInt32() == 11 && reader.ReadInt32() == 21
                && reader.ReadByte() == 4 && reader.ReadByte() == 5 && reader.ReadByte() == 1
                && reader.ReadInt32() == 2 && reader.ReadInt32() == 0 && reader.ReadInt32() == 0
                && reader.ReadInt32() == 7,
                "native basic score fields carry mode wins, recent ten and streaks in reader order");
            foreach (var expected in new[] { 12, 13, 22, 23, 31, 32, 33 })
                Require(reader.ReadInt32() == expected, "native detail mode counter matches");
            Require(reader.ReadByte() == 1 && reader.ReadByte() == 3 && reader.ReadByte() == 1
                && reader.ReadInt32() == 8 && reader.ReadInt32() == 9 && reader.ReadInt32() == 10
                && stream.Position == stream.Length && body.Length == 77,
                "native job detail consumes the complete 77-byte response");
        }

        private static void CheckFirstSeasonRequest()
        {
            var captured = new byte[] { 0xF0, 0x03 };
            Require(IntegrateMatchPvpScoreRequest.TryParse(captured, out var request)
                && request.TargetUserId == 1008,
                "first-season tab request matches native FA5560 and captured target UID");
            for (var length = 0; length <= 8; length++)
            {
                if (length == 2)
                    continue;
                var invalid = new byte[length];
                Array.Copy(captured, invalid, Math.Min(captured.Length, length));
                Require(!IntegrateMatchPvpScoreRequest.TryParse(invalid, out _),
                    $"first season rejects request length {length}");
            }
            foreach (var uid in new ushort[] { 0, ushort.MaxValue })
                Require(!IntegrateMatchPvpScoreRequest.TryParse(BitConverter.GetBytes(uid), out _),
                    $"first season rejects reserved UID {uid}");
            Require(!IntegrateMatchPvpScoreRequest.TryParse(null, out _),
                "first season rejects null request");
        }

        private static void CheckFirstSeasonResponse()
        {
            var body = IntegrateMatchPvpScoreResponseBuilder.BuildEmptyBody(0x1234);
            // Independent walk from 27415D0: twelve mode statistics, total wins,
            // two u32 + u16 hell-mode statistics, then the final unknown u32.
            using var stream = new MemoryStream(body);
            using var reader = new BinaryReader(stream);
            Require(reader.ReadByte() == 1 && reader.ReadUInt16() == 0x1234,
                "first-season response supplies the cached character lookup UID");
            var empty = true;
            for (var i = 0; i < 13; i++)
                empty &= reader.ReadUInt32() == 0;
            empty &= reader.ReadUInt32() == 0 && reader.ReadUInt32() == 0
                && reader.ReadUInt16() == 0 && reader.ReadUInt32() == 0;
            Require(empty && stream.Position == stream.Length && body.Length == 69,
                "first-season native field walk consumes 69 bytes with no invented history");
        }

        private static async Task CheckDispatchAsync(CmdPacketTypeA21 commandType)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"a21_fair_score_{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var database = new GameDatabase(Path.Combine(directory, "inventory.db"),
                    ServerPaths.SchemaFilePath);
                database.Write((connection, transaction) =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (5001, 'fair-self', ''), (5002, 'fair-other', '');
INSERT INTO characters (character_id, account_id, name, level, pvp_grade, pvp_rating_grade)
VALUES (5001, 5001, 'fair-self', 86, 1, 0), (5002, 5002, 'fair-other', 86, 20, 4);";
                    command.ExecuteNonQuery();
                });
                var sessions = new SessionDirectory();
                using var runtime = new ServerRuntimeBuilder(database);
                var protocol = runtime.BuildGameProtocolHandler(sessions);
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                using var reader = new TcpClient();
                TcpClient writer;
                try
                {
                    var connect = reader.ConnectAsync(IPAddress.Loopback,
                        ((IPEndPoint)listener.LocalEndpoint).Port);
                    writer = await listener.AcceptTcpClientAsync();
                    await connect;
                }
                finally
                {
                    listener.Stop();
                }
                using (writer)
                {
                    reader.ReceiveTimeout = 2000;
                    var requester = Session(5001, 5001, 10011, writer);
                    var target = Session(5002, 5002, 10011);
                    var replacement = Session(5002, 5002, 10011);
                    sessions.Register(5001, requester);
                    sessions.Register(5002, target);
                    var header = new GamePacketHeader
                    {
                        cmd = 1, type = (ushort)commandType
                    };
                    var firstSeason = commandType == CmdPacketTypeA21.INTEGRATE_MATCH_PVP_SCORE;
                    byte[] ScoreRequest(ushort uid, bool self, byte mode = 3) => firstSeason
                        ? BitConverter.GetBytes(uid)
                        : Request(uid, self, mode);
                    void CheckScorePacket(ushort uid, byte mode, byte grade, byte rating) =>
                        CheckPacket(ReadPacket(reader), uid, mode, grade, rating, commandType);
                    Task Send(byte[] request) => protocol.OnPacketReceived_86JP(
                        requester, header, request);
                    async Task Reject(byte[] request, string label)
                    {
                        await Send(request);
                        Require(!reader.Client.Poll(20000, SelectMode.SelectRead), label);
                    }
                    try
                    {
                        await Send(ScoreRequest(5001, true));
                        CheckScorePacket(5001, 3, 1, 0);
                        foreach (var mode in firstSeason ? new byte[] { 3 } : new byte[] { 1, 2, 3 })
                        {
                            await Send(ScoreRequest(5002, false, mode));
                            CheckScorePacket(5002, mode, 20, 4);
                        }
                        await Send(ScoreRequest(5002, false));
                        CheckScorePacket(5002, 3, 20, 4);
                        if (!firstSeason)
                        {
                            await Reject(ScoreRequest(5002, true), "reject false self claim");
                            await Reject(ScoreRequest(5001, false), "reject false other claim");
                        }
                        await Reject(ScoreRequest(5003, false), "reject absent target");
                        await Reject(new byte[7], "real dispatch rejects malformed body");
                        target.Account.AccountId = 5001;
                        await Reject(ScoreRequest(5002, false), "reject target account mismatch");
                        target.Account.AccountId = 5002;
                        sessions.Register(5002, Session(5002, 5002, 10060));
                        await Reject(ScoreRequest(5002, false), "reject cross-channel target");
                        sessions.Register(5002, target);
                        var collision = Session(5002 + 65536, 5002, 10011);
                        sessions.Register(collision.Player.CharacterId, collision);
                        await Reject(ScoreRequest(5002, false), "reject ambiguous 16-bit uid");
                        collision.Player.CharacterId = 0;
                        collision.Close();

                        var sendLock = (SemaphoreSlim)typeof(EnhancedClientSession)
                            .GetField("_sendLock", BindingFlags.Instance | BindingFlags.NonPublic)
                            .GetValue(requester);
                        await sendLock.WaitAsync();
                        Task pending;
                        try
                        {
                            pending = Send(ScoreRequest(5002, false));
                            Require(!pending.IsCompleted, "query waits behind the send lock");
                            sessions.Register(5002, replacement);
                        }
                        finally { sendLock.Release(); }
                        await pending;
                        Require(!reader.Client.Poll(20000, SelectMode.SelectRead),
                            "target reconnect cancels the queued old-generation result");
                        await Send(ScoreRequest(5002, false));
                        CheckScorePacket(5002, 3, 20, 4);

                        await sendLock.WaitAsync();
                        try
                        {
                            pending = Send(ScoreRequest(5001, true));
                            Require(!pending.IsCompleted, "self query waits behind the send lock");
                            requester.Player.CharacterId = 5003;
                            requester.Player.UserId = 5003;
                        }
                        finally { sendLock.Release(); }
                        await pending;
                        Require(!reader.Client.Poll(20000, SelectMode.SelectRead),
                            "character switch cancels the queued self result");
                        requester.Player.CharacterId = 5001;
                        requester.Player.UserId = 5001;
                        sessions.Register(5001, Session(5001, 5001, 10011));
                        await Reject(ScoreRequest(5002, false), "stale requester is rejected by real dispatch");
                    }
                    finally
                    {
                        requester.Close();
                        target.Close();
                        replacement.Close();
                    }
                }
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(directory, recursive: true);
            }
        }

        private static EnhancedClientSession Session(int cid, int aid, int port,
            TcpClient client = null)
        {
            var session = new EnhancedClientSession(client, new GamePacketHeader(), port)
            {
                Account = new AccountRecord { AccountId = aid }
            };
            session.Player.CharacterId = cid;
            session.Player.UserId = unchecked((ushort)cid);
            return session;
        }

        private static byte[] Request(ushort uid, bool self, byte mode = 3, int actor = -1)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(uid);
            writer.WriteByte(self ? (byte)1 : (byte)0);
            writer.WriteByte(mode);
            writer.WriteInt32(actor);
            return writer.ToArray();
        }

        private static byte[] ReadPacket(TcpClient client)
        {
            var stream = client.GetStream();
            var header = new byte[15];
            stream.ReadExactly(header);
            var length = BitConverter.ToInt32(header, 3);
            Require(length >= 15 && length <= 1024, "captured response has a bounded frame");
            var packet = new byte[length];
            header.CopyTo(packet, 0);
            stream.ReadExactly(packet.AsSpan(15));
            return packet;
        }

        private static void CheckPacket(byte[] packet, ushort uid, byte mode,
            byte grade, byte rating, CmdPacketTypeA21 commandType)
        {
            if (commandType == CmdPacketTypeA21.INTEGRATE_MATCH_PVP_SCORE)
            {
                Require(packet.Length == 84 && packet[0] == 1
                    && BitConverter.ToUInt16(packet, 1) == (ushort)commandType
                    && packet[15] == 1 && BitConverter.ToUInt16(packet, 16) == uid
                    && packet.Skip(18).All(value => value == 0),
                    $"real first-season dispatch returns empty historical record for uid={uid}");
                return;
            }
            Require(packet.Length == 78 && packet[0] == 1
                && BitConverter.ToUInt16(packet, 1) == (ushort)CmdPacketTypeA21.FAIR_PVP_SCORE
                && packet[15] == 1 && BitConverter.ToUInt16(packet, 16) == uid
                && packet[18] == mode && packet[19] == 1
                && packet[20] == grade && packet[21] == rating
                && packet.Skip(22).All(value => value == 0),
                $"real dispatch returns stored grade {grade}/{rating} for uid={uid}, mode={mode}");
        }

        private static void Require(bool condition, string name)
        {
            if (!condition)
                throw new InvalidOperationException(name);
            Console.WriteLine($"[PASS] {name}");
        }
    }
}

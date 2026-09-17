using DfoServer.Game.Guilds;
using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders.Guilds;
using DfoServer.Network.Parsers.Guilds;
using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class A21GuildCreationSelfTest
    {
        public static int Run()
        {
            int failures = 0;
            void Check(string label, bool passed)
            {
                Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {label}");
                if (!passed) failures++;
            }
            string path = Path.Combine(Path.GetTempPath(), $"guild-test-{Guid.NewGuid():N}.db");
            try
            {
                byte[] captured = { 4, 0, 0, 0, 0xB2, 0xE2, 0xCA, 0xD4 };
                Check("captured GBK guild name parses exactly", GuildTextRequest.TryParse(captured, 24, out var raw, out var name)
                    && name == "测试" && raw.Length == 4);
                Check("truncated, trailing, negative and excessive lengths rejected",
                    !GuildTextRequest.TryParse(captured[..^1], 24, out _, out _)
                    && !GuildTextRequest.TryParse(captured.Concat(new byte[] { 0 }).ToArray(), 24, out _, out _)
                    && !GuildTextRequest.TryParse(new byte[] { 255, 255, 255, 255 }, 24, out _, out _)
                    && !GuildTextRequest.TryParse(captured, 3, out _, out _));
                Check("malformed GBK and embedded NUL rejected",
                    !GuildTextRequest.TryParse(new byte[] { 1, 0, 0, 0, 0x81 }, 24, out _, out _)
                    && !GuildTextRequest.TryParse(new byte[] { 1, 0, 0, 0, 0 }, 24, out _, out _));
                Check("guild name policy rejects empty/oversized/control names",
                    !GuildCreationRules.TryValidateName(Array.Empty<byte>(), out _)
                    && !GuildCreationRules.TryValidateName(new byte[25], out _)
                    && !GuildCreationRules.TryValidateName(new byte[] { 10, 13 }, out _)
                    && GuildCreationRules.TryValidateName(ClientTextEncoding.GetBytes("测试公会"), out _));
                byte[] ack = GuildCreationPacketBuilder.Ack(CmdPacketTypeA21.CHECK_GUILD_NAME_DOUBLE);
                byte[] error = GuildCreationPacketBuilder.Ack(CmdPacketTypeA21.CHECK_GUILD_NAME_DOUBLE, 0x6B);
                byte[] permit = GuildCreationPacketBuilder.SinglePlayerPermit();
                Check("verified common ACK and duplicate-name error widths",
                    ack.Length == 16 && ack[0] == 1 && ack[15] == 1
                    && error.Length == 17 && error[15] == 0 && error[16] == 0x6B);
                Check("verified one-byte permit notification", permit.Length == 16 && permit[0] == 0
                    && BitConverter.ToUInt16(permit, 1) == (ushort)NotiPacketTypeA21.REPLY_GUILD_CREATE_PERMIT
                    && permit[15] == 1);
                var createdPacket = GuildCreationPacketBuilder.Created(new GuildRecord(321, "测试公会", "", 100));
                Check("create notification has exact current-client layout", createdPacket.Length == 31
                    && BitConverter.ToUInt16(createdPacket, 1) == (ushort)NotiPacketTypeA21.GUILD_CREATE
                    && BitConverter.ToInt32(createdPacket, 15) == 321
                    && BitConverter.ToInt32(createdPacket, 19) == 8
                    && createdPacket.AsSpan(23).SequenceEqual(ClientTextEncoding.GetBytes("测试公会")));
                var database = new GameDatabase(path, ServerPaths.SchemaFilePath);
                var repository = new GuildRepository(database);
                Check("creation cost comes from current PVF", GuildCreationService.ReadCreationCost() == 300_000);
                var guildTail = new Game.SelectCharacter.UserInfoMinimumTailSnapshot
                {
                    GuildId = 321, GuildNameBytes = ClientTextEncoding.GetBytes("测试公会"), GuildLevel = 1,
                    ExpertJobType = 2, ExpertJobExp = 1234, ProgressA = 9, ProgressB = 5678,
                    MoodValue = 17, SkillTreeIndex = 1, FatiguePenalty = 999
                };
                var guildTailBytes = Network.Builders.UserInfoSubtype0Builder.BuildA21AfterAlive(guildTail);
                int delta = guildTail.GuildNameBytes.Length - 2;
                Check("guild DSTR shifts later fields without corrupting them", guildTailBytes.Length == 64 + delta
                    && BitConverter.ToInt32(guildTailBytes, 6) == guildTail.GuildNameBytes.Length
                    && guildTailBytes.AsSpan(10, guildTail.GuildNameBytes.Length).SequenceEqual(guildTail.GuildNameBytes)
                    && BitConverter.ToUInt32(guildTailBytes, 10 + guildTail.GuildNameBytes.Length) == 1
                    && guildTailBytes[23 + delta] == 2 && BitConverter.ToUInt32(guildTailBytes, 24 + delta) == 1234
                    && BitConverter.ToUInt32(guildTailBytes, 43 + delta) == 5678
                    && BitConverter.ToUInt16(guildTailBytes, 59 + delta) == 17 && guildTailBytes[61 + delta] == 1);
                var guildAppearance = Network.Builders.UserInfoSubtype0Builder.BuildRemainingBytes(
                    new Game.Characters.CharacterRecord { Subtype0Tail = guildTail });
                Check("USERINFO0 publishes guild ID instead of legacy fatigue field", BitConverter.ToUInt32(guildAppearance, 24) == 321);
                Check("fresh schema has empty guild registry", !repository.NameExists("测试公会") && !repository.IsMember(100));
                database.Write((c, t) =>
                {
                    using var cmd = c.CreateCommand(); cmd.Transaction = t;
                    cmd.CommandText = @"
INSERT INTO accounts(account_id,m_id,password_hash) VALUES(100,'guild-test','');
INSERT INTO characters(character_id,account_id,name) VALUES(100,100,'guild-founder');
INSERT INTO guilds(guild_id,name,leader_character_id) VALUES(1,'TestGuild',100);
INSERT INTO guild_members(character_id,guild_id) VALUES(100,1);";
                    cmd.ExecuteNonQuery();
                });
                Check("name checks and member identity use durable registry", repository.NameExists("testguild") && repository.IsMember(100));
                database.Write((c,t) =>
                {
                    using var cmd=c.CreateCommand();cmd.Transaction=t;
                    cmd.CommandText=@"INSERT INTO guild_applications(guild_id,character_id) VALUES(1,100);
ALTER TABLE guild_applications DROP COLUMN message;
PRAGMA user_version=29; UPDATE schema_metadata SET schema_version=29;";
                    cmd.ExecuteNonQuery();
                });
                using (var connection = database.OpenConnection()) DfoServer.Sqlite.SqliteMigrations.Apply(connection);
                Check("v29 migration preserves existing guild, member and application", repository.IsMember(100)
                    && repository.GetApplicationsForLeader(100).Single().Message == ""
                    && repository.GetForMember(100).Name == "TestGuild");
                database.Write((c,t) =>
                {
                    using var cmd=c.CreateCommand();cmd.Transaction=t;
                    cmd.CommandText=@"ALTER TABLE guilds DROP COLUMN announcement;
ALTER TABLE guild_members DROP COLUMN rank; ALTER TABLE guild_members DROP COLUMN memo;
PRAGMA user_version=30; UPDATE schema_metadata SET schema_version=30;";
                    cmd.ExecuteNonQuery();
                });
                using (var connection=database.OpenConnection()) DfoServer.Sqlite.SqliteMigrations.Apply(connection);
                Check("v30 upgrade preserves roster and application with management defaults", repository.GetForMember(100).Announcement==""
                    && repository.GetRosterForMember(100).Members.Single().Rank==1
                    && repository.GetRosterForMember(100).Members.Single().Memo==""
                    && repository.GetApplicationsForLeader(100).Count==1);
                bool guarded = false;
                try { database.Write((c,t) => { using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="UPDATE characters SET delete_flag=1 WHERE character_id=100;";cmd.ExecuteNonQuery(); }); }
                catch (SqliteException) { guarded = true; }
                Check("member deletion cannot orphan a guild", guarded);
                database.Write((c,t) =>
                {
                    using var cmd=c.CreateCommand();cmd.Transaction=t;
                    cmd.CommandText=@"DROP TRIGGER guild_member_prevent_soft_delete;
DROP TABLE guild_applications; DROP TABLE guild_members; DROP TABLE guilds;
PRAGMA user_version=28; UPDATE schema_metadata SET schema_version=28;";
                    cmd.ExecuteNonQuery();
                });
                using (var connection = database.OpenConnection()) DfoServer.Sqlite.SqliteMigrations.Apply(connection);
                var migrated = database;
                Check("v28 upgrades to guild schema without replacing characters", migrated.Read(c =>
                {
                    using var cmd=c.CreateCommand();
                    cmd.CommandText="SELECT (SELECT COUNT(*) FROM characters WHERE character_id=100) + (SELECT schema_version FROM schema_metadata);";
                    return Convert.ToInt32(cmd.ExecuteScalar()) == 1 + DfoServer.Sqlite.SqliteMigrations.CurrentVersion;
                }) && !new GuildRepository(migrated).IsMember(100));
                database.Write((c,t) =>
                {
                    using var cmd=c.CreateCommand();cmd.Transaction=t;
                    cmd.CommandText=@"INSERT INTO characters(character_id,account_id,name) VALUES
(101,100,'guild-second'),(102,100,'guild-poor'),(103,100,'guild-rollback'),(104,100,'guild-applicant');";
                    cmd.ExecuteNonQuery();
                });
                var leases = new System.Collections.Generic.List<InventoryLease>();
                InventoryLease Lease(int id, int gold)
                {
                    var inventory = new InventoryService(id, 100, database);
                    inventory.SetMainVirtualCount(0, 0, gold);
                    var lease = InventoryContext.Register(Guid.NewGuid(), inventory);
                    leases.Add(lease);
                    if (!InventoryPersistenceService.SaveDirty(lease)) throw new Exception("seed gold failed");
                    return lease;
                }
                try
                {
                    var service = new GuildCreationService(repository, 300_000);
                    var founder = Lease(100, 600_000);
                    var second = Lease(101, 600_000);
                    var poor = Lease(102, 299_999);
                    var rollback = Lease(103, 600_000);
                    var created = service.Create(founder, ClientTextEncoding.GetBytes("测试公会"), "一起冒险");
                    Check("single founder creates guild and spends exactly 300000", created.Status == GuildResult.Success
                        && created.GoldAfter == 300_000 && repository.GetForMember(100)?.LeaderId == 100
                        && repository.GetMemberIds(created.Guild.Id).SequenceEqual(new[] { 100 }));
                    var restoredTail = new Game.CharacterData.SqliteSubtype0FieldsRepository(database).Load(100);
                    Check("login projection restores guild identity from membership", restoredTail.GuildId == created.Guild.Id
                        && restoredTail.GuildLevel == 1 && restoredTail.GuildNameBytes.SequenceEqual(ClientTextEncoding.GetBytes(created.Guild.Name)));
                    Check("repeated create does not charge twice", service.Create(founder,
                        ClientTextEncoding.GetBytes("再次创建"), "").Status == GuildResult.AlreadyMember
                        && founder.Inventory.GetMainVirtualCount(0).Count == 300_000);
                    Check("duplicate name rejected without charging another character", service.Create(second,
                        ClientTextEncoding.GetBytes("测试公会"), "").Status == GuildResult.DuplicateName
                        && second.Inventory.GetMainVirtualCount(0).Count == 600_000);
                    Check("insufficient gold leaves no guild", service.Create(poor,
                        ClientTextEncoding.GetBytes("余额不足"), "").Status == GuildResult.InsufficientGold
                        && !repository.IsMember(102));
                    database.Write((c,t) =>
                    {
                        using var cmd=c.CreateCommand();cmd.Transaction=t;
                        cmd.CommandText=@"CREATE TRIGGER fail_guild_member BEFORE INSERT ON guild_members
WHEN NEW.character_id=103 BEGIN SELECT RAISE(ABORT,'test guild rollback'); END;";
                        cmd.ExecuteNonQuery();
                    });
                    var failed = service.Create(rollback, ClientTextEncoding.GetBytes("回滚公会"), "");
                    Check("failed member insert rolls back guild and reloads online gold", failed.Status == GuildResult.PersistenceFailed
                        && !repository.NameExists("回滚公会") && !repository.IsMember(103)
                        && rollback.Inventory.GetMainVirtualCount(0).Count == 600_000);
                    var applicationBody = new GamePacketWriter();
                    applicationBody.WriteClientDstr(created.Guild.Name); applicationBody.WriteClientDstr("一起冒险");
                    Check("two-string application request parses strictly", GuildTextRequest.TryParseApplication(applicationBody.ToArray(),
                        out var applicationName, out var applicationMessage) && applicationName == created.Guild.Name && applicationMessage == "一起冒险"
                        && !GuildTextRequest.TryParseApplication(applicationBody.ToArray()[..^1], out _, out _)
                        && !GuildTextRequest.TryParseApplication(applicationBody.ToArray().Concat(new byte[] { 0 }).ToArray(), out _, out _)
                        && !GuildTextRequest.TryParseApplication(new byte[] { 255,255,255,127,0,0,0,0 }, out _, out _));
                    Check("search returns committed guild only", repository.FindByName(created.Guild.Name)?.MemberCount == 1
                        && repository.FindByName("不存在") == null);
                    Check("application message persists without duplicate overwrite", repository.Apply(created.Guild.Id,104,"一起冒险") == GuildResult.Success
                        && repository.Apply(created.Guild.Id,104,"第二次") == GuildResult.Success
                        && new GuildRepository(database).GetApplicationsForLeader(100).Single().Message == "一起冒险"
                        && repository.GetApplicationsForLeader(101).Count == 0);
                    var applications = repository.GetApplicationsForLeader(100);
                    var applicationPacket = GuildJoinPacketBuilder.Applications(applications);
                    using (var reader = new BinaryReader(new MemoryStream(applicationPacket,15,applicationPacket.Length-15)))
                    {
                        string Text() => ClientTextEncoding.GetString(reader.ReadBytes(reader.ReadInt32()));
                        bool valid = reader.ReadByte()==1 && reader.ReadInt32()==1 && reader.ReadInt32()==104
                            && Text()=="guild-applicant" && reader.ReadByte()==0 && reader.ReadByte()==0
                            && reader.ReadByte()==1 && reader.ReadByte()==0 && Text()=="一起冒险"
                            && reader.ReadUInt32()==applications[0].ElapsedSeconds;
                        Check("application list matches client reader through elapsed seconds", valid && reader.BaseStream.Position==reader.BaseStream.Length);
                    }
                    Check("new application sends elapsed seconds instead of Unix time", applications.Single().ElapsedSeconds < 60);
                    void SetApplicationTime(string timestamp)
                    {
                        database.Write((c,t) =>
                        {
                            using var cmd=c.CreateCommand();cmd.Transaction=t;
                            cmd.CommandText="UPDATE guild_applications SET applied_at="+timestamp+" WHERE guild_id=@guild AND character_id=104;";
                            cmd.Parameters.AddWithValue("@guild",created.Guild.Id);cmd.ExecuteNonQuery();
                        });
                    }
                    SetApplicationTime("datetime('now','-2 days','-1 hour')");
                    var aged = new GuildRepository(database).GetApplicationsForLeader(100).Single();
                    Check("persisted application age survives repository recreation", aged.ElapsedSeconds >= 176400 && aged.ElapsedSeconds <= 176405);
                    Check("wire age is elapsed duration", BitConverter.ToUInt32(GuildJoinPacketBuilder.Applications(new[]{aged})[^4..])==aged.ElapsedSeconds);
                    SetApplicationTime("datetime('now','+1 day')");
                    Check("future timestamp does not wrap elapsed age", repository.GetApplicationsForLeader(100).Single().ElapsedSeconds==0);
                    SetApplicationTime("'1900-01-01 00:00:00'");
                    Check("very old timestamp stays within client signed range", repository.GetApplicationsForLeader(100).Single().ElapsedSeconds==int.MaxValue);
                    SetApplicationTime("CURRENT_TIMESTAMP");
                    database.Write((c,t) =>
                    {
                        using var cmd=c.CreateCommand();cmd.Transaction=t;
                        cmd.CommandText="CREATE TRIGGER fail_approve BEFORE INSERT ON guild_members WHEN NEW.character_id=104 BEGIN SELECT RAISE(ABORT,'approval rollback'); END;";
                        cmd.ExecuteNonQuery();
                    });
                    bool approveRolledBack = false;
                    try { repository.Approve(created.Guild.Id,100,104); }
                    catch (SqliteException) { approveRolledBack=true; }
                    Check("failed approval preserves application and has no member", approveRolledBack && !repository.IsMember(104)
                        && repository.GetApplicationsForLeader(100).Count==1);
                    database.Write((c,t) => {using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="DROP TRIGGER fail_approve;";cmd.ExecuteNonQuery();});
                    Check("application is idempotent and requires leader approval", repository.Apply(created.Guild.Id, 104) == GuildResult.Success
                        && repository.Apply(created.Guild.Id, 104) == GuildResult.Success
                        && repository.Approve(created.Guild.Id, 101, 104) == GuildResult.NotLeader
                        && !repository.IsMember(104)
                        && repository.Approve(created.Guild.Id, 100, 104) == GuildResult.Success);
                    Check("member persists across repository recreation", new GuildRepository(database).GetForMember(104)?.Id == created.Guild.Id);
                    Check("approval removes applications atomically and rejects replay", repository.GetApplicationsForLeader(100).Count==0
                        && repository.Approve(created.Guild.Id,100,104)==GuildResult.AlreadyMember
                        && repository.Apply(created.Guild.Id,104)==GuildResult.AlreadyMember);
                    var approvalOk = GuildJoinPacketBuilder.Approved(104,true);
                    var approvalFailed = GuildJoinPacketBuilder.Approved(104,false);
                    Check("approval reply retains target ID in both branches", approvalOk.Length==20 && approvalOk[15]==1
                        && BitConverter.ToInt32(approvalOk,16)==104 && approvalFailed.Length==21 && approvalFailed[15]==0
                        && BitConverter.ToInt32(approvalFailed,17)==104);
                    Check("member cannot remove leader or other member", repository.RemoveMember(104, 100) == GuildResult.NotLeader
                        && repository.RemoveMember(100, 100) == GuildResult.LeaderCannotLeave);
                    var roster = repository.GetRosterForMember(104);
                    var info = GuildInfoPacketBuilder.Build(roster);
                    using (var reader = new BinaryReader(new MemoryStream(info,15,info.Length-15)))
                    {
                        string Text() => ClientTextEncoding.GetString(reader.ReadBytes(reader.ReadInt32()));
                        bool valid=Text()==created.Guild.Name && reader.ReadByte()==1 && reader.ReadByte()==0
                            && reader.ReadUInt16()==2 && reader.ReadByte()==1 && reader.ReadUInt32()==0 && reader.ReadByte()==0
                            && Text()=="";
                        valid &= reader.ReadBytes(4).SequenceEqual(new byte[]{0,0,0,1}) && reader.ReadUInt32()==0
                            && Text()=="" && Text()=="" && reader.ReadUInt16()==0 && Text()==created.Guild.Promotion;
                        for(int rank=0;rank<6;rank++)
                        {
                            // Includes bit 10, required by the current client's
                            // member-removal menu at 025A2580..025A25F2.
                            valid &= reader.ReadUInt32()==(rank==1 ? 0xC36u : 0u);
                            byte[] label=reader.ReadBytes(24);
                            valid &= label.Length==24 && label[23]==0;
                        }
                        valid &= reader.ReadBytes(48).All(b=>b==0);
                        Check("guild info reader reaches exact end after permissions and empty optional sections",
                            valid && reader.BaseStream.Position==reader.BaseStream.Length);
                    }
                    Check("own rank notification is independent from member roster", GuildJoinPacketBuilder.Rank(true)[15]==1
                        && GuildJoinPacketBuilder.Rank(false)[15]==4 && GuildJoinPacketBuilder.Rank(true).Length==16);
                    Check("roster resolves durable membership and includes founder and approved member",
                        roster?.Guild.Id == created.Guild.Id && roster.LeaderName == "guild-founder"
                        && roster.Members.Select(m => m.CharacterId).SequenceEqual(new[] { 100, 104 })
                        && roster.Members[0].IsLeader && !roster.Members[1].IsLeader
                        && repository.GetRosterForMember(102) == null);
                    byte[] rosterPacket = GuildMemberPacketBuilder.Build(roster, id => id == 100 ? (byte)11 : null);
                    using (var reader = new BinaryReader(new MemoryStream(rosterPacket, 15, rosterPacket.Length - 15)))
                    {
                        string Text() => ClientTextEncoding.GetString(reader.ReadBytes(reader.ReadInt32()));
                        bool valid = reader.ReadByte() == 1 && reader.ReadInt32() == created.Guild.Id
                            && Text() == "guild-founder" && reader.ReadUInt32() == 0
                            && reader.ReadUInt16() == 2 && reader.ReadUInt16() == 2;
                        for (int i = 0; i < 2; i++)
                        {
                            var member = roster.Members[i];
                            valid &= reader.ReadInt32() == member.CharacterId && Text() == member.Name
                                && Text() == "" && reader.ReadUInt16() == member.Level
                                && reader.ReadByte() == member.Job && reader.ReadByte() == member.GrowType
                                && reader.ReadByte() == GameNetworkConfig.ChannelServerIndex
                                && reader.ReadByte() == (i == 0 ? 11 : 255)
                                && reader.ReadByte() == 0 && reader.ReadByte() == 0
                                && reader.ReadByte() == (i == 0 ? 1 : 4)
                                && reader.ReadUInt32() == (i == 0 ? 0 : member.OfflineSeconds)
                                && reader.ReadInt32() == 100 && reader.ReadByte() == 0 && Text() == "";
                        }
                        Check("client roster reader consumes both rows exactly, including offline marker and rank",
                            valid && reader.BaseStream.Position == reader.BaseStream.Length
                            && BitConverter.ToUInt16(rosterPacket, 1) == (ushort)CmdPacketTypeA21.GUILD_MEMER_LIST);
                    }
                    Check("fresh repository restores roster after reconnect", new GuildRepository(database)
                        .GetRosterForMember(100).Members.Count == 2);
                    Check("member can leave and leader can approve again and remove", repository.RemoveMember(104, 104) == GuildResult.Success
                        && !repository.IsMember(104) && repository.Apply(created.Guild.Id, 104) == GuildResult.Success
                        && repository.Approve(created.Guild.Id, 100, 104) == GuildResult.Success
                        && repository.RemoveMember(100, 104) == GuildResult.Success);
                    Check("removed member cannot read former roster", repository.GetRosterForMember(104) == null
                        && repository.GetRosterForMember(100).Members.Count == 1);
                    Check("pending application survives fresh repository and cancel is owner-scoped",
                        repository.Apply(created.Guild.Id, 104, "申请留言") == GuildResult.Success
                        && new GuildRepository(database).GetPendingApplication(104)?.Message == "申请留言"
                        && repository.CancelApplication(102, created.Guild.Id) == GuildResult.NotApplicant
                        && repository.GetPendingApplication(104) != null);
                    Check("nonleader cannot deny application", repository.DenyApplication(102,104) == GuildResult.NotLeader
                        && repository.GetPendingApplication(104) != null);
                    Check("deny removes only pending application and duplicate fails", repository.DenyApplication(100,104) == GuildResult.Success
                        && repository.DenyApplication(100,104) == GuildResult.NotApplicant && !repository.IsMember(104));
                    Check("applicant cancels and reapplies", repository.Apply(created.Guild.Id,104)==GuildResult.Success
                        && repository.CancelApplication(104,created.Guild.Id)==GuildResult.Success
                        && repository.GetPendingApplication(104)==null && repository.Apply(created.Guild.Id,104)==GuildResult.Success
                        && repository.Approve(created.Guild.Id,100,104)==GuildResult.Success);
                    Check("text edits reject nonleader and malformed input", repository.EditText(104,GuildTextField.Announcement,"非法") == GuildResult.NotLeader
                        && repository.EditText(104,GuildTextField.Promotion,"非法") == GuildResult.NotLeader
                        && repository.EditText(100,GuildTextField.Announcement,new string('中',61)) == GuildResult.InvalidPromotion
                        && repository.EditText(104,GuildTextField.Memo,"a\0b") == GuildResult.InvalidPromotion);
                    Check("announcement, promotion and own memo persist", repository.EditText(100,GuildTextField.Announcement,"欢迎加入") == GuildResult.Success
                        && repository.EditText(100,GuildTextField.Promotion,"一起冒险") == GuildResult.Success
                        && repository.EditText(104,GuildTextField.Memo,"成员备注") == GuildResult.Success
                        && new GuildRepository(database).GetRosterForMember(104).Guild.Announcement == "欢迎加入"
                        && repository.GetForMember(104).Promotion == "一起冒险"
                        && repository.GetRosterForMember(104).Members.Single(m=>m.CharacterId==104).Memo == "成员备注");
                    Check("rank changes reject outsiders, self and invalid grades", repository.ChangeRank(104,100,2)==GuildResult.NotLeader
                        && repository.ChangeRank(100,100,2)==GuildResult.LeaderCannotLeave
                        && repository.ChangeRank(100,102,2)==GuildResult.NotFound
                        && repository.ChangeRank(100,104,0)==GuildResult.InvalidRank
                        && repository.ChangeRank(100,104,6)==GuildResult.InvalidRank);
                    Check("member grade persists without granting leadership", repository.ChangeRank(100,104,2)==GuildResult.Success
                        && new GuildRepository(database).GetRosterForMember(104).Members.Single(m=>m.CharacterId==104).Rank==2
                        && repository.Disband(104)==GuildResult.NotLeader && repository.ChangeRank(104,100,1)==GuildResult.NotLeader);
                    database.Write((c,t)=> { using var cmd=c.CreateCommand();cmd.Transaction=t;
                        cmd.CommandText="CREATE TRIGGER fail_transfer BEFORE UPDATE OF leader_character_id ON guilds BEGIN SELECT RAISE(ABORT,'test transfer rollback'); END;";cmd.ExecuteNonQuery(); });
                    bool transferRolledBack=false;
                    try { repository.ChangeRank(100,104,1); } catch(SqliteException) { transferRolledBack=true; }
                    Check("failed transfer rolls back both leader and member grade", transferRolledBack
                        && repository.GetForMember(104).LeaderId==100
                        && repository.GetRosterForMember(104).Members.Single(m=>m.CharacterId==104).Rank==2);
                    database.Write((c,t)=> { using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="DROP TRIGGER fail_transfer;";cmd.ExecuteNonQuery(); });
                    Check("transfer changes unique leader and demotes predecessor atomically", repository.ChangeRank(100,104,1)==GuildResult.Success
                        && repository.GetForMember(100).LeaderId==104
                        && repository.GetRosterForMember(100).Members.Single(m=>m.CharacterId==100).Rank==4
                        && repository.GetRosterForMember(104).Members.Single(m=>m.CharacterId==104).Rank==1);
                    Check("former leader loses authority immediately and new leader cannot leave", repository.Disband(100)==GuildResult.NotLeader
                        && repository.RemoveMember(100,104)==GuildResult.NotLeader
                        && repository.RemoveMember(104,104)==GuildResult.LeaderCannotLeave
                        && repository.ChangeRank(100,104,1)==GuildResult.NotLeader);
                    repository.Apply(created.Guild.Id,102,"待审批");
                    database.Write((c,t)=> { using var cmd=c.CreateCommand();cmd.Transaction=t;
                        cmd.CommandText="CREATE TRIGGER fail_disband BEFORE DELETE ON guild_members BEGIN SELECT RAISE(ABORT,'test disband rollback'); END;";cmd.ExecuteNonQuery(); });
                    bool disbandRolledBack=false;
                    try { repository.Disband(104); } catch(SqliteException) { disbandRolledBack=true; }
                    Check("failed disband preserves members, leader and pending applications", disbandRolledBack
                        && repository.GetForMember(100).LeaderId==104 && repository.GetMemberIds(created.Guild.Id).Count==2
                        && repository.GetPendingApplication(102)!=null);
                    database.Write((c,t)=> { using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="DROP TRIGGER fail_disband;";cmd.ExecuteNonQuery(); });
                    Check("disband cascades all memberships and applications", repository.Disband(104)==GuildResult.Success
                        && repository.Get(created.Guild.Id)==null && !repository.IsMember(100) && !repository.IsMember(104)
                        && repository.GetPendingApplication(102)==null && repository.Disband(104)==GuildResult.NotLeader);
                    foreach (bool kicked in new[]{false,true})
                    {
                        byte[] left=GuildManagementPacketBuilder.Left("成员","公会",kicked);
                        using var r=new BinaryReader(new MemoryStream(left,15,left.Length-15));
                        string Text()=>ClientTextEncoding.GetString(r.ReadBytes(r.ReadInt32()));
                        Check(kicked ? "kick dialog reads guild before member" : "leave dialog reads guild before member",
                            r.ReadByte()==1 && r.ReadByte()==(kicked ? 2 : 1) && Text()=="公会" && Text()=="成员"
                            && r.BaseStream.Position==r.BaseStream.Length);
                    }
                    byte[] pendingEmpty=GuildManagementPacketBuilder.Pending(null);
                    var pending=new GuildPendingApplication(created.Guild,"hello");
                    byte[] pendingPacket=GuildManagementPacketBuilder.Pending(pending);
                    using(var r=new BinaryReader(new MemoryStream(pendingPacket,15,pendingPacket.Length-15)))
                    {
                        string Text()=>ClientTextEncoding.GetString(r.ReadBytes(r.ReadInt32()));
                        Check("pending notification matches conditional client reader", pendingEmpty.Length==16 && pendingEmpty[15]==0
                            && r.ReadByte()==1 && r.ReadInt32()==created.Guild.Id && Text()==created.Guild.Name && Text()=="hello"
                            && r.ReadUInt32()==0 && r.ReadUInt32()==0 && r.BaseStream.Position==r.BaseStream.Length);
                    }
                    byte[] chat=Network.Handlers.ChatHandler.BuildGuildNotificationBody(ClientTextEncoding.GetBytes("成员"),ClientTextEncoding.GetBytes("你好"));
                    using(var r=new BinaryReader(new MemoryStream(chat)))
                    {
                        string Text()=>ClientTextEncoding.GetString(r.ReadBytes(r.ReadInt32()));
                        Check("guild cross-channel message carries authenticated sender name", r.ReadByte()==6 && r.ReadByte()==0
                            && Text()=="成员" && r.ReadByte()==GameNetworkConfig.ChannelServerIndex && Text()=="你好"
                            && r.BaseStream.Position==r.BaseStream.Length);
                    }
                    GuildIdentityProjection.Apply(created.Guild, guildTail);
                    GuildIdentityProjection.Apply(null, guildTail);
                    Check("leaving clears all projected guild identity", guildTail.GuildId == 0
                        && guildTail.GuildNameBytes.Length == 0 && guildTail.GuildLevel == 0);
                    InventoryContext.Unregister(second.SessionId);
                    Check("stale inventory lease cannot create or charge", service.Create(second,
                        ClientTextEncoding.GetBytes("过期会话"), "").Status == GuildResult.StaleSession
                        && !repository.NameExists("过期会话"));
                }
                finally
                {
                    foreach (var lease in leases) InventoryContext.Unregister(lease.SessionId);
                }
                GuildRuntimeSelfTest.RunAsync(database, Check).GetAwaiter().GetResult();
            }
            catch (Exception ex) { Console.WriteLine(ex); failures++; }
            finally
            {
                SqliteConnection.ClearAllPools();
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(path + suffix)) File.Delete(path + suffix);
            }
            Console.WriteLine($"Guild creation and members: {failures} failure(s)");
            return failures == 0 ? 0 : 1;
        }
    }
}

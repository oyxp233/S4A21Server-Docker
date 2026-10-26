using System;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Guilds;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders.Guilds;

namespace DfoServer.SelfTests
{
    internal static class GuildContentSelfTest
    {
        internal static void Run(IGameDatabase database, Action<string, bool> check)
        {
            var definitions = GuildContentRules.Current;
            check("PVF guild stat/EXP catalog has exact prices and terms", definitions.Count == 7
                && definitions[2].Price == 200 && definitions[3].Price == 200
                && definitions[8].Days == 30 && definitions[8].Price == 6000
                && definitions[1].Days == -1 && definitions[1].Price == 36000);
            database.Write((c,t) =>
            {
                using var cmd=c.CreateCommand();cmd.Transaction=t;
                cmd.CommandText="INSERT INTO characters(character_id,account_id,name,created_at,updated_at) VALUES(601,100,'content-leader',CURRENT_TIMESTAMP,CURRENT_TIMESTAMP),(602,100,'content-member',CURRENT_TIMESTAMP,CURRENT_TIMESTAMP),(603,100,'content-runner',CURRENT_TIMESTAMP,CURRENT_TIMESTAMP);";
                cmd.ExecuteNonQuery();
            });
            var repo=new GuildRepository(database);
            var guild=database.Write((c,t)=>GuildRepository.Insert(c,t,601,"content-guild",""));
            repo.Apply(guild.Id,602); repo.Approve(guild.Id,601,602);
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            void Funds(int value) => database.Write((c,t)=>
            {
                using var cmd=c.CreateCommand();cmd.Transaction=t;
                cmd.CommandText="UPDATE guilds SET contribution=@value WHERE guild_id=@guild;";
                cmd.Parameters.AddWithValue("@value",value);cmd.Parameters.AddWithValue("@guild",guild.Id);cmd.ExecuteNonQuery();
            });
            int Balance()=>repo.GetRosterForMember(601).Guild.Contribution;
            Funds(1000);
            check("only owner can buy; unsupported items do not charge",
                repo.PurchaseContent(602,definitions[2],now)==GuildResult.NotLeader
                && repo.PurchaseContent(601,null,now)==GuildResult.UnsupportedContent && Balance()==1000);
            check("insufficient guild points do not create content",
                repo.PurchaseContent(601,definitions[1],now)==GuildResult.InsufficientContribution
                && repo.GetContents(601,now).Count==0 && Balance()==1000);
            var outcomes=Task.WhenAll(Enumerable.Range(0,2).Select(_=>Task.Run(()=>repo.PurchaseContent(601,definitions[2],now)))).GetAwaiter().GetResult();
            check("concurrent stat purchase charges once and persists one entitlement",
                outcomes.Count(x=>x==GuildResult.Success)==1 && outcomes.Count(x=>x==GuildResult.ContentAlreadyActive)==1
                && Balance()==800 && repo.GetContents(601,now).Single().ExpiresAt==now+86400);
            check("EXP purchase is independently charged and restored for every member",
                repo.PurchaseContent(601,definitions[3],now)==GuildResult.Success && Balance()==600
                && GuildContentRules.ExperiencePercent(new GuildRepository(database).GetContents(602,now),now)==10
                && GuildContentRules.StatBonus(repo.GetContents(602,now),now)==60);
            database.Write((c,t)=>{using var cmd=c.CreateCommand();cmd.Transaction=t;
                cmd.CommandText="INSERT INTO character_subtype1_fields(character_id,stat_physical_attack,stat_physical_defense,stat_magical_attack,stat_magical_defense) VALUES(602,100,110,120,130);";
                cmd.ExecuteNonQuery();});
            var subtype=new SqliteSubtype1Repository(database);
            var projected=subtype.Load(602);
            check("guild buff does not overwrite persistent base combat fields", projected.StatPhysicalAttack==100
                && projected.StatPhysicalDefense==110 && projected.StatMagicalAttack==120 && projected.StatMagicalDefense==130
                && subtype.Load(602).StatPhysicalAttack==100
                && database.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT stat_physical_attack FROM character_subtype1_fields WHERE character_id=602;";return Convert.ToInt32(cmd.ExecuteScalar());})==100);
            check("stat purchase of another duration cannot stack while active",
                repo.PurchaseContent(601,definitions[8],now)==GuildResult.ContentAlreadyActive && Balance()==600);
            check("absolute expiry removes both effects at the boundary",
                repo.GetContents(602,now+86399).Count==2 && repo.GetContents(602,now+86400).Count==0);
            check("expired purchase starts a fresh paid term",
                repo.PurchaseContent(601,definitions[2],now+86400)==GuildResult.Success && Balance()==400
                && repo.GetContents(601,now+86400).Single().ExpiresAt==now+172800);
            repo.RemoveMember(602,602);
            check("leaving immediately loses all guild benefits", repo.GetContents(602,now).Count==0 && subtype.Load(602).StatPhysicalAttack==100);
            repo.Apply(guild.Id,602);repo.Approve(guild.Id,601,602);
            var buffs=GuildContentPacketBuilder.Buffs(repo.GetContents(602,now),now);
            check("guild stat refresh removes previous effects before replaying exact captured buff layout",
                buffs.Count==2 && BitConverter.ToUInt16(buffs[0],1)==(ushort)NotiPacketTypeA21.CHARACTER_DEL_BUFF
                && buffs[0].AsSpan(15).SequenceEqual(new byte[]{2,167,0,0,0,169,0,0,0})
                && BitConverter.ToUInt16(buffs[1],1)==(ushort)NotiPacketTypeA21.CHARACTER_ADD_BUFF
                && buffs[1].AsSpan(15).SequenceEqual(new byte[]{1,167,0,0,0,1,0,0,0,0,0,0,0,0,0,0,0}));
            check("expired or absent guild only removes owned guild buffs",
                GuildContentPacketBuilder.Buffs(repo.GetContents(602,now+172800),now+172800).Count==1);
            var exp=new DungeonParticipantExperienceRuntime();
            uint tinyBonus=0;
            for(int i=0;i<10;i++) tinyBonus+=exp.ApplyGuildBonusRate(1,10);
            check("guild EXP carries fractional points without mixing equipment bonus",tinyBonus==1
                && exp.ApplyGuildBonusRate(100,0)==0 && exp.ApplyEquipmentBonusRate(1,10)==0);
            exp.RecordMonster(1000,100,false,false,false,false,equipmentBonusExperience:50,guildBonusExperience:100);
            check("guild EXP remains separate from base/contract/equipment and clear bases",
                GuildContentRules.ExperienceBonus(1000,10)==100 && exp.MonsterTotalExperience==1250
                && exp.Capture().MonsterGuildBonusExperience==100 && exp.Capture().MonsterBaseExperience==1000);
            CheckPerRunGuildExperienceRead(database, repo, guild, now, check);
            Funds(40000);
            database.Write((c,t)=>
            {
                using var cmd=c.CreateCommand();cmd.Transaction=t;
                // SQLite rejects the INSERT, so the debited points must roll back
                // together with the whole purchase transaction.
                cmd.CommandText="CREATE TRIGGER guild_content_test_failure BEFORE INSERT ON guild_contents BEGIN SELECT RAISE(ABORT,'guild content rollback probe'); END;";
                cmd.ExecuteNonQuery();
            });
            bool failed=false;
            try { repo.PurchaseContent(601,definitions[1],now); }
            catch(DbException) { failed=true; }
            finally
            {
                database.Write((c,t)=>{using var cmd=c.CreateCommand();cmd.Transaction=t;cmd.CommandText="DROP TRIGGER guild_content_test_failure;";cmd.ExecuteNonQuery();});
            }
            check("content insertion failure rolls back the points debit",failed && Balance()==40000 && repo.GetContents(601,now).All(x=>x.Type!=5)
                && database.Read(c=>{using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='guild_content_test_failure';";return Convert.ToInt32(cmd.ExecuteScalar());})==0);
            check("permanent stats survive far future reads and cannot be repurchased",
                repo.PurchaseContent(601,definitions[1],now)==GuildResult.Success
                && repo.PurchaseContent(601,definitions[1],now)==GuildResult.ContentAlreadyActive
                && GuildContentRules.StatBonus(repo.GetContents(602,now+10000000),now+10000000)==60 && Balance()==4000);
            repo.Disband(601,out _);
            check("disband cascades entitlements",database.Read(c=>
            {using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM guild_contents WHERE guild_id=@guild;";cmd.Parameters.AddWithValue("@guild",guild.Id);return Convert.ToInt32(cmd.ExecuteScalar());})==0);
        }

        // 公会经验百分比每 participant 每 DungeonRun 只在首次有效结算时查一次库，
        // 结果冻结在该 participant 自己的经验台账里；离会、到期和本局内购买都
        // 从下一 run 才生效。
        private static void CheckPerRunGuildExperienceRead(
            IGameDatabase database,
            GuildRepository repo,
            GuildRecord guild,
            long now,
            Action<string, bool> check)
        {
            repo.Apply(guild.Id,603); repo.Approve(guild.Id,601,603);
            int reads = 0;
            int ReadPercent() { reads++; return GuildContentRules.ExperiencePercent(repo.GetContents(603, now), now); }
            var firstRun = new DungeonParticipantExperienceRuntime();
            check("no reward or zero base experience never reads the guild bonus",
                firstRun.ResolveGuildMonsterExperiencePercent(false, 100, ReadPercent) == 0
                && firstRun.ResolveGuildMonsterExperiencePercent(true, 0, ReadPercent) == 0
                && !firstRun.GuildMonsterExperiencePercent.HasValue && reads == 0);
            check("first eligible kill freezes the current guild EXP percent",
                firstRun.ResolveGuildMonsterExperiencePercent(true, 100, ReadPercent) == 10
                && reads == 1 && firstRun.GuildMonsterExperiencePercent == 10);
            check("later kills in the same run reuse the frozen percent without a query",
                firstRun.ResolveGuildMonsterExperiencePercent(true, 5000, ReadPercent) == 10
                && firstRun.ResolveGuildMonsterExperiencePercent(true, 1, ReadPercent) == 10 && reads == 1);
            check("a different run or participant reads the database again",
                new DungeonParticipantExperienceRuntime().ResolveGuildMonsterExperiencePercent(true, 100, ReadPercent) == 10
                && reads == 2);
            var concurrentRun = new DungeonParticipantExperienceRuntime();
            var syncRoot = new object();
            int concurrentReads = 0;
            var percents = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                lock (syncRoot)
                    return concurrentRun.ResolveGuildMonsterExperiencePercent(true, 100,
                        () => { concurrentReads++; return 10; });
            }))).GetAwaiter().GetResult();
            check("concurrent first kills read the guild bonus once under the run lock",
                percents.All(p => p == 10) && concurrentReads == 1 && concurrentRun.GuildMonsterExperiencePercent == 10);
            var failingRun = new DungeonParticipantExperienceRuntime();
            bool threw = false;
            try { failingRun.ResolveGuildMonsterExperiencePercent(true, 100, () => throw new InvalidOperationException("read failed")); }
            catch (InvalidOperationException) { threw = true; }
            check("a failed read is not frozen and does not fake a zero bonus",
                threw && !failingRun.GuildMonsterExperiencePercent.HasValue
                && failingRun.ResolveGuildMonsterExperiencePercent(true, 100, () => 30) == 30
                && failingRun.GuildMonsterExperiencePercent == 30);
            var zeroRun = new DungeonParticipantExperienceRuntime();
            check("a zero percent result is frozen instead of re-querying every kill",
                zeroRun.ResolveGuildMonsterExperiencePercent(true, 100, () => 0) == 0
                && zeroRun.ResolveGuildMonsterExperiencePercent(true, 100, () => 30) == 0
                && zeroRun.GuildMonsterExperiencePercent == 0 && zeroRun.ApplyGuildBonusRate(100, 0) == 0);

            var runBeforeDeparture = new DungeonParticipantExperienceRuntime();
            check("departure after the first kill keeps this run's frozen bonus",
                runBeforeDeparture.ResolveGuildMonsterExperiencePercent(true, 100, ReadPercent) == 10
                && reads == 3);
            repo.RemoveMember(603, 603);
            check("leaving the guild only affects the next run",
                runBeforeDeparture.ResolveGuildMonsterExperiencePercent(true, 100, ReadPercent) == 10 && reads == 3
                && new DungeonParticipantExperienceRuntime().ResolveGuildMonsterExperiencePercent(true, 100, ReadPercent) == 0
                && reads == 4);
            repo.Apply(guild.Id,603); repo.Approve(guild.Id,601,603);
            database.Write((c, t) =>
            {
                using var cmd = c.CreateCommand(); cmd.Transaction = t;
                cmd.CommandText = "DELETE FROM guild_contents WHERE guild_id=@guild AND content_type=1;";
                cmd.Parameters.AddWithValue("@guild", guild.Id);
                cmd.ExecuteNonQuery();
            });
            var runBeforePurchase = new DungeonParticipantExperienceRuntime();
            check("an expired or removed entitlement is frozen as zero for the current run",
                runBeforePurchase.ResolveGuildMonsterExperiencePercent(true, 100, ReadPercent) == 0 && reads == 5);
            database.Write((c, t) =>
            {
                using var cmd = c.CreateCommand(); cmd.Transaction = t;
                cmd.CommandText = @"INSERT INTO guild_contents(guild_id,content_type,content_id,content_value,expires_at)
VALUES(@guild,1,3,10,@expiry);";
                cmd.Parameters.AddWithValue("@guild", guild.Id);
                cmd.Parameters.AddWithValue("@expiry", now + 86400);
                cmd.ExecuteNonQuery();
            });
            check("an in-run purchase only applies to the next run",
                runBeforePurchase.ResolveGuildMonsterExperiencePercent(true, 100, ReadPercent) == 0 && reads == 5
                && new DungeonParticipantExperienceRuntime().ResolveGuildMonsterExperiencePercent(true, 100, ReadPercent) == 10
                && reads == 6);
        }
    }
}

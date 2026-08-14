using System;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    // 临时诊断探针: 对指定 DB 的指定角色跑一遍 SP/TP 派生, 打印客户端将看到的数值。
    // 用法: DfoServer.exe --probe-skill-state <dbPath> <characterId>
    internal static class SkillStateProbe
    {
        public static int Run(string[] args)
        {
            var csvIdx = Array.IndexOf(args, "--probe-skill-csv");
            if (csvIdx >= 0)
                return RunCsv(args, csvIdx);

            var idx = Array.IndexOf(args, "--probe-skill-state");
            if (idx < 0 || idx + 2 >= args.Length)
            {
                Console.WriteLine("usage: --probe-skill-state <dbPath> <characterId>");
                return 1;
            }
            var dbPath = args[idx + 1];
            var cid = int.Parse(args[idx + 2]);

            byte job = 0, level = 1, growRaw = 0;
            int bonusSp = 0, bonusTp = 0;
            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT job, level, grow_type, bonus_sp, bonus_tp FROM characters WHERE character_id=@cid";
                    cmd.Parameters.AddWithValue("@cid", cid);
                    using (var rdr = cmd.ExecuteReader())
                    {
                        if (!rdr.Read()) { Console.WriteLine($"character {cid} not found"); return 1; }
                        job = (byte)rdr.GetInt32(0);
                        level = (byte)rdr.GetInt32(1);
                        growRaw = (byte)rdr.GetInt32(2);
                        bonusSp = rdr.GetInt32(3);
                        bonusTp = rdr.GetInt32(4);
                    }
                }
            }

            CharacterStatComputer.DecodeGrowType(growRaw, out var first, out var second);
            Console.WriteLine($"cid={cid} job={job} level={level} grow=0x{growRaw:X2} first={first} second={second} bonusSp={bonusSp} bonusTp={bonusTp}");

            var repo = new SqliteCharacterProgressRepository(dbPath, ServerPaths.SchemaFilePath);
            var skills = repo.LoadSkills(cid);
            Console.WriteLine($"pages={skills.Pages.Count} page0 entries={skills.Pages[0].Entries.Count}" +
                (skills.Pages.Count > 1 ? $" page1 entries={skills.Pages[1].Entries.Count}" : ""));

            var p0 = SkillPointLedger.Compute(job, level, bonusSp, bonusTp, skills, 0, first, second);
            var p1 = SkillPointLedger.Compute(job, level, bonusSp, bonusTp, skills, 1, first, second);
            Console.WriteLine($"page0: totalSp={p0.TotalSp} spentSp={p0.SpentSp} remainSp={p0.RemainingSp} totalTp={p0.TotalTp} spentTp={p0.SpentTp}");
            Console.WriteLine($"page1: spentSp={p1.SpentSp} remainSp={p1.RemainingSp} spentTp={p1.SpentTp}");

            var pts = SkillStateService.ResolvePointState(skills, job, level, bonusSp, bonusTp, first, second);
            Console.WriteLine($"CLIENT VIEW: page0Sp={pts.RemainingSp} page1Sp={pts.RemainingSpPage1} sharedTp={pts.RemainingTp}");

            PrintBreakdown(skills, job, level, first, second);
            return 0;
        }

        // CSV 模式: pageIndex,slot,skillId,level 逐行, 用指定 (job,level,first,second) 跑派生。
        // 用法: --probe-skill-csv <csvPath> <job> <level> <first> <second>
        private static int RunCsv(string[] args, int idx)
        {
            var csvPath = args[idx + 1];
            var job = byte.Parse(args[idx + 2]);
            var level = byte.Parse(args[idx + 3]);
            var first = int.Parse(args[idx + 4]);
            var second = int.Parse(args[idx + 5]);

            var skills = new SkillInfoSnapshot();
            skills.Pages.Add(new SkillInfoPageSnapshot());
            skills.Pages.Add(new SkillInfoPageSnapshot());
            foreach (var line in System.IO.File.ReadAllLines(csvPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                var pi = int.Parse(parts[0]);
                skills.Pages[pi].Entries.Add(new SkillInfoEntrySnapshot
                {
                    Slot = byte.Parse(parts[1]),
                    SkillId = ushort.Parse(parts[2]),
                    Level = byte.Parse(parts[3]),
                });
            }
            Console.WriteLine($"csv job={job} level={level} first={first} second={second} page0={skills.Pages[0].Entries.Count} page1={skills.Pages[1].Entries.Count}");

            var p0 = SkillPointLedger.Compute(job, level, 0, 0, skills, 0, first, second);
            var p1 = SkillPointLedger.Compute(job, level, 0, 0, skills, 1, first, second);
            Console.WriteLine($"page0: totalSp={p0.TotalSp} spentSp={p0.SpentSp} remainSp={p0.RemainingSp} totalTp={p0.TotalTp} spentTp={p0.SpentTp}");
            Console.WriteLine($"page1: spentSp={p1.SpentSp} remainSp={p1.RemainingSp} spentTp={p1.SpentTp}");
            PrintBreakdown(skills, job, level, first, second);
            return 0;
        }

        private static void PrintBreakdown(SkillInfoSnapshot skills, byte job, byte level, int first, int second)
        {
            // 逐技能花费拆账(page0): 单技能快照跑同一派生, 差值即该技能计入的花费。
            Console.WriteLine("--- page0 per-skill spent breakdown ---");
            foreach (var entry in skills.Pages[0].Entries)
            {
                var solo = new SkillInfoSnapshot();
                solo.Pages.Add(new SkillInfoPageSnapshot());
                solo.Pages[0].Entries.Add(entry);
                var one = SkillPointLedger.Compute(job, level, 0, 0, solo, 0, first, second);
                var sd = SkillDataProvider.GetSkill(job, entry.SkillId);
                var name = sd != null ? (sd.IsTpSkill ? "TP" : "SP") : "??";
                if (one.SpentSp > 0 || one.SpentTp > 0 || sd == null)
                    Console.WriteLine($"  skill={entry.SkillId,4} lv={entry.Level,3} slot={entry.Slot,3} {name} spentSp={one.SpentSp,5} spentTp={one.SpentTp,3}" + (sd == null ? " (NO SKILL DATA)" : ""));
            }
        }
    }
}

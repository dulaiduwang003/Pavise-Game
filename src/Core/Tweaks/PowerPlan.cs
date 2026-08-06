// @author bdth 2074055628@qq.com
// 文件用途 创建并托管 Pavise 竞技电源计划 游戏时切入 退出还原
// 关键项一律显式写入不吃基底默认 HWP 与传统两条调频路径同时写满 机器没有的项探测后跳过

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal static class PowerPlan
    {
        public const string PlanTitle = "Pavise 竞技";
        private const string PlanNote = "由 Pavise 创建并托管，游戏结束自动切回原方案";

        private static readonly Guid Ultimate = new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61");
        private static readonly Guid HighPerf = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        private static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        private static readonly Guid PowerSaver = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");

        private const uint AccessScheme = 16;

        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid guid);
        [DllImport("powrprof.dll")] private static extern uint PowerDuplicateScheme(IntPtr root, ref Guid src, ref IntPtr dest);
        [DllImport("powrprof.dll")] private static extern uint PowerDeleteScheme(IntPtr root, ref Guid guid);
        [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(
            IntPtr root, IntPtr scheme, IntPtr subgroup, uint accessFlags, uint index, byte[] buffer, ref uint size);
        [DllImport("powrprof.dll")]
        private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, IntPtr buffer, ref uint size);
        [DllImport("powrprof.dll", EntryPoint = "PowerReadFriendlyName")]
        private static extern uint PowerReadFriendlyNameBuf(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, ref uint size);
        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);
        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteDescription(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

        private static readonly Guid SubNone      = new Guid("fea3413e-7e05-4911-9a71-700331f1c294");
        private static readonly Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        private static readonly Guid SubPcie      = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
        private static readonly Guid SubUsb       = new Guid("2a737441-1930-4402-8d77-b2bebba308a3");
        private static readonly Guid SubDisk      = new Guid("0012ee47-9041-4b5d-9b77-535fba8b1442");
        private static readonly Guid SubWireless  = new Guid("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");

        private static readonly Guid Personality       = new Guid("245d8541-3943-4422-b025-13a784f679b7");
        private static readonly Guid ProcThrottleMin   = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");
        private static readonly Guid ProcThrottleMax   = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");
        private static readonly Guid CpMinCores        = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");
        private static readonly Guid CpMaxCores        = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334028");
        private static readonly Guid PerfBoostMode     = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
        private static readonly Guid Throttling        = new Guid("3b04d4fd-1cc7-4f23-ab1c-d1337819c4bb");
        private static readonly Guid SysCoolPol        = new Guid("94d3a615-a899-4ac5-ae2b-e4d8f634367f");
        private static readonly Guid PcieAspm          = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");
        private static readonly Guid UsbSelSuspend     = new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
        private static readonly Guid DiskIdle          = new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e");

        private static readonly Guid PerfEpp           = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
        private static readonly Guid PerfBoostPol      = new Guid("45bcc044-d885-43e2-8605-ee0ec6e96b59");
        private static readonly Guid PerfIncPol        = new Guid("465e1f50-b610-473a-ab58-00d1077dc418");
        private static readonly Guid PerfDecPol        = new Guid("40fbefc7-2e9d-4d25-a185-0cfd8574bac6");
        private static readonly Guid PerfIncTime       = new Guid("984cf492-3bed-4488-a8f9-4286c97bf5aa");
        private static readonly Guid PerfDecTime       = new Guid("d8edeb9b-95cf-4f95-a73c-b061973693c8");
        private static readonly Guid PerfIncThreshold  = new Guid("06cadf0e-64ed-448a-8927-ce7bf90eb35d");
        private static readonly Guid PerfDecThreshold  = new Guid("12a0ab44-fe28-4fa9-b3bd-4b64f44960a6");
        private static readonly Guid LatencyHintPerf   = new Guid("619b7505-003b-4e82-b7a6-4dd29c300971");
        private static readonly Guid LatencyHintUnpark = new Guid("616cdaa5-695e-4545-97ad-97dc2d1bdd88");
        private static readonly Guid PerfDutyCycling   = new Guid("4e4450b3-6179-4e91-b8f1-5bb9938f81a1");
        private static readonly Guid ProcFreqMax       = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
        private static readonly Guid WirelessPowerSave = new Guid("12bbebe6-58d6-4636-95bb-3217ef867c1a");

        private static readonly Guid ProcThrottleMin1  = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964d");
        private static readonly Guid ProcThrottleMax1  = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ed");
        private static readonly Guid CpMinCores1       = new Guid("0cc5b647-c1df-4637-891a-dec35c318584");
        private static readonly Guid CpMaxCores1       = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334029");
        private static readonly Guid PerfEpp1          = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864");
        private static readonly Guid SchedPolicy       = new Guid("93b8b6dc-0698-4d1c-9ee4-0644e900c85d");
        private static readonly Guid ShortSchedPolicy  = new Guid("bae08b81-2d5e-4688-ad6a-13243356654b");

        private static readonly Guid IdleDisableSet    = new Guid("5d76a2ca-e8c0-402f-a133-2158492d58ad");

        private struct Knob
        {
            public readonly Guid Sub;
            public readonly Guid Setting;
            public readonly uint ArenaAc, ArenaDc, CalmAc, CalmDc;
            public readonly string Label;
            public Knob(Guid sub, Guid setting, uint arenaAc, uint arenaDc, uint calmAc, uint calmDc, string label)
            {
                Sub = sub; Setting = setting;
                ArenaAc = arenaAc; ArenaDc = arenaDc; CalmAc = calmAc; CalmDc = calmDc;
                Label = label;
            }
        }

        private static readonly Knob[] CoreKnobs = new Knob[]
        {
            new Knob(SubProcessor, ProcThrottleMin, 100, 100,  20, 10, "最小处理器状态"),
            new Knob(SubProcessor, ProcThrottleMax, 100, 100, 100, 100, "最大处理器状态"),
            new Knob(SubProcessor, CpMinCores,      100, 100,  50, 20, "核心停放最小核心数"),
            new Knob(SubProcessor, CpMaxCores,      100, 100, 100, 100, "核心停放最大核心数"),
            new Knob(SubProcessor, PerfBoostMode,     2,   2,   2,  3, "睿频模式"),
            new Knob(SubProcessor, Throttling,        0,   0,   2,  2, "允许节流状态"),
            new Knob(SubProcessor, SysCoolPol,        1,   1,   1,  1, "系统散热方式"),
            new Knob(SubPcie,      PcieAspm,          0,   0,   1,  2, "PCIe 链接电源管理"),
            new Knob(SubUsb,       UsbSelSuspend,     0,   0,   0,  1, "USB 选择性暂停"),
            new Knob(SubDisk,      DiskIdle,          0,   0, 1200, 600, "关闭硬盘时间"),
            new Knob(SubNone,      Personality,       1,   1,   1,  1, "电源计划类型"),
        };

        private static readonly Knob[] OptionalKnobs = new Knob[]
        {
            new Knob(SubProcessor, PerfEpp,           0,   0,  50, 70, "能源性能首选项"),
            new Knob(SubProcessor, PerfBoostPol,    100, 100,  60, 40, "睿频策略"),
            new Knob(SubProcessor, PerfIncPol,        2,   2,   1,  1, "升频策略"),
            new Knob(SubProcessor, PerfDecPol,        1,   1,   2,  2, "降频策略"),
            new Knob(SubProcessor, PerfIncTime,       1,   1,   3,  3, "升频时间"),
            new Knob(SubProcessor, PerfDecTime,      10,  10,   5,  5, "降频时间"),
            new Knob(SubProcessor, PerfIncThreshold, 10,  10,  30, 40, "升频阈值"),
            new Knob(SubProcessor, PerfDecThreshold,  8,   8,  20, 30, "降频阈值"),
            new Knob(SubProcessor, LatencyHintPerf, 100, 100,  75, 50, "延迟敏感性能"),
            new Knob(SubProcessor, LatencyHintUnpark,100,100,  50, 50, "延迟敏感解除停放"),
            new Knob(SubProcessor, PerfDutyCycling,   0,   0,   1,  1, "处理器忙闲度"),
            new Knob(SubProcessor, ProcFreqMax,       0,   0,   0,  0, "处理器最大频率"),
            new Knob(SubWireless,  WirelessPowerSave, 0,   0,   1,  2, "无线适配器节能"),
        };

        private static readonly Knob[] HybridKnobs = new Knob[]
        {
            new Knob(SubProcessor, ProcThrottleMin1, 100, 100,  20, 10, "E核最小处理器状态"),
            new Knob(SubProcessor, ProcThrottleMax1, 100, 100, 100, 100, "E核最大处理器状态"),
            new Knob(SubProcessor, CpMinCores1,      100, 100,  50, 20, "E核停放最小核心数"),
            new Knob(SubProcessor, CpMaxCores1,      100, 100, 100, 100, "E核停放最大核心数"),
            new Knob(SubProcessor, PerfEpp1,           0,   0,  50, 70, "E核能源性能首选项"),
            new Knob(SubProcessor, SchedPolicy,        2,   2,   5,  5, "异类线程调度策略"),
            new Knob(SubProcessor, ShortSchedPolicy,   2,   2,   5,  5, "异类短线程调度策略"),
        };

        private static bool TuneTarget(Guid g, bool aggressive, bool idleDisable)
        {
            try
            {
                int written = 0;
                int failed = 0;
                var skipped = new List<string>();

                foreach (Knob k in CoreKnobs)
                {
                    if (WriteKnob(g, k, aggressive)) written++;
                    else { failed++; Logger.Log("电源项「" + k.Label + "」写入失败"); }
                }
                foreach (Knob k in OptionalKnobs)
                {
                    if (!SettingPresent(g, k.Sub, k.Setting)) { skipped.Add(k.Label); continue; }
                    if (WriteKnob(g, k, aggressive)) written++; else failed++;
                }
                if (CpuTopology.Hybrid)
                {
                    foreach (Knob k in HybridKnobs)
                    {
                        if (!SettingPresent(g, k.Sub, k.Setting)) { skipped.Add(k.Label); continue; }
                        if (WriteKnob(g, k, aggressive)) written++; else failed++;
                    }
                }

                bool killIdle = aggressive && idleDisable;
                if (SettingPresent(g, SubProcessor, IdleDisableSet))
                {
                    if (WritePair(g, SubProcessor, IdleDisableSet, killIdle ? 1u : 0u, killIdle ? 1u : 0u)) written++;
                    else failed++;
                }
                else skipped.Add("处理器闲置禁用");

                if (failed > 0)
                {
                    Logger.Log("电源策略有 " + failed + " 项未能写入，未把本轮标记为成功");
                    return false;
                }

                Logger.Log("电源策略：" + (aggressive ? "竞技档" : "常规档")
                    + "（" + PlanTitle + "）写入 " + written + " 项"
                    + (CpuTopology.Hybrid ? "，含大小核专项" : "")
                    + (killIdle ? "，已禁用处理器空闲状态" : "")
                    + (skipped.Count > 0 ? "；本机不支持 " + skipped.Count + " 项：" + string.Join("、", skipped.ToArray()) : ""));
                return true;
            }
            catch { return false; }
        }

        private static bool WriteKnob(Guid scheme, Knob k, bool aggressive)
        {
            return WritePair(scheme, k.Sub, k.Setting,
                aggressive ? k.ArenaAc : k.CalmAc,
                aggressive ? k.ArenaDc : k.CalmDc);
        }

        private static bool SettingPresent(Guid scheme, Guid sub, Guid setting)
        {
            Guid sb = sub, set = setting;
            uint value;
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, out value) == 0;
        }

        private static bool WritePair(Guid scheme, Guid sub, Guid setting, uint ac, uint dc)
        {
            Guid sb = sub, set = setting;
            return PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, ac) == 0
                && PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, dc) == 0;
        }

        private static readonly object lk = new object();
        private static Guid saved;
        private static bool active;
        private static Guid target;
        private static bool resolved;
        private static int tuneState = -1;
        private static bool targetOwned;

        private static Guid? Current()
        {
            IntPtr p;
            if (PowerGetActiveScheme(IntPtr.Zero, out p) != 0 || p == IntPtr.Zero) return null;
            try { return (Guid)Marshal.PtrToStructure(p, typeof(Guid)); }
            catch { return null; }
            finally { LocalFree(p); }
        }

        public static string CurrentPlanLabel()
        {
            Guid? cur = Current();
            if (!cur.HasValue) return "读取失败";
            Guid g = cur.Value;
            if (g == Ultimate) return "卓越性能";
            if (g == HighPerf) return "高性能";
            if (g == Balanced) return "平衡";
            if (g == PowerSaver) return "节能";
            string name = ReadName(g);
            return name.Length > 0 ? name : "自定义计划";
        }

        private static bool Set(Guid g)
        {
            if (PowerSetActiveScheme(IntPtr.Zero, ref g) != 0) return false;
            Guid? actual = Current();
            return actual.HasValue && actual.Value == g;
        }

        private static string PlanLabel(Guid g)
        {
            if (g == HighPerf) return "高性能";
            if (g == Ultimate) return "卓越性能";
            string name = ReadName(g);
            return name.Length > 0 ? name : g.ToString();
        }

        private static bool SchemeUsable(Guid g)
        {
            uint size = 0;
            uint r = PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            return r == 0 || r == 234;
        }

        private static string ReadName(Guid g)
        {
            try
            {
                uint size = 0;
                uint probe = PowerReadFriendlyNameBuf(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref size);
                if ((probe != 0 && probe != 234) || size == 0 || size > 4096) return "";
                byte[] buf = new byte[size];
                if (PowerReadFriendlyNameBuf(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return "";
                return Encoding.Unicode.GetString(buf).TrimEnd('\0');
            }
            catch { return ""; }
        }

        private static bool WriteName(Guid g, string title, string note)
        {
            try
            {
                byte[] nameBuf = Encoding.Unicode.GetBytes(title + "\0");
                Guid s = g;
                if (PowerWriteFriendlyName(IntPtr.Zero, ref s, IntPtr.Zero, IntPtr.Zero, nameBuf, (uint)nameBuf.Length) != 0)
                    return false;
                byte[] noteBuf = Encoding.Unicode.GetBytes(note + "\0");
                s = g;
                PowerWriteDescription(IntPtr.Zero, ref s, IntPtr.Zero, IntPtr.Zero, noteBuf, (uint)noteBuf.Length);
                return true;
            }
            catch { return false; }
        }

        private static List<Guid> EnumerateSchemes()
        {
            var list = new List<Guid>();
            try
            {
                for (uint i = 0; i < 128; i++)
                {
                    uint size = 16;
                    byte[] buf = new byte[16];
                    if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, i, buf, ref size) != 0) break;
                    list.Add(new Guid(buf));
                }
            }
            catch { }
            return list;
        }

        private static Guid FindOwnScheme()
        {
            foreach (Guid g in EnumerateSchemes())
                if (ReadName(g) == PlanTitle) return g;
            return Guid.Empty;
        }

        private static void PurgeStaleClones(Guid keep)
        {
            Guid? cur = Current();
            foreach (Guid g in EnumerateSchemes())
            {
                if (g == keep) continue;
                if (cur.HasValue && g == cur.Value) continue;
                if (ReadName(g) != PlanTitle) continue;
                Guid tmp = g;
                if (PowerDeleteScheme(IntPtr.Zero, ref tmp) == 0)
                    Logger.Log("清理了重复的「" + PlanTitle + "」电源计划 " + g);
            }
        }

        private static void MigrateLegacyPlan() { MigrateLegacyPlan("UltimatePlanGuid"); }

        private static void MigrateLegacyPlan(string key)
        {
            string old = Settings.LoadStr(key, "");
            if (old.Length == 0) return;
            Guid g;
            if (TryGuid(old, out g) && SchemeUsable(g))
            {
                Guid? cur = Current();
                bool isActive = cur.HasValue && cur.Value == g;
                bool isSnapshot = Settings.LoadStr("PrevPowerPlan", "") == old;
                if (!isActive && !isSnapshot)
                {
                    Guid tmp = g;
                    if (PowerDeleteScheme(IntPtr.Zero, ref tmp) == 0)
                        Logger.Log("已删除旧版创建的电源计划副本 " + g);
                }
                else
                {
                    Logger.Log("旧版电源计划副本 " + g + " 正在使用中，暂不删除");
                    return;
                }
            }
            Settings.SaveStr(key, "");
        }

        private static Guid ResolveTarget()
        {
            if (resolved) return target;
            MigrateLegacyPlan();

            Guid prev;
            string s = Settings.LoadStr("ArenaPlanGuid", "");
            if (s.Length > 0 && TryGuid(s, out prev) && SchemeUsable(prev))
            {
                target = prev;
                targetOwned = true;
                if (ReadName(target) != PlanTitle) WriteName(target, PlanTitle, PlanNote);
                PurgeStaleClones(target);
                resolved = true;
                return target;
            }

            Guid found = FindOwnScheme();
            if (found != Guid.Empty)
            {
                target = found;
                targetOwned = true;
                Settings.SaveStr("ArenaPlanGuid", found.ToString());
                Logger.Log("认回已存在的「" + PlanTitle + "」电源计划 " + found);
                PurgeStaleClones(target);
                resolved = true;
                return target;
            }

            Guid created;
            if (Duplicate(Ultimate, out created) || Duplicate(HighPerf, out created))
            {
                target = created;
                targetOwned = true;
                WriteName(created, PlanTitle, PlanNote);
                Settings.SaveStr("ArenaPlanGuid", created.ToString());
                Logger.Log("已创建「" + PlanTitle + "」电源计划 " + created);
                PurgeStaleClones(target);
            }
            else
            {
                target = HighPerf;
                targetOwned = false;
                Logger.Log("无法创建独立电源计划，回退系统高性能");
            }
            resolved = true;
            return target;
        }

        private static bool Duplicate(Guid src, out Guid created)
        {
            created = Guid.Empty;
            IntPtr dest = IntPtr.Zero;
            Guid s = src;
            uint r = PowerDuplicateScheme(IntPtr.Zero, ref s, ref dest);
            if (r != 0 || dest == IntPtr.Zero) return false;
            try { created = (Guid)Marshal.PtrToStructure(dest, typeof(Guid)); }
            catch { return false; }
            finally { LocalFree(dest); }
            return created != Guid.Empty;
        }

        private static bool TryGuid(string s, out Guid g)
        {
            try { g = new Guid(s); return true; }
            catch { g = Guid.Empty; return false; }
        }

        private static int TuneKey(bool aggressive, bool idleDisable)
        {
            return (aggressive ? 1 : 0) | (idleDisable ? 2 : 0);
        }

        private static bool ActivateInner(bool aggressive, bool idleDisable)
        {
            if (active) return true;
            Guid tgt = ResolveTarget();
            if (tuneState != TuneKey(aggressive, idleDisable))
            {
                if (targetOwned && !TuneTarget(tgt, aggressive, idleDisable)) return false;
                tuneState = TuneKey(aggressive, idleDisable);
            }
            Guid? cur = Current();
            if (cur == null) return false;
            if (cur.Value == tgt) { active = true; return true; }
            saved = cur.Value;
            Settings.SaveStr("PrevPowerPlan", saved.ToString());
            if (Settings.LoadStr("PrevPowerPlan", "") != saved.ToString())
            {
                saved = Guid.Empty;
                Logger.Log("电源计划原值快照无法持久化，已取消切换");
                return false;
            }
            if (Set(tgt))
            {
                active = true;
                Logger.Log("电源计划 → " + PlanLabel(tgt) + "（原 " + PlanLabel(saved) + "）");
                return true;
            }
            Settings.SaveStr("PrevPowerPlan", "");
            saved = Guid.Empty;
            Logger.Log("电源计划切换失败，本轮未启用");
            return false;
        }

        public static bool Enforce(bool aggressive, bool idleDisable)
        {
            lock (lk)
            {
                if (!active) return ActivateInner(aggressive, idleDisable);
                Guid tgt = ResolveTarget();
                if (tuneState != TuneKey(aggressive, idleDisable))
                {
                    if (targetOwned && !TuneTarget(tgt, aggressive, idleDisable)) return false;
                    tuneState = TuneKey(aggressive, idleDisable);
                    Set(tgt);
                }
                Guid? cur = Current();
                if (cur != null && cur.Value != tgt)
                {
                    if (Set(tgt)) Logger.Log("电源计划被改动，已强制拉回 " + PlanLabel(tgt));
                    else return false;
                }
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                Guid restoreTarget = saved;
                if (restoreTarget == Guid.Empty)
                {
                    Guid persisted;
                    if (TryGuid(Settings.LoadStr("PrevPowerPlan", ""), out persisted)) restoreTarget = persisted;
                }
                bool ok = true;
                if (restoreTarget != Guid.Empty)
                {
                    if (Set(restoreTarget))
                    {
                        Settings.SaveStr("PrevPowerPlan", "");
                        Logger.Log("电源计划已还原");
                        ok = Settings.LoadStr("PrevPowerPlan", "").Length == 0;
                    }
                    else if (!SchemeUsable(restoreTarget))
                    {
                        Settings.SaveStr("PrevPowerPlan", "");
                        Logger.Log("原电源计划已不存在，无法还原");
                        ok = false;
                    }
                    else
                    {
                        Logger.Log("电源计划还原失败，快照保留待下次启动重试");
                        ok = false;
                    }
                }
                active = false; saved = Guid.Empty; tuneState = -1;
                return ok;
            }
        }

        public static void HealFromCrash()
        {
            string s = Settings.LoadStr("PrevPowerPlan", "");
            if (s.Length == 0) return;
            Guid g;
            if (!TryGuid(s, out g))
            {
                Settings.SaveStr("PrevPowerPlan", "");
                return;
            }
            if (Set(g))
            {
                Settings.SaveStr("PrevPowerPlan", "");
                Logger.Log("检测到上次未还原的电源计划，已恢复");
            }
            else if (!SchemeUsable(g))
            {
                Settings.SaveStr("PrevPowerPlan", "");
                Logger.Log("上次的电源计划已不存在，无法还原");
            }
            else Logger.Log("恢复上次电源计划失败，快照保留待下次重试");
        }

#if PAVISE_SELFTEST
        internal static bool SelfTestDuplicate(out Guid created) { return Duplicate(HighPerf, out created); }

        internal static void SelfTestPurge(Guid keep) { PurgeStaleClones(keep); }

        internal static void SelfTestMigrate(string key) { MigrateLegacyPlan(key); }

        internal static int SelfTestSchemeCount() { return EnumerateSchemes().Count; }

        internal static Guid SelfTestResolve() { return ResolveTarget(); }

        internal static bool SelfTestTargetOwned() { return targetOwned; }

        internal static void SelfTestResetResolve()
        {
            resolved = false;
            target = Guid.Empty;
            targetOwned = false;
            tuneState = -1;
        }

        internal static bool SelfTestDelete(Guid scheme)
        {
            Guid t = scheme;
            return PowerDeleteScheme(IntPtr.Zero, ref t) == 0;
        }

        internal static bool SelfTestTune(Guid scheme, bool aggressive, bool idleDisable)
        {
            return TuneTarget(scheme, aggressive, idleDisable);
        }

        internal static bool SelfTestReadKnob(Guid scheme, string label, out uint ac)
        {
            ac = uint.MaxValue;
            foreach (Knob k in AllKnobs())
            {
                if (k.Label != label) continue;
                Guid sb = k.Sub, set = k.Setting, sc = scheme;
                return PowerReadACValueIndex(IntPtr.Zero, ref sc, ref sb, ref set, out ac) == 0;
            }
            return false;
        }

        internal static bool SelfTestSupports(Guid scheme, string label)
        {
            foreach (Knob k in AllKnobs())
                if (k.Label == label) return SettingPresent(scheme, k.Sub, k.Setting);
            return false;
        }

        internal static string SelfTestName(Guid scheme) { return ReadName(scheme); }

        internal static bool SelfTestWriteName(Guid scheme, string title) { return WriteName(scheme, title, "selftest"); }

        internal static string[] SelfTestKnobPairs()
        {
            var list = new List<string>();
            foreach (Knob k in AllKnobs()) list.Add(k.Sub.ToString() + "|" + k.Setting.ToString());
            return list.ToArray();
        }

        internal static string[] SelfTestKnobLabels()
        {
            var list = new List<string>();
            foreach (Knob k in AllKnobs()) list.Add(k.Label);
            return list.ToArray();
        }

        internal static bool SelfTestArenaDiffersFromCalm(string label)
        {
            foreach (Knob k in AllKnobs())
                if (k.Label == label) return k.ArenaAc != k.CalmAc || k.ArenaDc != k.CalmDc;
            return false;
        }

        private static List<Knob> AllKnobs()
        {
            var all = new List<Knob>();
            all.AddRange(CoreKnobs);
            all.AddRange(OptionalKnobs);
            all.AddRange(HybridKnobs);
            return all;
        }
#endif
    }
}

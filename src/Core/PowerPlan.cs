// @author bdth 2074055628@qq.com
// 文件用途 切换 调整并恢复游戏电源计划

using System;
using System.Runtime.InteropServices;

namespace AegisApp
{
    internal static class PowerPlan
    {
        private static readonly Guid Ultimate = new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61");
        private static readonly Guid HighPerf = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid guid);
        [DllImport("powrprof.dll")] private static extern uint PowerDuplicateScheme(IntPtr root, ref Guid src, ref IntPtr dest);
        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, IntPtr buffer, ref uint size);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);

        private static readonly Guid SubProcessor   = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        private static readonly Guid CpMinCores     = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");
        private static readonly Guid IdleDisable    = new Guid("5d76a2ca-e8c0-402f-a133-2158492d58ad");
        private static readonly Guid ProcThrottleMin = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");
        private static readonly Guid PerfBoostMode  = new Guid("be337238-0d82-4146-a960-4f3749d470c7");
        private static readonly Guid SubPcie        = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
        private static readonly Guid PcieAspm       = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");
        private static readonly Guid SubUsb         = new Guid("2a737441-1930-4402-8d77-b2bebba308a3");
        private static readonly Guid UsbSelSuspend  = new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

        private static bool TuneTarget(Guid g, bool aggressive, bool idleDisable)
        {
            try
            {
                bool ok = true;
                ok &= WritePair(g, SubProcessor, CpMinCores, aggressive ? 100u : 50u, aggressive ? 50u : 20u);
                ok &= WritePair(g, SubProcessor, ProcThrottleMin, aggressive ? 100u : 35u, aggressive ? 60u : 10u);
                ok &= WritePair(g, SubProcessor, PerfBoostMode, aggressive ? 2u : 4u, aggressive ? 4u : 3u);
                ok &= WritePair(g, SubPcie, PcieAspm, aggressive ? 0u : 1u, aggressive ? 1u : 2u);
                ok &= WritePair(g, SubUsb, UsbSelSuspend, 0u, aggressive ? 0u : 1u);
                // 处理器空闲禁用：不让核心进入深度 C-state，省掉 1~15ms 的唤醒延迟，
                // 代价是发热和功耗明显上升。写在 Aegis 自建的方案上，切回用户原方案即自动失效，
                // 所以只在竞技级 + 交流供电时开；电池下一律保持 0，避免续航和热衰减双输。
                ok &= WritePair(g, SubProcessor, IdleDisable, (aggressive && idleDisable) ? 1u : 0u, 0u);
                if (!ok)
                {
                    Logger.Log("电源策略参数未能完整写入，未把本轮标记为成功");
                    return false;
                }
                Logger.Log(aggressive
                    ? "电源策略：竞技级（交流电全核心/100%下限"
                        + (idleDisable ? "/禁用空闲降低唤醒延迟" : "，空闲状态保持系统默认")
                        + "，电池降级以避免不可持续的热衰减）"
                    : "电源策略：常规持续性能（保留降频余量，减少热饱和后的频率回落）");
                return true;
            }
            catch { return false; }
        }

        private static bool WritePair(Guid scheme, Guid sub, Guid setting, uint ac, uint dc)
        {
            Guid sb = sub, set = setting;
            return PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, ac) == 0
                && PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, dc) == 0;
        }

        private static void WriteBoth(Guid scheme, Guid setting, uint v) { WriteBoth2(scheme, SubProcessor, setting, v); }

        private static void WriteBoth2(Guid scheme, Guid sub, Guid setting, uint v)
        {
            Guid sb = sub, set = setting;
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, v);
            PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sb, ref set, v);
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

        private static bool Set(Guid g)
        {
            if (PowerSetActiveScheme(IntPtr.Zero, ref g) != 0) return false;
            Guid? actual = Current();
            return actual.HasValue && actual.Value == g;
        }

        private static string PlanName(Guid g) { return g == HighPerf ? "高性能" : "卓越性能(Ultimate)"; }

        private static bool SchemeUsable(Guid g)
        {
            uint size = 0;
            uint r = PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            return r == 0 || r == 234;
        }

        private static Guid ResolveTarget()
        {
            if (resolved) return target;

            Guid prev;
            string s = Settings.LoadStr("UltimatePlanGuid", "");
            if (s.Length > 0 && TryGuid(s, out prev) && SchemeUsable(prev))
            {
                target = prev;
                targetOwned = true;
            }
            else
            {
                Guid created;
                if (Duplicate(Ultimate, out created))
                {
                    target = created;
                    targetOwned = true;
                    Settings.SaveStr("UltimatePlanGuid", created.ToString());
                    Logger.Log("已创建卓越性能(Ultimate)电源计划 " + created);
                }
                else
                {
                    target = HighPerf;
                    targetOwned = false;
                    Logger.Log("系统不支持卓越性能计划，回退高性能");
                }
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

        private static void ActivateInner(bool aggressive, bool idleDisable)
        {
            if (active) return;
            Guid tgt = ResolveTarget();
            if (targetOwned && !TuneTarget(tgt, aggressive, idleDisable)) return;
            tuneState = TuneKey(aggressive, idleDisable);
            Guid? cur = Current();
            if (cur == null) return;
            if (cur.Value == tgt) { active = true; return; }
            saved = cur.Value;
            Settings.SaveStr("PrevPowerPlan", saved.ToString());
            if (Settings.LoadStr("PrevPowerPlan", "") != saved.ToString())
            {
                saved = Guid.Empty;
                Logger.Log("电源计划原值快照无法持久化，已取消切换");
                return;
            }
            if (Set(tgt))
            {
                active = true;
                Logger.Log("电源计划 → " + PlanName(tgt) + "（原 " + saved + "）");
            }
            else
            {
                Settings.SaveStr("PrevPowerPlan", "");
                saved = Guid.Empty;
                Logger.Log("电源计划切换失败，本轮未启用");
            }
        }

        public static void Enforce(bool aggressive, bool idleDisable)
        {
            lock (lk)
            {
                if (!active) { ActivateInner(aggressive, idleDisable); return; }
                Guid tgt = ResolveTarget();
                if (tuneState != TuneKey(aggressive, idleDisable))
                {
                    if (targetOwned && !TuneTarget(tgt, aggressive, idleDisable)) return;
                    tuneState = TuneKey(aggressive, idleDisable);
                    Set(tgt);
                }
                Guid? cur = Current();
                if (cur != null && cur.Value != tgt)
                {
                    if (Set(tgt)) Logger.Log("电源计划被改动，已强制拉回 " + PlanName(tgt));
                }
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
    }
}

// @author bdth 2074055628@qq.com
// 文件用途 对局时切换电源计划 退出还原
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal sealed class PowerPlanEntry
    {
        public readonly Guid Id;
        public readonly string Name;
        public PowerPlanEntry(Guid id, string name) { Id = id; Name = name; }
    }

    internal static partial class PowerPlan
    {
        // 会话日记键由恢复完成判定共同引用 改名必须两边一起
        internal const string PlanJournalKey = "PrevPowerPlan";
        private const string ChoiceKey = "PowerPlanChoice";
        private const string DefaultPlanKey = "DefaultPlanGuid";
        private const string ManagedPlanKey = "PgPlanGuid";
        public const string ManagedChoice = "managed";
        private const string PlanNote = "由 Pavise 创建并托管 游戏结束自动切回原方案 删掉它 Pavise 会重建";

        private static readonly Guid Ultimate = new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61");
        private static readonly Guid HighPerf = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        private static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        private static readonly Guid PowerSaver = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");

        private static readonly string[] UltimateNames = { "卓越性能", "Ultimate Performance" };

        private static string machineSig;

        public static string MachineSignature()
        {
            if (machineSig != null) return machineSig;
            string seed = "";
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Cryptography"))
                    if (k != null) seed = (k.GetValue("MachineGuid") as string) ?? "";
            }
            catch { }
            if (seed.Length == 0)
                try { seed = Environment.MachineName; } catch { seed = "PAVISE"; }

            unchecked
            {
                uint h = 2166136261;
                foreach (char c in seed) { h ^= c; h *= 16777619; }
                machineSig = (h & 0xFFFF).ToString("X4");
            }
            return machineSig;
        }

        public static string ManagedPlanTitle
        {
            // 方案名是用户在电源选项里看到的 已存在的托管方案按存下的 GUID 认回后会被改成这个名
            get { return Lang.T("t.powerplan.2") + MachineSignature(); }
        }

        private static readonly object lk = new object();
        private static Guid saved;
        private static Guid? settledRestoreTarget;
        private static bool active;
        private static Guid target;
        private static bool resolved;
        private const string IdleDisableClearedKey = "IdleDisableCleared";
        private static bool targetOwned;
        private static int tuneState = -1;

        // 写进方案的那套值有三种 温和 激进 激进但掌机让功耗
        //   掌机那种在 aggressive 上也是真 所以不能再用 0/1 两态记 否则切档时会被当成没变
        private static int TuneCode(bool aggressive, bool handheld, bool extreme)
        {
            return (!aggressive ? 0 : handheld ? 2 : 1) | (extreme ? 4 : 0);
        }

        public static string CurrentPlanLabel()
        {
            Guid? cur = Current();
            if (!cur.HasValue) return Lang.T("t.systemaudithardware.3");
            Guid g = cur.Value;
            if (g == Ultimate) return Lang.T("t.powerplan.1");
            if (g == HighPerf) return Lang.T("t.powerplan.3");
            if (g == Balanced) return Lang.T("t.powerplan.4");
            if (g == PowerSaver) return Lang.T("t.powerplan.5");
            string name = ReadName(g);
            return name.Length > 0 ? name : Lang.T("t.powerplan.6");
        }

        private static string PlanLabel(Guid g)
        {
            if (g == Ultimate) return Lang.T("t.powerplan.1");
            if (g == HighPerf) return Lang.T("t.powerplan.3");
            string name = ReadName(g);
            return name.Length > 0 ? name : g.ToString();
        }

        public static List<PowerPlanEntry> ListUserPlans()
        {
            Guid mine = ManagedPlanGuid();
            var list = new List<PowerPlanEntry>();
            foreach (Guid g in EnumerateSchemes())
            {
                if (mine != Guid.Empty && g == mine) continue;
                string name = ReadName(g);
                list.Add(new PowerPlanEntry(g, name.Length > 0 ? name : g.ToString()));
            }
            return list;
        }

        public static string SelectedPlanId
        {
            get { return Settings.LoadStr(ChoiceKey, ""); }
        }

        public static bool ManagedSelected
        {
            get { return SelectedPlanId == ManagedChoice; }
        }

        public static string EffectivePlanId
        {
            get
            {
                string choice = SelectedPlanId;
                return choice.Length > 0 ? choice : ManagedChoice;
            }
        }

        public static void SelectPlan(string id)
        {
            string value = id ?? "";
            if (value == SelectedPlanId) return;
            Settings.SaveStr(ChoiceKey, value);
            lock (lk)
            {
                resolved = false;
                target = Guid.Empty;
                targetOwned = false;
                tuneState = -1;
            }
            if (value == ManagedChoice)
                Logger.Log(Lang.T("log.powerplan.7") + ManagedPlanTitle + Lang.T("log.powerplan.8"));
            else if (value.Length == 0)
                Logger.Log(Lang.T("log.powerplan.9") + ManagedPlanTitle);
            else
                Logger.Log(Lang.T("log.powerplan.10") + PlanLabelOf(value)
                    + Lang.T("log.powerplan.11"));
        }

        private static Guid ManagedPlanGuid()
        {
            Guid g;
            string id = Settings.LoadStr(ManagedPlanKey, "");
            if (id.Length == 0 || !TryGuid(id, out g)) return Guid.Empty;
            foreach (Guid s in EnumerateSchemes()) if (s == g) return g;
            return Guid.Empty;
        }

        private static Guid EnsureManagedPlan()
        {
            Guid kept = ManagedPlanGuid();
            if (kept != Guid.Empty)
            {
                if (ReadName(kept) != ManagedPlanTitle) WriteName(kept, ManagedPlanTitle, PlanNote);
                return kept;
            }

            string title = ManagedPlanTitle;
            foreach (Guid g in EnumerateSchemes())
                if (ReadName(g) == title)
                {
                    Settings.SaveStr(ManagedPlanKey, g.ToString());
                    Logger.Log(Lang.T("log.powerplan.12") + title);
                    return g;
                }

            Guid created;
            if (!Duplicate(Ultimate, out created) && !Duplicate(HighPerf, out created))
            {
                Logger.Log(Lang.T("log.powerplan.13"));
                return Guid.Empty;
            }
            Settings.SaveStr(ManagedPlanKey, created.ToString());
            WriteName(created, title, PlanNote);
            Guid? cur = Current();
            if (cur.HasValue) SyncDisplayFeel(cur.Value, created);
            Logger.Log(Lang.T("log.powerplan.14") + title);
            return created;
        }

        public static bool RemoveManagedPlan()
        {
            lock (lk)
            {
                if (!RestoreCpuIdle() || CpuIdleHasResidue) return false;
                return RemoveManagedPlanCore();
            }
        }

        private static bool RemoveManagedPlanCore()
        {
            string id = Settings.LoadStr(ManagedPlanKey, "");
            if (id.Length == 0) return true;
            Guid g;
            if (!TryGuid(id, out g)) { Settings.SaveStr(ManagedPlanKey, ""); return true; }
            Guid? cur = Current();
            if (cur.HasValue && cur.Value == g && !SwitchAwayFrom(g)) return false;
            Guid tmp = g;
            // 方案读取失败不等于已删除 当成删了会丢掉还要恢复的参数收据
            uint deleted = PowerDeleteScheme(IntPtr.Zero, ref tmp);
            if (deleted != 0 && deleted != 2 /* ERROR_FILE_NOT_FOUND */) return false;
            // 已经删掉的托管方案没有可恢复参数了 只清它自己的收据 别挡着下次重建
            if (!ForgetDeletedExtremeSnapshot(g)) return false;
            Settings.SaveStr(ManagedPlanKey, "");
            lock (lk) { resolved = false; target = Guid.Empty; targetOwned = false; tuneState = -1; }
            Logger.Log(Lang.T("log.powerplan.15") + ManagedPlanTitle);
            return true;
        }

        private static bool SwitchAwayFrom(Guid avoid)
        {
            Guid prev;
            if (TryGuid(Settings.LoadStr(PlanJournalKey, ""), out prev)
                && prev != avoid && SchemeUsable(prev) && Set(prev)) return true;
            if (SchemeUsable(Balanced) && Set(Balanced)) return true;
            foreach (Guid s in EnumerateSchemes())
                if (s != avoid && Set(s)) return true;
            return false;
        }

        private static string PlanLabelOf(string id)
        {
            Guid g;
            return TryGuid(id, out g) ? PlanLabel(g) : id;
        }

        public static Guid EnsureDefaultPlan()
        {
            List<Guid> schemes = EnumerateSchemes();
            foreach (Guid g in schemes) if (g == Ultimate) return Ultimate;

            Guid kept;
            string keptId = Settings.LoadStr(DefaultPlanKey, "");
            if (keptId.Length > 0)
            {
                if (TryGuid(keptId, out kept))
                    foreach (Guid g in schemes) if (g == kept) return kept;
                Settings.SaveStr(DefaultPlanKey, "");
            }

            foreach (Guid g in schemes)
            {
                string name = ReadName(g);
                foreach (string known in UltimateNames)
                    if (string.Equals(name, known, StringComparison.OrdinalIgnoreCase))
                        return g;
            }

            Guid created;
            if (Duplicate(Ultimate, out created))
            {
                Settings.SaveStr(DefaultPlanKey, created.ToString());
                Guid? cur = Current();
                if (cur.HasValue) SyncDisplayFeel(cur.Value, created);
                Logger.Log(Lang.T("log.powerplan.16") + PlanLabel(created)
                    + Lang.T("log.powerplan.17"));
                return created;
            }

            foreach (Guid g in schemes) if (g == HighPerf) return HighPerf;
            Logger.Log(Lang.T("log.powerplan.18"));
            return Guid.Empty;
        }

        public static string CreatedPlanId
        {
            get { return Settings.LoadStr(DefaultPlanKey, ""); }
        }

        public static bool RemoveCreatedPlan()
        {
            lock (lk)
            {
                if (!RestoreCpuIdle() || CpuIdleHasResidue) return false;
                return RemoveCreatedPlanCore();
            }
        }

        private static bool RemoveCreatedPlanCore()
        {
            string id = Settings.LoadStr(DefaultPlanKey, "");
            if (id.Length == 0) return true;
            Guid g;
            if (!TryGuid(id, out g)) { Settings.SaveStr(DefaultPlanKey, ""); return true; }
            Guid? cur = Current();
            if (cur.HasValue && cur.Value == g && !SwitchAwayFrom(g)) return false;
            Guid tmp = g;
            if (SchemeUsable(g) && PowerDeleteScheme(IntPtr.Zero, ref tmp) != 0) return false;
            Settings.SaveStr(DefaultPlanKey, "");
            lock (lk) { resolved = false; target = Guid.Empty; }
            Logger.Log(Lang.T("log.powerplan.19"));
            return true;
        }

        private static bool TryUserChosenTarget()
        {
            string choice = Settings.LoadStr(ChoiceKey, "");
            if (choice.Length == 0) return false;
            if (choice == ManagedChoice)
            {
                Guid managed = EnsureManagedPlan();
                if (managed == Guid.Empty) return false;
                target = managed;
                targetOwned = true;
                return true;
            }
            Guid picked;
            if (!TryGuid(choice, out picked) || !SchemeUsable(picked))
            {
                Logger.Log(Lang.T("log.powerplan.22") + ManagedPlanTitle);
                Settings.SaveStr(ChoiceKey, "");
                return false;
            }
            target = picked;
            targetOwned = false;
            Logger.Log(Lang.T("log.powerplan.23") + PlanLabel(picked) + Lang.T("log.powerplan.24"));
            return true;
        }

        private static Guid ResolveTarget()
        {
            if (resolved) return target;
            if (!TryUserChosenTarget())
            {
                Guid managed = EnsureManagedPlan();
                if (managed != Guid.Empty)
                {
                    target = managed;
                    targetOwned = true;
                }
                else
                {
                    target = EnsureDefaultPlan();
                    targetOwned = false;
                }
            }
            resolved = true;
            return target;
        }

        private static bool TryGuid(string s, out Guid g)
        {
            try { g = new Guid(s); return true; }
            catch { g = Guid.Empty; return false; }
        }

        private static bool ActivateInner(bool aggressive, bool handheld, bool extreme)
        {
            if (active) return true;
            string pending;
            if (settledRestoreTarget.HasValue || saved != Guid.Empty || !Settings.TryLoadStr(PlanJournalKey, out pending)
                || pending == null || pending.Length != 0)
            {
                // Set 失败也可能已经把方案切过去了 重试前先把它的
                // 原始值定下来 再取新快照
                if (!RestorePlanCore(false)) return false;
            }
            Guid tgt;
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (ActivateTargetPlanForTest == null) return false;
            tgt = ActivateTargetPlanForTest();
#else
            tgt = ResolveTarget();
#endif
            if (tgt == Guid.Empty) return false;
            if (targetOwned && tuneState != TuneCode(aggressive, handheld, extreme))
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                return false; // Activation tests must not tune native schemes.
#else
                if (!TuneTarget(tgt, aggressive, handheld, extreme)) return false;
                // 空闲策略那组没配完就不记账 下次配置再补 其余旋钮已经写进去了
                tuneState = extremeTunePending ? -1 : TuneCode(aggressive, handheld, extreme);
#endif
            }
            Guid? cur = RestoreCurrentPlan();
            if (!cur.HasValue || cur.Value == Guid.Empty) return false;
            if (cur.Value == tgt) { active = true; return true; }
            saved = cur.Value;
            if (targetOwned)
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                saved = Guid.Empty;
                return false;
#else
                SyncDisplayFeel(cur.Value, tgt);
#endif
            }
            Settings.SaveStr(PlanJournalKey, saved.ToString());
            if (Settings.LoadStr(PlanJournalKey, "") != saved.ToString())
            {
                saved = Guid.Empty;
                Logger.Log(Lang.T("log.powerplan.25"));
                return false;
            }
            bool switched = RestoreSetPlan(tgt);
            Guid? applied = switched ? RestoreCurrentPlan() : null;
            if (switched && applied.HasValue && applied.Value == tgt)
            {
                active = true;
                Logger.Log(Lang.T("log.powerplan.26") + RestorePlanLabel(tgt) + Lang.T("log.gpupowermax.7") + RestorePlanLabel(saved) + " ");
                return true;
            }
            // Set 在原生调用之后会核实当前方案 所以 false 也可能意味着
            // 已经改了但没法核实
            // 两份原始值都留着 给带所有权判断的恢复路径用
            Logger.Log(Lang.T("log.powerplan.27"));
            return false;
        }

        public static bool Enforce(bool aggressive, bool handheld, bool extreme)
        {
            lock (lk)
            {
                if (!active) return ActivateInner(aggressive, handheld, extreme);
                Guid tgt = ResolveTarget();
                if (tgt == Guid.Empty) return false;
                if (targetOwned && tuneState != TuneCode(aggressive, handheld, extreme))
                {
                    if (!TuneTarget(tgt, aggressive, handheld, extreme)) return false;
                    tuneState = extremeTunePending ? -1 : TuneCode(aggressive, handheld, extreme);
                    Set(tgt);
                }
                Guid? cur = Current();
                if (cur != null && cur.Value != tgt)
                {
                    if (Set(tgt)) Logger.Log(Lang.T("log.powerplan.28") + PlanLabel(tgt));
                    else return false;
                }
                return true;
            }
        }

        private static bool? IsOurActivePlan(Guid g)
        {
            if (resolved && g == target) return true;
            bool unknown = false;
            foreach (string key in new[] { ManagedPlanKey, ChoiceKey, "ArenaPlanGuid", "UltimatePlanGuid" })
            {
                string value;
                if (!Settings.TryLoadStr(key, out value) || value == null) { unknown = true; continue; }
                if (value.Length == 0 || (key == ChoiceKey && value == ManagedChoice)) continue;
                Guid owned;
                if (!TryGuid(value, out owned) || owned == Guid.Empty) { unknown = true; continue; }
                // Current 已经核实过这个 GUID 存在 再枚举一遍全部方案
                // 可能失败 反而把一个有效的归属者错误丢弃
                if (g == owned) return true;
            }
            return unknown ? (bool?)null : false;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool idleOk = RestoreCpuIdle();
                bool planOk = RestorePlanCore();
                return idleOk && planOk;
            }
        }

        private static bool RestorePlanCore()
        {
            return RestorePlanCore(true);
        }

        private static bool RestorePlanCore(bool healOrphanManaged)
        {
            try { return RestorePlanCoreChecked(healOrphanManaged); }
            catch { return false; }
        }

        private static bool RestorePlanCoreChecked(bool healOrphanManaged)
        {
            lock (lk)
            {
                if (settledRestoreTarget.HasValue && settledRestoreTarget.Value == saved)
                    return ClearRestoredPlan(saved);
                Guid restoreTarget = saved;
                if (restoreTarget == Guid.Empty)
                {
                    string persisted;
                    if (!Settings.TryLoadStr(PlanJournalKey, out persisted) || persisted == null) return false;
                    if (persisted.Length > 0 && (!TryGuid(persisted, out restoreTarget) || restoreTarget == Guid.Empty))
                        return ClearRestoredPlan(restoreTarget);
                }
                if (restoreTarget == Guid.Empty)
                {
                    if (healOrphanManaged && !RestoreOrphanManagedPlan()) return false;
                    active = false; tuneState = -1;
                    return true;
                }

                // 恢复流程和正常关闭一样 需要当前所有权
                // 当前方案读不到 不等于可以随便切它
                Guid? nowActive = RestoreCurrentPlan();
                if (!nowActive.HasValue || nowActive.Value == Guid.Empty) return false;
                if (nowActive.Value == restoreTarget) return ClearRestoredPlan(restoreTarget);
                // 正常对局里只要这次会话真的接管过方案 恢复责任就还在 Pavise
                // 哪怕当前方案被 TS G-Helper 或者手动切成了别的 GUID 也一样
                // 不然对方赶在退出前切一次 旧的尊重外部选择那条分支就会吞掉原值快照 退局停在第三方方案上
                // 崩溃重启后 active=false 照样用保守的所有权判断
                // 不拿一份历史收据去盖用户在 Pavise 没运行那段时间做的新选择
                bool? ours = active ? (bool?)true : RestorePlanIsOwned(nowActive.Value);
                if (!ours.HasValue) return false;
                if (!ours.Value)
                {
                    Logger.Log(Lang.T("log.powerplan.36") + RestorePlanLabel(nowActive.Value));
                    return ClearRestoredPlan(restoreTarget);
                }
                Guid? checkedActive = RestoreCurrentPlan();
                if (!checkedActive.HasValue || checkedActive.Value != nowActive.Value) return false;
                if (!RestoreSetPlan(restoreTarget))
                {
                    bool? usable = RestorePlanIsUsable(restoreTarget);
                    if (usable.HasValue && !usable.Value)
                    {
                        ClearRestoredPlan(restoreTarget);
                        Logger.Warn(Lang.T("log.powerplan.30"));
                    }
                    else Logger.Log(Lang.T("log.powerplan.31"));
                    return false;
                }
                Guid? restored = RestoreCurrentPlan();
                if (!restored.HasValue || restored.Value != restoreTarget) return false;
                Logger.Log(Lang.T("log.powerplan.29"));
                return ClearRestoredPlan(restoreTarget);
            }
        }

        private static bool ClearRestoredPlan(Guid restoreTarget)
        {
            // 原生还原已经完成 或者是有意放弃
            // 清理核实通过之前 保留一个绑定收据的内存墓碑
            // 重试不能推翻用户后来的选择 哪怕他选的就是我们的方案
            active = false; tuneState = -1;
            saved = restoreTarget;
            settledRestoreTarget = restoreTarget;
            string actual;
            if (!Settings.SaveStr(PlanJournalKey, "") || !Settings.TryLoadStr(PlanJournalKey, out actual)
                || actual != "") return false;
            saved = Guid.Empty;
            settledRestoreTarget = null;
            return true;
        }

        private static Guid? RestoreCurrentPlan()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            return RestoreCurrentPlanForTest == null ? null : RestoreCurrentPlanForTest();
#else
            return Current();
#endif
        }

        private static bool RestoreSetPlan(Guid scheme)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            return RestoreSetPlanForTest != null && RestoreSetPlanForTest(scheme);
#else
            return Set(scheme);
#endif
        }

        private static bool? RestorePlanIsOwned(Guid scheme)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            return RestorePlanIsOwnedForTest == null ? null : RestorePlanIsOwnedForTest(scheme);
#else
            return IsOurActivePlan(scheme);
#endif
        }

        private static bool? RestorePlanIsUsable(Guid scheme)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            return RestorePlanIsUsableForTest == null ? null : RestorePlanIsUsableForTest(scheme);
#else
            // 读不到友好名 不能证明这套方案已经被删
            // 要完整枚举成功之后才允许丢弃它
            for (uint index = 0; index < 128; index++)
            {
                uint size = 16;
                byte[] buffer = new byte[16];
                uint result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    AccessScheme, index, buffer, ref size);
                if (result == 259) return false; // ERROR_NO_MORE_ITEMS
                if (result != 0 || size != 16) return null;
                if (new Guid(buffer) == scheme) return true;
            }
            return null;
#endif
        }

        private static bool RestoreOrphanManagedPlan()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            return RestoreOrphanManagedPlanForTest != null && RestoreOrphanManagedPlanForTest();
#else
            return HealManagedResidue();
#endif
        }

        private static string RestorePlanLabel(Guid scheme)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            return scheme.ToString("D");
#else
            return PlanLabel(scheme);
#endif
        }

        private static bool HealManagedResidue()
        {
            Guid managed;
            Guid? cur = Current();
            if (!cur.HasValue || !TryGuid(Settings.LoadStr(ManagedPlanKey, ""), out managed)
                || cur.Value != managed) return true;
            if (!SwitchAwayFrom(managed)) return false;
            Logger.Log(Lang.T("log.powerplan.32"));
            return true;
        }

        public static bool HasResidue
        {
            get { return settledRestoreTarget.HasValue || CpuIdleHasResidue || Settings.LoadStr(PlanJournalKey, "").Length > 0; }
        }

        public static void HealFromCrash()
        {
            lock (lk)
            {
                RestoreCpuIdle();
                HealPlanFromCrashCore();
            }
        }

        private static void HealPlanFromCrashCore()
        {
            RestorePlanCore(false);
        }

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal static Func<Guid?> RestoreCurrentPlanForTest;
        internal static Func<Guid, bool> RestoreSetPlanForTest;
        internal static Func<Guid, bool?> RestorePlanIsOwnedForTest;
        internal static Func<Guid, bool?> RestorePlanIsUsableForTest;
        internal static Func<bool> RestoreOrphanManagedPlanForTest;
        internal static Func<Guid> ActivateTargetPlanForTest;

        internal static bool RestorePlanForTest(bool fromCrash) { return RestorePlanCore(!fromCrash); }
        internal static bool? ClassifyRestorePlanForTest(Guid scheme) { return IsOurActivePlan(scheme); }
        internal static bool ActivatePlanForTest() { lock (lk) return ActivateInner(false, false, false); }

        internal static void ResetPlanRestoreForTest()
        {
            lock (lk)
            {
                active = false; saved = Guid.Empty; target = Guid.Empty;
                settledRestoreTarget = null;
                resolved = false; targetOwned = false; tuneState = -1;
                RestoreCurrentPlanForTest = null;
                RestoreSetPlanForTest = null;
                RestorePlanIsOwnedForTest = null;
                RestorePlanIsUsableForTest = null;
                RestoreOrphanManagedPlanForTest = null;
                ActivateTargetPlanForTest = null;
            }
        }
#endif

#if PAVISE_SELFTEST
        internal static Guid BalancedGuid { get { return Balanced; } }

        internal static Guid UltimateGuid { get { return Ultimate; } }

        internal static Guid SelfTestResolve() { return ResolveTarget(); }

        internal static void SelfTestResetResolve()
        {
            resolved = false;
            target = Guid.Empty;
        }
#endif
    }
}

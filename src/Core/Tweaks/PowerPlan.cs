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
        private const string ChoiceKey = "PowerPlanChoice";
        private const string DefaultPlanKey = "DefaultPlanGuid";
        private const string ManagedPlanKey = "PgPlanGuid";
        public const string LegacyPlanTitle = "Pavise 竞技";
        public const string ManagedChoice = "managed";
        public const string PlanTag = "PG";
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
            get { return PlanTag + Lang.T("t.powerplan.2") + MachineSignature(); }
        }

        private static readonly object lk = new object();
        private static Guid saved;
        private static bool active;
        private static Guid target;
        private static bool resolved;
        private static bool targetOwned;
        private static int tuneState = -1;

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
            string id = Settings.LoadStr(ManagedPlanKey, "");
            if (id.Length == 0) return true;
            Guid g;
            if (!TryGuid(id, out g)) { Settings.SaveStr(ManagedPlanKey, ""); return true; }
            Guid? cur = Current();
            if (cur.HasValue && cur.Value == g && !SwitchAwayFrom(g)) return false;
            Guid tmp = g;
            if (SchemeUsable(g) && PowerDeleteScheme(IntPtr.Zero, ref tmp) != 0) return false;
            Settings.SaveStr(ManagedPlanKey, "");
            lock (lk) { resolved = false; target = Guid.Empty; targetOwned = false; tuneState = -1; }
            Logger.Log(Lang.T("log.powerplan.15") + ManagedPlanTitle);
            return true;
        }

        private static bool SwitchAwayFrom(Guid avoid)
        {
            Guid prev;
            if (TryGuid(Settings.LoadStr("PrevPowerPlan", ""), out prev)
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

        public static bool HasLegacyManagedResidue()
        {
            if (Settings.LoadStr("ArenaPlanGuid", "").Length > 0) return true;
            if (Settings.LoadStr("UltimatePlanGuid", "").Length > 0) return true;
            foreach (Guid g in EnumerateSchemes())
                if (ReadName(g) == LegacyPlanTitle) return true;
            return false;
        }

        public static bool PurgeLegacyManaged()
        {
            var doomed = new List<Guid>();
            foreach (Guid g in EnumerateSchemes())
                if (ReadName(g) == LegacyPlanTitle) doomed.Add(g);

            Guid? cur = Current();
            if (cur.HasValue && doomed.Contains(cur.Value))
            {
                Guid escape = EnsureDefaultPlan();
                if (escape == Guid.Empty) escape = Balanced;
                if (!Set(escape)) return false;
                Logger.Log(Lang.T("log.powerplan.20") + PlanLabel(escape));
            }

            bool ok = true;
            foreach (Guid g in doomed)
            {
                Guid tmp = g;
                if (PowerDeleteScheme(IntPtr.Zero, ref tmp) == 0)
                    Logger.Log(Lang.T("log.powerplan.21") + g);
                else ok = false;
            }
            if (!ok) return false;
            Settings.SaveStr("ArenaPlanGuid", "");
            Settings.SaveStr("UltimatePlanGuid", "");
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

        private static bool ActivateInner(bool aggressive)
        {
            if (active) return true;
            Guid tgt = ResolveTarget();
            if (tgt == Guid.Empty) return false;
            if (targetOwned && tuneState != (aggressive ? 1 : 0))
            {
                if (!TuneTarget(tgt, aggressive)) return false;
                tuneState = aggressive ? 1 : 0;
            }
            Guid? cur = Current();
            if (cur == null) return false;
            if (cur.Value == tgt) { active = true; return true; }
            saved = cur.Value;
            if (targetOwned) SyncDisplayFeel(cur.Value, tgt);
            Settings.SaveStr("PrevPowerPlan", saved.ToString());
            if (Settings.LoadStr("PrevPowerPlan", "") != saved.ToString())
            {
                saved = Guid.Empty;
                Logger.Log(Lang.T("log.powerplan.25"));
                return false;
            }
            if (Set(tgt))
            {
                active = true;
                Logger.Log(Lang.T("log.powerplan.26") + PlanLabel(tgt) + Lang.T("log.gpupowermax.7") + PlanLabel(saved) + " ");
                return true;
            }
            Settings.SaveStr("PrevPowerPlan", "");
            saved = Guid.Empty;
            Logger.Log(Lang.T("log.powerplan.27"));
            return false;
        }

        public static bool Enforce(bool aggressive)
        {
            lock (lk)
            {
                if (!active) return ActivateInner(aggressive);
                Guid tgt = ResolveTarget();
                if (tgt == Guid.Empty) return false;
                if (targetOwned && tuneState != (aggressive ? 1 : 0))
                {
                    if (!TuneTarget(tgt, aggressive)) return false;
                    tuneState = aggressive ? 1 : 0;
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

        // 所有权判定只查找不创建 覆盖跨进程自愈场景(resolved=false)
        private static bool IsOurActivePlan(Guid g)
        {
            if (resolved && g == target) return true;
            if (g != Guid.Empty && g == ManagedPlanGuid()) return true;
            string choice = Settings.LoadStr(ChoiceKey, "");
            Guid picked;
            if (choice.Length > 0 && choice != ManagedChoice
                && TryGuid(choice, out picked) && g == picked) return true;
            Guid legacy;
            if (TryGuid(Settings.LoadStr("ArenaPlanGuid", ""), out legacy) && g == legacy) return true;
            if (TryGuid(Settings.LoadStr("UltimatePlanGuid", ""), out legacy) && g == legacy) return true;
            return false;
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
                    // 当前活动方案既不是我们切过去的目标也不是待还原原方案 = 用户中途手动换过方案
                    // 所有权已转移 不抢回 只清记账(照 ReversibleReg 的所有权哲学)
                    Guid? nowActive = Current();
                    if (nowActive.HasValue && nowActive.Value != restoreTarget
                        && !IsOurActivePlan(nowActive.Value))
                    {
                        Settings.SaveStr("PrevPowerPlan", "");
                        Logger.Log(Lang.T("log.powerplan.36") + PlanLabel(nowActive.Value));
                        active = false; saved = Guid.Empty; tuneState = -1;
                        return Settings.LoadStr("PrevPowerPlan", "").Length == 0;
                    }
                    if (nowActive.HasValue && nowActive.Value == restoreTarget)
                    {
                        Settings.SaveStr("PrevPowerPlan", "");
                        Logger.Log(Lang.T("log.powerplan.29"));
                        active = false; saved = Guid.Empty; tuneState = -1;
                        return Settings.LoadStr("PrevPowerPlan", "").Length == 0;
                    }
                    if (Set(restoreTarget))
                    {
                        Settings.SaveStr("PrevPowerPlan", "");
                        Logger.Log(Lang.T("log.powerplan.29"));
                        ok = Settings.LoadStr("PrevPowerPlan", "").Length == 0;
                    }
                    else if (!SchemeUsable(restoreTarget))
                    {
                        Settings.SaveStr("PrevPowerPlan", "");
                        Logger.Log(Lang.T("log.powerplan.30"));
                        ok = false;
                    }
                    else
                    {
                        Logger.Log(Lang.T("log.powerplan.31"));
                        ok = false;
                    }
                }
                else ok = HealManagedResidue();
                active = false; saved = Guid.Empty; tuneState = -1;
                return ok;
            }
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

        public static void HealFromCrash()
        {
            string s = Settings.LoadStr("PrevPowerPlan", "");
            if (s.Length == 0) { HealManagedResidue(); return; }
            Guid g;
            if (!TryGuid(s, out g))
            {
                Settings.SaveStr("PrevPowerPlan", "");
                return;
            }
            if (Set(g))
            {
                Settings.SaveStr("PrevPowerPlan", "");
                Logger.Log(Lang.T("log.powerplan.33"));
            }
            else if (!SchemeUsable(g))
            {
                Settings.SaveStr("PrevPowerPlan", "");
                Logger.Log(Lang.T("log.powerplan.34"));
            }
            else Logger.Log(Lang.T("log.powerplan.35"));
        }

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

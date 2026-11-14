using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {
        private static int intactMismatchCount;
        private static string intactMismatchHead = "";

        public static bool Restore()
        {
            lock (lk)
            {
                if (!RestoreLaboratoryBeforeCoreChange()) return false;
                if (!RecoverPresetCreation(ManagedPlans)) return false;
                bool restoreLegacyCpuIdleSucceeded = RestoreLegacyCpuIdle();
                restoreLegacyCpuIdleSucceeded &= RestoreOwnedIdlePolicy();
                bool restorePlanCoreSucceeded = RestorePlanCore();
                if (restoreLegacyCpuIdleSucceeded && restorePlanCoreSucceeded)
                {
                    managedCreationBlocked = false;
                }
                return restoreLegacyCpuIdleSucceeded && restorePlanCoreSucceeded;
            }
        }

        internal static string AutomaticPolicySignature(bool aggressive)
        {
            lock (lk)
            {
                string selected = SelectedPlanId;
            // 空偏好也按托管计划计算身份，避免显式选择后重置同一配置的失败预算。
                if (selected.Length == 0) selected = ManagedChoice;
                if (selected != "managed") return selected;
                if (UsesLegacyManagedPolicy())
                    return selected + ":" + Legacy3110Source + ":" + Legacy3110Fingerprint(aggressive);
                ManagedPowerPresetSelection selection = SelectManagedPreset(aggressive);
                return selection.Preset == null ? selected + ":OEM/base:" + selection.Reason
                    : selected + ":" + selection.Preset.Id + ":" + PresetFingerprint(selection);
            }
        }

        private static bool RestorePlanCore()
        {
            return RestorePlanCore(true);
        }

        private static bool RestorePlanCore(bool healOrphanManaged)
        {
            try
            {
                return RestorePlanCoreChecked(healOrphanManaged);
            }
            catch
            {
                return false;
            }
        }

        private static bool RestorePlanCoreChecked(bool healOrphanManaged)
        {
            lock (lk)
            {
                if (settledRestoreTarget.HasValue && settledRestoreTarget.Value == saved)
                {
                    return ClearRestoredPlan(saved);
                }
                Guid g = saved;
                if (g == Guid.Empty)
                {
                    string value;
                    if (!Settings.TryLoadStr("PrevPowerPlan", out value) || value == null)
                    {
                        return false;
                    }
                    if (value.Length > 0 && (!TryGuid(value, out g) || g == Guid.Empty))
                    {
                        return ClearRestoredPlan(g);
                    }
                }
                if (g == Guid.Empty)
                {
                    if (healOrphanManaged && !NoSwitchSelected && !RestoreOrphanManagedPlan())
                    {
                        return false;
                    }
                    active = false;
                    tuneState = -1;
                    lastActivatedPlan = Guid.Empty;
                    return true;
                }
                Guid? guid = RestoreCurrentPlan();
                if (!guid.HasValue || guid.Value == Guid.Empty)
                {
                    return false;
                }
                if (guid.Value == g)
                {
                    return ClearRestoredPlan(g);
                }
                // 活跃会话不代表当前计划仍归本方；外部已切到其他 GUID 时只释放返回回执。
                // 冷启动没有内存中的激活身份，仍按原有持久回执与托管计划识别恢复范围。
                bool? nullable = lastActivatedPlan != Guid.Empty
                    ? new bool?(guid.Value == lastActivatedPlan) : RestorePlanIsOwned(guid.Value);
                if (!nullable.HasValue)
                {
                    return false;
                }
                if (!nullable.Value)
                {
                    Logger.Log(Lang.T("log.powerplan.36", 0) + RestorePlanLabel(guid.Value));
                    return ClearRestoredPlan(g);
                }
                Guid? guid2 = RestoreCurrentPlan();
                if (!guid2.HasValue || guid2.Value != guid.Value)
                {
                    return false;
                }
                if (!RestoreSetPlan(g))
                {
                    bool? restorePlanIsUsableResult = RestorePlanIsUsable(g);
                    if (restorePlanIsUsableResult.HasValue && !restorePlanIsUsableResult.Value)
                    {
                        ClearRestoredPlan(g);
                        Logger.Warn(Lang.T("log.powerplan.30", 0));
                    }
                    else
                    {
                        Logger.Log(Lang.T("log.powerplan.31", 0));
                    }
                    return false;
                }
                Guid? guid3 = RestoreCurrentPlan();
                if (!guid3.HasValue || guid3.Value != g)
                {
                    return false;
                }
                Logger.Log(Lang.T("log.powerplan.29", 0));
                return ClearRestoredPlan(g);
            }
        }

        // 守护写回不设累计次数上限：外部每改一次就写回一次。前三次逐条记录，之后每分钟最多一条并带本局累计，
        // 与外部程序长时间拉锯时不刷屏。首次接管、写入失败和退局恢复仍走各自的有限预算。
        private static void NoteExternalDriftCorrection(Guid currentPlan, Guid desiredPlan, bool planDrift, bool parameterDrift, string detail)
        {
            externalDriftCorrections++;
            System.Threading.Interlocked.Increment(ref externalDriftCorrectionTotal);
            externalDriftCorrectionPending = true;
            externalDriftDetail = detail;
            long now = DateTime.UtcNow.Ticks;
            if (externalDriftCorrections > 3 && now >= externalDriftLogTicks && now - externalDriftLogTicks < TimeSpan.TicksPerMinute) return;
            externalDriftLogTicks = now;
            string reason = planDrift && parameterDrift ? PerformancePolicy.Text("活动计划及参数", "active plan and parameters")
                : planDrift ? PerformancePolicy.Text("活动计划", "active plan")
                : parameterDrift ? PerformancePolicy.Text("参数", "parameters") : PerformancePolicy.Text("上次纠偏未完成", "previous correction incomplete");
            Logger.Warn(PerformancePolicy.Text("检测到外部电源改动，守护正在写回", "External power changes detected; the guard is writing them back")
                + PerformancePolicy.Text(" 当前GUID=", " current-guid=") + currentPlan
                + PerformancePolicy.Text(" 目标GUID=", " target-guid=") + desiredPlan
                + PerformancePolicy.Text(" 原因=", " reason=") + reason
                + PerformancePolicy.Text(" 本局累计=", " session-total=") + externalDriftCorrections + " detail=" + detail);
        }

        // 活动计划仍是本方计划且只有数值被改时，原地写回再重新应用同一计划，不新建计划也不切换计划。
        // 写入被拒、结构性不符或写回后仍不符时返回 false，由调用方改走重建。
        private static bool RepairManagedValues(Guid scheme, bool aggressive)
        {
            IManagedPowerPlans plans = ManagedPlans;
            var repair = plans as IManagedPowerActiveRepair;
            if (repair == null) return false;
            try
            {
                Guid? current = plans.Current();
                if (!current.HasValue || current.Value != scheme) return false;
                if (!ManagedPresetCheck(scheme, aggressive, repair)) return false;
                // 写入的是存储值，重新应用同一计划后才生效。
                if (!plans.Activate(scheme) || plans.Current() != scheme) return false;
                return ManagedPresetIntact(scheme, aggressive);
            }
            catch { return false; }
        }

        internal static void BeginAutomaticSession(long session)
        {
            lock (lk)
            {
                if (session <= externalDriftSession) return;
                externalDriftSession = session;
                externalDriftCorrections = 0;
                externalDriftCorrectionPending = false;
                externalDriftLogTicks = 0;
                externalDriftDetail = "";
            }
        }

        private static bool NoteIntactMismatch(string detail)
        {
            // 前四项写明细，其余只计数，日志能看出外部改了一项还是一整组。
            if (intactMismatch.Length == 0) intactMismatchCount = 0;
            intactMismatchCount++;
            if (intactMismatchCount <= 4) intactMismatchHead = intactMismatchCount == 1 ? detail : intactMismatchHead + " | " + detail;
            intactMismatch = intactMismatchCount <= 4 ? intactMismatchHead : intactMismatchHead + " | +" + (intactMismatchCount - 4) + " more";
            return false;
        }

        private static bool ClearRestoredPlan(Guid restoreTarget)
        {
            active = false;
            tuneState = -1;
            saved = restoreTarget;
            settledRestoreTarget = restoreTarget;
            string value;
            if (!Settings.SaveStr("PrevPowerPlan", "") || !Settings.TryLoadStr("PrevPowerPlan", out value) || value != "")
            {
                return false;
            }
            saved = Guid.Empty;
            settledRestoreTarget = null;
            lastActivatedPlan = Guid.Empty;
            return true;
        }

        private static Guid? RestoreCurrentPlan()
        {
            return Current();
        }

        private static bool RestoreSetPlan(Guid scheme)
        {
            return Set(scheme);
        }

        private static bool? RestorePlanIsOwned(Guid scheme)
        {
            return IsOurActivePlan(scheme);
        }

        private static bool? RestorePlanIsUsable(Guid scheme)
        {
            uint processedCount = 0u;
            while (processedCount < 128)
            {
                uint size = 16u;
                byte[] rawBytes = new byte[16];
                switch (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16u, processedCount, rawBytes, ref size))
                {
                case 259u:
                    return false;
                case 0u:
                    if (size == 16)
                    {
                        if (new Guid(rawBytes) == scheme)
                        {
                            return true;
                        }
                        processedCount++;
                        break;
                    }
                    goto default;
                default:
                    return null;
                }
            }
            return null;
        }

        private static bool RestoreOrphanManagedPlan()
        {
            return HealManagedResidue();
        }

        private static string RestorePlanLabel(Guid scheme)
        {
            return PlanLabel(scheme);
        }

        private static bool HealManagedResidue()
        {
            Guid? guid = Current();
            Guid g;
            if (!guid.HasValue || !TryGuid(Settings.LoadStr("PgPlanGuid", ""), out g) || guid.Value != g)
            {
                return true;
            }
            if (!SwitchAwayFrom(g))
            {
                return false;
            }
            Logger.Log(Lang.T("log.powerplan.32", 0));
            return true;
        }

        public static void HealFromCrash()
        {
            Settings.Remove("PowerPlanSourceV1");
            Settings.Remove("GmDisableCpuIdle");
            Settings.Remove("GmDisableCpuIdleV2");
            lock (lk)
            {
                if (!RestoreLaboratoryBeforeCoreChange() || !RecoverPresetCreation(ManagedPlans)) return;
                RestoreLegacyCpuIdle();
                RestoreOwnedIdlePolicy();
                HealPlanFromCrashCore();
            }
        }

        private static void HealPlanFromCrashCore()
        {
            RestorePlanCore(false);
        }

    }
}

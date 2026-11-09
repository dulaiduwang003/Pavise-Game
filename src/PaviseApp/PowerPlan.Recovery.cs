using System;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {

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
                bool? nullable = (active ? new bool?(true) : RestorePlanIsOwned(guid.Value));
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

        // 自动关闭后持久保存并读回，避免重启后再次触发同一电源计划冲突。
        private static bool SelectNoSwitchAfterExternalDrift()
        {
            const string choice = "v2:off";
            if (!Settings.SaveStrDurable("PowerPlanChoice", choice) || Settings.LoadStr("PowerPlanChoice", "") != choice) return false;
            managedCreationBlocked = false;
            resolved = false;
            target = Guid.Empty;
            targetOwned = false;
            tuneState = -1;
            Logger.Log(Lang.T("log.powerplan.off", 0));
            return true;
        }

        internal static void BeginAutomaticSession(long session)
        {
            lock (lk)
            {
                if (session <= externalDriftSession) return;
                externalDriftSession = session;
                externalDriftRebuilds = 0;
                externalDriftAccepted = Guid.Empty;
                externalDriftStopped = false;
            }
        }

        private static bool NoteIntactMismatch(string detail)
        {
            if (intactMismatch.Length == 0) intactMismatch = detail;
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

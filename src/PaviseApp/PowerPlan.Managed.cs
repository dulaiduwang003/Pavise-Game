using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {

        private sealed class ManagedPolicyRecord
        {
            internal Guid Plan;

            internal Guid Base;

            internal Guid Previous;

            internal bool CurrentTemplate;
            internal string Source = "", Fingerprint = "";

            internal string Encode()
            {
                // 不同构建间切换时，保持 3.1.1.0 回执封装可读。
                if (Source == Legacy3110Source)
                    return "2223|" + Plan.ToString("D") + "|" + Base.ToString("D") + "|" + Previous.ToString("D");
                return "presets2|" + Plan.ToString("D") + "|" + Base.ToString("D") + "|" + Previous.ToString("D") + "|" + Source + "|" + Fingerprint;
            }
        }

        private sealed class NativeManagedPowerPlans : IManagedPowerPresetPlans, IManagedPowerReadEvidence, IManagedPowerLegacy3110Plans, IManagedPowerLegacy3110WriteEvidence, IManagedPowerDeviceKind, IManagedPowerSchemeEvidence
        {
            public LaboratoryEppDeviceKind DeviceKind { get { return LaboratoryEppEligibility.DeviceKind; } }
            public Guid? Current()
            {
                return PowerPlan.Current();
            }

            public bool Exists(Guid scheme)
            {
                return SchemeUsable(scheme);
            }

            public ManagedPowerSchemeEvidence SchemeEvidence(Guid scheme)
            { return ReadManagedSchemeEvidenceNative(scheme); }

            public bool IsOwned(Guid scheme)
            {
                if (ManagedPolicyOwns(scheme))
                {
                    return true;
                }
                string[] textEntries = new string[1] { "PgPlanGuid" };
                foreach (string name in textEntries)
                {
                    Guid result;
                    if (Guid.TryParse(Settings.LoadStr(name, ""), out result) && result == scheme)
                    {
                        return true;
                    }
                }
                return ManagedPowerPlanPolicy.HasManagedName(ReadName(scheme), MachineSignature());
            }

            public bool CreatePreset(ManagedPowerPreset preset, Guid destination)
            { return CreatePresetNative(preset, destination); }

            public bool CreateLegacy3110(Guid destination)
            { return CreateLegacy3110Native(destination); }

            public uint WriteLegacy3110(Guid scheme, Guid sub, Guid setting, bool ac, uint value)
            {
                if (HandheldBypass) return 5;
                Guid? current = PowerPlan.Current();
                if (!current.HasValue || current == Guid.Empty || current == scheme || !IsOwned(scheme)) return 5;
                return ac ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value)
                    : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
            }

            public Guid[] Subgroups(Guid scheme) { return ReadPresetSubgroups(scheme); }
            public bool CopyBaseSetting(Guid source, Guid target, Guid sub, Guid setting, bool ac) { return CopyPresetBaseNative(source, target, sub, setting, ac); }

            public bool Duplicate(Guid source, out Guid created)
            {
                return PowerPlan.Duplicate(source, out created);
            }

            public bool Activate(Guid scheme)
            {
                return Set(scheme);
            }

            public bool Delete(Guid scheme)
            {
                Guid guid = scheme;
                uint powerDeleteSchemeResult = PowerDeleteScheme(IntPtr.Zero, ref guid);
                if (powerDeleteSchemeResult != 0)
                {
                    return powerDeleteSchemeResult == 2;
                }
                return true;
            }

            public bool Rename(Guid scheme, string title, string note)
            {
                if (WriteName(scheme, title, note))
                {
                    return ReadName(scheme) == title;
                }
                return false;
            }

            public string Name(Guid scheme)
            {
                if (scheme != Guid.Empty)
                {
                    return ReadName(scheme);
                }
                return "";
            }

            public Guid[] ProcessorSettings(Guid scheme, Guid processor)
            {
                Guid[] stored = ReadPresetStoredSettings(scheme, processor);
                if (stored == null) return null;
                List<Guid> list = new List<Guid>(stored);
                var enumerated = new HashSet<Guid>();
                uint processedCount = 0u;
                while (processedCount < 2048)
                {
                    uint size = 16u;
                    byte[] rawBytes = new byte[16];
                    switch (EnumerateProcessorSetting(IntPtr.Zero, ref scheme, ref processor, 18u, processedCount, rawBytes, ref size))
                    {
                    case 2u: case 3u: case 259u:
                        return list.ToArray();
                    case 0u:
                        if (size == 16)
                        {
                            Guid guid = new Guid(rawBytes);
                            if (guid == Guid.Empty || !enumerated.Add(guid))
                            {
                                return null;
                            }
                            if (!list.Contains(guid)) list.Add(guid);
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

            public uint? Read(Guid scheme, Guid sub, Guid setting, bool ac)
            {
                uint value;
                if (!(ac ? ReadAc(scheme, sub, setting, out value) : ReadDc(scheme, sub, setting, out value)))
                {
                    return null;
                }
                return value;
            }

            public ManagedPowerReadEvidence ReadEvidence(Guid scheme, Guid sub, Guid setting, bool ac)
            { return ReadPresetPowerEvidence(scheme, sub, setting, ac); }

            public bool Write(Guid scheme, Guid sub, Guid setting, bool ac, uint value)
            {
                if (!ac)
                {
                    return WriteDc(scheme, sub, setting, value);
                }
                return WriteAc(scheme, sub, setting, value);
            }
        }

        private static bool ReadManagedPolicy(out ManagedPolicyRecord record)
        {
            record = null;
            string value;
            if (!Settings.TryLoadStr("ManagedPlanPolicyV3", out value) || value == null)
            {
                return false;
            }
            if (value.Length == 0)
            {
                return true;
            }
            string[] valueParts = value.Split('|');
            Guid result;
            Guid result2;
            Guid result3;
            bool modern = valueParts.Length == 6 && valueParts[0] == "presets2";
            bool legacy = valueParts.Length == 4 && (valueParts[0] == "3" || valueParts[0] == "2223" || valueParts[0] == "preset1");
            bool legacy3110 = modern && valueParts[4] == Legacy3110Source
                && valueParts[5].StartsWith("legacy3110:", StringComparison.Ordinal);
            bool legacy3110Envelope = legacy && valueParts[0] == "2223";
            ManagedPowerPreset storedPreset = modern && !legacy3110 ? ManagedPowerPreset.FromPolicyRecord(valueParts[4], valueParts[5]) : null;
            if ((!modern && !legacy) || !Guid.TryParse(valueParts[1], out result) || result == Guid.Empty
                || !Guid.TryParse(valueParts[2], out result2) || result2 == Guid.Empty || result2 == result
                || !Guid.TryParse(valueParts[3], out result3) || result3 == result2 || result3 == result
                || IsBuiltInPlan(result) || IsBuiltInPlan(result3)
                || modern && !legacy3110 && storedPreset == null) return false;
            record = new ManagedPolicyRecord {
                Plan = result, Base = result2, Previous = result3, CurrentTemplate = modern || legacy3110Envelope,
                Source = legacy3110 || legacy3110Envelope ? Legacy3110Source : modern ? storedPreset.Id : "", Fingerprint = modern ? valueParts[5] : ""
            };
            return true;
        }

        private static Guid EnsureManagedPolicyPlan()
        {
            if (HandheldBypass) return Guid.Empty;
            if (!RestoreLaboratoryBeforeCoreChange())
                return ManagedPlanFailed("laboratory-recovery", "Laboratory power recovery pending");
            if (!RestoreLegacyCpuIdle() || LegacyCpuIdleHasResidue || !RestoreOwnedIdlePolicy())
                return ManagedPlanFailed("idle-policy-recovery", "Legacy processor idle-policy recovery pending");
            if (managedCreationBlocked)
                return ManagedPlanFailed("creation-blocked", "Managed creation remains blocked after failure");
            IManagedPowerPlans plans = ManagedPlans;
            if (!RecoverPresetCreation(plans))
                return ManagedPlanFailed("creation-recovery", "Previous creation recovery pending");
            ManagedPolicyRecord old;
            if (!ReadManagedPolicy(out old))
                return ManagedPlanFailed("ownership-record-read", "Managed ownership record unreadable or invalid");
            Guid? current = plans.Current();
            if (HandheldBypass) return Guid.Empty;
            if (!current.HasValue || current == Guid.Empty)
                return ManagedPlanFailed("active-plan-read", "Active power-plan query failed");
            Guid previous; Guid.TryParse(Settings.LoadStr(PlanJournalKey, ""), out previous);
            if (saved != Guid.Empty) previous = saved;
            if (previous == Guid.Empty && old != null && plans.IsOwned(current.Value)) previous = old.Base;
            Guid baseline = ManagedPowerPlanPolicy.ChooseBase(current, previous, Balanced, plans);
            if (saved != Guid.Empty && plans.Exists(saved) && !plans.IsOwned(saved)) baseline = saved;
            if (baseline == Guid.Empty)
                return ManagedPlanFailed("baseline-resolution", "Usable local baseline unavailable");
            if (HandheldBypass) return Guid.Empty;
            if (plans.IsOwned(current.Value) && (!plans.Activate(baseline) || plans.Current() != baseline))
                return ManagedPlanFailed("baseline-activation", "Baseline activation or readback failed");
            if (pendingManagedCleanup != Guid.Empty)
            {
                if (!DeleteInactiveManaged(plans, pendingManagedCleanup, true))
                    return ManagedPlanFailed("creation-cleanup", "Uncommitted managed-plan cleanup pending");
                pendingManagedCleanup = Guid.Empty;
            }
            if (old != null && old.Previous != Guid.Empty && !DeleteInactiveManaged(plans, old.Previous))
                return ManagedPlanFailed("previous-managed-cleanup", "Previous managed-plan cleanup pending");
            if (HandheldBypass) return Guid.Empty;
            bool legacy = UsesLegacyManagedPolicy();
            var legacyCreator = plans as IManagedPowerLegacy3110Plans;
            var presetCreator = plans as IManagedPowerPresetPlans;
            ManagedPowerPresetSelection selection = legacy ? null : SelectManagedPreset(requestedAggressive);
            if (!legacy && selection.Preset == null)
            {
                // 桌面 CPU 或平台证据不受支持时，保留实际 OEM 或用户电源计划。
                Logger.Info("电源 来源=OEM或基准 原因=" + selection.Reason);
                return baseline;
            }
            if (legacy ? legacyCreator == null : presetCreator == null || selection == null || selection.Preset == null)
                return ManagedPlanFailed("creation-interface", "Selected managed device-policy creation interface unavailable");
            Guid created = Guid.NewGuid();
            string intent = "1|" + created.ToString("D") + "|" + baseline.ToString("D");
            if (!Settings.SaveStrDurable(PresetCreationKey, intent) || Settings.LoadStr(PresetCreationKey, "") != intent)
                return ManagedPlanFailed("creation-intent-save", "Creation receipt persistence or readback failed");
            if (HandheldBypass) return Guid.Empty;
            bool createdOk = legacy ? legacyCreator.CreateLegacy3110(created) : presetCreator.CreatePreset(selection.Preset, created);
            if (HandheldBypass) return Guid.Empty;
            if (!createdOk || !plans.Exists(created))
            {
                PowerApplyFailed(legacy ? "legacy3110-creation" : "preset-creation", "Selected template copy failed or created plan unavailable");
                AbandonManagedCreation(plans, created); RecoverPresetCreation(plans); return Guid.Empty;
            }
            Guid oldPlan = old != null ? old.Plan : Guid.Empty;
            if (oldPlan == Guid.Empty) Guid.TryParse(Settings.LoadStr(ManagedPlanKey, ""), out oldPlan);
            if (oldPlan != Guid.Empty)
            {
                ManagedPowerSchemeEvidence oldState = ReadManagedSchemeEvidence(plans, oldPlan);
                if (!ManagedSchemeEvidenceAvailable(oldState, oldPlan))
                {
                    AbandonManagedCreation(plans, created); return Guid.Empty;
                }
                if (oldPlan == baseline || IsBuiltInPlan(oldPlan) || oldState.State == ManagedPowerSchemeState.ConfirmedMissing
                    || (old == null || old.Plan != oldPlan)
                        && !ManagedPowerPlanPolicy.HasManagedName(plans.Name(oldPlan), MachineSignature())) oldPlan = Guid.Empty;
            }
            var record = new ManagedPolicyRecord { Plan = created, Base = baseline, Previous = oldPlan,
                CurrentTemplate = true,
                Source = legacy ? Legacy3110Source : selection.Preset.Id,
                Fingerprint = legacy ? Legacy3110Fingerprint(requestedAggressive) : PresetFingerprint(selection) };
            string encoded = record.Encode();
            if (!plans.Rename(created, ManagedPlanTitle, PlanNote))
            {
                PowerApplyFailed("managed-name-save", "Managed plan name write or readback failed");
                AbandonManagedCreation(plans, created); return Guid.Empty;
            }
            if (!Settings.SaveStrDurable(ManagedPolicyKey, encoded) || Settings.LoadStr(ManagedPolicyKey, "") != encoded)
            {
                PowerApplyFailed("ownership-record-save", "Managed ownership persistence or readback failed");
                AbandonManagedCreation(plans, created); return Guid.Empty;
            }
            string owned = created.ToString("D");
            if (!Settings.SaveStrDurable(ManagedPlanKey, owned) || Settings.LoadStr(ManagedPlanKey, "") != owned)
            {
                PowerApplyFailed("managed-guid-save", "Managed plan identity persistence or readback failed");
                AbandonManagedCreation(plans, created); return Guid.Empty;
            }
            if (!Settings.SaveStrDurable(PresetCreationKey, "") || Settings.LoadStr(PresetCreationKey, "!") != "")
                return ManagedPlanFailed("creation-intent-clear", "Completed creation receipt persistence or readback failed");
            tuneState = -1;
            return created;
        }

        private static bool IsBuiltInPlan(Guid plan)
        {
            if (plan != Guid.Empty)
            {
                if (plan != Balanced && plan != HighPerf && plan != Ultimate)
                {
                    return plan == PowerSaver;
                }
                return true;
            }
            return false;
        }

        private static bool DeleteInactiveManaged(IManagedPowerPlans plans, Guid plan, bool createdHere = false)
        {
            Guid? active = plans.Current();
            if (plan == Guid.Empty || IsBuiltInPlan(plan) || !active.HasValue || active == Guid.Empty || active == plan)
            {
                return false;
            }
            ManagedPowerSchemeEvidence presence = ReadManagedSchemeEvidence(plans, plan);
            if (!ManagedSchemeEvidenceAvailable(presence, plan)) return false;
            if (presence.State == ManagedPowerSchemeState.Present && !createdHere
                && !ManagedPowerPlanPolicy.HasManagedName(plans.Name(plan), MachineSignature()))
            {
                return false;
            }
            string value;
            string value2;
            if (!Settings.TryLoadStr("PrevPowerPlan", out value) || !Settings.TryLoadStr("ExtremePowerKnobSnap", out value2))
            {
                return false;
            }
            Guid result;
            if ((Guid.TryParse(value, out result) && result == plan) || !string.IsNullOrEmpty(value2))
            {
                return false;
            }
            if (presence.State == ManagedPowerSchemeState.ConfirmedMissing) return true;
            if (plans.Delete(plan))
            {
                presence = ReadManagedSchemeEvidence(plans, plan);
                return ManagedSchemeEvidenceAvailable(presence, plan)
                    && presence.State == ManagedPowerSchemeState.ConfirmedMissing;
            }
            return false;
        }

        private static void AbandonManagedCreation(IManagedPowerPlans plans, Guid created)
        {
            BlockManagedCreation();
            if (!DeleteInactiveManaged(plans, created, true))
            {
                pendingManagedCleanup = created;
            }
        }

        private static void BlockManagedCreation()
        {
            managedCreationBlocked = true;
        }

        private static bool ManagedPolicyOwns(Guid scheme)
        {
            ManagedPolicyRecord record;
            if (ReadManagedPolicy(out record) && record != null)
            {
                if (record.Plan != scheme)
                {
                    return record.Previous == scheme;
                }
                return true;
            }
            return false;
        }

        private static Guid ManagedReturnPlan(Guid current, Guid managed)
        {
            if (!ManagedPlans.IsOwned(current))
            {
                return current;
            }
            ManagedPolicyRecord record;
            if (!ReadManagedPolicy(out record) || record == null || record.Plan != managed || !ManagedPlans.Exists(record.Base) || ManagedPlans.IsOwned(record.Base))
            {
                return Guid.Empty;
            }
            return record.Base;
        }

        private static bool PrepareManagedScheme(Guid scheme, bool aggressive)
        {
            if (HandheldBypass) return false;
            if (!RestoreLaboratoryBeforeCoreChange())
                return PowerApplyFailed("laboratory-recovery", "Laboratory power recovery pending");
            ManagedPolicyRecord record;
            if (!ReadManagedPolicy(out record) || record == null || record.Plan != scheme || record.Base != saved)
            {
                return PowerApplyFailed("ownership-record-validation", "Managed ownership target or baseline mismatch");
            }
            IManagedPowerPlans managedPlans = ManagedPlans;
            Guid? guid = managedPlans.Current();
            if (HandheldBypass) return false;
            if (!guid.HasValue)
            {
                return PowerApplyFailed("active-plan-read", "Active power-plan query failed");
            }
            Guid? guid2 = guid;
            if (guid2.HasValue && guid2.GetValueOrDefault() == scheme && (saved == Guid.Empty || !managedPlans.Activate(saved) || managedPlans.Current() != saved))
            {
                return PowerApplyFailed("baseline-activation", "Baseline activation or readback failed");
            }
            if (!RestoreLegacyCpuIdle() || LegacyCpuIdleHasResidue || !RestoreOwnedIdlePolicy())
            {
                return PowerApplyFailed("idle-policy-recovery", "Legacy processor idle-policy recovery pending");
            }
            // 只通过已选设备路径调节本方持有且尚未激活的候选计划。
            if (HandheldBypass) return false;
            bool legacy = UsesLegacyManagedPolicy();
            if (!record.CurrentTemplate || (record.Source == Legacy3110Source) != legacy || !TuneTarget(scheme, aggressive, legacy))
                return PowerApplyFailed("managed-parameters", "Managed source or local adaptation verification failed");
            if (legacy && !SyncLegacy3110DisplayFeel(record.Base, scheme))
                return PowerApplyFailed("managed-display-preferences", "3.1.1.0 display preference copy or verification failed");
            // 激活前重新核对设备策略；旧路径下准备的候选仍保留所有权。
            if (UsesLegacyManagedPolicy() != legacy)
                return PowerApplyFailed("device-policy-changed", "Device policy changed while preparing the inactive candidate");
            tuneState = TuneCode(aggressive);
            return true;
        }

        private static bool RetuneManagedScheme(Guid scheme, bool aggressive)
        {
            bool prepared = PrepareManagedScheme(scheme, aggressive);
            if (HandheldBypass) return RestoreHandheldApply();
            if (!prepared || !ManagedPlans.Activate(scheme) || ManagedPlans.Current() != scheme)
            {
                active = false;
                tuneState = -1;
                return false;
            }
            CleanupPreviousPresetPlan();
            return true;
        }

        internal static bool IsManagedPlanName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            string[] textEntries = new string[5] { "PG ", "由软件调度", "Scheduled by Pavise", "Aegis", "Pavise" };
            foreach (string value in textEntries)
            {
                if (name.StartsWith(value, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        internal static void SweepUntrackedManagedPlansOnce()
        {
            if (System.Threading.Interlocked.Exchange(ref orphanSweepDone, 1) != 0) return;
            lock (lk)
            {
                if (HandheldBypass || active || LaboratoryHasResidue || IdlePolicyHasResidue || LegacyCpuIdleHasResidue) return;
                // 未完成的创建记录表示所有权尚未稳定，此时不做改动。
                if (Settings.LoadStr(PresetCreationKey, "").Length > 0) return;
                ManagedPolicyRecord record;
                if (!ReadManagedPolicy(out record)) return;
                var keep = new HashSet<Guid> { pendingManagedCleanup };
                if (record != null)
                {
                    keep.Add(record.Plan);
                    keep.Add(record.Previous);
                    keep.Add(record.Base);
                }
                foreach (string key in new[] { ManagedPlanKey, DefaultPlanKey, PlanJournalKey })
                {
                    Guid tracked;
                    if (Guid.TryParse(Settings.LoadStr(key, ""), out tracked)) keep.Add(tracked);
                }
                List<Guid> schemes;
                if (!TryEnumerateSchemes(out schemes)) return;
                IManagedPowerPlans plans = ManagedPlans;
                string signature = MachineSignature();
                int deleted = 0, failed = 0;
                foreach (Guid scheme in schemes)
                {
                    if (keep.Contains(scheme) || !ManagedPowerPlanPolicy.HasManagedName(ReadName(scheme), signature)) continue;
                    if (DeleteInactiveManaged(plans, scheme)) deleted++;
                    else failed++;
                }
                if (deleted > 0 || failed > 0)
                    Logger.Info("电源 事件=遗留清理 已删除=" + deleted + " 失败项=" + failed);
            }
        }

        internal static int DeleteManagedPlansByName()
        {
            int processedCount = 0;
            lock (lk)
            {
                if (HandheldBypass) return 0;
                if (!RestoreLaboratoryBeforeCoreChange()) return 0;
                Guid? guid = Current();
                foreach (Guid item in EnumerateSchemes())
                {
                    if (!IsManagedPlanName(ReadName(item)))
                    {
                        continue;
                    }
                    if (guid.HasValue && guid.Value == item)
                    {
                        if (!SwitchAwayFrom(item))
                        {
                            continue;
                        }
                        guid = Current();
                    }
                    Guid guid2 = item;
                    if (PowerDeleteScheme(IntPtr.Zero, ref guid2) == 0)
                    {
                        processedCount++;
                    }
                }
                return processedCount;
            }
        }

    }
}

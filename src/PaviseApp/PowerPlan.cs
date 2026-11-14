using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {

        private const string ParkSnapKey = "CoreParkSnap";

        private const string ParkTriesKey = "CoreParkTries";

        private const string ParkAbsent = "-";

        private const int MaxRestoreTries = 3;

        internal const string PlanJournalKey = "PrevPowerPlan";

        private const string ChoiceKey = "PowerPlanChoice";

        private const string ChoicePrefix = "v2:";

        private const string DefaultPlanKey = "DefaultPlanGuid";

        private const string ManagedPlanKey = "PgPlanGuid";

        public const string ManagedChoice = "managed";

        public const string NoSwitchChoice = "off";

        private const string PlanNote = "Pavise动态计划";

        private const string IdleDisableClearedKey = "IdleDisableCleared";

        internal const string ExtremeSnapKey = "ExtremePowerKnobSnap";

        internal const string LegacyCpuIdleLedgerKey = "CpuIdleStateV1";

        private const string ManagedPolicyKey = "ManagedPlanPolicyV3";

        private const uint AccessScheme = 16u;

        private const uint ErrorInvalidParameter = 87u;

        private static readonly Guid Ultimate = new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61");

        private static readonly Guid HighPerf = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

        private static readonly Guid Balanced = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");

        private static readonly Guid PowerSaver = new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");

        private static readonly string[] UltimateNames = new string[2] { "卓越性能", "Ultimate Performance" };

        private static string machineSig;

        private static readonly object lk = new object();

        private static Guid saved;

        private static Guid? settledRestoreTarget;

        private static bool active;

        private static Guid target;

        private static bool resolved;

        private static bool targetOwned;


        private static int tuneState = -1;

        // 外部改动由守护持续写回，不设累计次数上限；本局计数只用于日志，总计数供守护线程判断是否正在拉锯。

        private static int externalDriftCorrections;
        private static long externalDriftCorrectionTotal;
        private static long externalDriftSession = -1;
        private static long externalDriftLogTicks;
        private static bool externalDriftCorrectionPending;
        private static string externalDriftDetail = "";
        private static Guid lastActivatedPlan;
        private static string intactMismatch = "";

        // 进程生命周期内只增不减，读取不进电源锁，守护线程不会被一次进行中的应用挡住。
        internal static long ExternalDriftCorrectionTotal
        {
            get { return System.Threading.Interlocked.Read(ref externalDriftCorrectionTotal); }
        }

        // 这里只管理退役版本遗留回执的恢复状态，不能重新启用旧调节策略。
        private static bool extremeTunePending;

        private static readonly Guid IdlePromote = new Guid("7b224883-b3cc-4d79-819f-8374152cbe7c");

        private static readonly Guid IdleDemote = new Guid("4b92d758-5a24-4851-a470-815d78aee119");

        private static readonly Guid IdleScaling = new Guid("6c2993b0-8f48-481f-bcc6-00dd2742aa06");

        private static readonly Guid RetiredIdleCheck = new Guid("c4581c31-89ab-4597-8e2b-9c9cab440e6b");

        private static CpuIdleReceipt cpuIdleReceipt;

        private static bool managedCreationBlocked;

        private static Guid pendingManagedCleanup;

        private static readonly Guid SubVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");

        private static readonly Guid VideoBrightness = new Guid("aded5e82-b909-4619-9949-f5d71dac0bcb");

        private static readonly Guid VideoDimBrightness = new Guid("f1fbfde2-a960-4165-9f88-50667911ce96");

        private static readonly Guid VideoAdaptive = new Guid("fbd9aa66-9553-4097-ba44-ed6e9d65eab8");

        private static readonly Guid SubNone = new Guid("fea3413e-7e05-4911-9a71-700331f1c294");

        private static readonly Guid SubProcessor = new Guid("54533251-82be-4824-96c1-47b60b740d00");

        private static readonly Guid SubPcie = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");

        private static readonly Guid SubUsb = new Guid("2a737441-1930-4402-8d77-b2bebba308a3");

        private static readonly Guid SubDisk = new Guid("0012ee47-9041-4b5d-9b77-535fba8b1442");

        private static readonly Guid SubWireless = new Guid("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");

        private static readonly Guid Personality = new Guid("245d8541-3943-4422-b025-13a784f679b7");

        private static readonly Guid ProcThrottleMin = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");

        private static readonly Guid ProcThrottleMax = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec");

        private static readonly Guid CpMinCores = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");

        private static readonly Guid CpMaxCores = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334028");

        private static readonly Guid PerfBoostMode = new Guid("be337238-0d82-4146-a960-4f3749d470c7");

        private static readonly Guid Throttling = new Guid("3b04d4fd-1cc7-4f23-ab1c-d1337819c4bb");

        private static readonly Guid SysCoolPol = new Guid("94d3a615-a899-4ac5-ae2b-e4d8f634367f");

        private static readonly Guid PcieAspm = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");

        private static readonly Guid UsbSelSuspend = new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

        private static readonly Guid DiskIdle = new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e");

        private static readonly Guid NvmeLatTolPrimary = new Guid("fc95af4d-40e7-4b6d-835a-56d131dbc80e");

        private static readonly Guid NvmeLatTolSecondary = new Guid("dbc9e238-6de9-49e3-92cd-8c2b4946b472");

        private static readonly Guid AhciLpm = new Guid("0b2d69d7-a2a1-449c-9680-f91c70521c60");

        private static readonly Guid Usb3Lpm = new Guid("d4e98f31-5ffe-4ce1-be31-1b38b384c009");

        private static readonly Guid SubIntelGfx = new Guid("44f3beca-a7c0-460e-9df2-bb8b99e0cba6");

        private static readonly Guid IntelGfxPlan = new Guid("3619c3f2-afb2-4afc-b0e9-e7fef372de36");

        private static readonly Guid SubSwitchGfx = new Guid("e276e160-7cb0-43c6-b20b-73f5dce39954");

        private static readonly Guid SwitchGfxPolicy = new Guid("a1662ab2-9d34-4e53-ba8b-2639b9e20857");

        private static readonly Guid PerfEpp = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");

        private static readonly Guid PerfAutonomous = new Guid("8baa4a8a-14c6-4451-8e8b-14bdbd197537");

        private static readonly Guid PerfBoostPol = new Guid("45bcc044-d885-43e2-8605-ee0ec6e96b59");

        private static readonly Guid PerfIncPol = new Guid("465e1f50-b610-473a-ab58-00d1077dc418");

        private static readonly Guid PerfDecPol = new Guid("40fbefc7-2e9d-4d25-a185-0cfd8574bac6");

        private static readonly Guid PerfIncTime = new Guid("984cf492-3bed-4488-a8f9-4286c97bf5aa");

        private static readonly Guid PerfDecTime = new Guid("d8edeb9b-95cf-4f95-a73c-b061973693c8");

        private static readonly Guid PerfIncThreshold = new Guid("06cadf0e-64ed-448a-8927-ce7bf90eb35d");

        private static readonly Guid PerfDecThreshold = new Guid("12a0ab44-fe28-4fa9-b3bd-4b64f44960a6");

        private static readonly Guid LatencyHintPerf = new Guid("619b7505-003b-4e82-b7a6-4dd29c300971");

        private static readonly Guid LatencyHintUnpark = new Guid("616cdaa5-695e-4545-97ad-97dc2d1bdd88");

        private static readonly Guid PerfDutyCycling = new Guid("4e4450b3-6179-4e91-b8f1-5bb9938f81a1");

        private static readonly Guid ProcFreqMax = new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100");

        private static readonly Guid WirelessPowerSave = new Guid("12bbebe6-58d6-4636-95bb-3217ef867c1a");

        private static readonly Guid ProcThrottleMin1 = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964d");

        private static readonly Guid ProcThrottleMax1 = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ed");

        private static readonly Guid CpMinCores1 = new Guid("0cc5b647-c1df-4637-891a-dec35c318584");

        private static readonly Guid CpMaxCores1 = new Guid("ea062031-0e34-4ff1-9b6d-eb1059334029");

        private static readonly Guid PerfEpp1 = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864");

        private static readonly Guid SchedPolicy = new Guid("93b8b6dc-0698-4d1c-9ee4-0644e900c85d");

        private static readonly Guid ShortSchedPolicy = new Guid("bae08b81-2d5e-4688-ad6a-13243356654b");

        private static readonly Guid PerfBoostPol1 = new Guid("45bcc044-d885-43e2-8605-ee0ec6e96b5a");

        private static readonly Guid PerfIncPol1 = new Guid("465e1f50-b610-473a-ab58-00d1077dc419");

        private static readonly Guid PerfDecPol1 = new Guid("40fbefc7-2e9d-4d25-a185-0cfd8574bac7");

        private static readonly Guid PerfIncTime1 = new Guid("984cf492-3bed-4488-a8f9-4286c97bf5ab");

        private static readonly Guid PerfDecTime1 = new Guid("d8edeb9b-95cf-4f95-a73c-b061973693c9");

        private static readonly Guid PerfIncThreshold1 = new Guid("06cadf0e-64ed-448a-8927-ce7bf90eb35e");

        private static readonly Guid PerfDecThreshold1 = new Guid("12a0ab44-fe28-4fa9-b3bd-4b64f44960a7");

        private static readonly Guid LatencyHintPerf1 = new Guid("619b7505-003b-4e82-b7a6-4dd29c300972");

        private static readonly Guid LatencyHintUnpark1 = new Guid("616cdaa5-695e-4545-97ad-97dc2d1bdd89");

        private static readonly Guid IdleDisableSet = new Guid("5d76a2ca-e8c0-402f-a133-2158492d58ad");

        public static string ManagedPlanTitle
        {
            get
            {
                return Lang.T("t.powerplan.2") + MachineSignature();
            }
        }

        public static string SelectedPlanId
        {
            get
            {
                string value;
                if (!Settings.TryLoadStr("PowerPlanChoice", out value))
                {
                    return "off";
                }
                bool value2;
                if (value.StartsWith("v2:", StringComparison.Ordinal))
                {
                    value = value.Substring("v2:".Length);
                    if (value.Length == 0)
                    {
                        return "off";
                    }
                }
                else if (!Settings.TryLoadBool("PowerPlanOn", true, out value2) || !value2)
                {
                    return "off";
                }
                Guid g;
                if (value.Length != 0 && value != "managed" && value != "off" && (!TryGuid(value, out g) || g == Guid.Empty))
                {
                    return "off";
                }
                return value;
            }
        }

        public static bool ManagedSelected
        {
            get
            {
                return SelectedPlanId == "managed";
            }
        }

        internal static bool HandheldBypass
        {
            get { return GamingHandheldDetection.Detected; }
        }

        public static string EffectivePlanId
        {
            get
            {
                if (HandheldBypass) return "off";
                string selectedPlanId = SelectedPlanId;
                if (selectedPlanId.Length <= 0)
                {
                    return "managed";
                }
                return selectedPlanId;
            }
        }

        public static bool AutomaticSelection
        {
            get
            {
                return EffectivePlanId == "managed";
            }
        }

        public static bool NoSwitchSelected
        {
            get
            {
                return EffectivePlanId == "off";
            }
        }

        public static string SelectionLabel
        {
            get
            {
                if (!NoSwitchSelected)
                {
                    if (!AutomaticSelection)
                    {
                        return PlanLabelOf(EffectivePlanId);
                    }
                    return Lang.T("plan.pick.auto");
                }
                return Lang.T("plan.pick.off");
            }
        }

        public static string CreatedPlanId
        {
            get
            {
                return Settings.LoadStr("DefaultPlanGuid", "");
            }
        }

        public static bool HasResidue
        {
            get
            {
                if (Settings.LoadStr(PresetCreationKey, "").Length != 0) return true;
                if (!settledRestoreTarget.HasValue && !LaboratoryHasResidue && !IdlePolicyHasResidue && !LegacyCpuIdleHasResidue)
                {
                    return Settings.LoadStr("PrevPowerPlan", "").Length > 0;
                }
                return true;
            }
        }

        private static IManagedPowerPlans ManagedPlans
        {
            get
            {
                return new NativeManagedPowerPlans();
            }
        }

        internal static bool ManagedPlanIsActive
        {
            get
            {
                try
                {
                    Guid guid = ManagedPlanGuid();
                    if (guid == Guid.Empty)
                    {
                        return false;
                    }
                    Guid? guid2 = Current();
                    return guid2.HasValue && guid2.Value == guid;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static bool HasParkResidue()
        {
            string value;
            return !Settings.TryLoadStr("CoreParkSnap", out value) || value == null || value.Length > 0;
        }

        private static int TuneCode(bool aggressive)
        {
            return aggressive ? 1 : 0;
        }

        public static string CurrentPlanLabel()
        {
            Guid? guid = Current();
            if (!guid.HasValue)
            {
                return Lang.T("t.systemaudithardware.3");
            }
            Guid value = guid.Value;
            if (value == Ultimate)
            {
                return Lang.T("t.powerplan.1");
            }
            if (value == HighPerf)
            {
                return Lang.T("t.powerplan.3");
            }
            if (value == Balanced)
            {
                return Lang.T("t.powerplan.4");
            }
            if (value == PowerSaver)
            {
                return Lang.T("t.powerplan.5");
            }
            string name = ReadName(value);
            if (name.Length <= 0)
            {
                return Lang.T("t.powerplan.6");
            }
            return name;
        }

        private static string PlanLabel(Guid g)
        {
            if (g == Ultimate)
            {
                return Lang.T("t.powerplan.1");
            }
            if (g == HighPerf)
            {
                return Lang.T("t.powerplan.3");
            }
            string name = ReadName(g);
            if (name.Length <= 0)
            {
                return g.ToString();
            }
            return name;
        }

        public static List<PowerPlanEntry> ListUserPlans()
        {
            Guid guid = ManagedPlanGuid();
            List<PowerPlanEntry> list = new List<PowerPlanEntry>();
            foreach (Guid item in EnumerateSchemes())
            {
                if (!ManagedPolicyOwns(item) && (guid == Guid.Empty || item != guid))
                {
                    string name = ReadName(item);
                    if (!string.Equals(name, "由软件调度 " + MachineSignature(), StringComparison.OrdinalIgnoreCase) && !string.Equals(name, "Scheduled by Pavise " + MachineSignature(), StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(new PowerPlanEntry(item, (name.Length > 0) ? name : item.ToString()));
                    }
                }
            }
            return list;
        }

        public static bool SelectPlan(string id)
        {
            string idOffset = id ?? "";
            if (HandheldBypass && idOffset != "off") return false;
            Guid g;
            if (idOffset.Length > 0 && idOffset != "managed" && idOffset != "off" && (!TryGuid(idOffset, out g) || g == Guid.Empty))
            {
                return false;
            }
            string value;
            if (!Settings.TryLoadStr("PowerPlanChoice", out value))
            {
                return false;
            }
            string message = "v2:" + ((idOffset.Length == 0) ? "managed" : idOffset);
            if (value != message && !Settings.SaveStr("PowerPlanChoice", message))
            {
                return false;
            }
            if (value == message) return true;
            lock (lk)
            {
                managedCreationBlocked = false;
                resolved = false;
                target = Guid.Empty;
                targetOwned = false;
                tuneState = -1;
            }
            if (idOffset == "off")
            {
                Logger.Log(Lang.T("log.powerplan.off", 0));
            }
            else if (idOffset == "managed")
            {
                Logger.Log(Lang.T("log.powerplan.7", 0) + ManagedPlanTitle + Lang.T("log.powerplan.8", 0));
            }
            else if (idOffset.Length == 0)
            {
                Logger.Log(Lang.T("log.powerplan.9", 0) + ManagedPlanTitle);
            }
            else
            {
                Logger.Log(Lang.T("log.powerplan.10", 0) + PlanLabelOf(idOffset) + Lang.T("log.powerplan.11", 0));
            }
            return true;
        }

        private static Guid ManagedPlanGuid()
        {
            string pgPlanGuidValue = Settings.LoadStr("PgPlanGuid", "");
            Guid g;
            if (pgPlanGuidValue.Length == 0 || !TryGuid(pgPlanGuidValue, out g))
            {
                return Guid.Empty;
            }
            foreach (Guid item in EnumerateSchemes())
            {
                if (item == g)
                {
                    return g;
                }
            }
            return Guid.Empty;
        }

        private static Guid EnsureManagedPlan()
        {
            return EnsureManagedPolicyPlan();
        }

        public static bool RemoveManagedPlan()
        {
            lock (lk)
            {
                if (HandheldBypass) return false;
                if (!RestoreLaboratoryBeforeCoreChange()) return false;
                if (!RestoreLegacyCpuIdle() || LegacyCpuIdleHasResidue)
                {
                    return false;
                }
                return RemoveManagedPlanCore();
            }
        }

        private static bool RemoveManagedPlanCore()
        {
            string pgPlanGuidValue = Settings.LoadStr("PgPlanGuid", "");
            if (pgPlanGuidValue.Length == 0)
            {
                return true;
            }
            Guid g;
            if (!TryGuid(pgPlanGuidValue, out g))
            {
                Settings.SaveStr("PgPlanGuid", "");
                return true;
            }
            Guid? guid = Current();
            if (guid.HasValue && guid.Value == g && !SwitchAwayFrom(g))
            {
                return false;
            }
            Guid guid2 = g;
            uint powerDeleteSchemeResult = PowerDeleteScheme(IntPtr.Zero, ref guid2);
            if (powerDeleteSchemeResult != 0 && powerDeleteSchemeResult != 2)
            {
                return false;
            }
            if (!ForgetDeletedExtremeSnapshot(g))
            {
                return false;
            }
            if (!Settings.SaveStrDurable("PgPlanGuid", ""))
            {
                return false;
            }
            lock (lk)
            {
                resolved = false;
                target = Guid.Empty;
                targetOwned = false;
                tuneState = -1;
            }
            Logger.Log(Lang.T("log.powerplan.15", 0) + ManagedPlanTitle);
            return true;
        }

        private static bool SwitchAwayFrom(Guid avoid)
        {
            Guid g;
            if (TryGuid(Settings.LoadStr("PrevPowerPlan", ""), out g) && g != avoid && SchemeUsable(g) && Set(g))
            {
                return true;
            }
            if (SchemeUsable(Balanced) && Set(Balanced))
            {
                return true;
            }
            foreach (Guid item in EnumerateSchemes())
            {
                if (item != avoid && Set(item))
                {
                    return true;
                }
            }
            return false;
        }

        private static string PlanLabelOf(string id)
        {
            Guid g;
            if (!TryGuid(id, out g))
            {
                return id;
            }
            return PlanLabel(g);
        }

        public static bool RemoveCreatedPlan()
        {
            lock (lk)
            {
                if (HandheldBypass) return false;
                if (!RestoreLaboratoryBeforeCoreChange()) return false;
                if (!RestoreLegacyCpuIdle() || LegacyCpuIdleHasResidue)
                {
                    return false;
                }
                return RemoveCreatedPlanCore();
            }
        }

        private static bool RemoveCreatedPlanCore()
        {
            string defaultPlanGuidValue = Settings.LoadStr("DefaultPlanGuid", "");
            if (defaultPlanGuidValue.Length == 0)
            {
                return true;
            }
            Guid g;
            if (!TryGuid(defaultPlanGuidValue, out g))
            {
                Settings.SaveStr("DefaultPlanGuid", "");
                return true;
            }
            Guid? guid = Current();
            if (guid.HasValue && guid.Value == g && !SwitchAwayFrom(g))
            {
                return false;
            }
            Guid guid2 = g;
            if (SchemeUsable(g) && PowerDeleteScheme(IntPtr.Zero, ref guid2) != 0)
            {
                return false;
            }
            Settings.SaveStr("DefaultPlanGuid", "");
            lock (lk)
            {
                resolved = false;
                target = Guid.Empty;
            }
            Logger.Log(Lang.T("log.powerplan.19", 0));
            return true;
        }

        private static bool TryUserChosenTarget()
        {
            string selectedPlanId = SelectedPlanId;
            if (selectedPlanId.Length == 0)
            {
                return false;
            }
            if (selectedPlanId == "managed")
            {
                Guid guid = EnsureManagedPlan();
                if (guid == Guid.Empty)
                {
                    return false;
                }
                target = guid;
                targetOwned = ManagedPlans.IsOwned(guid);
                return true;
            }
            Guid g;
            if (!TryGuid(selectedPlanId, out g) || !SchemeUsable(g))
            {
                return false;
            }
            target = g;
            targetOwned = false;
            Logger.Log(Lang.T("log.powerplan.23", 0) + PlanLabel(g) + Lang.T("log.powerplan.24", 0));
            return true;
        }

        private static Guid ResolveTarget(bool aggressive)
        {
            if (NoSwitchSelected)
            {
                return Guid.Empty;
            }
            requestedAggressive = aggressive;
            if (resolved && targetOwned && tuneState != TuneCode(aggressive)) resolved = false;
            if (resolved)
            {
                return target;
            }
            resolved = false;
            target = Guid.Empty;
            targetOwned = false;
            if (!TryUserChosenTarget())
            {
                if (!AutomaticSelection || SelectedPlanId == "managed")
                {
                    return Guid.Empty;
                }
                Guid guid = EnsureManagedPlan();
                if (guid == Guid.Empty)
                {
                    return Guid.Empty;
                }
                target = guid;
                targetOwned = ManagedPlans.IsOwned(guid);
            }
            resolved = true;
            return target;
        }

        private static bool TryGuid(string s, out Guid g)
        {
            try
            {
                g = new Guid(s);
                return true;
            }
            catch
            {
                g = Guid.Empty;
                return false;
            }
        }

        private static bool ActivateInner(bool aggressive)
        {
            if (HandheldBypass) return RestoreHandheldApply();
            if (NoSwitchSelected)
            {
                return false;
            }
            if (active)
            {
                return true;
            }
            if (!RestoreLaboratoryBeforeCoreChange())
                return PowerApplyFailed("laboratory-recovery", "Laboratory power recovery pending");
            string value;
            if ((settledRestoreTarget.HasValue || saved != Guid.Empty || !Settings.TryLoadStr("PrevPowerPlan", out value) || value == null || value.Length != 0) && !RestorePlanCore(false))
            {
                return PowerApplyFailed("previous-plan-recovery", "Previous power-plan recovery pending");
            }
            if (AutomaticSelection)
            {
                resolved = false;
                target = Guid.Empty;
                targetOwned = false;
            }
            Guid guid = ResolveTarget(aggressive);
            if (HandheldBypass) return RestoreHandheldApply();
            if (guid == Guid.Empty)
            {
                return PowerApplyFailed("target-resolution", "Selected power-plan target unavailable");
            }
            if (!targetOwned && !RestoreOwnedIdlePolicy())
            {
                return PowerApplyFailed("idle-policy-recovery", "Legacy processor idle-policy recovery pending");
            }
            Guid? guid2 = RestoreCurrentPlan();
            if (HandheldBypass) return RestoreHandheldApply();
            if (!guid2.HasValue || guid2.Value == Guid.Empty)
            {
                return PowerApplyFailed("active-plan-read", "Active power-plan query failed");
            }
            if (guid2.Value == guid && !targetOwned)
            {
                lastActivatedPlan = guid;
                active = true;
                return true;
            }
            saved = guid2.Value;
            if (targetOwned)
            {
                saved = ManagedReturnPlan(guid2.Value, guid);
                if (saved == Guid.Empty)
                {
                    return PowerApplyFailed("return-plan-resolution", "Managed return plan invalid or unavailable");
                }
            }
            if (!Settings.SaveStrDurable("PrevPowerPlan", saved.ToString()) || Settings.LoadStr("PrevPowerPlan", "") != saved.ToString())
            {
                saved = Guid.Empty;
                Logger.Log(Lang.T("log.powerplan.25", 0));
                return PowerApplyFailed("return-plan-save", "Return-plan receipt persistence or readback failed");
            }
            if (HandheldBypass) return RestoreHandheldApply();
            bool prepared = !targetOwned || PrepareManagedScheme(guid, aggressive);
            if (HandheldBypass) return RestoreHandheldApply();
            if (!prepared) return PowerApplyFailed("managed-preparation", "Managed parameter preparation failed");
            bool restoreSetPlanSucceeded = RestoreSetPlan(guid);
            if (restoreSetPlanSucceeded) lastActivatedPlan = guid;
            Guid? guid3 = (restoreSetPlanSucceeded ? RestoreCurrentPlan() : null);
            if (restoreSetPlanSucceeded && guid3.HasValue && guid3.Value == guid)
            {
                active = true;
                if (targetOwned) CleanupPreviousPresetPlan();
                Logger.Log(Lang.T("log.powerplan.26", 0) + RestorePlanLabel(guid) + Lang.T("log.gpupowermax.7", 0) + RestorePlanLabel(saved) + " ");
                return true;
            }
            Logger.Log(Lang.T("log.powerplan.27", 0));
            return PowerApplyFailed(restoreSetPlanSucceeded ? "activation-readback" : "activation",
                restoreSetPlanSucceeded ? "Active power-plan readback mismatch" : "Selected power-plan activation rejected");
        }

        private static bool RestoreHandheldApply()
        {
            ClearApplyFailureReason();
            return Restore();
        }

        public static bool Enforce(bool aggressive)
        {
            ClearApplyFailureReason();
            lock (lk)
            {
                // 应用排队期间设备策略可能变化；掌机跳过只结清已有记录，不新增应用失败。
                if (HandheldBypass) return RestoreHandheldApply();
                if (NoSwitchSelected)
                {
                    return false;
                }
                Guid? observed = RestoreCurrentPlan();
                if (!observed.HasValue || observed.Value == Guid.Empty)
                    return PowerApplyFailed("active-plan-read", "Active power-plan query failed");
                Guid heldPlan = lastActivatedPlan != Guid.Empty ? lastActivatedPlan : (active ? target : Guid.Empty);
                bool planDrift = active && heldPlan != Guid.Empty && observed.Value != heldPlan;
                bool parameterDrift = active && targetOwned && tuneState == TuneCode(aggressive)
                    && !ManagedPresetIntact(target, aggressive);
                bool correctionAdmitted = false;
                if (externalDriftCorrectionPending || planDrift || parameterDrift)
                {
                    string detail = parameterDrift ? (intactMismatch.Length == 0 ? "parameters-unverified" : intactMismatch) : "";
                    if (detail.Length == 0) detail = "previous-correction-incomplete";
                    NoteExternalDriftCorrection(observed.Value, heldPlan, planDrift, parameterDrift, detail);
                    correctionAdmitted = true;
                }
                if (!active)
                {
                    bool activated = ActivateInner(aggressive);
                    if (activated) externalDriftCorrectionPending = false;
                    return activated;
                }
                // 活动计划没被切走时先原地写回被改的数值；写不回去才重建，重建与重新激活合并为同一次纠偏。
                if (parameterDrift && (planDrift || !RepairManagedValues(target, aggressive)))
                {
                    resolved = false;
                    tuneState = -1;
                }
                Guid guid = ResolveTarget(aggressive);
                if (HandheldBypass) return RestoreHandheldApply();
                if (guid == Guid.Empty)
                {
                    return PowerApplyFailed("target-resolution", "Selected power-plan target unavailable");
                }
                if (targetOwned && saved == Guid.Empty)
                {
                    active = false;
                    bool activated = ActivateInner(aggressive);
                    if (activated) externalDriftCorrectionPending = false;
                    return activated;
                }
                if (!targetOwned && !RestoreOwnedIdlePolicy())
                {
                    return PowerApplyFailed("idle-policy-recovery", "Legacy processor idle-policy recovery pending");
                }
                if (HandheldBypass) return RestoreHandheldApply();
                bool needsRetune = targetOwned && tuneState != TuneCode(aggressive);
                bool retuned = !needsRetune || RetuneManagedScheme(guid, aggressive);
                if (retuned && needsRetune) lastActivatedPlan = guid;
                if (HandheldBypass) return RestoreHandheldApply();
                if (!retuned)
                {
                    return PowerApplyFailed("managed-retune", "Managed parameter preparation or reactivation failed");
                }
                Guid? guid2 = RestoreCurrentPlan();
                if (HandheldBypass) return RestoreHandheldApply();
                if (!guid2.HasValue || guid2.Value == Guid.Empty)
                {
                    return PowerApplyFailed("active-plan-read", "Active power-plan query failed");
                }
                if (guid2.HasValue && guid2.Value != guid)
                {
                    // 重调已激活并读回后再次被切走，是新的一次外部改动，单独计数。
                    // 尚未执行前向激活时，最初同时发现的参数与 GUID 漂移仍只计一次。
                    if (needsRetune || (!correctionAdmitted && heldPlan != Guid.Empty && guid2.Value != heldPlan))
                        NoteExternalDriftCorrection(guid2.Value, guid, true, false, "active-plan-changed-after-observation");
                    if (!RestoreSetPlan(guid))
                        return PowerApplyFailed("activation", "Selected power-plan activation rejected");
                    lastActivatedPlan = guid;
                    if (RestoreCurrentPlan() != guid)
                        return PowerApplyFailed("activation-readback", "Active power-plan readback mismatch");
                    Logger.Log(Lang.T("log.powerplan.28", 0) + RestorePlanLabel(guid));
                }
                externalDriftCorrectionPending = false;
                return true;
            }
        }

        public static bool EnforceSettled(bool aggressive)
        {
            lock (lk)
            {
                if (HandheldBypass) return !active && !HasResidue;
                if (NoSwitchSelected || !active)
                {
                    return false;
                }
                Guid guid = (resolved ? target : Guid.Empty);
                if (guid == Guid.Empty)
                {
                    return false;
                }
                if (targetOwned && saved == Guid.Empty)
                {
                    return false;
                }
                bool idlePolicyHasResidueSnapshot = targetOwned ? tuneState != TuneCode(aggressive) : IdlePolicyHasResidue;
                if (!idlePolicyHasResidueSnapshot)
                {
                    Guid? guid2 = RestoreCurrentPlan();
                        return guid2.HasValue && guid2.Value == guid
                            && (!targetOwned || ManagedPresetIntact(guid, aggressive));
                }
                return false;
            }
        }

        private static bool? IsOurActivePlan(Guid g)
        {
            if (ManagedPolicyOwns(g))
            {
                return true;
            }
            if (resolved && g == target)
            {
                return true;
            }
            ManagedPolicyRecord record;
            bool policyUnconfirmed = !ReadManagedPolicy(out record);
            string[] textEntries = new string[4] { "PgPlanGuid", "PowerPlanChoice", "ArenaPlanGuid", "UltimatePlanGuid" };
            foreach (string entryText in textEntries)
            {
                string value;
                if (!Settings.TryLoadStr(entryText, out value) || value == null)
                {
                    policyUnconfirmed = true;
                    continue;
                }
                if (entryText == "PowerPlanChoice" && value.StartsWith("v2:", StringComparison.Ordinal))
                {
                    value = value.Substring("v2:".Length);
                    if (value.Length == 0)
                    {
                        policyUnconfirmed = true;
                        continue;
                    }
                }
                if (value.Length != 0 && (entryText != "PowerPlanChoice" || (value != "managed" && value != "off")))
                {
                    Guid g2;
                    if (!TryGuid(value, out g2) || g2 == Guid.Empty)
                    {
                        policyUnconfirmed = true;
                    }
                    else if (g == g2)
                    {
                        return true;
                    }
                }
            }
            if (!policyUnconfirmed)
            {
                return false;
            }
            return null;
        }

        // 局外及崩溃恢复后，删除所有所有权键均未引用的非活动托管计划；会话清理只处理当前记录计划及其前任。

        private static int orphanSweepDone;
    }
}

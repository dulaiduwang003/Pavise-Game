using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static partial class PowerPlan
	{
		private sealed class ExtremeSavedValue
		{
			internal Guid Setting;

			internal uint Ac;

			internal uint Dc;

			internal string Text
			{
				get
				{
					return Setting.ToString("N") + "=" + Ac.ToString(CultureInfo.InvariantCulture) + "," + Dc.ToString(CultureInfo.InvariantCulture);
				}
			}
		}

		private enum CpuIdlePhase
		{
			Prepared,
			Owned,
			Restoring,
			Settled
		}

		private sealed class CpuIdleReceipt
		{
			internal Guid Scheme;

			internal CpuIdlePhase Phase;

			// -1 is a legacy receipt; v3 bits 1/2 authorize restoring only the still-owned AC/DC value 1.
			internal int PendingMask = -1;

			internal string Text(CpuIdlePhase phase)
			{
				object instance;
				switch (phase)
				{
				default:
					instance = "S";
					break;
				case CpuIdlePhase.Restoring:
					instance = "R";
					break;
				case CpuIdlePhase.Owned:
					instance = "O";
					break;
				case CpuIdlePhase.Prepared:
					instance = "P";
					break;
				}
				string message = (string)instance;
				return PendingMask < 0 ? "2|" + Scheme.ToString("D") + "|0|" + message
					: "3|" + Scheme.ToString("D") + "|0|" + message + "|" + PendingMask.ToString(CultureInfo.InvariantCulture);
			}
		}

		private sealed class ManagedPolicyRecord
		{
			internal Guid Plan;

			internal Guid Base;

			internal Guid Previous;

            internal bool CurrentTemplate;
            internal string Source = "", Fingerprint = "";

			internal string Encode()
			{
				// Keep the 3.1.1.0 receipt envelope readable when switching between builds.
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
				if (!(scheme == Guid.Empty))
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

        // Bound managed-plan rebuilds per game session to avoid competing with OEM controllers.
        // On exhaustion, disable switching and restore the original plan through normal recovery.
        private const int ExternalDriftRebuildLimit = 3;
        private static int externalDriftRebuilds;
        private static long externalDriftSession = -1;
        private static bool externalDriftStopped;
        private static Guid externalDriftAccepted;
        private static string intactMismatch = "";

        // Recovery state for receipts written by retired releases; never enables the old tuning policy.
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
				if (value.Length != 0 && !(value == "managed") && !(value == "off") && (!TryGuid(value, out g) || !(g != Guid.Empty)))
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

		internal static bool IdlePolicyHasResidue
		{
			get
			{
				string value;
				if (Settings.TryLoadStr("ExtremePowerKnobSnap", out value))
				{
					return !string.IsNullOrEmpty(value);
				}
				return true;
			}
		}

		internal static bool LegacyCpuIdleHasResidue
		{
			get
			{
				lock (lk)
				{
					if (cpuIdleReceipt != null)
					{
						return true;
					}
					try
					{
						string message;
						return !CpuIdleReadLedger(out message) || message == null || message.Length != 0;
					}
					catch
					{
						return true;
					}
				}
			}
		}

		private static IManagedPowerPlans ManagedPlans
		{
			get
			{
				return new NativeManagedPowerPlans();
			}
		}

        internal static bool ExtremeTunePending
		{
			get
			{
				return extremeTunePending;
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

		public static bool TryCurrentUnparked(out bool unparked)
		{
			unparked = false;
			Guid? guid = Current();
			if (!guid.HasValue)
			{
				return false;
			}
			IManagedPowerPlans managedPlans = ManagedPlans;
			return ManagedPowerPlanPolicy.ParkingDisabled(managedPlans.Read(guid.Value, SubProcessor, CpMinCores, true), managedPlans.Read(guid.Value, SubProcessor, CpMinCores, false), managedPlans.Read(guid.Value, SubProcessor, CpMinCores1, true), managedPlans.Read(guid.Value, SubProcessor, CpMinCores1, false), Native.OnAcPower(), CpuTopology.Hybrid, out unparked);
		}

		public static bool RestoreParkState()
		{
			lock (lk)
			{
				string message;
				if (!Settings.TryLoadStr("CoreParkSnap", out message) || message == null)
				{
					return false;
				}
				if (message.Length == 0)
				{
					return true;
				}
				string[] messageParts = message.Split('|');
				bool valuesRestored = messageParts.Length == 7 && messageParts[0] == "2" && messageParts[6] == "F";
				if (valuesRestored)
				{
					messageParts = new[] { messageParts[1], messageParts[2], messageParts[3], messageParts[4], messageParts[5] };
				}
				Guid guid;
				if (messageParts.Length != 5 || !Guid.TryParse(messageParts[0], out guid) || guid == Guid.Empty)
				{
					return ParkRestoreFailed();
				}
				foreach (string original in new[] { messageParts[1], messageParts[2], messageParts[3], messageParts[4] })
				{
					uint value;
					if (original != "-" && (!uint.TryParse(original, out value) || value > 100))
					{
						return ParkRestoreFailed();
					}
				}
				if (!valuesRestored)
				{
					bool primary = WriteBack(guid, CpMinCores, messageParts[1], messageParts[2]);
					bool secondary = WriteBack(guid, CpMinCores1, messageParts[3], messageParts[4]);
					if (!primary || !secondary)
					{
						return ParkRestoreFailed();
					}
					// Once values are restored, a failed activation must only retry the
					// activation, even if another owner later chooses the old value 100.
					string flushReceipt = "2|" + message + "|F";
					string persisted;
					if (!Settings.SaveStrDurable("CoreParkSnap", flushReceipt)
						|| !Settings.TryLoadStr("CoreParkSnap", out persisted) || persisted != flushReceipt)
					{
						return ParkRestoreFailed();
					}
				}
				Guid? current = Current();
				if (!current.HasValue || current == Guid.Empty)
				{
					return ParkRestoreFailed();
				}
				if (current == guid)
				{
					Guid? confirmed = Current();
					if (!confirmed.HasValue || confirmed == Guid.Empty
						|| (confirmed == guid && (!Set(guid) || Current() != guid)))
					{
						return ParkRestoreFailed();
					}
				}
				if (!ClearParkSnap())
				{
					return ParkRestoreFailed();
				}
				Logger.Log(Lang.T("log.powerplancorepark.4"));
				return true;
			}
		}

		private static bool ParkRestoreFailed()
		{
			int result;
			int.TryParse(Settings.LoadStr("CoreParkTries", "0"), out result);
			int tries = Math.Min(Math.Max(result, 0), 3);
			if (tries < 3)
			{
				Settings.SaveStr("CoreParkTries", (tries + 1).ToString(CultureInfo.InvariantCulture));
			}
			if (tries == 2) Logger.Warn(Lang.T("log.powerplancorepark.8"));
			else Logger.Log(Lang.T("log.powerplancorepark.3"));
			return false;
		}

		private static bool ClearParkSnap()
		{
			string remaining;
			if (!Settings.SaveStrDurable("CoreParkSnap", "")
				|| !Settings.TryLoadStr("CoreParkSnap", out remaining) || remaining != "")
			{
				return false;
			}
			Settings.Remove("CoreParkTries");
			return true;
		}

		private static bool WriteBack(Guid scheme, Guid setting, string acStr, string dcStr)
		{
			bool ac = RestoreParkIndex(scheme, setting, true, acStr);
			bool dc = RestoreParkIndex(scheme, setting, false, dcStr);
			return ac && dc;
		}

		private static bool RestoreParkIndex(Guid scheme, Guid setting, bool ac, string original)
		{
			try
			{
				if (original == "-") return true;
				uint saved, current;
				if (!uint.TryParse(original, out saved) || saved > 100
					|| !(ac ? ReadAc(scheme, SubProcessor, setting, out current) : ReadDc(scheme, SubProcessor, setting, out current)))
				{
					return false;
				}
				// Only the old Pavise value is owned; preserve later external changes.
				if (current != 100 || saved == 100) return true;
				if (!(ac ? WriteAc(scheme, SubProcessor, setting, saved) : WriteDc(scheme, SubProcessor, setting, saved)))
				{
					return false;
				}
				return (ac ? ReadAc(scheme, SubProcessor, setting, out current) : ReadDc(scheme, SubProcessor, setting, out current)) && current == saved;
			}
			catch
			{
				return false;
			}
		}

		public static string MachineSignature()
		{
			if (machineSig != null)
			{
				return machineSig;
			}
			string message = "";
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Cryptography"))
				{
					if (registryKey != null)
					{
						message = (registryKey.GetValue("MachineGuid") as string) ?? "";
					}
				}
			}
			catch
			{
			}
			if (message.Length == 0)
			{
				try
				{
					message = Environment.MachineName;
				}
				catch
				{
					message = "PAVISE";
				}
			}
			uint numericValue = 2166136261u;
			string message2 = message;
			foreach (char c in message2)
			{
				numericValue ^= c;
				numericValue *= 16777619;
			}
			machineSig = (numericValue & 0xFFFFu).ToString("X4");
			return machineSig;
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
				if (!ManagedPolicyOwns(item) && (!(guid != Guid.Empty) || !(item == guid)))
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
				Logger.Log(Lang.T("log.powerplan.off"));
			}
			else if (idOffset == "managed")
			{
				Logger.Log(Lang.T("log.powerplan.7") + ManagedPlanTitle + Lang.T("log.powerplan.8"));
			}
			else if (idOffset.Length == 0)
			{
				Logger.Log(Lang.T("log.powerplan.9") + ManagedPlanTitle);
			}
			else
			{
				Logger.Log(Lang.T("log.powerplan.10") + PlanLabelOf(idOffset) + Lang.T("log.powerplan.11"));
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
			Logger.Log(Lang.T("log.powerplan.15") + ManagedPlanTitle);
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
			Logger.Log(Lang.T("log.powerplan.19"));
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
			Logger.Log(Lang.T("log.powerplan.23") + PlanLabel(g) + Lang.T("log.powerplan.24"));
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
				if (!(guid != Guid.Empty))
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
				Logger.Log(Lang.T("log.powerplan.25"));
				return PowerApplyFailed("return-plan-save", "Return-plan receipt persistence or readback failed");
			}
			if (HandheldBypass) return RestoreHandheldApply();
			bool prepared = !targetOwned || PrepareManagedScheme(guid, aggressive);
			if (HandheldBypass) return RestoreHandheldApply();
			if (!prepared) return PowerApplyFailed("managed-preparation", "Managed parameter preparation failed");
			bool restoreSetPlanSucceeded = RestoreSetPlan(guid);
			Guid? guid3 = (restoreSetPlanSucceeded ? RestoreCurrentPlan() : null);
			if (restoreSetPlanSucceeded && guid3.HasValue && guid3.Value == guid)
			{
                active = true;
                if (targetOwned) CleanupPreviousPresetPlan();
                Logger.Log(Lang.T("log.powerplan.26") + RestorePlanLabel(guid) + Lang.T("log.gpupowermax.7") + RestorePlanLabel(saved) + " ");
				return true;
			}
			Logger.Log(Lang.T("log.powerplan.27"));
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
				// Device policy may change while apply is queued; handheld bypass settles existing receipts without a new apply failure.
				if (HandheldBypass) return RestoreHandheldApply();
				if (NoSwitchSelected)
				{
					return false;
				}
				if (AutomaticSelection && externalDriftStopped
					&& !(active && targetOwned && target == externalDriftAccepted && tuneState == TuneCode(aggressive)))
				{
					return PowerApplyFailed("external-drift-exhausted", "Managed power rebuilding remains stopped until the next game session");
				}
				if (!active)
				{
					return ActivateInner(aggressive);
				}
				if (targetOwned && tuneState == TuneCode(aggressive) && target != externalDriftAccepted && !ManagedPresetIntact(target, aggressive))
				{
					string detail = intactMismatch.Length == 0 ? "unknown" : intactMismatch;
					if (externalDriftRebuilds >= ExternalDriftRebuildLimit)
					{
						// Repeated drift exhausts rebuilding for the session, even if persisting the fallback selection fails.
						externalDriftAccepted = target;
						externalDriftStopped = true;
						if (SelectNoSwitchAfterExternalDrift())
						{
							Logger.Warn(PerformancePolicy.Text("托管电源参数反复被其他程序修改（常见于笔记本厂商控制中心），已超过 "
								+ ExternalDriftRebuildLimit + " 次，电源计划已自动改为「不切换」，本局退回原计划；需要时可在电源计划选项中重新开启",
								"Managed power settings were repeatedly changed by another program (often an OEM control center) more than "
								+ ExternalDriftRebuildLimit + " times; power-plan switching was set to \"Don't switch\" and the original plan will be restored. It can be re-enabled in the power-plan options")
								+ " detail=" + detail);
							return true;
						}
						// If OFF cannot be saved, retain the managed plan and return receipt without further session rebuilds.
						Logger.Warn(PerformancePolicy.Text("托管电源参数反复被其他程序修改，但「不切换」设置保存失败；本局保留当前计划，不再重建",
							"Managed power settings were repeatedly changed by another program, but saving \"Don't switch\" failed; keeping the current plan without further rebuilds this session")
							+ " detail=" + detail + " rebuilds=" + externalDriftRebuilds + "/" + ExternalDriftRebuildLimit);
					}
					else
					{
						// Preserve the original return-plan receipt when replacing a drifted or differently routed plan.
						externalDriftRebuilds++;
						resolved = false;
						tuneState = -1;
						Logger.Warn(PerformancePolicy.Text("托管电源性能参数发生变化，正在重建并核验", "Managed power performance settings changed; rebuilding and verifying")
							+ " detail=" + detail + " rebuild=" + externalDriftRebuilds + "/" + ExternalDriftRebuildLimit);
					}
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
					return ActivateInner(aggressive);
				}
				if (!targetOwned && !RestoreOwnedIdlePolicy())
				{
					return PowerApplyFailed("idle-policy-recovery", "Legacy processor idle-policy recovery pending");
				}
				if (HandheldBypass) return RestoreHandheldApply();
				bool retuned = !targetOwned || tuneState == TuneCode(aggressive) || RetuneManagedScheme(guid, aggressive);
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
					if (!RestoreSetPlan(guid))
                        return PowerApplyFailed("activation", "Selected power-plan activation rejected");
                    if (!(RestoreCurrentPlan() == guid))
                        return PowerApplyFailed("activation-readback", "Active power-plan readback mismatch");
					Logger.Log(Lang.T("log.powerplan.28") + RestorePlanLabel(guid));
				}
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
				bool idlePolicyHasResidueSnapshot;
				if (!targetOwned)
				{
					idlePolicyHasResidueSnapshot = IdlePolicyHasResidue;
				}
				else
				{
					if (saved == Guid.Empty)
					{
						goto RejectUnrecognizedPlan;
					}
					idlePolicyHasResidueSnapshot = tuneState != TuneCode(aggressive);
				}
				if (!idlePolicyHasResidueSnapshot)
				{
					Guid? guid2 = RestoreCurrentPlan();
						return guid2.HasValue && guid2.Value == guid
							&& (!targetOwned || guid == externalDriftAccepted || ManagedPresetIntact(guid, aggressive));
				}
				goto RejectUnrecognizedPlan;
				RejectUnrecognizedPlan:
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
			bool isNotManagedPolicy = !ReadManagedPolicy(out record);
			string[] textEntries = new string[4] { "PgPlanGuid", "PowerPlanChoice", "ArenaPlanGuid", "UltimatePlanGuid" };
			foreach (string entryText in textEntries)
			{
				string value;
				if (!Settings.TryLoadStr(entryText, out value) || value == null)
				{
					isNotManagedPolicy = true;
					continue;
				}
				if (entryText == "PowerPlanChoice" && value.StartsWith("v2:", StringComparison.Ordinal))
				{
					value = value.Substring("v2:".Length);
					if (value.Length == 0)
					{
						isNotManagedPolicy = true;
						continue;
					}
				}
				if (value.Length != 0 && (!(entryText == "PowerPlanChoice") || (!(value == "managed") && !(value == "off"))))
				{
					Guid g2;
					if (!TryGuid(value, out g2) || g2 == Guid.Empty)
					{
						isNotManagedPolicy = true;
					}
					else if (g == g2)
					{
						return true;
					}
				}
			}
			if (!isNotManagedPolicy)
			{
				return false;
			}
			return null;
		}

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
					Logger.Log(Lang.T("log.powerplan.36") + RestorePlanLabel(guid.Value));
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
						Logger.Warn(Lang.T("log.powerplan.30"));
					}
					else
					{
						Logger.Log(Lang.T("log.powerplan.31"));
					}
					return false;
				}
				Guid? guid3 = RestoreCurrentPlan();
				if (!guid3.HasValue || guid3.Value != g)
				{
					return false;
				}
				Logger.Log(Lang.T("log.powerplan.29"));
				return ClearRestoredPlan(g);
			}
		}

		// Persist and read back automatic OFF so a restart cannot resume the same power-plan conflict.
		private static bool SelectNoSwitchAfterExternalDrift()
		{
			const string choice = "v2:off";
			if (!Settings.SaveStrDurable("PowerPlanChoice", choice) || Settings.LoadStr("PowerPlanChoice", "") != choice) return false;
			managedCreationBlocked = false;
			resolved = false;
			target = Guid.Empty;
			targetOwned = false;
			tuneState = -1;
			Logger.Log(Lang.T("log.powerplan.off"));
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
			Logger.Log(Lang.T("log.powerplan.32"));
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

		private static bool RestoreOwnedIdlePolicy()
		{
			string value;
			if (!Settings.TryLoadStr("ExtremePowerKnobSnap", out value))
			{
				return false;
			}
			if (string.IsNullOrEmpty(value))
			{
				return true;
			}
			string[] valueParts = value.Split('|');
			Guid result;
			if (valueParts.Length == 3 && valueParts[0] == "2")
			{
				if (!Guid.TryParse(valueParts[1], out result) || result == Guid.Empty)
				{
					return false;
				}
			}
			else
			{
				if (valueParts.Length != 1)
				{
					return false;
				}
				if (!Guid.TryParse(Settings.LoadStr("PgPlanGuid", ""), out result) || result == Guid.Empty)
				{
					return false;
				}
			}
			if (!RestoreExtremeKnobs(result))
			{
				return false;
			}
			tuneState = -1;
			return true;
		}

		private static bool IsRetiredIdleKnob(Guid setting)
		{
			if (!(setting == IdlePromote) && !(setting == IdleDemote) && !(setting == IdleScaling))
			{
				return setting == RetiredIdleCheck;
			}
			return true;
		}

		private static bool ReadExtremeSnapshot(Guid scheme, out List<ExtremeSavedValue> values)
		{
			values = new List<ExtremeSavedValue>();
			string value;
			if (!Settings.TryLoadStr("ExtremePowerKnobSnap", out value))
			{
				return false;
			}
			if (value.Length == 0)
			{
				return true;
			}
			if (value.IndexOf('|') >= 0)
			{
				string[] valueParts = value.Split('|');
				Guid result;
				if (valueParts.Length != 3 || valueParts[0] != "2" || !Guid.TryParse(valueParts[1], out result) || result == Guid.Empty || result != scheme)
				{
					return false;
				}
				value = valueParts[2];
			}
			HashSet<Guid> hashSet = new HashSet<Guid>();
			string[] valueParts2 = value.Split(';');
			foreach (string entryText in valueParts2)
			{
				string[] entryTextParts = entryText.Split('=');
				Guid result2;
				if (entryTextParts.Length != 2 || !Guid.TryParse(entryTextParts[0], out result2) || !hashSet.Add(result2) || !IsRetiredIdleKnob(result2))
				{
					return false;
				}
				string[] parts = entryTextParts[1].Split(',');
				uint result3;
				uint result4;
				if (parts.Length != 2 || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out result3) || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out result4))
				{
					return false;
				}
				values.Add(new ExtremeSavedValue
				{
					Setting = result2,
					Ac = result3,
					Dc = result4
				});
			}
			return true;
		}

		private static bool SaveExtremeSnapshot(Guid scheme, List<ExtremeSavedValue> values)
		{
			List<string> list = new List<string>();
			foreach (ExtremeSavedValue value in values)
			{
				list.Add(value.Text);
			}
			string val = ((list.Count == 0) ? "" : ("2|" + scheme.ToString("N") + "|" + string.Join(";", list.ToArray())));
			return Settings.SaveStrDurable("ExtremePowerKnobSnap", val);
		}

		private static bool ForgetDeletedExtremeSnapshot(Guid scheme)
		{
			List<ExtremeSavedValue> values;
			if (ReadExtremeSnapshot(scheme, out values))
			{
				if (values.Count != 0)
				{
					return SaveExtremeSnapshot(scheme, new List<ExtremeSavedValue>());
				}
				return true;
			}
			return false;
		}

		private static bool DropOrphanExtremeSnapshot(Guid configuringScheme)
		{
			string value;
			if (!Settings.TryLoadStr("ExtremePowerKnobSnap", out value) || value.Length == 0)
			{
				return false;
			}
			if (value.IndexOf('|') < 0)
			{
				return false;
			}
			string[] valueParts = value.Split('|');
			Guid result;
			if (valueParts.Length != 3 || valueParts[0] != "2" || !Guid.TryParse(valueParts[1], out result) || result == Guid.Empty || result == configuringScheme)
			{
				return false;
			}
			List<Guid> list;
			if (!TryEnumerateSchemes(out list) || list.Contains(result))
			{
				return false;
			}
			if (!SaveExtremeSnapshot(result, new List<ExtremeSavedValue>()))
			{
				return false;
			}
			Logger.Log(Lang.T("log.powerplanschemes.extremeOrphan"));
			return true;
		}

		private static bool PrepareExtremeKnobs(Guid scheme)
		{
			bool restoreExtremeKnobsSucceeded = RestoreExtremeKnobs(scheme);
			if (!restoreExtremeKnobsSucceeded && DropOrphanExtremeSnapshot(scheme))
			{
				restoreExtremeKnobsSucceeded = RestoreExtremeKnobs(scheme);
			}
			extremeTunePending = !restoreExtremeKnobsSucceeded;
			return restoreExtremeKnobsSucceeded;
		}

		private static bool RestoreExtremeKnobs(Guid scheme)
		{
			List<ExtremeSavedValue> values;
			if (!ReadExtremeSnapshot(scheme, out values))
			{
				return false;
			}
			if (values.Count == 0)
			{
				return true;
			}
			List<ExtremeSavedValue> list = new List<ExtremeSavedValue>();
			bool result = true;
			foreach (ExtremeSavedValue item in values)
			{
				if (!RestoreExtremeIndex(scheme, item.Setting, true, item.Ac) || !RestoreExtremeIndex(scheme, item.Setting, false, item.Dc))
				{
					list.Add(item);
					result = false;
				}
			}
			Guid? guid = Current();
			if (!guid.HasValue || (guid.Value == scheme && !Set(scheme)))
			{
				return false;
			}
			if (SaveExtremeSnapshot(scheme, list))
			{
				return result;
			}
			return false;
		}

		private static bool RestoreExtremeIndex(Guid scheme, Guid setting, bool ac, uint original)
		{
			uint? extremeIndex = ReadExtremeIndex(scheme, setting, ac);
			if (!extremeIndex.HasValue)
			{
				return false;
			}
			if (extremeIndex.Value == original)
			{
				return true;
			}
			if (!(ac ? WriteAc(scheme, SubProcessor, setting, original) : WriteDc(scheme, SubProcessor, setting, original)))
			{
				return false;
			}
			return ReadExtremeIndex(scheme, setting, ac) == original;
		}

		private static uint? ReadExtremeIndex(Guid scheme, Guid setting, bool ac)
		{
			uint value;
			if (!(ac ? ReadAc(scheme, SubProcessor, setting, out value) : ReadDc(scheme, SubProcessor, setting, out value)))
			{
				return null;
			}
			return value;
		}

		internal static bool RestoreLegacyCpuIdle()
		{
			lock (lk)
			{
				try
				{
					if (!LoadCpuIdleReceipt())
					{
						return false;
					}
					CpuIdleReceipt cpuIdleReceipt = PowerPlan.cpuIdleReceipt;
					if (cpuIdleReceipt == null)
					{
						return true;
					}
					if (cpuIdleReceipt.Phase == CpuIdlePhase.Settled)
					{
						return ClearCpuIdleReceipt();
					}
					uint ac;
					uint dc;
					if (cpuIdleReceipt.PendingMask < 0)
					{
						if (!CpuIdleReadValues(cpuIdleReceipt.Scheme, out ac, out dc)) return false;
						if (ac == 0 && dc == 0)
						{
							cpuIdleReceipt.PendingMask = 0;
						}
						else if (cpuIdleReceipt.Phase == CpuIdlePhase.Owned && ac == 1 && dc == 1)
						{
							cpuIdleReceipt.PendingMask = 3;
						}
						else if (ac == 1 || dc == 1)
						{
							// Old Prepared/Restoring records cannot distinguish a partial restore from an external edit.
							// Preserve the debt without claiming either remaining 1.
							return false;
						}
						else
						{
							cpuIdleReceipt.Phase = CpuIdlePhase.Settled;
							return ClearCpuIdleReceipt();
						}
						cpuIdleReceipt.Phase = CpuIdlePhase.Restoring;
					}
					// Persist and verify authority before writing either source, including a prior failed progress save.
					if (!WriteCpuIdleLedgerVerified(cpuIdleReceipt.Text(CpuIdlePhase.Restoring))
						|| !RestoreCpuIdleSide(cpuIdleReceipt, true)
						|| !RestoreCpuIdleSide(cpuIdleReceipt, false)
						|| !CpuIdleReadValues(cpuIdleReceipt.Scheme, out ac, out dc)) return false;
					// A completed source may since have been changed by another owner. Do not reapply that policy.
					if (ac == 0 && dc == 0 && !ReapplyCpuIdle(cpuIdleReceipt.Scheme))
					{
						return false;
					}
					cpuIdleReceipt.Phase = CpuIdlePhase.Settled;
					return ClearCpuIdleReceipt();
				}
				catch
				{
					return false;
				}
			}
		}

		private static bool LoadCpuIdleReceipt()
		{
			if (PowerPlan.cpuIdleReceipt != null)
			{
				return true;
			}
			string message;
			if (!CpuIdleReadLedger(out message) || message == null)
			{
				return false;
			}
			if (message.Length == 0)
			{
				return true;
			}
			if (message.Length > 80)
			{
				return false;
			}
			string[] messageParts = message.Split('|');
			Guid result;
			bool legacy = messageParts.Length == 4 && (messageParts[0] == "1" || messageParts[0] == "2");
			bool current = messageParts.Length == 5 && messageParts[0] == "3";
			if ((!legacy && !current) || messageParts[2] != "0" || !Guid.TryParseExact(messageParts[1], "D", out result) || result == Guid.Empty
				|| (messageParts[0] == "1" && messageParts[3] != "A" && messageParts[3] != "R" && messageParts[3] != "S")
				|| (messageParts[0] == "2" && messageParts[3] != "P" && messageParts[3] != "O" && messageParts[3] != "R" && messageParts[3] != "S")
				|| (current && ((messageParts[3] != "R" && messageParts[3] != "S") || messageParts[4].Length != 1 || messageParts[4][0] < '0' || messageParts[4][0] > '3' || (messageParts[3] == "S" && messageParts[4] != "0"))))
			{
				return false;
			}
			CpuIdlePhase cpuIdlePhase = ((messageParts[3] == "R") ? CpuIdlePhase.Restoring : ((messageParts[3] == "S") ? CpuIdlePhase.Settled : ((messageParts[3] == "O") ? CpuIdlePhase.Owned : CpuIdlePhase.Prepared)));
			CpuIdleReceipt cpuIdleReceipt = new CpuIdleReceipt();
			cpuIdleReceipt.Scheme = result;
			cpuIdleReceipt.Phase = cpuIdlePhase;
			cpuIdleReceipt.PendingMask = current ? messageParts[4][0] - '0' : -1;
			PowerPlan.cpuIdleReceipt = cpuIdleReceipt;
			return true;
		}

		private static bool ClearCpuIdleReceipt()
		{
			if (cpuIdleReceipt == null || cpuIdleReceipt.Phase != CpuIdlePhase.Settled)
			{
				return false;
			}
			if (!WriteCpuIdleLedgerVerified(cpuIdleReceipt.Text(CpuIdlePhase.Settled)))
			{
				return false;
			}
			if (!WriteCpuIdleLedgerVerified(""))
			{
				return false;
			}
			cpuIdleReceipt = null;
			return true;
		}

		private static bool WriteCpuIdleLedgerVerified(string text)
		{
			string message;
			if (CpuIdleWriteLedger(text) && CpuIdleReadLedger(out message))
			{
				return string.Equals(message, text, StringComparison.Ordinal);
			}
			return false;
		}

		private static bool ReapplyCpuIdle(Guid scheme)
		{
			Guid? guid = CpuIdleCurrentScheme();
			if (!guid.HasValue || guid.Value == Guid.Empty)
			{
				return false;
			}
			if (guid.Value != scheme)
			{
				return true;
			}
			uint ac;
			uint dc;
			if (!CpuIdleReadValues(scheme, out ac, out dc))
			{
				return false;
			}
			if (ac != 0 || dc != 0) return true;
			guid = CpuIdleCurrentScheme();
			if (!guid.HasValue || guid.Value == Guid.Empty)
			{
				return false;
			}
			if (guid.Value != scheme)
			{
				return true;
			}
			if (!CpuIdleSetActive(scheme))
			{
				return false;
			}
			guid = CpuIdleCurrentScheme();
			if (!guid.HasValue || guid.Value != scheme || !CpuIdleReadValues(scheme, out ac, out dc))
			{
				return false;
			}
			return true;
		}

		private static bool RestoreCpuIdleSide(CpuIdleReceipt receipt, bool ac)
		{
			int bit = ac ? 1 : 2;
			if ((receipt.PendingMask & bit) == 0) return true;
			uint value;
			if (!CpuIdleReadValue(receipt.Scheme, ac, out value)) return false;
			if (value == 1)
			{
				if (!(ac ? WriteAc(receipt.Scheme, SubProcessor, IdleDisableSet, 0u)
					: WriteDc(receipt.Scheme, SubProcessor, IdleDisableSet, 0u))) return false;
				if (!CpuIdleReadValue(receipt.Scheme, ac, out value) || value == 1) return false;
			}
			// Zero is restored; any other non-owned value belongs to an external writer.
			// Commit this source's release before the next source, so a later 1 is never reclaimed.
			receipt.PendingMask &= ~bit;
			return WriteCpuIdleLedgerVerified(receipt.Text(CpuIdlePhase.Restoring));
		}

		private static Guid? CpuIdleCurrentScheme()
		{
			return Current();
		}

		private static bool CpuIdleReadValue(Guid scheme, bool ac, out uint value)
		{
			return ac ? ReadAc(scheme, SubProcessor, IdleDisableSet, out value)
				: ReadDc(scheme, SubProcessor, IdleDisableSet, out value);
		}

		private static bool CpuIdleReadValues(Guid scheme, out uint ac, out uint dc)
		{
			dc = 0u;
			return CpuIdleReadValue(scheme, true, out ac) && CpuIdleReadValue(scheme, false, out dc);
		}

		private static bool CpuIdleSetActive(Guid scheme)
		{
			return Set(scheme);
		}

		private static bool CpuIdleReadLedger(out string text)
		{
			return Settings.TryLoadStr("CpuIdleStateV1", out text);
		}

		private static bool CpuIdleWriteLedger(string text)
		{
			return Settings.SaveStrDurable("CpuIdleStateV1", text);
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
                // Unsupported desktop CPU/platform evidence keeps the real OEM/user plan.
                Logger.Info("POWER source=OEM/base reason=" + selection.Reason);
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
				if (!(plan == Balanced) && !(plan == HighPerf) && !(plan == Ultimate))
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
				if (!(record.Plan == scheme))
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
			if (!ReadManagedPolicy(out record) || record == null || !(record.Plan == managed) || !ManagedPlans.Exists(record.Base) || ManagedPlans.IsOwned(record.Base))
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
            // Tune only an owned inactive candidate through the selected device route.
            if (HandheldBypass) return false;
            bool legacy = UsesLegacyManagedPolicy();
            if (!record.CurrentTemplate || (record.Source == Legacy3110Source) != legacy || !TuneTarget(scheme, aggressive, legacy))
                return PowerApplyFailed("managed-parameters", "Managed source or local adaptation verification failed");
            if (legacy && !SyncLegacy3110DisplayFeel(record.Base, scheme))
                return PowerApplyFailed("managed-display-preferences", "3.1.1.0 display preference copy or verification failed");
            // Recheck chassis policy before activation; retain ownership of a candidate prepared under an older route.
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

        [DllImport("powrprof.dll", EntryPoint = "PowerEnumerate")]
		private static extern uint EnumerateProcessorSetting(IntPtr root, ref Guid scheme, ref Guid sub, uint access, uint index, byte[] buffer, ref uint size);

		[DllImport("powrprof.dll")]
		private static extern uint PowerReadValueMin(IntPtr root, ref Guid sub, ref Guid setting, out uint min);

		[DllImport("powrprof.dll")]
		private static extern uint PowerReadValueMax(IntPtr root, ref Guid sub, ref Guid setting, out uint max);

		[DllImport("powrprof.dll")]
		private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);

		[DllImport("powrprof.dll")]
		private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid guid);

		[DllImport("powrprof.dll")]
		private static extern uint PowerDuplicateScheme(IntPtr root, ref Guid src, ref IntPtr dest);

		[DllImport("powrprof.dll")]
		private static extern uint PowerDeleteScheme(IntPtr root, ref Guid guid);

		[DllImport("powrprof.dll")]
		private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subgroup, uint accessFlags, uint index, byte[] buffer, ref uint size);

		[DllImport("powrprof.dll")]
		private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, IntPtr buffer, ref uint size);

		[DllImport("powrprof.dll", EntryPoint = "PowerReadFriendlyName")]
		private static extern uint PowerReadFriendlyNameBuf(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, ref uint size);

		[DllImport("powrprof.dll")]
		private static extern uint PowerWriteFriendlyName(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);

		[DllImport("powrprof.dll")]
		private static extern uint PowerWriteDescription(IntPtr root, ref Guid scheme, IntPtr subgroup, IntPtr setting, byte[] buffer, uint size);

		[DllImport("kernel32.dll")]
		private static extern IntPtr LocalFree(IntPtr h);

		[DllImport("powrprof.dll")]
		private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);

		[DllImport("powrprof.dll")]
		private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);

		[DllImport("powrprof.dll")]
		private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

		[DllImport("powrprof.dll")]
		private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);

        private static bool TuneTarget(Guid g, bool aggressive, bool legacy)
        {
            return legacy ? TuneLegacy3110Target(g, aggressive) : TunePresetTarget(g, aggressive);
        }

		private static void ReadAutonomousScaling(Guid scheme, out bool? ac, out bool? dc)
		{
			ac = null;
			dc = null;
			try
			{
				ProcessorPowerPlatform.Interface current = ProcessorPowerPlatform.Current;
				uint value;
				uint? requested = (ReadAc(scheme, SubProcessor, PerfAutonomous, out value) ? new uint?(value) : null);
				uint? requested2 = (ReadDc(scheme, SubProcessor, PerfAutonomous, out value) ? new uint?(value) : null);
				ac = ProcessorPowerPlatform.AutonomousMinimum(current, requested);
				dc = ProcessorPowerPlatform.AutonomousMinimum(current, requested2);
			}
			catch
			{
			}
		}

		internal static bool ClearLegacyIdleDisableOnce()
		{
			lock (lk)
			{
				if (!RestoreLegacyCpuIdle() || LegacyCpuIdleHasResidue)
				{
					return false;
				}
				return ClearLegacyIdleDisableOnceCore();
			}
		}

		private static bool ClearLegacyIdleDisableOnceCore()
		{
            // Restore existing receipts first; an old managed-plan name cannot authorize a new handheld repair.
            if (HandheldBypass) return true;
            ManagedPolicyRecord currentRecord;
            if (ReadManagedPolicy(out currentRecord) && currentRecord != null && currentRecord.CurrentTemplate) return true;
			if (Settings.Load("IdleDisableCleared", false))
			{
				return true;
			}
			try
			{
				Guid guid = ManagedPlanGuid();
				if (guid == Guid.Empty || !SettingPresent(guid, SubProcessor, IdleDisableSet))
				{
					Settings.Save("IdleDisableCleared", true);
					return true;
				}
				if (!WritePair(guid, SubProcessor, IdleDisableSet, 0u, 0u))
				{
					return false;
				}
				Guid? guid2 = Current();
				if (guid2.HasValue && guid2.Value == guid && !Set(guid))
				{
					return false;
				}
				Settings.Save("IdleDisableCleared", true);
				Logger.Log(Lang.T("log.powerplanschemes.47"));
				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static bool ReadAc(Guid scheme, Guid sub, Guid setting, out uint value)
		{
			Guid sub2 = sub;
			Guid setting2 = setting;
			return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, out value) == 0;
		}

		internal static uint? ActivePersonality()
		{
			Guid? guid = Current();
			uint value;
			if (!guid.HasValue || !ReadAc(guid.Value, SubNone, Personality, out value))
			{
				return null;
			}
			return value;
		}

		internal static bool ReadDc(Guid scheme, Guid sub, Guid setting, out uint value)
		{
			Guid sub2 = sub;
			Guid setting2 = setting;
			return PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, out value) == 0;
		}

		internal static bool WriteDc(Guid scheme, Guid sub, Guid setting, uint value)
		{
			Guid sub2 = sub;
			Guid setting2 = setting;
			return PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, value) == 0;
		}

		internal static bool WriteAc(Guid scheme, Guid sub, Guid setting, uint value)
		{
			Guid sub2 = sub;
			Guid setting2 = setting;
			return PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, value) == 0;
		}

		private static bool SettingPresent(Guid scheme, Guid sub, Guid setting)
		{
			Guid sub2 = sub;
			Guid setting2 = setting;
			uint value;
			return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, out value) == 0;
		}

		private static uint Clamp(Guid sub, Guid setting, uint v)
		{
			Guid sub2 = sub;
			Guid setting2 = setting;
			uint min;
			if (PowerReadValueMin(IntPtr.Zero, ref sub2, ref setting2, out min) != 0)
			{
				return v;
			}
			sub2 = sub;
			setting2 = setting;
			uint max;
			if (PowerReadValueMax(IntPtr.Zero, ref sub2, ref setting2, out max) != 0)
			{
				return v;
			}
			if (min > max)
			{
				return v;
			}
			if (v < min)
			{
				return min;
			}
			if (v > max)
			{
				return max;
			}
			return v;
		}

		private static bool WritePair(Guid scheme, Guid sub, Guid setting, uint ac, uint dc)
		{
			uint code;
			return WritePair(scheme, sub, setting, ac, dc, out code);
		}

		private static bool WritePair(Guid scheme, Guid sub, Guid setting, uint ac, uint dc, out uint code)
		{
			Guid sub2 = sub;
			Guid setting2 = setting;
			code = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, ac);
			if (code == 87)
			{
				uint acLimit = Clamp(sub, setting, ac);
				if (acLimit != ac)
				{
					sub2 = sub;
					setting2 = setting;
					code = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, acLimit);
				}
			}
			if (code != 0)
			{
				return false;
			}
			sub2 = sub;
			setting2 = setting;
			code = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, dc);
			if (code == 87)
			{
				uint dcLimit = Clamp(sub, setting, dc);
				if (dcLimit != dc)
				{
					sub2 = sub;
					setting2 = setting;
					code = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub2, ref setting2, dcLimit);
				}
			}
			return code == 0;
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

		// Outside sessions and after crash recovery, remove inactive managed plans absent from all ownership keys.
		// Session cleanup touches only the currently recorded plan and its predecessor.
		private static int orphanSweepDone;

		internal static void SweepUntrackedManagedPlansOnce()
		{
			if (System.Threading.Interlocked.Exchange(ref orphanSweepDone, 1) != 0) return;
			lock (lk)
			{
				if (HandheldBypass || active || LaboratoryHasResidue || IdlePolicyHasResidue || LegacyCpuIdleHasResidue) return;
				// An unfinished creation receipt means ownership is in flux; leave everything alone.
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
					Logger.Info("POWER event=orphan-sweep deleted=" + deleted + " failed=" + failed);
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

		private static Guid? Current()
		{
			IntPtr guid;
			if (PowerGetActiveScheme(IntPtr.Zero, out guid) != 0 || guid == IntPtr.Zero)
			{
				return null;
			}
			try
			{
				return (Guid)Marshal.PtrToStructure(guid, typeof(Guid));
			}
			catch
			{
				return null;
			}
			finally
			{
				LocalFree(guid);
			}
		}

		private static bool Set(Guid g)
		{
			uint code = PowerSetActiveScheme(IntPtr.Zero, ref g);
			if (code != 0)
			{
				if (string.IsNullOrEmpty(applyFailureReason)) applyFailurePermanent = code == 5 || code == 50 || code == 120;
				Guid? observed = Current();
				return PlanActivationFailed(g, "api=PowerSetActiveScheme code=" + code + " observed=" + (observed.HasValue ? observed.Value.ToString("D") : "unreadable"));
			}
			Guid? guid = Current();
			return guid == g || PlanActivationFailed(g, "api=PowerSetActiveScheme code=0 observed=" + (guid.HasValue ? guid.Value.ToString("D") : "unreadable"));
		}

        private static bool PlanActivationFailed(Guid targetPlan, string detail)
        {
            string failure = "stage=activation target=" + targetPlan.ToString("D") + " " + detail;
            if (string.IsNullOrEmpty(applyFailureReason))
                applyFailureReason = failure;
            // Recovery errors report their own native failure without replacing the original apply failure.
            Logger.Warn("POWER phase=native " + failure);
            return false;
        }

		private static bool SchemeUsable(Guid g)
		{
			uint size = 0u;
			uint powerReadFriendlyNameResult = PowerReadFriendlyName(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
			if (powerReadFriendlyNameResult != 0)
			{
				return powerReadFriendlyNameResult == 234;
			}
			return true;
		}

        private static ManagedPowerSchemeEvidence ReadManagedSchemeEvidenceNative(Guid scheme)
        {
            try
            {
                uint size = 0;
                return PowerPresetWriter.ClassifySchemeEvidence(PowerReadFriendlyName(IntPtr.Zero,
                    ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size));
            }
            catch { return PowerPresetWriter.ClassifySchemeEvidence(31); }
        }

        private static ManagedPowerSchemeEvidence ReadManagedSchemeEvidence(IManagedPowerPlans plans, Guid scheme)
        {
            var evidence = plans as IManagedPowerSchemeEvidence;
            return evidence != null ? evidence.SchemeEvidence(scheme)
                : PowerPresetWriter.ClassifySchemeEvidence(plans.Exists(scheme) ? 0u : 2u);
        }

        private static bool ManagedSchemeEvidenceAvailable(ManagedPowerSchemeEvidence evidence, Guid scheme)
        {
            if (evidence.State != ManagedPowerSchemeState.Unavailable) return true;
            applyFailurePermanent |= PowerPresetWriter.PermanentNativeFailure(evidence.NativeCode);
            return PowerApplyFailed("plan-existence", "Power-plan existence unavailable code=" + evidence.NativeCode
                + " scheme=" + scheme.ToString("D"));
        }

		private static string ReadName(Guid g)
		{
			try
			{
				uint size = 0u;
				uint powerReadFriendlyNameBufResult = PowerReadFriendlyNameBuf(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, null, ref size);
				if ((powerReadFriendlyNameBufResult != 0 && powerReadFriendlyNameBufResult != 234) || size == 0 || size > 4096)
				{
					return "";
				}
				byte[] rawBytes = new byte[size];
				if (PowerReadFriendlyNameBuf(IntPtr.Zero, ref g, IntPtr.Zero, IntPtr.Zero, rawBytes, ref size) != 0)
				{
					return "";
				}
				string @string = Encoding.Unicode.GetString(rawBytes);
				char[] trimChars = new char[1];
				return @string.TrimEnd(trimChars);
			}
			catch
			{
				return "";
			}
		}

		private static bool WriteName(Guid g, string title, string note)
		{
			try
			{
				byte[] bytes = Encoding.Unicode.GetBytes(title + "\0");
				Guid scheme = g;
				if (PowerWriteFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, bytes, (uint)bytes.Length) != 0)
				{
					return false;
				}
				byte[] bytes2 = Encoding.Unicode.GetBytes(note + "\0");
				scheme = g;
				PowerWriteDescription(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, bytes2, (uint)bytes2.Length);
				return true;
			}
			catch
			{
				return false;
			}
		}

		private static List<Guid> EnumerateSchemes()
		{
			List<Guid> list;
			TryEnumerateSchemes(out list);
			return list;
		}

		private static bool TryEnumerateSchemes(out List<Guid> list)
		{
			list = new List<Guid>();
			try
			{
				uint processedCount = 0u;
				while (processedCount < 128)
				{
					uint size = 16u;
					byte[] rawBytes = new byte[16];
					switch (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16u, processedCount, rawBytes, ref size))
					{
					case 259u:
						return true;
					case 0u:
						if (size == 16)
						{
							Guid guid = new Guid(rawBytes);
							if (guid == Guid.Empty || list.Contains(guid))
							{
								return false;
							}
							list.Add(guid);
							processedCount++;
							break;
						}
						goto default;
					default:
						return false;
					}
				}
			}
			catch
			{
			}
			return false;
		}

		internal static void SyncDisplayFeel(Guid src, Guid dst)
		{
			Guid[] guidItems = new Guid[3] { VideoBrightness, VideoDimBrightness, VideoAdaptive };
			Guid[] guidItems2 = guidItems;
			foreach (Guid guid in guidItems2)
			{
				Guid scheme = src;
				Guid scheme2 = dst;
				Guid sub = SubVideo;
				Guid setting = guid;
				uint value;
				if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value) == 0)
				{
					PowerWriteACValueIndex(IntPtr.Zero, ref scheme2, ref sub, ref setting, value);
				}
				if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out value) == 0)
				{
					PowerWriteDCValueIndex(IntPtr.Zero, ref scheme2, ref sub, ref setting, value);
				}
			}
		}

		private static bool Duplicate(Guid src, out Guid created)
		{
			created = Guid.Empty;
			IntPtr dest = IntPtr.Zero;
			Guid src2 = src;
			if (PowerDuplicateScheme(IntPtr.Zero, ref src2, ref dest) != 0 || dest == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				created = (Guid)Marshal.PtrToStructure(dest, typeof(Guid));
			}
			catch
			{
				return false;
			}
			finally
			{
				LocalFree(dest);
			}
			return created != Guid.Empty;
		}
	}
}

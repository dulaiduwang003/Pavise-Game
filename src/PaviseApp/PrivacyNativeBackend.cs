using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using Microsoft.Win32;

namespace PaviseApp
{
	internal sealed class PrivacyNativeBackend : IPrivacyBackend, IPrivacyRecoveryBackend
	{
		private sealed class RegSetting
		{
			internal string Target;

			internal int Value;

			internal RegSetting(string hive, string path, string name, int value)
			{
				Target = R(hive, path, name);
				Value = value;
			}
		}

		private struct PrivacyServiceStatus
		{
			internal int ServiceType;

			internal int CurrentState;

			internal int ControlsAccepted;

			internal int Win32ExitCode;

			internal int ServiceSpecificExitCode;

			internal int CheckPoint;

			internal int WaitHint;
		}

		private const string AppPrivacyPath = "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy";

		private static string R(string hive, string path, string name)
		{
			return "reg|" + hive + "|" + path + "|" + name;
		}

		private static string CurrentMachine()
		{
			string input;
			using (RegistryKey registryKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
			{
				using (RegistryKey registryKey2 = registryKey.OpenSubKey("SOFTWARE\\Microsoft\\Cryptography"))
				{
					input = ((registryKey2 == null) ? null : (registryKey2.GetValue("MachineGuid", null) as string));
				}
			}
			Guid result;
			if (!Guid.TryParse(input, out result) || result == Guid.Empty)
			{
				throw new InvalidOperationException("Privacy machine identity unavailable");
			}
			return result.ToString("N");
		}

		private static string CurrentUser()
		{
			string value;
			using (WindowsIdentity windowsIdentity = WindowsIdentity.GetCurrent())
			{
				value = ((windowsIdentity.User == null) ? null : windowsIdentity.User.Value);
			}
			if (string.IsNullOrEmpty(value) || !Regex.IsMatch(value, "\\AS-1-(?:\\d+-){1,14}\\d+\\z", RegexOptions.CultureInvariant))
			{
				throw new InvalidOperationException("Privacy user identity unavailable");
			}
			return new SecurityIdentifier(value).Value;
		}

		private static string ScopeTarget(string target)
		{
			return "ctx|" + CurrentMachine() + "|" + (target.StartsWith("reg|CU|", StringComparison.Ordinal) ? CurrentUser() : "system") + "|" + target;
		}

		private static string LocalTarget(string target)
		{
			if (target == null || !target.StartsWith("ctx|", StringComparison.Ordinal))
			{
				throw new ArgumentException("Privacy target has no device identity");
			}
			string canonicalTargetResult = PrivacyControl.CanonicalTarget(target);
			string[] targetParts = target.Split(new char[1] { '|' }, 4);
			string currentUserResult = (canonicalTargetResult.StartsWith("reg|CU|", StringComparison.Ordinal) ? CurrentUser() : "system");
			if (!string.Equals(targetParts[1], CurrentMachine(), StringComparison.OrdinalIgnoreCase) || targetParts[2] != currentUserResult)
			{
				throw new NotSupportedException(PerformancePolicy.Text("恢复记录属于另一台电脑或另一账户，已保留记录并拒绝修改。", "This recovery record belongs to another device or account. It is retained without changing this system."));
			}
			return canonicalTargetResult;
		}

		private static RegSetting[] SettingsFor(string action, PrivacyPlatform platform)
		{
			switch (action)
			{
			case "EnvPrivacyDiagnosticsV1":
			{
				List<RegSetting> list = new List<RegSetting>();
				list.Add(new RegSetting("LM", platform.PolicyCapable ? "SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection" : "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\DataCollection", "AllowTelemetry", (!platform.Enterprise) ? 1 : 0));
				List<RegSetting> list2 = list;
				if (platform.PolicyCapable && platform.Build >= 22000)
				{
					list2.Add(new RegSetting("LM", "SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "LimitDiagnosticLogCollection", 1));
					list2.Add(new RegSetting("LM", "SOFTWARE\\Policies\\Microsoft\\Windows\\DataCollection", "LimitDumpCollection", 1));
				}
				return list2.ToArray();
			}
			case "EnvPrivacyServiceV1":
				return new RegSetting[0];
			case "EnvPrivacyCeipV1":
				return new RegSetting[1]
				{
					new RegSetting("LM", platform.PolicyCapable ? "SOFTWARE\\Policies\\Microsoft\\SQMClient\\Windows" : "SOFTWARE\\Microsoft\\SQMClient\\Windows", "CEIPEnable", 0)
				};
			case "EnvPrivacyAppraiserV1":
				if (!platform.PolicyCapable)
				{
					return new RegSetting[0];
				}
				return new RegSetting[1]
				{
					new RegSetting("LM", "SOFTWARE\\Policies\\Microsoft\\Windows\\AppCompat", "DisableInventory", 1)
				};
			case "EnvPrivacyErrorReportsV1":
			{
				string path = (platform.PolicyCapable ? "SOFTWARE\\Policies\\Microsoft\\Windows\\Windows Error Reporting" : "SOFTWARE\\Microsoft\\Windows\\Windows Error Reporting");
				return new RegSetting[2]
				{
					new RegSetting("LM", path, "Disabled", 1),
					new RegSetting("LM", path, "DontSendAdditionalData", 1)
				};
			}
			case "EnvPrivacySecurityReportsV1":
				return new RegSetting[1]
				{
					new RegSetting("LM", "SOFTWARE\\Policies\\Microsoft\\MRT", "DontReportInfectionInformation", 1)
				};
			case "EnvPrivacyInputV1":
			{
				List<RegSetting> list3 = new List<RegSetting>();
				list3.Add(new RegSetting("CU", "SOFTWARE\\Microsoft\\Input\\TIPC", "Enabled", 0));
				List<RegSetting> list4 = list3;
				if (platform.PolicyCapable)
				{
					list4.Add(new RegSetting("LM", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\TextInput", "AllowLinguisticDataCollection", 0));
					list4.Add(new RegSetting("LM", "SOFTWARE\\Policies\\Microsoft\\Windows\\TabletPC", "PreventHandwritingDataSharing", 1));
					list4.Add(new RegSetting("LM", "SOFTWARE\\Policies\\Microsoft\\Windows\\HandwritingErrorReports", "PreventHandwritingErrorReports", 1));
				}
				return list4.ToArray();
			}
			case "EnvPrivacyPersonalizedV1":
				return new RegSetting[2]
				{
					new RegSetting("CU", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
					new RegSetting("CU", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AdvertisingInfo", "Enabled", 0)
				};
			case "EnvPrivacyCompatibilityV1":
				if (!platform.PolicyCapable)
				{
					return new RegSetting[0];
				}
				return new RegSetting[1]
				{
					new RegSetting("LM", "SOFTWARE\\Policies\\Microsoft\\Windows\\AppCompat", "DisablePCA", 1)
				};
			case "EnvPrivacyNtfsMemoryV1":
				return new RegSetting[1]
				{
					new RegSetting("LM", "SYSTEM\\CurrentControlSet\\Control\\FileSystem", "NtfsMemoryUsage", 2)
				};
			case "EnvPrivacyBackgroundGlobalV1":
				if (!platform.PolicyCapable)
				{
					return new RegSetting[1]
					{
						new RegSetting("CU", "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\BackgroundAccessApplications", "GlobalUserDisabled", 1)
					};
				}
				return new RegSetting[1]
				{
					new RegSetting("LM", "SOFTWARE\\Policies\\Microsoft\\Windows\\AppPrivacy", "LetAppsRunInBackground", 2)
				};
			default:
				return new RegSetting[0];
			}
		}

		private static PrivacyPlatform Platform()
		{
			using (RegistryKey registryKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
			{
				using (RegistryKey registryKey2 = registryKey.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion"))
				{
					if (registryKey2 == null)
					{
						throw new InvalidOperationException("Windows version unavailable");
					}
					string getValueOffset = registryKey2.GetValue("EditionID", null) as string;
					int result;
					if (string.IsNullOrEmpty(getValueOffset) || !int.TryParse(Convert.ToString(registryKey2.GetValue("CurrentBuildNumber", null), CultureInfo.InvariantCulture), out result))
					{
						throw new InvalidOperationException("Windows version unavailable");
					}
					PrivacyPlatform privacyPlatform = new PrivacyPlatform();
					privacyPlatform.Build = result;
					privacyPlatform.Edition = getValueOffset.Trim();
					privacyPlatform.Managed = OrganizationManaged();
					return privacyPlatform;
				}
			}
		}

		private static bool OrganizationManaged()
		{
			IntPtr buffer = IntPtr.Zero;
			try
			{
				int status;
				if (PrivacyNetGetJoinInformation(null, out buffer, out status) == 0)
				{
					switch (status)
					{
					case 3:
						return true;
					case 1:
					case 2:
						goto FinishPrivacyWrite;
					}
				}
				throw new InvalidOperationException("Organization membership could not be verified");
				FinishPrivacyWrite:;
			}
			finally
			{
				if (buffer != IntPtr.Zero)
				{
					PrivacyNetApiBufferFree(buffer);
				}
			}
			bool registered;
			if (PrivacyIsDeviceRegisteredWithManagement(out registered, 0u, IntPtr.Zero) != 0)
			{
				throw new InvalidOperationException("Device management registration could not be verified");
			}
			return registered;
		}

        public PrivacyPlan Inspect(string action, string selectedTarget)
        {
            if (PrivacyActionCatalog.Find(action) == null) throw new ArgumentException("Unknown environment action");
            var plan = new PrivacyPlan { Targets = new PrivacyTarget[0] };
            if (PrivacyActionCatalog.IsRetired(action))
            {
                plan.UnavailableReason = "retired";
                plan.Note = PerformancePolicy.Text("此功能已移除，仅恢复旧版本保存的原配置。", "This feature was removed. Only settings saved by an earlier version can be restored.");
                return plan;
            }
            PrivacyPlatform platform = Platform();
            if (platform.Build < 19041)
            { plan.UnavailableReason = "system"; plan.Note = PerformancePolicy.Text("需要 Windows 10 2004 或更新版本。", "Requires Windows 10 version 2004 or newer."); return plan; }
            if (platform.Managed)
            { plan.Managed = true; plan.Note = PerformancePolicy.Text("设备受组织管理，请由管理员调整策略。", "This device is managed by an organization. Contact its administrator."); return plan; }
            if (action == PrivacyActionCatalog.NtfsMemory && !EnvironmentEnhancementEligibility.Allowed)
            { plan.UnavailableReason = "memory"; plan.Note = EnvironmentEnhancementEligibility.Description; return plan; }
            var targets = new List<PrivacyTarget>();
            foreach (RegSetting setting in SettingsFor(action, platform))
                targets.Add(new PrivacyTarget { Key = ScopeTarget(setting.Target), Wanted = "d:" + setting.Value.ToString(CultureInfo.InvariantCulture), RestartRequired = action != PrivacyActionCatalog.BackgroundGlobal });
            plan.Supported = targets.Count != 0;
            plan.Targets = targets.ToArray();
            plan.Note = action == PrivacyActionCatalog.BackgroundGlobal
                ? (platform.PolicyCapable
                    ? PerformancePolicy.Text("配置已写入后，重新启动受影响的 Windows 应用以采用新策略；保留逐应用例外。", "After configuration is written, restart affected Windows apps to adopt the policy; per-app exceptions are retained.")
                    : PerformancePolicy.Text("家庭版采用当前账户的后台设置。更改后重新启动相关应用；若未采用，注销后重新登录。未验证资源管理器重启可以刷新此设置。", "Home uses the current account's background settings. Restart affected apps after changes; if they do not adopt the setting, sign out and back in. Restarting Explorer is not a verified refresh route."))
                : PerformancePolicy.Text("内存充足时扩大文件系统缓存；开启/恢复需重启。", "Expands file-system caching when memory is sufficient. Enabling or restoring requires restart.");
            // The device-wide policy is a default: explicit per-app exceptions take precedence.
            if (action == PrivacyActionCatalog.BackgroundGlobal) plan.Limited = true;
            return plan;
        }

		private static string ValidateTarget(string action, string scopedTarget)
		{
			string localTargetResult = LocalTarget(scopedTarget);
			if (PrivacyActionCatalog.Find(action) == null || string.IsNullOrEmpty(localTargetResult))
			{
				throw new ArgumentException("Unknown privacy target");
			}
			if (action == "EnvPrivacyServiceV1" && localTargetResult == "svc|DiagTrack")
			{
				return localTargetResult;
			}
			if (action == "EnvPrivacyServiceV1" && localTargetResult == "start|DiagTrack")
			{
				return localTargetResult;
			}
			if (action == "EnvPrivacyCompatibilityV1" && (localTargetResult == "svc|PcaSvc" || localTargetResult == "start|PcaSvc"))
			{
				return localTargetResult;
			}
			if (localTargetResult.StartsWith("task|", StringComparison.Ordinal))
			{
				string[] tasksForResult = TasksFor(action);
				foreach (string entryText in tasksForResult)
				{
					if (localTargetResult == "task|" + entryText)
					{
						return localTargetResult;
					}
				}
			}
			string[] textEntries = new string[2] { "Enterprise", "Core" };
			foreach (string edition in textEntries)
			{
				RegSetting[] settingsForResult = SettingsFor(action, new PrivacyPlatform
				{
					Edition = edition,
					Build = int.MaxValue
				});
				foreach (RegSetting regSetting in settingsForResult)
				{
					if (string.Equals(regSetting.Target, localTargetResult, StringComparison.OrdinalIgnoreCase))
					{
						return localTargetResult;
					}
				}
			}
			throw new ArgumentException("Privacy target is outside its action allowlist");
		}

		public string Read(string action, string target)
		{
			target = ValidateTarget(action, target);
			if (target.StartsWith("svc|", StringComparison.Ordinal))
			{
				return ServiceRead(target.Substring(4));
			}
			if (target.StartsWith("start|", StringComparison.Ordinal))
			{
				return ServiceStartRead(target.Substring(6));
			}
			if (target.StartsWith("task|", StringComparison.Ordinal))
			{
				return TaskRead(target.Substring(5));
			}
			return RegistryRead(target);
		}

        public bool Write(string action, string target, string value)
        {
            if (PrivacyActionCatalog.IsRetired(action)
                || (action != PrivacyActionCatalog.NtfsMemory && action != PrivacyActionCatalog.BackgroundGlobal)) return false;
            if (action == PrivacyActionCatalog.NtfsMemory && !EnvironmentEnhancementEligibility.Allowed)
                throw new NotSupportedException(EnvironmentEnhancementEligibility.Description);
            string canonical = ValidateTarget(action, target);
            bool allowed = false;
            foreach (RegSetting setting in SettingsFor(action, Platform()))
                if (setting.Target == canonical && value == "d:" + setting.Value.ToString(CultureInfo.InvariantCulture)) allowed = true;
            if (!allowed) return false;
            return WriteRestoringOrActive(action, target, value);
        }

		private bool WriteRestoringOrActive(string action, string target, string value)
		{
			target = ValidateTarget(action, target);
			if (Platform().Managed)
			{
				throw new NotSupportedException("Organization-managed privacy settings must be changed by the administrator");
			}
			if (target.StartsWith("svc|", StringComparison.Ordinal))
			{
				return ServiceWrite(target.Substring(4), value);
			}
			if (target.StartsWith("start|", StringComparison.Ordinal))
			{
				return ServiceStartWrite(target.Substring(6), value);
			}
			if (target.StartsWith("task|", StringComparison.Ordinal))
			{
				return TaskWrite(target.Substring(5), value);
			}
			return RegistryWrite(target, value);
		}

		public bool Satisfied(string action, string target, string current, string wanted)
		{
			ValidateTarget(action, target);
			return current == wanted;
		}

		public bool Restore(PerformanceActionStore.Receipt receipt)
		{
			if (receipt == null || PrivacyActionCatalog.Find(receipt.Id) == null)
			{
				return false;
			}
			ValidateTarget(receipt.Id, receipt.Target);
			return PerformanceActionStore.Restore(receipt, () => Read(receipt.Id, receipt.Target), delegate(string value)
			{
				PrivacyControl.TrackRebootWrite(receipt.Id, true);
				return WriteRestoringOrActive(receipt.Id, receipt.Target, value);
			}, (string current) => current == receipt.Expected);
		}

		internal static string PackRegistry(object value, RegistryValueKind kind)
		{
			if (value == null)
			{
				return "absent";
			}
			switch (kind)
			{
			case RegistryValueKind.DWord:
				return "d:" + Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
			case RegistryValueKind.QWord:
				return "q:" + Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
			case RegistryValueKind.String:
			case RegistryValueKind.ExpandString:
				return ((kind == RegistryValueKind.String) ? "s:" : "e:") + B64((string)value);
			case RegistryValueKind.MultiString:
			{
				List<string> list = new List<string>();
				string[] valueSnapshot = (string[])value;
				foreach (string value2 in valueSnapshot)
				{
					list.Add(B64(value2));
				}
				return "m:" + string.Join(",", list.ToArray());
			}
			case RegistryValueKind.Binary:
				return "b:" + Convert.ToBase64String((byte[])value);
			default:
				throw new NotSupportedException("Unsupported registry value type");
			}
		}

		private static object UnpackRegistry(string text, out RegistryValueKind kind)
		{
			if (text == "absent")
			{
				kind = RegistryValueKind.None;
				return null;
			}
			if (text == null || text.Length < 2 || text[1] != ':')
			{
				throw new FormatException("Invalid privacy value");
			}
			string substringResult = text.Substring(2);
			switch (text[0])
			{
			case 'd':
				kind = RegistryValueKind.DWord;
				return int.Parse(substringResult, CultureInfo.InvariantCulture);
			case 'q':
				kind = RegistryValueKind.QWord;
				return long.Parse(substringResult, CultureInfo.InvariantCulture);
			case 's':
				kind = RegistryValueKind.String;
				return UnB64(substringResult);
			case 'e':
				kind = RegistryValueKind.ExpandString;
				return UnB64(substringResult);
			case 'b':
				kind = RegistryValueKind.Binary;
				return Convert.FromBase64String(substringResult);
			case 'm':
			{
				kind = RegistryValueKind.MultiString;
				if (substringResult.Length == 0)
				{
					return new string[0];
				}
				string[] substringResultParts = substringResult.Split(',');
				for (int i = 0; i < substringResultParts.Length; i++)
				{
					substringResultParts[i] = UnB64(substringResultParts[i]);
				}
				return substringResultParts;
			}
			default:
				throw new FormatException("Invalid privacy value type");
			}
		}

		internal static string B64(string value)
		{
			return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
		}

		internal static string UnB64(string value)
		{
			return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value));
		}

		private static string[] RegistryParts(string target)
		{
			string[] targetParts = target.Split('|');
			if (targetParts.Length != 4 || targetParts[0] != "reg" || (targetParts[1] != "LM" && targetParts[1] != "CU") || targetParts[2].Length == 0 || targetParts[3].Length == 0 || targetParts[2].IndexOf('\0') >= 0 || targetParts[3].IndexOf('\0') >= 0)
			{
				throw new FormatException("Invalid registry target");
			}
			return targetParts;
		}

		private static string RegistryRead(string target)
		{
			string[] registryPartsResult = RegistryParts(target);
			using (RegistryKey registryKey = RegistryKey.OpenBaseKey((registryPartsResult[1] == "LM") ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64))
			{
				using (RegistryKey registryKey2 = registryKey.OpenSubKey(registryPartsResult[2]))
				{
					if (registryKey2 == null)
					{
						return "absent";
					}
					object value = registryKey2.GetValue(registryPartsResult[3], null, RegistryValueOptions.DoNotExpandEnvironmentNames);
					return PackRegistry(value, (value == null) ? RegistryValueKind.None : registryKey2.GetValueKind(registryPartsResult[3]));
				}
			}
		}

		private static bool RegistryWrite(string target, string text)
		{
			RegistryValueKind kind;
			object unpackRegistryResult = UnpackRegistry(text, out kind);
			string[] registryPartsResult = RegistryParts(target);
			using (RegistryKey registryKey = RegistryKey.OpenBaseKey((registryPartsResult[1] == "LM") ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64))
			{
				if (unpackRegistryResult == null)
				{
					using (RegistryKey registryKey2 = registryKey.OpenSubKey(registryPartsResult[2], true))
					{
						if (registryKey2 != null)
						{
							registryKey2.DeleteValue(registryPartsResult[3], false);
						}
					}
				}
				else
				{
					using (RegistryKey registryKey3 = registryKey.CreateSubKey(registryPartsResult[2]))
					{
						if (registryKey3 == null)
						{
							return false;
						}
						registryKey3.SetValue(registryPartsResult[3], unpackRegistryResult, kind);
						registryKey3.Flush();
					}
				}
			}
			return RegistryRead(target) == text;
		}

		[DllImport("netapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "NetGetJoinInformation", ExactSpelling = true)]
		private static extern uint PrivacyNetGetJoinInformation(string server, out IntPtr buffer, out int status);

		[DllImport("netapi32.dll", EntryPoint = "NetApiBufferFree", ExactSpelling = true)]
		private static extern uint PrivacyNetApiBufferFree(IntPtr buffer);

		[DllImport("MDMRegistration.dll", EntryPoint = "IsDeviceRegisteredWithManagement", ExactSpelling = true)]
		private static extern int PrivacyIsDeviceRegisteredWithManagement([MarshalAs(UnmanagedType.Bool)] out bool registered, uint size, IntPtr user);

		private static void ValidateServiceName(string name)
		{
			if (name != "DiagTrack" && name != "PcaSvc")
			{
				throw new ArgumentException("Unsupported persistent service");
			}
		}

		private static OptionalServiceSnapshot QueryService(string name)
		{
			ValidateServiceName(name);
			OptionalServiceSnapshot snapshot;
			if (!new OptionalServiceControl().TryQuery(name, out snapshot) || snapshot == null)
			{
				throw new InvalidOperationException("Service state unavailable");
			}
			return snapshot;
		}

		private static string ServiceIdentity(string configuration)
		{
			if (configuration == null || !configuration.StartsWith("svc1|", StringComparison.Ordinal))
			{
				throw new FormatException("Unknown service identity");
			}
			int serviceIdentityValue = 5;
			StringBuilder stringBuilder = new StringBuilder("svc1|");
			int processedCount = 0;
			while (serviceIdentityValue < configuration.Length)
			{
				int matchIndex = configuration.IndexOf(':', serviceIdentityValue);
				int result;
				if (matchIndex < serviceIdentityValue || !int.TryParse(configuration.Substring(serviceIdentityValue, matchIndex - serviceIdentityValue), out result) || result < 0 || result > configuration.Length - matchIndex - 2 || configuration[matchIndex + 1 + result] != '|')
				{
					throw new FormatException("Invalid service identity");
				}
				string value = configuration.Substring(serviceIdentityValue, matchIndex + result + 2 - serviceIdentityValue);
				if (processedCount != 1)
				{
					stringBuilder.Append(value);
				}
				serviceIdentityValue = matchIndex + result + 2;
				processedCount++;
			}
			return stringBuilder.ToString();
		}

		private static string ServiceRead(string name)
		{
			ValidateServiceName(name);
			OptionalServiceSnapshot optionalServiceSnapshot = QueryService(name);
			if (!optionalServiceSnapshot.Exists)
			{
				return "missing";
			}
			if (optionalServiceSnapshot.State != 1 && optionalServiceSnapshot.State != 4)
			{
				throw new NotSupportedException("Service is transitioning; retry after it settles");
			}
			return "v:" + optionalServiceSnapshot.State.ToString(CultureInfo.InvariantCulture) + ":" + B64(ServiceIdentity(optionalServiceSnapshot.Configuration));
		}

		private static string throwUnknown()
		{
			throw new InvalidOperationException("Unknown service state");
		}

		private static string ServiceStartRead(string name)
		{
			ValidateServiceName(name);
			OptionalServiceSnapshot optionalServiceSnapshot = QueryService(name);
			if (!optionalServiceSnapshot.Exists)
			{
				return "missing";
			}
			return "c:" + optionalServiceSnapshot.StartType.ToString(CultureInfo.InvariantCulture) + ":" + B64(ServiceIdentity(optionalServiceSnapshot.Configuration));
		}

		private static string ChangeServiceValue(string value, char prefix, int desired)
		{
			string[] parts = (value ?? "").Split(':');
			int result;
			if (parts.Length != 3 || parts[0] != prefix.ToString() || !int.TryParse(parts[1], out result) || string.IsNullOrEmpty(UnB64(parts[2])))
			{
				throw new FormatException("Invalid service snapshot");
			}
			return prefix + ":" + desired.ToString(CultureInfo.InvariantCulture) + ":" + parts[2];
		}

		internal static string ServiceWanted(string current, bool running)
		{
			return ChangeServiceValue(current, 'v', (!running) ? 1 : 4);
		}

		private static string ServiceStartWanted(string current, int startup)
		{
			return ChangeServiceValue(current, 'c', startup);
		}

		private static bool ServiceStartWrite(string name, string desired)
		{
			string[] parts = (desired ?? "").Split(':');
			uint result;
			if (parts.Length != 3 || parts[0] != "c" || !uint.TryParse(parts[1], out result) || result > 4)
			{
				return false;
			}
			string[] serviceStartReadParts = ServiceStartRead(name).Split(':');
			if (serviceStartReadParts.Length != 3 || serviceStartReadParts[2] != parts[2])
			{
				return false;
			}
			if (serviceStartReadParts[1] == parts[1])
			{
				return true;
			}
			IntPtr intPtr = PrivacyOpenSCManager(null, null, 1u);
			if (intPtr == IntPtr.Zero)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
			try
			{
				IntPtr intPtr2 = PrivacyOpenService(intPtr, name, 15u);
				if (intPtr2 == IntPtr.Zero)
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}
				try
				{
					OptionalServiceSnapshot snapshot;
					if (!OptionalServiceControl.TryQueryHandleForPrivacy(intPtr2, out snapshot) || !snapshot.Exists || B64(ServiceIdentity(snapshot.Configuration)) != parts[2] || snapshot.StartType.ToString(CultureInfo.InvariantCulture) != serviceStartReadParts[1] || (result == 4 && snapshot.State != 1))
					{
						return false;
					}
					return PrivacyChangeServiceConfig(intPtr2, uint.MaxValue, result, uint.MaxValue, null, null, IntPtr.Zero, null, null, null, null) && ServiceStartRead(name) == desired;
				}
				finally
				{
					PrivacyCloseServiceHandle(intPtr2);
				}
			}
			finally
			{
				PrivacyCloseServiceHandle(intPtr);
			}
		}

		private static bool ServiceWrite(string name, string desired)
		{
			string[] parts = (desired ?? "").Split(':');
			int result;
			if (parts.Length != 3 || parts[0] != "v" || !int.TryParse(parts[1], out result) || (result != 1 && result != 4))
			{
				return false;
			}
			string[] serviceReadParts = ServiceRead(name).Split(':');
			if (serviceReadParts.Length != 3 || serviceReadParts[2] != parts[2])
			{
				return false;
			}
			if (serviceReadParts[1] == parts[1])
			{
				return true;
			}
			OptionalServiceSnapshot optionalServiceSnapshot = QueryService(name);
			if (B64(ServiceIdentity(optionalServiceSnapshot.Configuration)) != parts[2] || optionalServiceSnapshot.State.ToString(CultureInfo.InvariantCulture) != serviceReadParts[1])
			{
				return false;
			}
			if (result == 1 && (!optionalServiceSnapshot.AcceptsStop || optionalServiceSnapshot.HasActiveDependents))
			{
				throw new NotSupportedException("Service cannot safely stop");
			}
			if (result == 4 && optionalServiceSnapshot.StartType == 4)
			{
				return false;
			}
			IntPtr intPtr = PrivacyOpenSCManager(null, null, 1u);
			if (intPtr == IntPtr.Zero)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}
			try
			{
				IntPtr intPtr2 = PrivacyOpenService(intPtr, name, 61u);
				if (intPtr2 == IntPtr.Zero)
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}
				try
				{
					OptionalServiceSnapshot snapshot;
					if (!OptionalServiceControl.TryQueryHandleForPrivacy(intPtr2, out snapshot) || !snapshot.Exists || B64(ServiceIdentity(snapshot.Configuration)) != parts[2] || snapshot.State != optionalServiceSnapshot.State || snapshot.StartType != optionalServiceSnapshot.StartType || (result == 1 && (!snapshot.AcceptsStop || snapshot.HasActiveDependents)))
					{
						return false;
					}
					PrivacyServiceStatus status;
					if (!((result == 1) ? PrivacyControlService(intPtr2, 1u, out status) : PrivacyStartService(intPtr2, 0u, IntPtr.Zero)))
					{
						return false;
					}
					Stopwatch stopwatch = Stopwatch.StartNew();
					while (stopwatch.ElapsedMilliseconds < 5000)
					{
						if (!PrivacyQueryServiceStatus(intPtr2, out status))
						{
							return false;
						}
						if (status.CurrentState == result)
						{
							try
							{
								return ServiceRead(name) == desired;
							}
							catch
							{
								return false;
							}
						}
						Thread.Sleep(40);
					}
					return false;
				}
				finally
				{
					PrivacyCloseServiceHandle(intPtr2);
				}
			}
			finally
			{
				PrivacyCloseServiceHandle(intPtr);
			}
		}

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenSCManagerW", SetLastError = true)]
		private static extern IntPtr PrivacyOpenSCManager(string machine, string database, uint access);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenServiceW", SetLastError = true)]
		private static extern IntPtr PrivacyOpenService(IntPtr manager, string name, uint access);

		[DllImport("advapi32.dll", EntryPoint = "CloseServiceHandle")]
		private static extern bool PrivacyCloseServiceHandle(IntPtr service);

		[DllImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
		private static extern bool PrivacyControlService(IntPtr service, uint control, out PrivacyServiceStatus status);

		[DllImport("advapi32.dll", EntryPoint = "QueryServiceStatus", SetLastError = true)]
		private static extern bool PrivacyQueryServiceStatus(IntPtr service, out PrivacyServiceStatus status);

		[DllImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
		private static extern bool PrivacyStartService(IntPtr service, uint count, IntPtr arguments);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeServiceConfigW", SetLastError = true)]
		private static extern bool PrivacyChangeServiceConfig(IntPtr service, uint type, uint start, uint error, string binary, string group, IntPtr tag, string dependencies, string account, string password, string display);

		private static string[] TasksFor(string action)
		{
			if (action == "EnvPrivacyCeipV1")
			{
				return new string[3] { "\\Microsoft\\Windows\\Customer Experience Improvement Program\\Consolidator", "\\Microsoft\\Windows\\Customer Experience Improvement Program\\UsbCeip", "\\Microsoft\\Windows\\Customer Experience Improvement Program\\KernelCeipTask" };
			}
			if (action == "EnvPrivacyAppraiserV1")
			{
				return new string[2] { "\\Microsoft\\Windows\\Application Experience\\Microsoft Compatibility Appraiser", "\\Microsoft\\Windows\\Application Experience\\ProgramDataUpdater" };
			}
			return new string[0];
		}

		private static object Com(object instance, string member, BindingFlags flags, params object[] args)
		{
			return instance.GetType().InvokeMember(member, flags, null, instance, args, CultureInfo.InvariantCulture);
		}

		private static void ReleaseCom(object instance)
		{
			if (instance != null && Marshal.IsComObject(instance))
			{
				Marshal.FinalReleaseComObject(instance);
			}
		}

		private static object OpenTask(string path, out object scheduler, out object folder)
		{
			scheduler = (folder = null);
			Type typeFromProgID = Type.GetTypeFromProgID("Schedule.Service");
			if (typeFromProgID == null)
			{
				throw new NotSupportedException("Task Scheduler interface unavailable");
			}
			scheduler = Activator.CreateInstance(typeFromProgID);
			Com(scheduler, "Connect", BindingFlags.InvokeMethod, Type.Missing, Type.Missing, Type.Missing, Type.Missing);
			int matchIndex = path.LastIndexOf('\\');
			folder = Com(scheduler, "GetFolder", BindingFlags.InvokeMethod, path.Substring(0, matchIndex));
			return Com(folder, "GetTask", BindingFlags.InvokeMethod, path.Substring(matchIndex + 1));
		}

		internal static bool MissingTask(Exception error)
		{
			while (error is TargetInvocationException && error.InnerException != null)
			{
				error = error.InnerException;
			}
			if (error != null)
			{
				if (error.HResult != -2147024894)
				{
					return error.HResult == -2147024893;
				}
				return true;
			}
			return false;
		}

		internal static string TaskIdentity(string xml)
		{
			XmlDocument xmlDocument = new XmlDocument();
			xmlDocument.XmlResolver = null;
			XmlDocument xmlDocument2 = xmlDocument;
			using (XmlReader reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
			{
				DtdProcessing = DtdProcessing.Prohibit,
				XmlResolver = null,
				MaxCharactersInDocument = 1048576L
			}))
			{
				xmlDocument2.Load(reader);
			}
			XmlNamespaceManager xmlNamespaceManager = new XmlNamespaceManager(xmlDocument2.NameTable);
			xmlNamespaceManager.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
			XmlNode xmlNode = xmlDocument2.SelectSingleNode("/t:Task/t:Settings/t:Enabled", xmlNamespaceManager);
			if (xmlNode != null)
			{
				xmlNode.ParentNode.RemoveChild(xmlNode);
			}
			using (SHA256 sHA = SHA256.Create())
			{
				return Convert.ToBase64String(sHA.ComputeHash(Encoding.UTF8.GetBytes(xmlDocument2.OuterXml)));
			}
		}

		private static string TaskRead(string path)
		{
			object scheduler = null;
			object folder = null;
			object instance = null;
			try
			{
				instance = OpenTask(path, out scheduler, out folder);
				return "t:" + (Convert.ToBoolean(Com(instance, "Enabled", BindingFlags.GetProperty), CultureInfo.InvariantCulture) ? "1:" : "0:") + TaskIdentity((string)Com(instance, "Xml", BindingFlags.GetProperty));
			}
			catch (Exception error)
			{
				if (MissingTask(error))
				{
					return "missing";
				}
				throw;
			}
			finally
			{
				ReleaseCom(instance);
				ReleaseCom(folder);
				ReleaseCom(scheduler);
			}
		}

		internal static string TaskWanted(string current, bool enabled)
		{
			string[] parts = (current ?? "").Split(':');
			if (parts.Length != 3 || parts[0] != "t" || (parts[1] != "0" && parts[1] != "1") || Convert.FromBase64String(parts[2]).Length != 32)
			{
				throw new FormatException("Unknown task identity");
			}
			return "t:" + (enabled ? "1:" : "0:") + parts[2];
		}

		private static bool TaskWrite(string path, string value)
		{
			string[] parts = (value ?? "").Split(':');
			if (parts.Length != 3 || parts[0] != "t" || (parts[1] != "0" && parts[1] != "1"))
			{
				return false;
			}
			string taskReadResult = TaskRead(path);
			if (taskReadResult == "missing")
			{
				return false;
			}
			string[] taskReadResultParts = taskReadResult.Split(':');
			if (taskReadResultParts.Length != 3 || taskReadResultParts[2] != parts[2])
			{
				return false;
			}
			object scheduler = null;
			object folder = null;
			object instance = null;
			try
			{
				instance = OpenTask(path, out scheduler, out folder);
				if (TaskIdentity((string)Com(instance, "Xml", BindingFlags.GetProperty)) != parts[2])
				{
					return false;
				}
				Com(instance, "Enabled", BindingFlags.SetProperty, parts[1] == "1");
				return TaskRead(path) == value;
			}
			finally
			{
				ReleaseCom(instance);
				ReleaseCom(folder);
				ReleaseCom(scheduler);
			}
		}
	}
}

using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class VbsTweak
	{
		public struct State
		{
			public bool WmiOk;

			public bool VbsRunning;
		}

		private const string DgKey = "SYSTEM\\CurrentControlSet\\Control\\DeviceGuard";

		private const string HvciKey = "SYSTEM\\CurrentControlSet\\Control\\DeviceGuard\\Scenarios\\HypervisorEnforcedCodeIntegrity";

		private static readonly ReversibleReg Vbs = new ReversibleReg(Registry.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\DeviceGuard", "EnableVirtualizationBasedSecurity", RegistryValueKind.DWord, "PrevVbsEnable");

		private static readonly ReversibleReg Hvci = new ReversibleReg(Registry.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\DeviceGuard\\Scenarios\\HypervisorEnforcedCodeIntegrity", "Enabled", RegistryValueKind.DWord, "PrevVbsHvci");

		private static readonly object lk = new object();

		public static bool DisabledByPavise
		{
			get
			{
				return Settings.Load("VbsDisabledByPavise", false);
			}
		}

		internal static bool OwnsState
		{
			get
			{
				if (!DisabledByPavise && !Vbs.HasBackup && !Hvci.HasBackup)
				{
					return Settings.LoadStr("PrevHvLaunch", "").Length > 0;
				}
				return true;
			}
		}

		internal static bool CurrentlyConfiguredOff()
		{
			if (!Vbs.Matches(0) || !Hvci.Matches(0))
			{
				return false;
			}
			bool ok;
			if (ReadHvLaunch(out ok) == "off")
			{
				return ok;
			}
			return false;
		}

		internal static bool VirtualizationInUse(bool vmcomputeInstalled, bool vmmsInstalled, int lsaCfgFlags)
		{
			if (!vmcomputeInstalled && !vmmsInstalled)
			{
				return lsaCfgFlags > 0;
			}
			return true;
		}

		public static bool VirtualizationInUse()
		{
			return VirtualizationInUse(ServiceInstalled("vmcompute"), ServiceInstalled("vmms"), Math.Max(ReadDword("SYSTEM\\CurrentControlSet\\Control\\Lsa", "LsaCfgFlags"), 0));
		}

		private static bool ServiceInstalled(string name)
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\" + name))
				{
					if (registryKey == null)
					{
						return false;
					}
					string value = registryKey.GetValue("ImagePath") as string;
					if (string.IsNullOrEmpty(value))
					{
						return false;
					}
					object value2 = registryKey.GetValue("Start");
					return !(value2 is int) || (int)value2 != 4;
				}
			}
			catch
			{
				return false;
			}
		}

		public static bool BlockedReason(out string reasonKey)
		{
			reasonKey = null;
			if (ReadDword("SOFTWARE\\Policies\\Microsoft\\Windows\\DeviceGuard", "EnableVirtualizationBasedSecurity") == 1)
			{
				reasonKey = "vbs.blocked.policy";
				return true;
			}
			if (ReadDword("SYSTEM\\CurrentControlSet\\Control\\DeviceGuard", "Locked") == 1 || ReadDword("SYSTEM\\CurrentControlSet\\Control\\DeviceGuard\\Scenarios\\HypervisorEnforcedCodeIntegrity", "Locked") == 1)
			{
				reasonKey = "vbs.blocked.lock";
				return true;
			}
			return false;
		}

		private static int ReadDword(string path, string name)
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey(path))
				{
					if (registryKey == null)
					{
						return -1;
					}
					object value = registryKey.GetValue(name);
					return (value is int) ? ((int)value) : (-1);
				}
			}
			catch
			{
				return -1;
			}
		}

		public static State Query()
		{
			State result = default(State);
			try
			{
				using (ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("root\\Microsoft\\Windows\\DeviceGuard", "SELECT VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard"))
				{
					using (ManagementObjectCollection managementObjectCollection = managementObjectSearcher.Get())
					{
						using (ManagementObjectCollection.ManagementObjectEnumerator managementObjectEnumerator = managementObjectCollection.GetEnumerator())
						{
							if (managementObjectEnumerator.MoveNext())
							{
								ManagementObject managementObject = (ManagementObject)managementObjectEnumerator.Current;
								using (managementObject)
								{
									result.WmiOk = true;
									object instance = managementObject["VirtualizationBasedSecurityStatus"];
									if (instance != null)
									{
										result.VbsRunning = Convert.ToInt32(instance) == 2;
									}
								}
							}
						}
					}
				}
			}
			catch
			{
				result.WmiOk = false;
			}
			return result;
		}

		internal static bool CanConfigureForPreset(out string reason)
		{
			reason = PerformancePolicy.Text("VBS 配置或启动身份无法核验", "VBS configuration or boot identity could not be verified");
			try
			{
				if (Native.OsBuild() < 14393 || !BootSessionIdentity.Valid(BootSessionIdentity.Read())) return false;
				string blocked;
				if (BlockedReason(out blocked)) { reason = Lang.T(blocked); return false; }
				// Access failures must not look like absent policy values.
				foreach (string path in new[] { DgKey, HvciKey, "SOFTWARE\\Policies\\Microsoft\\Windows\\DeviceGuard" })
					using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path))
						if (key != null) key.GetValueNames();
				if (!Query().WmiOk) return false;
				bool readable; ReadHvLaunch(out readable); return readable;
			}
			catch { return false; }
		}

		public static bool Disable()
		{
			lock (lk)
			{
				try
				{
					string reasonKey;
					if (BlockedReason(out reasonKey))
					{
						Logger.Log(Lang.T(reasonKey));
						return false;
					}
					if (Settings.LoadStr("PrevHvLaunch", "").Length == 0)
					{
						bool ok;
						string hvLaunch = ReadHvLaunch(out ok);
						if (!ok)
						{
							Logger.Warn(Lang.T("log.vbstweak.1"));
							return false;
						}
						if (!Settings.SaveStrDurable("PrevHvLaunch", hvLaunch))
						{
							return false;
						}
						if (Settings.LoadStr("PrevHvLaunch", "") != hvLaunch)
						{
							return false;
						}
					}
					if (!(Vbs.Apply(0) & Hvci.Apply(0)))
					{
						Vbs.Restore();
						Hvci.Restore();
						if (!DisabledByPavise)
						{
							Settings.SaveStr("PrevHvLaunch", "");
						}
						Logger.Log(Lang.T("log.vbstweak.2"));
						return false;
					}
					int code;
					RunBcd("/set hypervisorlaunchtype off", out code);
					if (code != 0)
					{
						Vbs.Restore();
						Hvci.Restore();
						Logger.Log(Lang.T("log.vbstweak.3") + code + Lang.T("log.vbstweak.4"));
						return false;
					}
					Settings.Save("VbsDisabledByPavise", true);
					if (!Settings.Load("VbsDisabledByPavise", false))
					{
						string normHvLaunchResult = NormHvLaunch(Settings.LoadStr("PrevHvLaunch", "auto"));
						int code2;
						RunBcd((normHvLaunchResult == "__pavise_absent__") ? "/deletevalue hypervisorlaunchtype" : ("/set hypervisorlaunchtype " + normHvLaunchResult), out code2);
						Vbs.Restore();
						Hvci.Restore();
						if (code2 == 0)
						{
							Settings.SaveStr("PrevHvLaunch", "");
						}
						Logger.Log(Lang.T("log.vbstweak.5"));
						return false;
					}
					Logger.Log(Lang.T("log.vbstweak.6"));
					return true;
				}
				catch (Exception ex)
				{
					Logger.Log(Lang.T("log.vbstweak.7") + ex.Message);
					return false;
				}
			}
		}

		public static bool Restore()
		{
			lock (lk)
			{
				try
				{
					string prevHvLaunchValue = Settings.LoadStr("PrevHvLaunch", "");
					if (!Vbs.HasBackup && !Hvci.HasBackup && prevHvLaunchValue.Length == 0 && !DisabledByPavise)
					{
						return true;
					}
					bool matchesRestore = Vbs.Restore() & Hvci.Restore();
					bool matchesDisabledByPavise = DisabledByPavise || prevHvLaunchValue.Length > 0;
					string normHvLaunchResult = NormHvLaunch((prevHvLaunchValue.Length == 0) ? "auto" : prevHvLaunchValue);
					int code = 0;
					if (matchesDisabledByPavise)
					{
						bool ok;
						string current = ReadHvLaunch(out ok);
						if (!ok)
						{
							matchesRestore = false;
						}
						else if (ShouldRestoreHvLaunch(current, normHvLaunchResult))
						{
							RunBcd((normHvLaunchResult == "__pavise_absent__") ? "/deletevalue hypervisorlaunchtype" : ("/set hypervisorlaunchtype " + normHvLaunchResult), out code);
							if (code != 0)
							{
								matchesRestore = false;
							}
						}
					}
					if (matchesRestore)
					{
						Settings.Save("VbsDisabledByPavise", false);
						if (Settings.Load("VbsDisabledByPavise", true))
						{
							Logger.Log(Lang.T("log.vbstweak.8"));
							return false;
						}
						Settings.SaveStr("PrevHvLaunch", "");
						Logger.Log(matchesDisabledByPavise ? (Lang.T("log.vbstweak.9") + normHvLaunchResult + Lang.T("log.nettweak.4")) : Lang.T("log.vbstweak.10"));
					}
					else
					{
						Logger.Log(Lang.T("log.vbstweak.11") + code + Lang.T("log.vbstweak.12"));
					}
					return matchesRestore;
				}
				catch (Exception ex)
				{
					Logger.Log(Lang.T("log.vbstweak.13") + ex.Message);
					return false;
				}
			}
		}

		internal static bool ShouldRestoreHvLaunch(string current, string original)
		{
			if (current == "off")
			{
				return current != original;
			}
			return false;
		}

		private static string ReadHvLaunch(out bool ok)
		{
			int code;
			string runBcdResult = RunBcd("/enum {current}", out code);
			ok = code == 0 && !string.IsNullOrEmpty(runBcdResult);
			if (!ok)
			{
				return null;
			}
			string[] runBcdResultParts = runBcdResult.Split('\n');
			foreach (string entryText in runBcdResultParts)
			{
				string entryTextTrimmed = entryText.Trim();
				if (entryTextTrimmed.StartsWith("hypervisorlaunchtype", StringComparison.OrdinalIgnoreCase))
				{
					string[] entryTextTrimmedParts = entryTextTrimmed.Split(new char[2] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
					if (entryTextTrimmedParts.Length >= 2)
					{
						return NormHvLaunch(entryTextTrimmedParts[entryTextTrimmedParts.Length - 1]);
					}
				}
			}
			return "__pavise_absent__";
		}

		private static string NormHvLaunch(string s)
		{
			if (string.IsNullOrEmpty(s))
			{
				return "auto";
			}
			if (s == "__pavise_absent__")
			{
				return "__pavise_absent__";
			}
			switch (s.Trim().ToLowerInvariant())
			{
			case "off":
				return "off";
			case "on":
				return "on";
			case "optout":
				return "optout";
			default:
				return "auto";
			}
		}

		internal static string RunBcd(string args, out int code)
		{
			code = -1;
			try
			{
				ProcessStartInfo processStartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "bcdedit.exe"), args);
				processStartInfo.CreateNoWindow = true;
				processStartInfo.UseShellExecute = false;
				processStartInfo.RedirectStandardOutput = true;
				processStartInfo.RedirectStandardError = true;
				using (Process process = Process.Start(processStartInfo))
				{
					StringBuilder sb = new StringBuilder();
					process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
					{
						if (e.Data != null)
						{
							lock (sb)
							{
								sb.Append(e.Data).Append('\n');
							}
						}
					};
					process.ErrorDataReceived += delegate
					{
					};
					process.BeginOutputReadLine();
					process.BeginErrorReadLine();
					if (!process.WaitForExit(15000))
					{
						try
						{
							process.Kill();
						}
						catch
						{
						}
						Logger.Log(Lang.T("log.vbstweak.14") + args + " ");
						return "";
					}
					process.WaitForExit();
					code = process.ExitCode;
					lock (sb)
					{
						return sb.ToString();
					}
				}
			}
			catch
			{
				return "";
			}
		}
	}
}

using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class MsiModeTweak
	{
		internal struct Candidate
		{
			public string InstanceId;

			public string Description;

			public bool HasKey;

			public int? Value;
		}

		private const string EnumRoot = "SYSTEM\\CurrentControlSet\\Enum";

		private const string MsiLeaf = "Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties";

		private const string ListKey = "MsiList";

		private const string FlagKey = "MsiOnByPavise";

		private static readonly object lk = new object();

		public static bool EnabledByPavise
		{
			get
			{
				return Settings.Load("MsiOnByPavise", false);
			}
		}

		public static List<Candidate> Scan()
		{
			List<Candidate> list = new List<Candidate>();
			HashSet<string> hashSet;
			try
			{
				hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (string item2 in PresentDevices.ByClass(new Guid("4d36e968-e325-11ce-bfc1-08002be10318")))
				{
					if (!string.IsNullOrEmpty(item2))
					{
						hashSet.Add(item2);
					}
				}
			}
			catch
			{
				return list;
			}
			if (hashSet.Count == 0)
			{
				return list;
			}
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\PCI"))
				{
					if (registryKey == null)
					{
						return list;
					}
					string[] subKeyNames = registryKey.GetSubKeyNames();
					foreach (string entryKey in subKeyNames)
					{
						using (RegistryKey registryKey2 = registryKey.OpenSubKey(entryKey))
						{
							if (registryKey2 == null)
							{
								continue;
							}
							string[] subKeyNames2 = registryKey2.GetSubKeyNames();
							foreach (string entryKey2 in subKeyNames2)
							{
								using (RegistryKey registryKey3 = registryKey2.OpenSubKey(entryKey2))
								{
									if (registryKey3 == null)
									{
										continue;
									}
									string a = registryKey3.GetValue("Class") as string;
									if (!string.Equals(a, "Display", StringComparison.OrdinalIgnoreCase))
									{
										continue;
									}
									string instanceIdValue = "PCI\\" + entryKey + "\\" + entryKey2;
									if (hashSet.Contains(instanceIdValue))
									{
										Candidate candidate = default(Candidate);
										candidate.InstanceId = instanceIdValue;
										candidate.Description = (registryKey3.GetValue("DeviceDesc") as string) ?? instanceIdValue;
										Candidate item = candidate;
										int matchIndex = item.Description.LastIndexOf(';');
										if (matchIndex >= 0)
										{
											item.Description = item.Description.Substring(matchIndex + 1);
										}
										using (RegistryKey registryKey4 = registryKey3.OpenSubKey("Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties"))
										{
											item.HasKey = registryKey4 != null;
											object value = ((registryKey4 == null) ? null : registryKey4.GetValue("MSISupported"));
											item.Value = ((value is int) ? new int?((int)value) : null);
										}
										list.Add(item);
									}
								}
							}
						}
					}
				}
			}
			catch
			{
			}
			return list;
		}

		public static List<Candidate> Disabled()
		{
			List<Candidate> list = new List<Candidate>();
			foreach (Candidate item in Scan())
			{
				if (!item.HasKey)
				{
					continue;
				}
				int? value = item.Value;
				if (value.HasValue)
				{
					int? value2 = item.Value;
					if (value2.Value == 0)
					{
						list.Add(item);
					}
				}
			}
			return list;
		}

		public static bool Enable()
		{
			lock (lk)
			{
				List<Candidate> list = Disabled();
				if (list.Count == 0)
				{
					Logger.Log(Lang.T("log.msimodetweak.1"));
					return true;
				}
				List<string> list2 = new List<string>();
				foreach (Candidate item in list)
				{
					if (Reg(item.InstanceId).Apply(1) || Reg(item.InstanceId).HasBackup)
					{
						list2.Add(item.InstanceId);
					}
					else
					{
						Logger.Log(Lang.T("log.msimodetweak.2") + item.Description);
					}
				}
				if (list2.Count == 0)
				{
					return false;
				}
				if (!Settings.SaveStr("MsiList", string.Join(";", list2.ToArray())))
				{
					foreach (string item2 in list2)
					{
						Reg(item2).Restore();
					}
					Logger.Log(Lang.T("log.msimodetweak.3"));
					return false;
				}
				Settings.Save("MsiOnByPavise", true);
				Logger.Log(Lang.T("log.msimodetweak.4") + list2.Count + Lang.T("log.msimodetweak.5"));
				return true;
			}
		}

		public static bool HasResidue()
		{
			if (!Settings.Load("MsiOnByPavise", false))
			{
				return ParseList(Settings.LoadStr("MsiList", "")).Length > 0;
			}
			return true;
		}

		public static bool Restore()
		{
			lock (lk)
			{
				bool restoreSucceeded = true;
				string[] list = ParseList(Settings.LoadStr("MsiList", ""));
				foreach (string instanceId in list)
				{
					restoreSucceeded &= Reg(instanceId).Restore();
				}
				if (restoreSucceeded)
				{
					Settings.SaveStr("MsiList", "");
					Settings.Save("MsiOnByPavise", false);
				}
				return restoreSucceeded;
			}
		}

		private static ReversibleReg Reg(string instanceId)
		{
			return new ReversibleReg(Registry.LocalMachine, "SYSTEM\\CurrentControlSet\\Enum\\" + instanceId + "\\Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties", "MSISupported", RegistryValueKind.DWord, "Msi_" + instanceId.Replace('\\', '_'));
		}

		internal static string[] ParseList(string raw)
		{
			return (raw ?? "").Split(new char[1] { ';' }, StringSplitOptions.RemoveEmptyEntries);
		}
	}
}

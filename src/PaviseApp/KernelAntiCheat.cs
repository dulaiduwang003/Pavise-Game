using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class KernelAntiCheat
	{
		private sealed class Sig
		{
			public string Name;

			public string[] Services;
		}

		private const string ServiceRoot = "SYSTEM\\CurrentControlSet\\Services";

		private static readonly Sig[] Known = new Sig[11]
		{
			new Sig
			{
				Name = "Vanguard",
				Services = new string[2] { "vgk", "vgc" }
			},
			new Sig
			{
				Name = "EasyAntiCheat",
				Services = new string[2] { "EasyAntiCheat", "EasyAntiCheat_EOS" }
			},
			new Sig
			{
				Name = "BattlEye",
				Services = new string[2] { "BEDaisy", "BEService" }
			},
			new Sig
			{
				Name = "EA Javelin",
				Services = new string[1] { "EAAntiCheat.GameService" }
			},
			new Sig
			{
				Name = "ACE-Guard",
				Services = new string[3] { "ACE-BASE", "ACE-GAME", "AntiCheatExpert" }
			},
			new Sig
			{
				Name = "nProtect GameGuard",
				Services = new string[1] { "npggsvc" }
			},
			new Sig
			{
				Name = "Faceit AC",
				Services = new string[1] { "faceit" }
			},
			new Sig
			{
				Name = "TenProtect",
				Services = new string[1] { "TesSafe" }
			},
			new Sig
			{
				Name = "NEAC",
				Services = new string[1] { "NeacSafe64" }
			},
			new Sig
			{
				Name = "HoYoKProtect",
				Services = new string[3] { "HoYoKProtect", "mhyprot3", "mhyprot2" }
			},
			new Sig
			{
				Name = "B5 BBI",
				Services = new string[2] { "B5AntiCheat64", "B5AntiCheat32" }
			}
		};

		private static readonly string[][] ByExePrefix = new string[3][]
		{
			new string[5] { "Ricochet", "=cod", "modernwarfare", "blackops", "warzone" },
			new string[6] { "HoYoKProtect", "yuanshen", "genshinimpact", "starrail", "zenlesszonezero", "bh3" },
			new string[3] { "EA Javelin", "bf6", "bf2042" }
		};

		private static readonly object lk = new object();

		private static string cached;

		private static bool probed;

		internal static bool ServiceExists(string name)
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\" + name))
				{
					return registryKey != null;
				}
			}
			catch
			{
				return false;
			}
		}

		public static string InstalledName()
		{
			lock (lk)
			{
				if (probed)
				{
					return cached;
				}
				probed = true;
				List<string> list = new List<string>();
				Sig[] known = Known;
				foreach (Sig sig in known)
				{
					string[] services = sig.Services;
					foreach (string name in services)
					{
						if (ServiceExists(name))
						{
							list.Add(sig.Name);
							break;
						}
					}
				}
				cached = ((list.Count == 0) ? null : string.Join(" ", list.ToArray()));
				return cached;
			}
		}

		internal static string MatchByExe(string rendererName)
		{
			if (string.IsNullOrEmpty(rendererName))
			{
				return null;
			}
			string rendererNameSnapshot = rendererName;
			int lastIndexOfAnyResult = rendererNameSnapshot.LastIndexOfAny(new char[2] { '\\', '/' });
			if (lastIndexOfAnyResult >= 0)
			{
				rendererNameSnapshot = rendererNameSnapshot.Substring(lastIndexOfAnyResult + 1);
			}
			if (rendererNameSnapshot.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				rendererNameSnapshot = rendererNameSnapshot.Substring(0, rendererNameSnapshot.Length - 4);
			}
			string[][] byExePrefix = ByExePrefix;
			foreach (string[] textEntriesEntry in byExePrefix)
			{
				for (int j = 1; j < textEntriesEntry.Length; j++)
				{
					string message = textEntriesEntry[j];
					if ((message.Length > 1 && message[0] == '=') ? string.Equals(rendererNameSnapshot, message.Substring(1), StringComparison.OrdinalIgnoreCase) : rendererNameSnapshot.StartsWith(message, StringComparison.OrdinalIgnoreCase))
					{
						return textEntriesEntry[0];
					}
				}
			}
			return null;
		}

		public static string DescribeForLog(string rendererName)
		{
			string matchByExeResult = MatchByExe(rendererName);
			if (matchByExeResult != null)
			{
				return matchByExeResult;
			}
			string installedNameResult = InstalledName();
			if (installedNameResult != null)
			{
				return Lang.F("log.kernelac.installed", installedNameResult);
			}
			return null;
		}
	}
}

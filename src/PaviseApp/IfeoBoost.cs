using Microsoft.Win32;

namespace PaviseApp
{
	internal static class IfeoBoost
	{
		private const string Sub = "PerfOptions";

		private const string ListKey = "IfeoList";

		private const string ArmKey = "IfeoArm";

		private static readonly object lk = new object();

		private static string PerfPath(string exe)
		{
			return IfeoStore.RootPath + "\\" + exe + "\\PerfOptions";
		}

		private static ReversibleReg RegOf(string exe)
		{
			return new ReversibleReg(IfeoStore.Hive, PerfPath(exe), "CpuPriorityClass", RegistryValueKind.DWord, "IfeoPri_" + exe);
		}

		private static ReversibleReg IoRegOf(string exe)
		{
			return new ReversibleReg(IfeoStore.Hive, PerfPath(exe), "IoPriority", RegistryValueKind.DWord, "IfeoIo_" + exe);
		}

		private static ReversibleReg PageRegOf(string exe)
		{
			return new ReversibleReg(IfeoStore.Hive, PerfPath(exe), "PagePriority", RegistryValueKind.DWord, "IfeoPg_" + exe);
		}

		public static bool HasResidue()
		{
			if (Settings.LoadStr("IfeoList", "").Length == 0 && Settings.LoadStr("IfeoArm", "").Length == 0)
			{
				return Settings.Load("GmIfeoBoost", false);
			}
			return true;
		}

		public static bool RestoreAll()
		{
			lock (lk)
			{
				bool flag = Settings.Save("GmIfeoBoost", false);
				flag &= Settings.SaveStr("IfeoArm", "");
				string[] array = ParseList(Settings.LoadStr("IfeoList", ""));
				foreach (string text in array)
				{
					bool flag2 = RegOf(text).Restore();
					flag2 &= IoRegOf(text).Restore();
					if (!(flag2 & PageRegOf(text).Restore()))
					{
						flag = false;
						continue;
					}
					string text2 = Settings.LoadStr("IfeoMk_" + text, "11");
					bool ourSub = text2.Length < 2 || text2[1] == '0';
					bool ourExe = text2.Length < 1 || text2[0] == '0';
					IfeoStore.CleanupEmpty(text, "PerfOptions", ourSub, ourExe);
					Settings.SaveStr("IfeoMk_" + text, "");
					IfeoStore.RemoveFromList("IfeoList", text);
					Logger.Log(Lang.T("log.ifeoboost.8") + text);
				}
				if (!flag)
				{
					Logger.Log(Lang.T("log.ifeoboost.9"));
				}
				return flag;
			}
		}

		internal static string[] ParseList(string raw)
		{
			return IfeoStore.ParseList(raw);
		}
	}
}

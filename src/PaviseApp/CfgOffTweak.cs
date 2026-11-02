using Microsoft.Win32;

namespace PaviseApp
{
	internal static class CfgOffTweak
	{
		private const string ValName = "MitigationOptions";

		private const string ListKey = "CfgList";

		private const string EnableKey = "CfgOff";

		private static readonly object lk = new object();

		private static ReversibleReg RegOf(string exe)
		{
			return new ReversibleReg(IfeoStore.Hive, IfeoStore.RootPath + "\\" + exe, "MitigationOptions", RegistryValueKind.Binary, "CfgOpt_" + exe);
		}

		public static bool HasResidue()
		{
			if (Settings.LoadStr("CfgList", "").Length == 0)
			{
				return Settings.Load("CfgOff", false);
			}
			return true;
		}

		public static bool RestoreAll()
		{
			lock (lk)
			{
				bool flag = Settings.Save("CfgOff", false);
				string[] array = ParseList(Settings.LoadStr("CfgList", ""));
				foreach (string text in array)
				{
					if (!RegOf(text).Restore())
					{
						flag = false;
						continue;
					}
					string text2 = Settings.LoadStr("CfgMk_" + text, "1");
					bool ourExe = text2.Length == 0 || text2[0] != '1';
					IfeoStore.CleanupEmpty(text, null, false, ourExe);
					Settings.SaveStr("CfgMk_" + text, "");
					IfeoStore.RemoveFromList("CfgList", text);
					Logger.Log(Lang.T("log.cfgofftweak.6") + text);
				}
				if (!flag)
				{
					Logger.Log(Lang.T("log.cfgofftweak.8"));
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

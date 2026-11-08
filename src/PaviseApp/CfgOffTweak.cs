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
				bool cfgOffEnabled = Settings.Save("CfgOff", false);
				string[] list = ParseList(Settings.LoadStr("CfgList", ""));
				foreach (string entryText in list)
				{
					if (!RegOf(entryText).Restore())
					{
						cfgOffEnabled = false;
						continue;
					}
					string value = Settings.LoadStr("CfgMk_" + entryText, "1");
					bool ourExe = value.Length == 0 || value[0] != '1';
					IfeoStore.CleanupEmpty(entryText, null, false, ourExe);
					Settings.SaveStr("CfgMk_" + entryText, "");
					IfeoStore.RemoveFromList("CfgList", entryText);
					Logger.Log(Lang.T("log.cfgofftweak.6") + entryText);
				}
				if (!cfgOffEnabled)
				{
					Logger.Log(Lang.T("log.cfgofftweak.8"));
				}
				return cfgOffEnabled;
			}
		}

		internal static string[] ParseList(string raw)
		{
			return IfeoStore.ParseList(raw);
		}
	}
}

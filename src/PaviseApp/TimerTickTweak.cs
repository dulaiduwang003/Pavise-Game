using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal static class TimerTickTweak
	{
		private const string OnKey = "TimerTickByPavise";

		private const string SnapKey = "PrevTimerTick";

		internal static readonly string[] Names = new string[3] { "useplatformclock", "useplatformtick", "disabledynamictick" };

		internal static readonly string[] Targets = new string[3] { "no", "no", "yes" };

		private static readonly object lk = new object();

		private static volatile bool lastOn;

		private static readonly HashSet<string> YesTokens = new HashSet<string>(StringComparer.Ordinal)
		{
			"yes", "true", "1", "是", "はい", "예", "да", "oui", "ja", "sí",
			"sì", "si", "sim", "evet", "tak", "ano", "igen", "kyllä", "ναι", "כן"
		};

		private static readonly HashSet<string> NoTokens = new HashSet<string>(StringComparer.Ordinal)
		{
			"no", "false", "0", "否", "いいえ", "아니요", "아니오", "нет", "non", "nein",
			"não", "nao", "hayır", "hayir", "nie", "ne", "nem", "ei", "nej", "nei",
			"όχι", "לא"
		};

		public static bool EnabledByPavise
		{
			get
			{
				return Settings.Load("TimerTickByPavise", false);
			}
		}

		public static bool LastKnownOn
		{
			get
			{
				return lastOn;
			}
		}

		public static bool OwnsState
		{
			get
			{
				if (!EnabledByPavise)
				{
					return Settings.LoadStr("PrevTimerTick", "").Length > 0;
				}
				return true;
			}
		}

		private static string Bcd(string args, out int code)
		{
			return VbsTweak.RunBcd(args, out code);
		}

		internal static bool CanReadForPreset()
		{
			try { bool readable; ReadValues(out readable); return readable; }
			catch { return false; }
		}

		internal static string[] ParseValues(string enumText)
		{
			string[] array = new string[3] { "__pavise_absent__", "__pavise_absent__", "__pavise_absent__" };
			if (string.IsNullOrEmpty(enumText))
			{
				return array;
			}
			string[] array2 = enumText.Split('\n');
			foreach (string text in array2)
			{
				string text2 = text.Trim();
				for (int j = 0; j < Names.Length; j++)
				{
					if (text2.StartsWith(Names[j], StringComparison.OrdinalIgnoreCase))
					{
						string text3 = text2.Substring(Names[j].Length).Trim();
						if (text3.Length != 0 && array[j] == "__pavise_absent__")
						{
							array[j] = Normalize(text3);
						}
					}
				}
			}
			return array;
		}

		internal static string Normalize(string s)
		{
			if (string.IsNullOrEmpty(s) || s == "__pavise_absent__")
			{
				return "__pavise_absent__";
			}
			string text = s.Trim().ToLowerInvariant();
			if (YesTokens.Contains(text))
			{
				return "yes";
			}
			if (NoTokens.Contains(text))
			{
				return "no";
			}
			return text;
		}

		internal static bool AllOn(string[] vals)
		{
			if (vals == null || vals.Length < Names.Length)
			{
				return false;
			}
			for (int i = 0; i < Names.Length; i++)
			{
				if (vals[i] != Targets[i])
				{
					return false;
				}
			}
			return true;
		}

		private static string[] ReadValues(out bool ok)
		{
			int code;
			string text = Bcd("/enum {current}", out code);
			ok = code == 0 && !string.IsNullOrEmpty(text);
			if (!ok)
			{
				return null;
			}
			return ParseValues(text);
		}

		public static bool CurrentlyOn()
		{
			bool ok;
			string[] vals = ReadValues(out ok);
			lastOn = ok && AllOn(vals);
			return lastOn;
		}

		public static bool Enable()
		{
			if (LaboratoryClockRepair.HasReceipt) return false;
			lock (lk)
			{
				bool ok;
				string[] array = ReadValues(out ok);
				if (!ok)
				{
					Logger.Warn(Lang.T("log.timerticktweak.1"));
					return false;
				}
				if (AllOn(array))
				{
					lastOn = true;
					return true;
				}
				if (Settings.LoadStr("PrevTimerTick", "").Length == 0)
				{
					string text = string.Join("|", array);
					if (!Settings.SaveStrDurable("PrevTimerTick", text))
					{
						return false;
					}
					if (Settings.LoadStr("PrevTimerTick", "") != text)
					{
						Logger.Log(Lang.T("log.timerticktweak.2"));
						return false;
					}
				}
				for (int i = 0; i < Names.Length; i++)
				{
					int code;
					Bcd("/set {current} " + Names[i] + " " + Targets[i], out code);
					if (code != 0)
					{
						Logger.Log(Lang.T("log.timerticktweak.3") + Names[i] + " rc " + code);
						if (WriteBack())
						{
							Settings.SaveStr("PrevTimerTick", "");
						}
						return false;
					}
				}
				if (!CurrentlyOn())
				{
					return false;
				}
				if (!Settings.Save("TimerTickByPavise", true))
				{
					return false;
				}
				lastOn = true;
				Logger.Log(Lang.T("log.timerticktweak.4"));
				return true;
			}
		}

		public static bool Restore()
		{
			lock (lk)
			{
				if (!OwnsState)
				{
					return true;
				}
				if (!WriteBack())
				{
					Logger.Log(Lang.T("log.timerticktweak.6"));
					return false;
				}
				if (!Settings.Save("TimerTickByPavise", false) || !Settings.SaveStr("PrevTimerTick", ""))
				{
					return false;
				}
				lastOn = false;
				Logger.Log(Lang.T("log.timerticktweak.5"));
				return true;
			}
		}

		private static bool WriteBack()
		{
			bool ok;
			string[] array = ReadValues(out ok);
			if (!ok)
			{
				return false;
			}
			string text = Settings.LoadStr("PrevTimerTick", "");
			string[] array2 = ((text.Length > 0) ? text.Split('|') : null);
			if (array2 == null || array2.Length != Names.Length)
			{
				return false;
			}
			bool result = true;
			for (int i = 0; i < Names.Length; i++)
			{
				string text2 = ((array2 != null && i < array2.Length) ? array2[i] : "__pavise_absent__");
				if (array[i] != Targets[i])
				{
					continue;
				}
				int code = 0;
				if (text2 == "__pavise_absent__")
				{
					if (array[i] != "__pavise_absent__")
					{
						Bcd("/deletevalue {current} " + Names[i], out code);
					}
				}
				else if (array[i] != text2)
				{
					Bcd("/set {current} " + Names[i] + " " + text2, out code);
				}
				if (code != 0)
				{
					result = false;
				}
			}
			string[] array3 = ReadValues(out ok);
			if (!ok)
			{
				return false;
			}
			for (int j = 0; j < Names.Length; j++)
			{
				if (array[j] == Targets[j] && array3[j] != array2[j])
				{
					result = false;
				}
			}
			return result;
		}
	}
}

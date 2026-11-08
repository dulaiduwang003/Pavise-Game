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
			string[] textEntries = new string[3] { "__pavise_absent__", "__pavise_absent__", "__pavise_absent__" };
			if (string.IsNullOrEmpty(enumText))
			{
				return textEntries;
			}
			string[] enumTextParts = enumText.Split('\n');
			foreach (string entryText in enumTextParts)
			{
				string entryTextTrimmed = entryText.Trim();
				for (int j = 0; j < Names.Length; j++)
				{
					if (entryTextTrimmed.StartsWith(Names[j], StringComparison.OrdinalIgnoreCase))
					{
						string substringTrimmed = entryTextTrimmed.Substring(Names[j].Length).Trim();
						if (substringTrimmed.Length != 0 && textEntries[j] == "__pavise_absent__")
						{
							textEntries[j] = Normalize(substringTrimmed);
						}
					}
				}
			}
			return textEntries;
		}

		internal static string Normalize(string s)
		{
			if (string.IsNullOrEmpty(s) || s == "__pavise_absent__")
			{
				return "__pavise_absent__";
			}
			string toLowerInvariantResult = s.Trim().ToLowerInvariant();
			if (YesTokens.Contains(toLowerInvariantResult))
			{
				return "yes";
			}
			if (NoTokens.Contains(toLowerInvariantResult))
			{
				return "no";
			}
			return toLowerInvariantResult;
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
			string bcdResult = Bcd("/enum {current}", out code);
			ok = code == 0 && !string.IsNullOrEmpty(bcdResult);
			if (!ok)
			{
				return null;
			}
			return ParseValues(bcdResult);
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
				string[] values = ReadValues(out ok);
				if (!ok)
				{
					Logger.Warn(Lang.T("log.timerticktweak.1"));
					return false;
				}
				if (AllOn(values))
				{
					lastOn = true;
					return true;
				}
				if (Settings.LoadStr("PrevTimerTick", "").Length == 0)
				{
					string joinResult = string.Join("|", values);
					if (!Settings.SaveStrDurable("PrevTimerTick", joinResult))
					{
						return false;
					}
					if (Settings.LoadStr("PrevTimerTick", "") != joinResult)
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
			string[] values = ReadValues(out ok);
			if (!ok)
			{
				return false;
			}
			string prevTimerTickValue = Settings.LoadStr("PrevTimerTick", "");
			string[] prevTimerTickValueParts = ((prevTimerTickValue.Length > 0) ? prevTimerTickValue.Split('|') : null);
			if (prevTimerTickValueParts == null || prevTimerTickValueParts.Length != Names.Length)
			{
				return false;
			}
			bool result = true;
			for (int i = 0; i < Names.Length; i++)
			{
				string message = ((prevTimerTickValueParts != null && i < prevTimerTickValueParts.Length) ? prevTimerTickValueParts[i] : "__pavise_absent__");
				if (values[i] != Targets[i])
				{
					continue;
				}
				int code = 0;
				if (message == "__pavise_absent__")
				{
					if (values[i] != "__pavise_absent__")
					{
						Bcd("/deletevalue {current} " + Names[i], out code);
					}
				}
				else if (values[i] != message)
				{
					Bcd("/set {current} " + Names[i] + " " + message, out code);
				}
				if (code != 0)
				{
					result = false;
				}
			}
			string[] values2 = ReadValues(out ok);
			if (!ok)
			{
				return false;
			}
			for (int j = 0; j < Names.Length; j++)
			{
				if (values[j] == Targets[j] && values2[j] != prevTimerTickValueParts[j])
				{
					result = false;
				}
			}
			return result;
		}
	}
}

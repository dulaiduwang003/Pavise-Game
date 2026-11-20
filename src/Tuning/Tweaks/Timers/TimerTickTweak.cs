using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // 计时器节拍已下线 这里只剩按旧版留下的快照把启动配置还原
    internal static class TimerTickTweak
    {
        private const string OnKey = "TimerTickByPavise";

        private const string SnapKey = "PrevTimerTick";

        internal static readonly string[] Names = new string[3] { "useplatformclock", "useplatformtick", "disabledynamictick" };

        internal static readonly string[] Targets = new string[3] { "no", "no", "yes" };

        private static readonly object lk = new object();

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

#if PAVISE_SELFTEST
        internal delegate string BcdCall(string args, out int code);
        // 自检构建里 bcdedit 调用整个交给替身 没注入就当场拒绝
        internal static BcdCall BcdForTest = null;
        // 启动进程的接缝 自检构建里本该交给 bcdedit.exe 的参数只会交到这里 没注入同样拒绝
        internal static BcdCall BcdStartForTest = null;
#endif
        private static string Bcd(string args, out int code)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
#if PAVISE_SELFTEST
            if (BcdForTest == null) throw new InvalidOperationException("Unmocked bcdedit call");
            if (BcdForTest != null) return BcdForTest(args, out code);
            return StartBcd(args, out code);
#else
            code = -1;
            return "";
#endif
#else
            return VbsTweak.RunBcd(args, out code);
#endif
        }
#if PAVISE_SELFTEST
        internal static string StartBcd(string args, out int code)
        {
            if (BcdStartForTest == null) throw new InvalidOperationException("Selftest builds cannot start bcdedit.exe");
            return BcdStartForTest(args, out code);
        }
#endif

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
                    Logger.Log(() => Lang.T("log.timerticktweak.6"));
                    return false;
                }
                if (!Settings.Save("TimerTickByPavise", false) || !Settings.SaveStr("PrevTimerTick", ""))
                {
                    return false;
                }
                Logger.Log(() => Lang.T("log.timerticktweak.5"));
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

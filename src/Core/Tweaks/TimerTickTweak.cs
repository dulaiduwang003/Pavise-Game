// @author bdth 2074055628@qq.com
// File purpose Timer constant tick toggle; writes all three boot config items together, takes effect after reboot
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class TimerTickTweak
    {
        internal static readonly string[] Names =
            { "useplatformclock", "useplatformtick", "disabledynamictick" };
        internal static readonly string[] Targets = { "no", "no", "yes" };

        private const string OnKey = "TimerTickByPavise";
        private const string SnapKey = "PrevTimerTick";
        private static readonly object lk = new object();
        private static volatile bool lastOn;

        public static bool EnabledByPavise { get { return Settings.Load(OnKey, false); } }
        public static bool LastKnownOn { get { return lastOn; } }
        public static bool OwnsState
        { get { return EnabledByPavise || Settings.LoadStr(SnapKey, "").Length > 0; } }

        internal static string[] ParseValues(string enumText)
        {
            var vals = new[] { ReversibleReg.Absent, ReversibleReg.Absent, ReversibleReg.Absent };
            if (string.IsNullOrEmpty(enumText)) return vals;
            foreach (string raw in enumText.Split('\n'))
            {
                string line = raw.Trim();
                for (int i = 0; i < Names.Length; i++)
                {
                    if (!line.StartsWith(Names[i], StringComparison.OrdinalIgnoreCase)) continue;
                    string rest = line.Substring(Names[i].Length).Trim();
                    if (rest.Length == 0) continue;
                    if (vals[i] == ReversibleReg.Absent) vals[i] = Normalize(rest);
                }
            }
            return vals;
        }

        private static readonly HashSet<string> YesTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "yes", "true", "1", "是", "はい", "예", "да", "oui", "ja", "sí", "sì", "si", "sim",
            "evet", "tak", "ano", "igen", "kyllä", "ναι", "כן"
        };
        private static readonly HashSet<string> NoTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "no", "false", "0", "否", "いいえ", "아니요", "아니오", "нет", "non", "nein",
            "não", "nao", "hayır", "hayir", "nie", "ne", "nem", "ei", "nej", "nei", "όχι", "לא"
        };

        internal static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s) || s == ReversibleReg.Absent) return ReversibleReg.Absent;
            string low = s.Trim().ToLowerInvariant();
            if (YesTokens.Contains(low)) return "yes";
            if (NoTokens.Contains(low)) return "no";
            return low;
        }

        internal static bool AllOn(string[] vals)
        {
            if (vals == null || vals.Length < Names.Length) return false;
            for (int i = 0; i < Names.Length; i++) if (vals[i] != Targets[i]) return false;
            return true;
        }

        private static string[] ReadValues(out bool ok)
        {
            int code;
            string o = VbsTweak.RunBcd("/enum {current}", out code);
            ok = code == 0 && !string.IsNullOrEmpty(o);
            return ok ? ParseValues(o) : null;
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
            lock (lk)
            {
                bool ok;
                string[] before = ReadValues(out ok);
                if (!ok) { Logger.Warn(Lang.T("log.timerticktweak.1")); return false; }
                if (Settings.LoadStr(SnapKey, "").Length == 0)
                {
                    string snap = string.Join("|", before);
                    Settings.SaveStr(SnapKey, snap);
                    if (Settings.LoadStr(SnapKey, "") != snap)
                    { Logger.Log(Lang.T("log.timerticktweak.2")); return false; }
                }
                for (int i = 0; i < Names.Length; i++)
                {
                    int code;
                    VbsTweak.RunBcd("/set " + Names[i] + " " + Targets[i], out code);
                    if (code != 0)
                    {
                        Logger.Log(Lang.T("log.timerticktweak.3") + Names[i] + " rc " + code);
                        if (WriteBack()) Settings.SaveStr(SnapKey, "");
                        return false;
                    }
                }
                Settings.Save(OnKey, true);
                lastOn = true;
                Logger.Log(Lang.T("log.timerticktweak.4"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (!WriteBack()) { Logger.Log(Lang.T("log.timerticktweak.6")); return false; }
                Settings.Save(OnKey, false);
                Settings.SaveStr(SnapKey, "");
                lastOn = false;
                Logger.Log(Lang.T("log.timerticktweak.5"));
                return true;
            }
        }

        private static bool WriteBack()
        {
            bool readOk;
            string[] now = ReadValues(out readOk);
            if (!readOk) return false;
            string snap = Settings.LoadStr(SnapKey, "");
            string[] back = snap.Length > 0 ? snap.Split('|') : null;
            bool all = true;
            for (int i = 0; i < Names.Length; i++)
            {
                string want = back != null && i < back.Length ? back[i] : ReversibleReg.Absent;
                int code = 0;
                if (want == ReversibleReg.Absent)
                {
                    if (now[i] != ReversibleReg.Absent)
                        VbsTweak.RunBcd("/deletevalue " + Names[i], out code);
                }
                else if (now[i] != want)
                    VbsTweak.RunBcd("/set " + Names[i] + " " + want, out code);
                if (code != 0) all = false;
            }
            return all;
        }
    }
}

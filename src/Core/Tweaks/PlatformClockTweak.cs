// @author bdth 2074055628@qq.com
// 文件用途 校正老优化教程写进启动配置的平台时钟覆盖 回到系统默认

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PaviseApp
{
    internal static class PlatformClockTweak
    {
        internal static readonly string[] WatchedNames =
            { "useplatformclock", "useplatformtick", "disabledynamictick" };

        private const string RemovedKey = "ClockRemoved";
        private static readonly object lk = new object();

        public static bool RepairedByPavise { get { return Settings.LoadStr(RemovedKey, "").Length > 0; } }

        private static string RunBcdedit(string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "bcdedit.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    string outText = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(10000)) return null;
                    return p.ExitCode == 0 ? outText : null;
                }
            }
            catch { return null; }
        }

        // bcdedit 的布尔显示随系统语言本地化 覆盖主流语言的是/否令牌
        // 已知否定=显式关闭≈默认 不算残留 未知语言按残留处理但原样记录 还原写不回会显式报错而不是写反
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

        internal static List<KeyValuePair<string, string>> ParseOverridePairs(string enumText)
        {
            var hits = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(enumText)) return hits;
            foreach (string raw in enumText.Split('\n'))
            {
                string line = raw.Trim();
                foreach (string name in WatchedNames)
                {
                    if (!line.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
                    string rest = line.Substring(name.Length).Trim().ToLowerInvariant();
                    if (rest.Length == 0 || NoTokens.Contains(rest)) continue;
                    {
                        bool known = false;
                        foreach (KeyValuePair<string, string> kv in hits) if (kv.Key == name) { known = true; break; }
                        if (!known) hits.Add(new KeyValuePair<string, string>(name, YesTokens.Contains(rest) ? "yes" : rest));
                    }
                }
                if (line.StartsWith("tscsyncpolicy", StringComparison.OrdinalIgnoreCase))
                {
                    string rest = line.Substring("tscsyncpolicy".Length).Trim().ToLowerInvariant();
                    if (rest == "legacy" || rest == "enhanced")
                    {
                        bool known = false;
                        foreach (KeyValuePair<string, string> kv in hits) if (kv.Key == "tscsyncpolicy") { known = true; break; }
                        if (!known) hits.Add(new KeyValuePair<string, string>("tscsyncpolicy", rest));
                    }
                }
            }
            return hits;
        }

        internal static List<string> ParseOverrides(string enumText)
        {
            var names = new List<string>();
            foreach (KeyValuePair<string, string> kv in ParseOverridePairs(enumText)) names.Add(kv.Key);
            return names;
        }

        private static List<KeyValuePair<string, string>> StalePairs()
        {
            return ParseOverridePairs(RunBcdedit("/enum {current}"));
        }

        public static List<string> StaleOverrides()
        {
            return ParseOverrides(RunBcdedit("/enum {current}"));
        }

        public static bool NeedsRepair() { return StaleOverrides().Count > 0; }

        public static string Describe()
        {
            List<string> stale = StaleOverrides();
            if (stale.Count == 0)
                return RepairedByPavise ? Lang.T("clock.repaired") : Lang.T("clock.ok");
            return Lang.F("clock.broken", string.Join(" ", stale.ToArray()));
        }

        public static bool Repair()
        {
            lock (lk)
            {
                List<KeyValuePair<string, string>> stale = StalePairs();
                if (stale.Count == 0)
                {
                    Logger.Log(Lang.T("log.platformclocktweak.1"));
                    return true;
                }
                var removed = new List<string>();
                foreach (string s in Settings.LoadStr(RemovedKey, "").Split('|'))
                    if (s.Length > 0 && !removed.Contains(s)) removed.Add(s);
                var names = new List<string>();
                foreach (KeyValuePair<string, string> kv in stale)
                {
                    string record = kv.Key + "=" + kv.Value;
                    bool preRecorded = false;
                    if (!removed.Contains(record))
                    {
                        removed.Add(record);
                        if (!Settings.SaveStr(RemovedKey, string.Join("|", removed.ToArray())))
                        {
                            removed.Remove(record);
                            Logger.Log(Lang.T("log.platformclocktweak.2") + kv.Key + Lang.T("log.platformclocktweak.3"));
                            continue;
                        }
                        preRecorded = true;
                    }
                    if (RunBcdedit("/deletevalue {current} " + kv.Key) == null)
                    {
                        Logger.Log(Lang.T("log.platformclocktweak.2") + kv.Key + Lang.T("log.platformclocktweak.4"));
                        if (preRecorded)
                        {
                            removed.Remove(record);
                            Settings.SaveStr(RemovedKey, string.Join("|", removed.ToArray()));
                        }
                        continue;
                    }
                    names.Add(kv.Key);
                }
                List<string> after = StaleOverrides();
                if (after.Count > 0)
                {
                    Logger.Log(Lang.T("log.platformclocktweak.5") + string.Join(" ", after.ToArray()));
                    return false;
                }
                Logger.Log(Lang.T("log.platformclocktweak.6") + string.Join(" ", names.ToArray()) + Lang.T("log.platformclocktweak.7"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                var remain = new List<string>();
                foreach (string s in Settings.LoadStr(RemovedKey, "").Split('|'))
                {
                    if (s.Length == 0) continue;
                    int sep = s.IndexOf('=');
                    string name = sep > 0 ? s.Substring(0, sep) : s;
                    string value = sep > 0 ? s.Substring(sep + 1) : "yes";
                    if (RunBcdedit("/set {current} " + name + " " + value) == null) remain.Add(s);
                }
                Settings.SaveStr(RemovedKey, string.Join("|", remain.ToArray()));
                if (remain.Count == 0)
                {
                    Logger.Log(Lang.T("log.platformclocktweak.8"));
                    return true;
                }
                Logger.Log(Lang.T("log.platformclocktweak.9") + string.Join(" ", remain.ToArray()));
                return false;
            }
        }
    }
}

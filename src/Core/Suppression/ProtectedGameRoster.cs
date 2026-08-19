// @author bdth 2074055628@qq.com
// 文件用途 记录被内核反作弊拒绝整进程写入的游戏 只跳过必然失败的那几项写入 换版本自动重试
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class ProtectedGameRoster
    {
        private const string Key = "AcProtectedGames";
        private static readonly object lk = new object();
        private static Dictionary<string, string> cache;

        private static string Norm(string rendererName)
        {
            if (string.IsNullOrEmpty(rendererName)) return null;
            string s = rendererName;
            int slash = s.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) s = s.Substring(slash + 1);
            if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s += ".exe";
            s = s.Trim();
            return s.Length == 0 || s.IndexOf(';') >= 0 || s.IndexOf('|') >= 0
                ? null : s.ToLowerInvariant();
        }

        private static Dictionary<string, string> Load()
        {
            if (cache != null) return cache;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in Settings.LoadStr(Key, "")
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int bar = entry.LastIndexOf('|');
                if (bar <= 0 || bar >= entry.Length - 1) continue;
                map[entry.Substring(0, bar)] = entry.Substring(bar + 1);
            }
            cache = map;
            return cache;
        }

        private static void Persist()
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, string> kv in cache) parts.Add(kv.Key + "|" + kv.Value);
            Settings.SaveStr(Key, string.Join(";", parts.ToArray()));
        }

        private static bool Current(string version)
        {
            return string.Equals(version, App.Version, StringComparison.OrdinalIgnoreCase);
        }

        public static bool Contains(string rendererName)
        {
            string exe = Norm(rendererName);
            if (exe == null) return false;
            lock (lk)
            {
                string version;
                return Load().TryGetValue(exe, out version) && Current(version);
            }
        }

        public static void Remember(string rendererName)
        {
            string exe = Norm(rendererName);
            if (exe == null) return;
            lock (lk)
            {
                Dictionary<string, string> map = Load();
                string version;
                if (map.TryGetValue(exe, out version) && Current(version)) return;
                map[exe] = App.Version;
                Persist();
            }
            Logger.Log(Lang.T("log.protectedgameroster.1") + exe + Lang.T("log.protectedgameroster.2"));
        }

        public static bool Forget(string rendererName)
        {
            string exe = Norm(rendererName);
            if (exe == null) return false;
            lock (lk)
            {
                if (!Load().Remove(exe)) return false;
                Persist();
            }
            Logger.Log(Lang.T("log.protectedgameroster.3") + exe);
            return true;
        }

        public static int Clear()
        {
            int n;
            lock (lk)
            {
                n = Load().Count;
                if (n == 0) return 0;
                cache.Clear();
                Persist();
            }
            Logger.Log(Lang.T("log.protectedgameroster.4") + n);
            return n;
        }

#if PAVISE_SELFTEST
        internal static void ResetCache()
        {
            lock (lk) cache = null;
        }
#endif

        public static string[] Names()
        {
            lock (lk)
            {
                var list = new List<string>();
                foreach (KeyValuePair<string, string> kv in Load())
                    if (Current(kv.Value)) list.Add(kv.Key);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                return list.ToArray();
            }
        }
    }
}

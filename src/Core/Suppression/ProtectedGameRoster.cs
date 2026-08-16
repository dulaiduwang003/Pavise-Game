// @author bdth 2074055628@qq.com
// 文件用途 记录被内核反作弊拒绝写句柄的游戏 本体提优对它们一律不再尝试

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class ProtectedGameRoster
    {
        private const string Key = "AcProtectedGames";
        private static readonly object lk = new object();
        private static HashSet<string> cache;

        private static string Norm(string rendererName)
        {
            if (string.IsNullOrEmpty(rendererName)) return null;
            string s = rendererName;
            int slash = s.LastIndexOfAny(new[] { '\\', '/' });
            if (slash >= 0) s = s.Substring(slash + 1);
            if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s += ".exe";
            s = s.Trim();
            return s.Length == 0 || s.IndexOf(';') >= 0 ? null : s.ToLowerInvariant();
        }

        private static HashSet<string> Load()
        {
            if (cache != null) return cache;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string s in Settings.LoadStr(Key, "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                set.Add(s);
            cache = set;
            return cache;
        }

        public static bool Contains(string rendererName)
        {
            string exe = Norm(rendererName);
            if (exe == null) return false;
            lock (lk) return Load().Contains(exe);
        }

        public static void Remember(string rendererName)
        {
            string exe = Norm(rendererName);
            if (exe == null) return;
            lock (lk)
            {
                var set = Load();
                if (!set.Add(exe)) return;
                Settings.SaveStr(Key, string.Join(";", new List<string>(set).ToArray()));
            }
            Logger.Log(Lang.T("log.protectedgameroster.1") + exe + Lang.T("log.protectedgameroster.2"));
        }
    }
}

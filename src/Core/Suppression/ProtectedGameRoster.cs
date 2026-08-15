// @author bdth 2074055628@qq.com
// 文件用途 记录被内核反作弊拒绝写句柄的游戏 本体提优对它们一律不再尝试
// 这些对游戏进程/线程的写操作在反作弊(TP/ACE/EAC 等)下本就被 ObRegisterCallbacks 剥离或拒绝
// 从未真正生效 跳过它们零性能损失 换来的是不再以 2Hz 反复取写句柄触发反作弊冻结/闪退
// 名单按 exe 基名持久化 首次被拒即记入 后续对局从第一 tick 起就不碰本体 后台压制不受影响
// 只在"确属被拒(非进程已退)"时记入 避免把启动瞬间短暂打不开句柄的正常游戏误列

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
            Logger.Log("反作弊相容 " + exe + " 本体写入被拒 已记入相容名单 后续对局不再尝试本体提优 后台压制照常");
        }
    }
}

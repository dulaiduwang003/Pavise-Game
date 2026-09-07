// @author bdth 2074055628@qq.com
// 文件用途 WeGame 脱壳的纯规则 识别 WeGame 游戏目录 决定何时脱壳 何时熔断 不碰进程不读注册表
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal static class WeGameShell
    {
        // 对局确认运行这么久之后才动手 启动器交接 反作弊初始化 首屏加载都过去了再收壳
        public const int StabilizeSeconds = 30;
        // 脱壳之后游戏在这个窗口内退出 就认定壳被这个游戏依赖 本机停用该游戏的自动脱壳
        public const int ExitFuseSeconds = 20;
        // 对局结束通知比进程退出晚一段宽限 通知到达时距上次脱壳不超过这个数也算
        public const int ExitFuseNotifySeconds = 45;
        // 脱壳后先在这个时刻复查游戏还在不在 之后按 RespawnCheckSeconds 看壳有没有重生
        public const int RespawnCheckSeconds = 60;
        // 壳进程反复重生 五分钟内结束三轮就本局停手 不和 WeGame 的自我拉起打架
        public const int RespawnWindowSeconds = 300;
        public const int RespawnLimit = 3;

        private static readonly string[] RootMarkers = { "TCLS", "rail_files", "WeGameLauncher", "Cross" };
        private const string AppsFolderName = "WeGameApps";
        private const int MaxParentWalk = 4;

        // 目录里带 WeGame 的启动链标记 或者它就是 WeGameApps 的直接子目录
        public static bool IsWeGameGameRoot(string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            try
            {
                string full = Path.GetFullPath(root.Trim().Trim('"')).TrimEnd('\\');
                if (!Directory.Exists(full)) return false;
                return HasRootMarker(full) || DirectChildOfAppsFolder(full);
            }
            catch { return false; }
        }

        internal static bool HasRootMarker(string full)
        {
            if (string.IsNullOrEmpty(full)) return false;
            foreach (string marker in RootMarkers)
                if (Directory.Exists(Path.Combine(full, marker))) return true;
            return false;
        }

        // 只有 WeGameApps 的直接子目录才是游戏根 更深的目录属于游戏内部
        internal static bool DirectChildOfAppsFolder(string full)
        {
            if (string.IsNullOrEmpty(full)) return false;
            string parent;
            try { parent = Path.GetDirectoryName(full); } catch { return false; }
            return parent != null
                && string.Equals(Path.GetFileName(parent), AppsFolderName, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool UnderAppsFolder(string full)
        {
            if (string.IsNullOrEmpty(full)) return false;
            string[] parts = full.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
                if (string.Equals(parts[i], AppsFolderName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // 先信档案里的 Root 再从可执行文件往上找 最多四层
        //   带标记的目录直接算根 没标记的一路爬到 WeGameApps 的直接子目录为止
        public static string ResolveGameRoot(string profileRoot, string executablePath)
        {
            if (IsWeGameGameRoot(profileRoot)) return NormalizeDir(profileRoot);
            if (string.IsNullOrEmpty(executablePath)) return null;
            string dir;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(executablePath)); }
            catch { return null; }
            for (int depth = 0; depth <= MaxParentWalk && !string.IsNullOrEmpty(dir); depth++)
            {
                if (HasRootMarker(dir) || DirectChildOfAppsFolder(dir)) return NormalizeDir(dir);
                string parent;
                try { parent = Path.GetDirectoryName(dir); } catch { break; }
                if (parent == null) break;
                dir = parent;
            }
            return null;
        }

        private static string NormalizeDir(string dir)
        {
            try { return Path.GetFullPath(dir.Trim().Trim('"')).TrimEnd('\\'); }
            catch { return null; }
        }

        // 什么时候可以动手 对局在跑 已过稳定期 本局没脱过 没熔断 没停手
        public static bool ShouldClean(long sessionStartTicks, long nowTicks, bool cleanedThisSession,
            bool fused, bool circuitOpen, bool autoEnabled)
        {
            if (!autoEnabled || fused || circuitOpen || cleanedThisSession) return false;
            if (sessionStartTicks <= 0 || nowTicks < sessionStartTicks) return false;
            return nowTicks - sessionStartTicks >= StabilizeSeconds * TimeSpan.TicksPerSecond;
        }

        // 游戏在脱壳后这么快就没了 算壳被依赖 timer 复查用短窗 结束通知用带宽限的长窗
        public static bool ExitBlamesCleanup(long cleanedTicks, long exitTicks, bool fromNotification)
        {
            if (cleanedTicks <= 0 || exitTicks < cleanedTicks) return false;
            long window = (fromNotification ? ExitFuseNotifySeconds : ExitFuseSeconds) * TimeSpan.TicksPerSecond;
            return exitTicks - cleanedTicks <= window;
        }

        // 记一轮真正结束了进程的脱壳 五分钟内满三轮返回真 表示壳在反复重生
        public static bool RegisterKillCycle(Queue<long> cycles, long nowTicks)
        {
            if (cycles == null) return false;
            long window = RespawnWindowSeconds * TimeSpan.TicksPerSecond;
            while (cycles.Count > 0 && nowTicks - cycles.Peek() > window) cycles.Dequeue();
            cycles.Enqueue(nowTicks);
            return cycles.Count >= RespawnLimit;
        }

        // 下一次该在什么时候复查 刚脱完先看游戏还活着没 之后按重生周期
        public static int NextCheckDelayMs(bool justCleaned)
        {
            return (justCleaned ? ExitFuseSeconds : RespawnCheckSeconds) * 1000;
        }
    }
}

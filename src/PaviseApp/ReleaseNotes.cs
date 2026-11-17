using System;

namespace PaviseApp
{
    internal static class ReleaseNotes
    {
        private const string SeenKey = "LastSeenNotesVersion";

        public static readonly ReleaseNote[] All = new ReleaseNote[1]
        {
            new ReleaseNote("3.3.1.2", "2026-11-16", new string[][] {
                new[] { "移除独立的反作弊引擎和深度调优中的反作弊压制选项：ACE（安装在 Program Files\\AntiCheatExpert 下）改为随普通后台治理压制，其余反作弊只保护；旧版本留下的反作弊写入不再恢复。",
                    "Removed the separate anti-cheat engine and the anti-cheat option in advanced tuning: ACE installed under Program Files\\AntiCheatExpert is now governed as ordinary background, other anti-cheat software is only protected, and writes left by earlier versions are no longer restored." },
                new[] { "WeGame 主进程改为按普通后台治理，不再因为是英雄联盟的启动来源或开着窗口而豁免。",
                    "The WeGame main process is now governed as ordinary background instead of being exempt as the League of Legends launcher or because its window is open." },
                new[] { "渲染线程调度的“游戏优先”恢复 3.3.0.8 的做法并标为推荐，未保存方案的游戏默认使用它：游戏进程设为高，磁盘与显卡同时提优，渲染线程设为最高、配合线程略提；不再固定核心，也不再提供清核和资源子开关。",
                    "Game priority returns to the 3.3.0.8 approach, is marked recommended, and is the default for games without a saved strategy: the game process runs at High with disk and GPU priority, the render thread at highest and its partner thread slightly higher; it no longer pins a core and has no clear-core or resource sub-switches." },
                new[] { "“多媒体调度”恢复为独立开关，默认关闭，配置预设不开启；开启后只在保护开启期间写入，关闭即恢复。",
                    "Multimedia scheduling is a separate switch again, off by default and not enabled by presets; when on, it applies only while protection is on and restores when turned off." },
                new[] { "不再恢复 1.x、2.x 版本留下的系统改动（如中断迁移、内核保留核、IFEO 优先级、旧 AMD 显卡项等），仍存在的会保持现值；3.x 写入的内容照常恢复。",
                    "Changes left by 1.x and 2.x builds (such as interrupt relocation, kernel reserved cores, IFEO priorities and retired AMD graphics items) are no longer restored and keep their current values; anything written by 3.x builds is still restored." },
                new[] { "微软后台治理并入普通后台治理：不再单独扫描，也不再使用独立 Job 与 0.01% CPU 配额，服务白名单现在也能豁免这些服务；后台下载活动改为每局只检查一次。",
                    "Microsoft background governance is now part of ordinary background governance: no separate scan, dedicated Job, or 0.01% CPU quota, and service allowlists can exempt these services; background download activity is checked once per match." },
                new[] { "优化运行开销与界面缩放；新增服务白名单，仅豁免普通后台。",
                    "Reduced runtime overhead and improved UI scaling; added service whitelisting for ordinary background control only." },
            }),

        };

        public static ReleaseNote Current
        {
            get
            {
                ReleaseNote[] all = All;
                foreach (ReleaseNote releaseNote in all)
                {
                    if (string.Equals(releaseNote.Version, App.Version, StringComparison.OrdinalIgnoreCase))
                    {
                        return releaseNote;
                    }
                }
                return null;
            }
        }

        public static bool HasUnseen
        {
            get
            {
                return !string.Equals(Settings.LoadStr(SeenKey, ""), App.Version, StringComparison.OrdinalIgnoreCase);
            }
        }

        public static bool MarkSeen()
        {
            string saved;
            return Settings.SaveStrDurable(SeenKey, App.Version)
                && Settings.TryLoadStr(SeenKey, out saved)
                && string.Equals(saved, App.Version, StringComparison.OrdinalIgnoreCase);
        }
    }
}

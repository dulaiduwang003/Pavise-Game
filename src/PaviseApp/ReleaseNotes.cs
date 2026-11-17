using System;

namespace PaviseApp
{
    internal static class ReleaseNotes
    {
        private const string SeenKey = "LastSeenNotesVersion";

        public static readonly ReleaseNote[] All = new ReleaseNote[1]
        {
            new ReleaseNote("3.3.1.2", "2026-11-16", new string[][] {
                new[] { "移除独立的反作弊引擎：ACE（安装在 Program Files\\AntiCheatExpert 下）改为随普通后台治理压制，其余反作弊只保护；旧版本留下的反作弊写入不再恢复。深度调优的“反作弊压制”改为逐游戏的单一开关，默认开启，关闭后该游戏对局中 ACE 只保护不压制，切换后几秒内生效。",
                    "Removed the separate anti-cheat engine: ACE installed under Program Files\\AntiCheatExpert is now governed as ordinary background, other anti-cheat software is only protected, and writes left by earlier versions are no longer restored. Anti-cheat suppression in advanced tuning is now a single per-game switch, on by default; when off, ACE is only protected during that game's matches. Changes take effect within seconds." },
                new[] { "白名单新增内置“语音通话软件”：KOOK、YY、TeamSpeak、Mumble、Discord 在对局中不再被后台治理压制，开黑语音不因此卡顿；只认进程名与程序文件名一致的进程。",
                    "Added a built-in Voice chat apps whitelist category: KOOK, YY, TeamSpeak, Mumble and Discord are no longer governed as background during matches, so voice chat is not starved; only processes whose name matches the program file are recognized." },
                new[] { "恢复 AI 调优提示词：设置 → 常规可生成包含本机硬件、全部功能与当前设置的提示词，复制给 AI 获取建议。",
                    "The AI tuning prompt is back: Settings → General generates a prompt with this PC's hardware, every feature and the current settings to copy to an AI for advice." },
                new[] { "WeGame 主进程改为按普通后台治理，不再因为是英雄联盟的启动来源或开着窗口而豁免。",
                    "The WeGame main process is now governed as ordinary background instead of being exempt as the League of Legends launcher or because its window is open." },
                new[] { "渲染线程调度的“游戏优先”标为推荐，未保存方案的游戏默认使用它：游戏进程设为高，磁盘与显卡同时提优，渲染线程设为最高、配合线程略提；不固定核心，也不提供清核和资源子开关。",
                    "Game priority is marked recommended and is the default for games without a saved strategy: the game process runs at High with disk and GPU priority, the render thread at highest and its partner thread slightly higher; it does not pin a core and has no clear-core or resource sub-switches." },
                new[] { "渲染线程调度改为默认开启，进游戏即按“游戏优先”提优，新加入的游戏不用再逐个打开；手动关过的游戏保持关闭，保存过其他方案但没打开过开关的游戏也保持关闭。",
                    "Render thread scheduling is now on by default: games get Game priority as soon as a match starts, so newly added games no longer need it switched on one by one. Games you turned off stay off, and so do games that saved another strategy but never had the switch turned on." },
                new[] { "“多媒体调度”恢复为独立开关，默认开启，两套配置预设也开启；只在保护开启期间写入，关闭即恢复。",
                    "Multimedia scheduling is a separate switch again, on by default and enabled by both presets; it applies only while protection is on and restores when turned off." },
                new[] { "加强渲染线程调度的其他方案：渲染优先、渲染专核、多核协同和依赖协同现在也把游戏进程设为高，渲染线程随之提到最高，提优成员比其他游戏线程高一级；渲染专核会再把渲染线程固定在一个独立核心上。专核强化不变。",
                    "Strengthened the other render scheduling strategies: Render priority, Dedicated rendering, Multicore coordination and Dependency coordination now also run the game process at High, which lifts the render thread to the highest level and boosted members one step above other game threads; Dedicated rendering also keeps the render thread on one dedicated core. Enhanced dedicated rendering is unchanged." },
                new[] { "后台治理“激进”档新增后台收核：普通后台只在一小组核心上运行，把游戏常用的核心让出来（大小核用能效核，X3D 用不带 3D 缓存的一组，双 CCD 用另一个 CCD，其余机型用 0 号核和末尾核）；只在各进程原范围内收紧，退局恢复。“正常”档不变。",
                    "Aggressive background governance now also confines background apps to a small set of cores so the game keeps its preferred ones (efficiency cores on hybrid CPUs, the non-3D-cache CCD on X3D, the other CCD on dual-CCD CPUs, core 0 and the last cores elsewhere). It only narrows each process's existing range and restores at game end. Normal is unchanged." },
                new[] { "渲染线程调度开启时，对局中给游戏锁定高性能 QoS：游戏没焦点或被遮挡时不会被系统降到能效核，Win11 上它的计时器精度请求照常生效；关闭调度或退局时恢复。",
                    "With render thread scheduling on, the game is held at high-performance QoS during matches: Windows does not move it to efficiency cores when it loses focus or is covered, and on Windows 11 its timer-resolution requests still apply. Restored when scheduling is turned off or the match ends." },
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

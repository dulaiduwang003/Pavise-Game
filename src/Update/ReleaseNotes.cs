using System;

namespace PaviseApp
{
    internal static class ReleaseNotes
    {
        private const string SeenKey = "LastSeenNotesVersion";

        public static readonly ReleaseNote[] All = new ReleaseNote[1]
        {
            new ReleaseNote("3.3.1.3", "2026-11-19", new string[][] {
                new[] { "新增逐游戏「反作弊压制」，支持 ACE、EasyAntiCheat 和 BattlEye，默认关闭。",
                    "Added an optional per-game setting for ACE, EasyAntiCheat and BattlEye suppression. It is off by default." },
                new[] { "普通后台治理跳过系统服务和 Windows 目录下的组件，移除服务白名单。",
                    "Background limits now exclude system services and components in the Windows folder. Removed the service whitelist." },
                new[] { "后台治理新增对 Steam 64 位覆盖层、Edge 游戏助手、网易 UU 启动器和 Oopz 的保护。",
                    "Background limits now spare the 64-bit Steam overlay, Edge Game Assist, the NetEase UU launcher and Oopz." },
                new[] { "外设保护新增飞智等品牌，并支持按设备连接或自定义关键词识别驱动。",
                    "Peripheral protection now covers Flydigi and other brands and can identify drivers by device connection or custom keywords." },
                new[] { "激进档为游戏留出首选核心，双 CCD 处理器优先使用首选核所在的 CCD。",
                    "Aggressive mode keeps the preferred core available for games and favors its CCD on dual-CCD processors." },
                new[] { "后台治理恢复先前调低的 GPU 优先级，磁盘限速失败时仍保留 CPU 限额和 Idle 优先级。",
                    "Background limits restore previously lowered GPU priorities and retain CPU caps and Idle priority if disk limits fail." },
                new[] { "修复微信等程序在退局后仍以低优先级运行的问题。",
                    "Fixed helper processes in apps such as WeChat staying at low priority after a match." },
                new[] { "移除恢复守护进程，异常退出后由 Pavise 在下次启动时恢复设置。",
                    "Pavise now restores settings on the next launch after a crash, without a separate guard process." },
                new[] { "全局计时器按本机实测选档，支持 Windows 10。",
                    "Global timer resolution is now selected by testing the available settings on the PC, including on Windows 10." },
                new[] { "移除一键配置、缓存预热及多项图形和系统调优开关，保留这些开关写入的设置。",
                    "Removed one-click setup, cache warm-up and several graphics and system tuning options. Their existing settings are preserved." },
                new[] { "旧计时器节拍和 NTFS 调整会自动还原，体检可恢复 Pavise 改过的前台时间片。",
                    "Old timer-tick and NTFS tweaks are restored automatically. Checkup can restore foreground scheduling changes made by Pavise." },
                new[] { "对局采集和 CPU 核型探测组件已内置于 Pavise.exe。",
                    "Match tracing and CPU core-type detection are now built into Pavise.exe." },
                new[] { "主导航「调优」更名为「环境」。",
                    "Renamed Tuning to Environment in the main navigation." },
                new[] { "环境页将多项系统增强合并为一个开关，并加入「待机内存清理」及其参数。",
                    "Environment now groups system enhancements under one switch and includes standby memory cleanup and its settings." },
                new[] { "界面缩放移至外观页顶部，移除 12 款动效，预览改用静态缩略图。",
                    "UI scale moved to the top of Appearance. Removed 12 animations and replaced live previews with thumbnails." },
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

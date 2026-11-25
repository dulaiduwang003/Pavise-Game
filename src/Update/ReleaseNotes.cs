using System;

namespace PaviseApp
{
    internal static class ReleaseNotes
    {
        private const string SeenKey = "LastSeenNotesVersion";

        public static readonly ReleaseNote[] All = new ReleaseNote[1]
        {
            new ReleaseNote("3.3.1.6", "2026-11-24",
                new[] { "Pavise 免费开源，反馈问题时请保持礼貌。", "Pavise is free and open source. Please be courteous when reporting issues." },
                new string[][]
                {
                    new[] { "调整后台治理。",
                        "Adjusted background governance." },
                    new[] { "「进程规则」页新增「重点压制」，仅在激进档生效。白名单的「添加进程」也能选中没有窗口的后台程序。",
                        "Process rules now includes Focused suppression, which applies in Aggressive only. Add process in the whitelist also lists background apps without a window." },
                    new[] { "外设保护改为按进程名和程序信息识别，补充了更多外设支持，减少普通程序被误放行。",
                        "Peripheral protection now matches process names and program information, covers more peripherals, and exempts fewer unrelated apps." },
                    new[] { "游戏库的深度调优改为双栏，新增逐游戏「NVIDIA 低延迟」，默认关闭。",
                        "Advanced tuning in the Library has two columns and adds per-game NVIDIA Low Latency, off by default." },
                    new[] { "环境页新增显卡伪装，可将独显名称显示为低端、高端或旗舰型号。",
                        "Added GPU spoofing to Environment to display discrete GPU names as entry-level, high-end, or flagship models." },
                    new[] { "反作弊压制只作用于 ACE，改用 Job 锁定优先级和核心。",
                        "Anti-cheat suppression applies only to ACE and now uses Jobs to lock priority and CPU affinity." },
                    new[] { "系统增强新增「多媒体调度」，启用后需重启电脑。",
                        "System enhancement adds multimedia scheduling. Restart the PC after enabling it." },
                    new[] { "渲染线程亲和性移除自动模式，只保留渲染单核和手动。",
                        "Removed Automatic mode from render thread affinity. Single render core and Manual remain." },
                    new[] { "渲染单核改用轻量采样，系统采样会话不足时也能识别渲染线程。",
                        "Single render core now uses lightweight sampling so it can identify the render thread even when system trace sessions are scarce." },
                    new[] { "游戏线程提优预留系统采样会话，减少名额不足导致的识别失败。",
                        "Game thread boost reserves a system trace session to reduce identification failures when sessions are scarce." },
                    new[] { "减少对局和界面的无效采样，修复游戏进程启动识别、英雄联盟附加层统计和恢复记录相关问题。",
                        "Reduced unnecessary sampling during matches and in the interface. Fixed issues with game process start detection, League of Legends add-on counts, and recovery records." },
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

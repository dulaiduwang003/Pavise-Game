using System;

namespace PaviseApp
{
    internal static class ReleaseNotes
    {
        private const string SeenKey = "LastSeenNotesVersion";

        public static readonly ReleaseNote[] All = new ReleaseNote[1]
        {
            new ReleaseNote("3.3.1.4", "2026-11-22", new string[][] {
                new[] { "反馈问题请好好说话。Pavise 免费开源，作者不欠任何人。",
                    "Please be civil when reporting issues. Pavise is free and open source, and I don't owe anyone anything." },
                new[] { "后台治理档位改为关闭、正常、激进、疯狂四项。",
                    "Background governance now has four levels: Off, Normal, Aggressive and Insane." },
                new[] { "激进档每个后台进程最多用 0.5 个核、磁盘限速 10 MiB/s，键鼠驱动、改键工具和耳机软件仍能按时响应。",
                    "Aggressive caps each background app at 0.5 of a core and 10 MiB/s of disk; keyboard and mouse drivers, remapping tools and headset software still respond on time." },
                new[] { "疯狂档每个后台进程只给全机 0.01% 的 CPU，后台基本停住，切换前会弹出提示。",
                    "Insane gives each background app only 0.01% of the CPU so background apps nearly stop, and asks for confirmation before switching." },
                new[] { "竞技场景切出游戏后，正在操作的程序连同它的子进程完全放开，其余后台照常治理。",
                    "In Competitive, the app you switch to after leaving the game is fully released along with its child processes; other background apps stay governed." },
                new[] { "白名单优先级最高：开启「压制家族进程」后，白名单和 Steam 平台保护覆盖的程序也不会被压制。",
                    "The whitelist always takes priority: with Suppress family processes on, programs covered by the whitelist or Steam platform protection are no longer suppressed." },
                new[] { "加速器保护新增 Steam++（Watt Toolkit）。",
                    "Accelerator protection now covers Steam++ (Watt Toolkit)." },
                new[] { "游戏库深度调优新增逐游戏「关闭 CFG」，默认关闭。",
                    "Added a per-game Disable CFG option in Advanced tuning. It is off by default." },
                new[] { "外设驱动改用原生方式识别，修复部分用户在激进档下键鼠驱动仍被压制的问题。",
                    "Peripheral drivers are now identified natively, fixing keyboard and mouse drivers that some users still saw suppressed on Aggressive." },
                new[] { "降低对局中后台扫描的 CPU 占用和内存分配。",
                    "Background sweeps during matches now use less CPU and memory." },
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

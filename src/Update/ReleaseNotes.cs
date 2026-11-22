using System;

namespace PaviseApp
{
    internal static class ReleaseNotes
    {
        private const string SeenKey = "LastSeenNotesVersion";

        public static readonly ReleaseNote[] All = new ReleaseNote[1]
        {
            new ReleaseNote("3.3.1.5", "2026-11-22", new string[][] {
                new[] { "反馈问题请好好说话。Pavise 免费开源，作者不欠任何人。",
                    "Please be civil when reporting issues. Pavise is free and open source, and I don't owe anyone anything." },
                new[] { "升级后后台治理改回正常档。",
                    "After updating, background governance is reset to Normal." },
                new[] { "疯狂档默认隐藏，需在设置里打开「后台治理疯狂档」；切换前的提示停留 10 秒。",
                    "Insane is hidden by default; turn on Insane background governance in Settings to use it. Its warning stays up for 10 seconds." },
                new[] { "后台治理各档都不挪核。",
                    "No governance level moves background apps to other cores." },
                new[] { "正常档不限制 Windows 临时提优。",
                    "Normal allows Windows priority boosts for background apps." },
                new[] { "激进档每个后台进程 0.5 核、磁盘 10 MiB/s。",
                    "Aggressive: 0.5 of a core and 10 MiB/s of disk per background app." },
                new[] { "疯狂档每个后台进程全机 0.01% CPU、磁盘 1 MiB/s。",
                    "Insane: 0.01% of the CPU and 1 MiB/s of disk per background app." },
                new[] { "开启「压制家族进程」时也不压白名单和 Steam 平台保护覆盖的程序。",
                    "With Suppress family processes on, the whitelist and Steam platform protection still exempt their programs." },
                new[] { "加速器保护新增 Steam++。",
                    "Accelerator protection now covers Steam++." },
                new[] { "游戏库深度调优新增逐游戏「关闭 CFG」，默认关闭。",
                    "Added a per-game Disable CFG option in Advanced tuning. It is off by default." },
                new[] { "外设驱动改用原生识别，修复激进档下键鼠驱动被压。",
                    "Peripheral drivers are now identified natively, fixing keyboard and mouse drivers being limited on Aggressive." },
                new[] { "降低对局中后台扫描的 CPU 和内存占用。",
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

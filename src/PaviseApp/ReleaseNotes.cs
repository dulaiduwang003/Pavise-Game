using System;

namespace PaviseApp
{
    internal static class ReleaseNotes
    {
        private const string SeenKey = "LastSeenNotesVersion";

        public static readonly ReleaseNote[] All = new ReleaseNote[1]
        {
            new ReleaseNote("3.3.0.8", "2026-11-08", new string[][] {
                new[] { "后台治理内置诊断服务、搜索索引和小组件家族名单，正常与激进档联动 CPU 配额、核心及优先级限制，退局自动恢复。",
                    "Background governance now includes diagnostics, search indexing and the Widgets family, with CPU quotas, core placement and priority limits tied to Normal/Aggressive modes and automatic restoration after play." },
                new[] { "专项压制改用 ACE、Easy Anti-Cheat 和 BattlEye 的单色品牌 SVG 标志。",
                    "Replaced the dedicated suppression icons with monochrome ACE, Easy Anti-Cheat and BattlEye brand SVG symbols." },
                new[] { "渲染亲和性支持自动/手动选核与整核隔离，联动游戏核心范围；调整下局生效，关闭立即撤回。",
                    "Added automatic/manual render CPU selection with whole-core isolation. Choices follow the game CPU range; edits apply next session and disabling withdraws the binding immediately." },
                new[] { "精简渲染选核窗口，多核列表独立滚动，保存与取消按钮始终可见。",
                    "Simplified the render CPU selector with a scrolling core grid that keeps Save and Cancel visible." },
                new[] { "减少线程采样、核心隔离和后台治理开销，移除自动后台节能显卡登记。",
                    "Reduced thread-sampling, core-isolation and background-governance overhead; removed automatic background power-saving GPU enrollment." },
                new[] { "新增动作/诊断日志，直观查看压制、落核、清核及恢复结果，支持分别导出。",
                    "Added Action/Diagnostic logs for suppression, CPU placement, core isolation and restoration, with separate exports." },
                new[] { "优化鸣潮等游戏识别，减少内置网页被误认作游戏本体。",
                    "Improved game detection for Wuthering Waves and others to reduce embedded web views being mistaken for the game." },
                new[] { "改善笔记本厂商电源兼容，连续冲突时停止切换并恢复原计划。",
                    "Improved compatibility with laptop vendor power controls; repeated conflicts stop plan switching and restore the original plan." },
                new[] { "新增安全软件兼容提示，启动时检查一次，支持“不再显示”。",
                    "Added a security-software compatibility notice checked once at startup, with a Don't show again option." },
                new[] { "修复退局恢复、电源状态误报，减少重复日志和无效警告。",
                    "Fixed false restoration and power-state reports, and reduced repetitive logs and unnecessary warnings." },
            }),

        };

        public static ReleaseNote Current
        {
            get
            {
                foreach (ReleaseNote releaseNote in All)
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

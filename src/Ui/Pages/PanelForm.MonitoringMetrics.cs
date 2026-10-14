using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private const MonitoringMetrics CommonMonitoringMetrics = MonitoringMetrics.Fps | MonitoringMetrics.Low1
            | MonitoringMetrics.FrameTime | MonitoringMetrics.GpuUsage | MonitoringMetrics.GpuTemperature
            | MonitoringMetrics.VideoMemory | MonitoringMetrics.CpuUsage | MonitoringMetrics.CpuTemperature | MonitoringMetrics.SystemMemory;
        private Panel monitoringMetricDetailsPanel;
        private PillButton btnMonitoringMetricDetails, btnMonitoringMetricsReset;
        private Label lblMonitoringMetricSelection, lblMonitoringMetricDetailsSummary;
        private Label lblMonitoringMetricWarningLegend, lblMonitoringMetricCriticalLegend;
        private bool monitoringMetricsExpanded;

        private int BuildMonitoringMetricsCard(Control parent, int y)
        {
            var metrics = MakeConsolePanel(parent, 6, y, ScrollContentW, 64, false);
            monitoringMetricsPanel = metrics; metrics.Name = "monitoringMetricsCard";
            CardLabel(metrics, MonitoringText("显示指标", "Metrics"), 18, 10, 220, 26, 10f, true, Theme.Fg);
            lblMonitoringMetricSelection = CardLabel(metrics, "", ScrollContentW - 528, 13, 150, 22, 8.5f, false, Theme.Dim);
            lblMonitoringMetricSelection.Name = "monitoringMetricSelection";
            lblMonitoringMetricSelection.TextAlign = ContentAlignment.MiddleRight;
            btnMonitoringMetricsReset = new PillButton(MonitoringText("恢复默认指标", "Reset metrics")) { Name = "monitoringMetricsReset" };
            btnMonitoringMetricsReset.SetBounds(Theme.S(ScrollContentW - 356), Theme.S(10), Theme.S(164), Theme.S(28));
            btnMonitoringMetricsReset.AccessibleDescription = MonitoringText("仅保留帧率、帧时间、GPU 利用率和 GPU 温度；不改变监测开关与外观。",
                "Select only FPS, frame time, GPU usage and GPU temperature. Monitoring and appearance settings stay unchanged.");
            btnMonitoringMetricsReset.Click += delegate {
                if (!AllowMonitoringInteraction()) return;
                SetMonitoringResult(MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Default));
            };
            metrics.Controls.Add(btnMonitoringMetricsReset);
            btnMonitoringMetricDetails = new PillButton("") { Name = "monitoringMetricDetailsToggle" };
            btnMonitoringMetricDetails.SetBounds(Theme.S(ScrollContentW - 182), Theme.S(10), Theme.S(164), Theme.S(28));
            btnMonitoringMetricDetails.AccessibleDescription = MonitoringText("展开选择全部 18 项指标，收起保留已选项。", "Choose from all 18 metrics. Collapsing preserves selections.");
            btnMonitoringMetricDetails.Click += delegate {
                if (!AllowMonitoringInteraction()) return;
                monitoringMetricsExpanded = !monitoringMetricsExpanded;
                SyncMonitoringMetricsPresentation(); ReflowMonitoringSettings();
            };
            metrics.Controls.Add(btnMonitoringMetricDetails);
            lblMonitoringMetricDetailsSummary = CardLabel(metrics, "", 18, 39, ScrollContentW - 36, 22, 8.2f, false, Theme.Dim);
            lblMonitoringMetricDetailsSummary.Name = "monitoringMetricDetailsSummary";
            lblMonitoringMetricDetailsSummary.AutoEllipsis = true;
            lblMonitoringSelectedNames = lblMonitoringMetricDetailsSummary;
            monitoringMetricDetailsPanel = new Panel { Name = "monitoringMetricDetails", BackColor = Color.Transparent, Visible = false };
            monitoringMetricDetailsPanel.SetBounds(Theme.S(18), Theme.S(78), Theme.S(ScrollContentW - 36), Theme.S(324));
            metrics.Controls.Add(monitoringMetricDetailsPanel);
            int width = (ScrollContentW - 60) / 3;
            BuildMonitoringMetricGroup(monitoringMetricDetailsPanel, "monitoringMetricFrames", MonitoringText("帧率与帧时间", "Frame rate and timing"), 0, 0, width, 140,
                new[] { MonitoringMetrics.Fps, MonitoringMetrics.Low1, MonitoringMetrics.FrameTime },
                new[] { MonitoringText("帧率 (FPS)", "Frame rate (FPS)"), MonitoringText("1% Low 帧率", "1% low FPS"), MonitoringText("帧时间", "Frame time") },
                new[] { MonitoringText("直接读取 RTSS 的平滑 FPS，遵循 RTSS 的平滑周期；始终跟随当前模式的主题色。", "Reads RTSS's native smoothed FPS using its averaging interval; follows the current mode theme color."),
                    MonitoringText("无 RTSS 活跃基准记录时，取最近最多 6,000 帧的线性 P99 帧时间边界并换算帧率（nearest-rank）；至少需 300 帧且持续 3 秒。有活跃记录时，平均帧率与 Low 整组采用 RTSS 原生统计及其窗口，不混用自动窗口。",
                        "Without an active RTSS benchmark, converts the nearest-rank P99 frame time of up to 6,000 recent frames to FPS; at least 300 frames and 3 seconds. During a benchmark, average and lows use RTSS's native statistics and window as one source."),
                    MonitoringText("优先读取 RTSS 导出的新鲜 NVIDIA Reflex GPU 帧完成周期（GPU TIME）；不可用时显示 RTSS 呈现调用间隔（PRESENT）。帧生成时 PRESENT 不等于原生渲染或屏幕显示间隔；GPU TIME 也不保证等同游戏引擎/CPU 帧时间，不以 FPS 倒数代替测量。",
                        "Prefers the fresh NVIDIA Reflex GPU completion interval exported by RTSS (GPU TIME); otherwise shows RTSS presentation-call timing (PRESENT). With frame generation, PRESENT is not native render or display timing. GPU TIME need not match engine/CPU frame time; neither value is derived from FPS.") });
            BuildMonitoringMetricGroup(monitoringMetricDetailsPanel, "monitoringMetricGpu", "GPU", width + 12, 0, width, 140,
                new[] { MonitoringMetrics.GpuUsage, MonitoringMetrics.GpuTemperature, MonitoringMetrics.VideoMemory },
                new[] { MonitoringText("GPU 利用率", "GPU usage"), MonitoringText("GPU 温度", "GPU temperature"), MonitoringText("显存用量", "VRAM usage") },
                new[] { MonitoringText("整张 GPU 的占用率；满负载通常正常，不因高占用标红。", "Whole-GPU usage; full load is often normal and does not trigger a red warning."),
                    MonitoringText("GPU 温度：80°C 起黄色提醒，88°C 起红色提醒。", "GPU temperature: yellow from 80°C, red from 88°C."),
                    MonitoringText("同一项显示显存已用容量与占用率；任一数据不可用时显示另一项。占用率 85% 起黄色提醒，95% 起红色提醒。",
                        "Shows used VRAM and its percentage together; if either value is unavailable, shows the other. Yellow from 85%, red from 95%.") });
            BuildMonitoringMetricGroup(monitoringMetricDetailsPanel, "monitoringMetricSystem", MonitoringText("CPU 与内存", "CPU and memory"), width * 2 + 24, 0, width, 140,
                new[] { MonitoringMetrics.CpuUsage, MonitoringMetrics.CpuTemperature, MonitoringMetrics.SystemMemory },
                new[] { MonitoringText("游戏 CPU 占用率", "Game CPU usage"), MonitoringText("CPU 封装温度", "CPU package temp"), MonitoringText("系统内存用量", "System RAM usage") },
                new[] { MonitoringText("游戏占整机 CPU 容量的比例；85% 起黄色、95% 起红色，仅提示资源压力。", "The game's share of total CPU capacity; yellow from 85%, red from 95% indicate resource pressure."),
                    MonitoringText("读取 RTSS 硬件数据源唯一的 CPU 封装/Die 温度；不支持或来源不唯一显示 --。85°C 起黄色、95°C 起红色，仅作显示提醒，并非硬件安全限值。",
                        "Reads the unique CPU package/die temperature from the RTSS hardware source; unavailable or ambiguous sources show --. Yellow from 85°C, red from 95°C are display reminders, not hardware safety limits."),
                    MonitoringText("同一项显示整机已用物理内存与占用率；任一数据不可用时显示另一项。占用率 85% 起黄色提醒，95% 起红色提醒。",
                        "Shows physical RAM used by the system and its percentage together; if either value is unavailable, shows the other. Yellow from 85%, red from 95%.") });
            BuildMonitoringMetricGroup(monitoringMetricDetailsPanel, "monitoringMetricDetailedFrames", MonitoringText("帧率统计", "Frame statistics"), 0, 152, width, 172,
                new[] { MonitoringMetrics.AverageFps, MonitoringMetrics.Low01, MonitoringMetrics.FrameTimeDeviation },
                new[] { MonitoringText("平均帧率", "Average FPS"), MonitoringText("0.1% Low 帧率", "0.1% low FPS"), MonitoringText("帧时间标准差", "Frame-time std dev") },
                new[] { MonitoringText("无 RTSS 活跃基准记录时，按最近最多 6,000 个实际帧的总时长计算平均帧率；至少需 300 帧且持续 3 秒。有活跃记录时，平均帧率与 Low 整组使用 RTSS 原生记录口径，可能覆盖整段记录而非自动窗口。",
                        "Without an active RTSS benchmark, averages up to 6,000 recent actual frames; at least 300 frames and 3 seconds. During a benchmark, average and lows use RTSS's recording definition and window, which may cover the full recording."),
                    MonitoringText("无 RTSS 活跃基准记录时，取最近最多 6,000 帧的线性 P99.9 帧时间边界并换算帧率（nearest-rank）；至少需 1,000 帧且持续 3 秒。有活跃记录时整组采用 RTSS 原生统计，遵循其线性/耗时分位模式与记录窗口。",
                        "Without an active RTSS benchmark, converts the nearest-rank P99.9 frame time of up to 6,000 recent frames to FPS; at least 1,000 frames and 3 seconds. During a benchmark, the group uses RTSS's native linear/integral mode and recording window."),
                    MonitoringText("最新最多 6,000 实际帧的帧时间标准差；至少需 300 帧且持续 3 秒，越低通常代表帧时间更稳定。",
                        "Frame-time standard deviation in the latest 6,000 actual frames; at least 300 frames and 3 seconds. A lower value usually indicates more consistent timing.") });
            BuildMonitoringMetricGroup(monitoringMetricDetailsPanel, "monitoringMetricDetailedGpu", MonitoringText("GPU 详情", "GPU details"), width + 12, 152, width, 172,
                new[] { MonitoringMetrics.GpuClock, MonitoringMetrics.GpuPower, MonitoringMetrics.GpuMemoryClock, MonitoringMetrics.GpuFan },
                new[] { MonitoringText("GPU 核心频率", "GPU core clock"), MonitoringText("GPU 功耗", "GPU power"),
                    MonitoringText("显存频率", "VRAM clock"), MonitoringText("GPU 风扇转速", "GPU fan speed") },
                new[] { "", MonitoringText("整张 GPU 的功耗；不按固定瓦数判断是否异常。", "Whole-GPU power; a fixed wattage is not treated as a fault threshold."),
                    "",
                    MonitoringText("驱动报告的首个 GPU 风扇，按支持值显示 RPM 或百分比；不可用显示 --。",
                        "The first GPU fan reported by the driver, in RPM or percent according to support; unavailable values show --.") });
            BuildMonitoringMetricGroup(monitoringMetricDetailsPanel, "monitoringMetricDetailedSystem", MonitoringText("CPU 与内存详情", "CPU and memory details"), width * 2 + 24, 152, width, 172,
                new[] { MonitoringMetrics.CpuPower, MonitoringMetrics.GameRam },
                new[] { MonitoringText("CPU 封装功耗", "CPU package power"), MonitoringText("游戏内存工作集", "Game working set") },
                new[] { MonitoringText("读取 RTSS 硬件数据源的整颗 CPU 封装功耗，并非游戏功耗；硬件或驱动不支持时显示 --。", "CPU package power from the RTSS hardware source, not the game's power; shows -- when hardware or driver support is unavailable."),
                    MonitoringText("游戏进程的工作集，即当前驻留物理内存的页面，包含共享页，不包含子进程；并非整机内存或专用内存。",
                        "The game process working set: pages resident in physical memory, including shared pages and excluding child processes; not system-wide or private memory.") });
            var definitions = CardLabel(monitoringMetricDetailsPanel, MonitoringText(
                "CPU 封装功耗/温度与 GPU 由 RTSS 提供；系统内存直接读取 Windows。GPU 为唯一独显或唯一导出显卡整卡数据。\n游戏 CPU 为占整机比例；游戏工作集包含共享页。内存与显存合并容量和占用率，不支持显示 --。\nFPS 遵循 RTSS 平滑周期；GPU TIME 为 Reflex GPU 完成周期，PRESENT 为呈现调用间隔，不可直接等同引擎或屏显帧时间。\n平均与 Low 默认统计最近 6,000 帧、线性 P99/P99.9；活跃记录时整组采用 RTSS 口径。切换或中断重置自动窗口。",
                "RTSS supplies CPU/GPU sensors; Windows supplies RAM. GPU readings cover the unique discrete or sole exported GPU.\nGame CPU is its share of total capacity; game RAM includes shared pages. RAM/VRAM combine amount and usage; missing data shows --.\nFPS uses RTSS smoothing. GPU TIME: Reflex GPU completion interval. PRESENT: API call interval, not engine/display timing.\nAvg/lows: recent 6,000 frames, linear P99/P99.9; active benchmarks use RTSS's window. Gaps reset the recent window."),
                0, 0, ScrollContentW - 36, 80, 8.2f, false, Theme.Dim);
            definitions.Name = "monitoringMetricDefinitions";
            definitions.Visible = false;
            definitions.AccessibleDescription = definitions.Text;
            monitoringTips.SetToolTip(btnMonitoringMetricDetails, definitions.Text);
            lblMonitoringFpsLegend = CardLabel(metrics, MonitoringText("FPS · 跟随主题", "FPS · theme color"), 18, 408, 208, 22, 8.2f, false,
                Theme.ModeColor(Theme.CurrentMode));
            lblMonitoringFpsLegend.Name = "monitoringMetricFpsLegend";
            lblMonitoringMetricWarningLegend = CardLabel(metrics, MonitoringText("黄色 · 温度 / 资源压力提醒", "Yellow · temperature / pressure"), 242, 408, 310, 22, 8.2f, false,
                Color.FromArgb(255, Color.FromArgb(MonitoringMetricPresentation.WarningRgb)));
            lblMonitoringMetricWarningLegend.Name = "monitoringMetricWarningLegend";
            lblMonitoringMetricCriticalLegend = CardLabel(metrics, MonitoringText("红色 · 温度 / 压力更高", "Red · higher temperature / pressure"), 570, 408, ScrollContentW - 588, 22, 8.2f, false,
                Color.FromArgb(255, Color.FromArgb(MonitoringMetricPresentation.CriticalRgb)));
            lblMonitoringMetricCriticalLegend.Name = "monitoringMetricCriticalLegend";
            SyncMonitoringMetricsPresentation();
            return y + 74;
        }

        private void BuildMonitoringMetricGroup(Control parent, string name, string title, int x, int y, int width, int height,
            MonitoringMetrics[] keys, string[] names, string[] descriptions)
        {
            var group = MakeConsolePanel(parent, x, y, width, height, false);
            group.Name = name; group.Fill = Theme.CardHover; group.Border = Color.Empty;
            CardLabel(group, title, 16, 10, width - 32, 22, 9f, true, Theme.Fg);
            int columnWidth = width - 32;
            for (int i = 0; i < keys.Length; i++)
            {
                MonitoringMetrics key = keys[i];
                int left = 16, top = 38 + i * 32;
                Label caption = CardLabel(group, names[i], left, top + 1, columnWidth - 64, 23, 8.5f, false, Theme.Fg);
                caption.Name = "monitoringMetricCaption" + key; caption.AccessibleDescription = descriptions[i];
                if (!String.IsNullOrEmpty(descriptions[i])) monitoringTips.SetToolTip(caption, descriptions[i]);
                Toggle toggle = null;
                toggle = MakeSwitch((MonitoringPreferences.Metrics & key) != 0, delegate {
                    if (!AllowMonitoringInteraction()) return;
                    MonitoringMetrics next = toggle.Checked ? MonitoringPreferences.Metrics | key : MonitoringPreferences.Metrics & ~key;
                    if (next == MonitoringMetrics.None)
                    {
                        monitoringMessage = MonitoringText("至少选择一项指标。", "Select at least one metric.");
                        monitoringMessageError = true; SyncMonitoringSettings(); return;
                    }
                    SetMonitoringResult(MonitoringPreferences.TrySetMetrics(next));
                });
                toggle.Name = "monitoringMetric" + key;
                toggle.AccessibleName = names[i];
                toggle.AccessibleDescription = descriptions[i];
                toggle.Bg = Theme.LightMode ? Col.Lerp(Col.Lerp(group.Fill, Theme.Accent, .035f),
                    Col.Lerp(group.Fill, Theme.Bg, .13f), (top + 12.5f) / Math.Max(1, height - 1)) : group.Fill;
                toggle.SetBounds(Theme.S(left + columnWidth - 54), Theme.S(top), Theme.S(46), Theme.S(25));
                group.Controls.Add(toggle); monitoringMetricSwitches.Add(key, toggle);
            }
        }

        private void SyncMonitoringMetricsPresentation()
        {
            if (monitoringMetricDetailsPanel == null || monitoringMetricDetailsPanel.IsDisposed) return;
            int selected = 0;
            var names = new System.Collections.Generic.List<string>();
            foreach (var pair in monitoringMetricSwitches)
                if ((MonitoringPreferences.Metrics & pair.Key) != 0) { selected++; names.Add(pair.Value.AccessibleName); }
            lblMonitoringMetricSelection.Text = MonitoringText("已选 ", "Selected ") + selected + " / 18";
            lblMonitoringSelectedNames.Text = String.Join(" · ", names.ToArray());
            lblMonitoringSelectedNames.AccessibleDescription = lblMonitoringSelectedNames.Text;
            monitoringTips.SetToolTip(lblMonitoringSelectedNames, lblMonitoringSelectedNames.Text);
            btnMonitoringMetricDetails.Text = monitoringMetricsExpanded
                ? MonitoringText("收起指标 ∧", "Hide metrics ∧") : MonitoringText("选择指标 ∨", "Choose metrics ∨");
            btnMonitoringMetricDetails.AccessibleName = btnMonitoringMetricDetails.Text;
            monitoringMetricDetailsPanel.Visible = monitoringMetricsExpanded;
            btnMonitoringMetricsReset.Visible = monitoringMetricsExpanded;
            lblMonitoringFpsLegend.Visible = lblMonitoringMetricWarningLegend.Visible = lblMonitoringMetricCriticalLegend.Visible = monitoringMetricsExpanded;
            monitoringMetricsPanel.Height = Theme.S(monitoringMetricsExpanded ? 436 : 64);
        }

    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private ModuleBanner automationBanner;
        private readonly List<Action> automationSync = new List<Action>();
        private static string AutoText(string zh, string en) { return AutomaticPolicy.Text(zh, en); }
        private PillButton AddAutomationLink(Control parent, string zh, string en, PageId target, int x, int y, int width)
        {
            var button = new PillButton(AutoText(zh, en));
            button.SetBounds(Theme.S(x), Theme.S(y), Theme.S(width), Theme.S(34));
            button.Click += delegate { nav.Select((int)target); };
            parent.Controls.Add(button);
            return button;
        }
        private void BuildAutomationPage()
        {
            automationSync.Clear();
            pagePolicy.SetBounds(0, Theme.S(TopH), Theme.S(WinW), Theme.S(PageH));
            int contentWidth = ContentW + RailW;
            int y = PageHeader(pagePolicy, AutoText("自动执行", "Automatic actions"),
                AutoText("操作名称与参与状态；实际执行和恢复结果见日志。", "Action names and participation states; execution and restoration results are in logs."), 2, false, 0, contentWidth);
            automationBanner = new ModuleBanner();
            automationBanner.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(contentWidth), Theme.S(78));
            automationBanner.Code = "AUTOMATION // LIVE"; automationBanner.Glyph = "shield";
            pagePolicy.Controls.Add(automationBanner); y += 90;
            var scroll = new WorkspacePanel();
            scroll.SetBounds(Theme.S(20), Theme.S(y), Theme.S(WinW - 40), Theme.S(PageH - y - 8));
            scroll.AutoScroll = true; scroll.BackColor = Theme.Bg; Native.Dark(scroll);
            pagePolicy.Controls.Add(scroll);
            int sy = 2, height;
            foreach (PolicyItem item in PolicyCatalog.All)
            {
                if (item.Key == PolicyCatalog.KeyAdaptiveEscalate) continue;
                if (AutomaticPolicy.IsIntent(item.Key) || AutomaticPolicy.IsEnvironment(item.Key) || AutomaticPolicy.IsLowLatency(item.Key)
                    || AutomaticPolicy.IsRetiredInAuto(item.Key)) continue;
                string key = item.Key;
                bool extremeOnly = key == PolicyCatalog.KeyIdlePolicy || key == PolicyCatalog.KeyDwmBoost
                    || key == PolicyCatalog.KeyIntelEndurance;
                var state = new Label { Name = "automationState_" + key, Font = Theme.UI(8.5f, true), ForeColor = Theme.Accent,
                    BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleRight };
                state.Size = new Size(Theme.S(150), Theme.S(30));
                string title = item.Key == PolicyCatalog.KeySuppress ? AutoText("后台治理", "Background protection") : Lang.T(item.LangKey);
                var card = MakeAutoCard(scroll, 6, sy, ScrollContentW + RailW, 48, title, "", state, out height);
                sy += height + 8;
                automationSync.Add(delegate {
                    PolicySnapshot policy = gameMode.AutomationPolicySnapshot;
                    string value = policy.ValueOf(key);
                    bool participates = value != "0" && value != "off";
                    bool unsupportedMode = extremeOnly && policy.Preset != PerformancePreset.Extreme;
                    state.Text = participates ? AutoText("自动参与", "Automatic") : AutoText("本模式不启用", "Not used in this mode");
                    string actual = gameMode.AutomaticExecutionState(key);
                    if (actual == "applied") state.Text = AutoText("已执行", "Applied");
                    else if (actual == "configured") state.Text = AutoText("驱动配置已写入", "Driver configured");
                    else if (actual == "restored") state.Text = AutoText("已恢复", "Restored");
                    else if (actual == "pending-reboot") state.Text = AutoText("已配置 · 待重启", "Restart required");
                    else if (actual == "restore-pending-reboot") state.Text = AutoText("已恢复 · 待重启", "Restored; restart required");
                    else if (actual == "ready") state.Text = AutoText("配置已就绪", "Configuration ready");
                    else if (actual == "unverified") state.Text = Lang.T("env.performance.unverified");
                    else if (actual == "timer-requested") state.Text = AutoText("计时请求已执行", "Timer requested");
                    else if (actual == "timer-locked") state.Text = AutoText("0.5 ms 已生效", "0.5 ms active");
                    else if (actual == "timer-limited") state.Text = AutoText("系统精度未达标", "Resolution limited");
                    else if (actual == "timer-unverified") state.Text = AutoText("实际精度待验证", "Resolution unverified");
                    else if (actual == "unsupported") state.Text = AutoText("系统不支持", "OS not supported");
                    else if (actual == "cooldown") state.Text = AutoText("失败冷却", "Cooldown");
                    else if (actual == "stopped") state.Text = AutoText("失败 · 已停止重试", "Failed; retries stopped");
                    else if (actual == "failed") state.Text = AutoText("执行未完成", "Incomplete");
                    else if (actual == "restore-failed") state.Text = AutoText("恢复未完成", "Restore incomplete");
                    if (participates && key == PolicyCatalog.KeyIntelEndurance && !IntelGraphicsApi.HasAvailable)
                        state.Text = AutoText("本机不适用", "Not supported here");
                    if (participates && key == PolicyCatalog.KeyNvMaxPerf && !Native.OnAcPower())
                        state.Text = AutoText("电池供电不启用", "Not used on battery");
                    if ((key == PolicyCatalog.KeyNvMaxPerf || key == PolicyCatalog.KeyNvShaderCache) && !NvApi.Available)
                        state.Text = AutoText("本机不适用", "Not supported here");
                    if (unsupportedMode)
                        state.Text = AutoText("当前模式不支持", "Mode not supported");
                    else if (key == PolicyCatalog.KeyIdlePolicy && !participates && actual != "restore-failed")
                        state.Text = AutoText("需软件管理电源", "Requires managed power");
                    if (key == PolicyCatalog.KeyPowerPlan && PowerPlan.NoSwitchSelected && actual != "restore-failed")
                        state.Text = Lang.T("plan.pick.off");
                    state.ForeColor = actual == "failed" || actual == "restore-failed" || actual == "cooldown" || actual == "stopped" ? Theme.Warning : participates ? Theme.Accent : Theme.Dim;
                    card.Desc = "";
                    if (unsupportedMode && actual == "restore-pending-reboot")
                        state.Text = AutoText("已恢复 · 待重启", "Restored; restart required");
                    if (unsupportedMode && (actual == "failed" || actual == "restore-failed"))
                        state.Text = AutoText("恢复受限", "Restore failed");
                });
            }
            var cpuCapState = new Label { Name = "automationBackgroundCpuCap", Font = Theme.UI(8.5f, true),
                ForeColor = Theme.Accent, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleRight,
                Size = new Size(Theme.S(200), Theme.S(32)) };
            var cpuCapCard = MakeAutoCard(scroll, 6, sy, ScrollContentW + RailW,
                48,
                Lang.T("cpucap.title"), "", cpuCapState, out height);
            cpuCapCard.Height += Theme.S(SettingCard.StatusLineH);
            automationSync.Add(delegate {
                bool supported = GameMode.CpuCapSupported(gameMode.Preset);
                cpuCapState.Text = supported ? AutoText("冻结备用", "Freeze fallback") : AutoText("本模式不启用", "Not used in this mode");
                cpuCapCard.SetStatus(gameMode.BackgroundCpuCapStatus, Theme.Dim);
            });
            sy += height + SettingCard.StatusLineH + 10;
            var freezeState = new Label { Name = "automationBackgroundFreeze", Font = Theme.UI(8.5f, true),
                ForeColor = Theme.Accent, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleRight,
                Size = new Size(Theme.S(200), Theme.S(32)) };
            var freezeCard = MakeAutoCard(scroll, 6, sy, ScrollContentW + RailW,
                48,
                Lang.T("freeze.title"), "", freezeState, out height);
            freezeCard.Height += Theme.S(SettingCard.StatusLineH);
            automationSync.Add(delegate {
                freezeState.Text = gameMode.Preset == PerformancePreset.Extreme ? AutoText("随模式自动执行", "Automatic with mode") : AutoText("本模式不启用", "Not used in this mode");
                freezeCard.SetStatus(gameMode.BackgroundFreezeStatus, Theme.Dim);
            });
            sy += height + SettingCard.StatusLineH + 10;
            AddAutomationInventory(scroll, ref sy);
            AddAutomationLink(pagePolicy, "查看日志", "View logs", PageId.Log, ContentX + contentWidth - 180, 28, 180);
            RefreshAutomationPage();
        }
        private void RefreshAutomationPage()
        {
            if (automationBanner == null) return;
            automationBanner.TitleText = gameMode.AutomationStatus;
            automationBanner.Detail = AutoText("保护工作应用 · 独立恢复 · 故障立即停用", "Protected work apps · owned restoration · immediate fault isolation");
            automationBanner.State = ModeButton.ModeName(gameMode.ActivePreset);
            automationBanner.StateColor = Theme.Accent;
            foreach (Action sync in automationSync) sync();
        }
    }
}

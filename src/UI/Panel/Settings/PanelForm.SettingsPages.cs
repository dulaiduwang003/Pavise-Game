using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm : Form
    {
        private void BuildAuditPage()
        {
            Native.Dark(pageAudit);
            int pageHeaderResult = PageHeader(pageAudit, Lang.T("nav.audit"), Lang.T("audit.sub"), 2, false);
            btnAuditRun = new PillButton(Lang.F("audit.rerun", 120), BtnKind.Primary);
            btnAuditRun.SetBounds(Theme.S(30), Theme.S(pageHeaderResult), Theme.S(150), Theme.S(34));
            btnAuditRun.Click += delegate
            {
                StartAudit();
            };
            pageAudit.Controls.Add(btnAuditRun);
            int auditPageValue = 158;
            int auditPageValue2 = 150;
            lblAuditStatus = CardLabel(pageAudit, "", 30 + auditPageValue, pageHeaderResult + 8, ContentW - auditPageValue - auditPageValue2 - 12, 20, 8f, false, Theme.Dim);
            btnAuditFixAll = new PillButton(Lang.T("audit.fixall.none"), BtnKind.Primary);
            btnAuditFixAll.SetBounds(Theme.S(30 + ContentW - auditPageValue2), Theme.S(pageHeaderResult), Theme.S(auditPageValue2), Theme.S(34));
            btnAuditFixAll.Enabled = false;
            btnAuditFixAll.Click += delegate
            {
                OnAuditFixAllClick();
            };
            pageAudit.Controls.Add(btnAuditFixAll);
            btnAuditCancel = new PillButton(Lang.T("dlg.cancel"), BtnKind.Normal);
            btnAuditCancel.Name = "auditCancel";
            btnAuditCancel.Bounds = btnAuditFixAll.Bounds;
            btnAuditCancel.Visible = false;
            btnAuditCancel.Click += delegate
            {
                auditCancelRequested = true;
                btnAuditCancel.Enabled = false;
                auditScan.ReportProgress(0f, Lang.T("irqcheck.dialog.stopping"));
            };
            pageAudit.Controls.Add(btnAuditCancel);
            pageHeaderResult += 44;
            auditScroll = new DBPanel();
            auditScroll.SetBounds(Theme.S(20), Theme.S(pageHeaderResult), Theme.S(PageW - 40), Theme.S(Math.Max(180, PageH - pageHeaderResult - 8)));
            auditScroll.BackColor = Theme.Bg;
            auditScroll.AutoScroll = true;
            auditScroll.Visible = false;
            Native.Dark(auditScroll);
            pageAudit.Controls.Add(auditScroll);
            auditScan = new ScanView();
            auditScan.SetBounds(Theme.S(30), Theme.S(pageHeaderResult + 6), Theme.S(ContentW), Theme.S(Math.Max(380, PageH - pageHeaderResult - 20)));
            pageAudit.Controls.Add(auditScan);
            btnAuditStart = new PillButton(Lang.T("audit.start"), BtnKind.Primary);
            btnAuditStart.Bg = Theme.Card;
            btnAuditStart.Click += delegate
            {
                StartAudit();
            };
            auditScan.Controls.Add(btnAuditStart);
            RestoreAuditPagePresentation();
            pageAudit.SizeChanged += delegate
            {
                LayoutAuditViewport();
            };
            LayoutAuditViewport();
        }
        private Dictionary<string, AuditFix> BuildAuditFixes()
        {
            Dictionary<string, AuditFix> dictionary = new Dictionary<string, AuditFix>(StringComparer.Ordinal);
            dictionary.Add("memcompress", new AuditFix
            {
                CanFix = () => auditReport != null && auditReport.MemoryCompressionCanEnable,
                CanRevert = () => MemCompressTweak.OwnsState,
                Fix = delegate
                {
                    ApplyAuditMemoryCompression(false);
                },
                Revert = delegate
                {
                    ApplyAuditMemoryCompression(true);
                },
                FixLabel = "audit.mem.enable"
            });
            dictionary.Add("gpuirq", new AuditFix
            {
                CanFix = () => auditReport != null && auditReport.GpuIrq != null && (auditReport.GpuIrq.Outcome == GpuIrqOutcome.NeedsPlacement || auditReport.GpuIrq.Outcome == GpuIrqOutcome.NeedsVerification) && !GpuIrqCheck.AwaitingReboot(GpuIrqCheck.LoadState()) && !gameMode.IsActive,
                CanRevert = () => false,
                Fix = delegate
                {
                    auditIrqFix = RunIrqCheck();
                },
                FixLabel = ((auditReport != null && auditReport.GpuIrq != null && auditReport.GpuIrq.Outcome == GpuIrqOutcome.NeedsVerification) ? "irqcheck.btn.verify" : "audit.irq.apply")
            });
            dictionary.Add("gpuirqrestore", new AuditFix
            {
                CanFix = () => false,
                CanRevert = () => GpuIrqCheck.HasResidue,
                Revert = delegate
                {
                    OnIrqRestore(null, EventArgs.Empty);
                }
            });
            dictionary.Add("msi", new AuditFix
            {
                CanFix = () => MsiModeTweak.Disabled().Count > 0,
                CanRevert = () => MsiModeTweak.EnabledByPavise,
                Fix = delegate
                {
                    MsiModeTweak.Enable();
                },
                Revert = delegate
                {
                    MsiModeTweak.Restore();
                },
                ConfirmKey = "audit.msi.confirm"
            });
            // 前台时间片已下线 体检只负责把 Pavise 自己留下的时间片记录恢复回去
            dictionary.Add("quantum", new AuditFix
            {
                CanFix = () => false,
                CanRevert = () => QuantumAudit.HasRestorableRecord(),
                Revert = delegate
                {
                    QuantumTweak.Restore();
                }
            });
            dictionary.Add("net", new AuditFix
            {
                CanFix = () => NetTweak.NeedsRepair(),
                CanRevert = () => NetTweak.RepairedByPavise,
                Fix = delegate
                {
                    NetTweak.Repair();
                },
                Revert = delegate
                {
                    NetTweak.Restore();
                }
            });
            dictionary.Add("linkmetric", new AuditFix
            {
                CanFix = () => LinkMetricTweak.NeedsRepair(),
                CanRevert = () => LinkMetricTweak.RepairedByPavise,
                Fix = delegate
                {
                    LinkMetricTweak.Repair();
                },
                Revert = delegate
                {
                    LinkMetricTweak.Restore();
                }
            });
            dictionary.Add("fth", new AuditFix
            {
                CanFix = () => FthTweak.NeedsRepair(),
                CanRevert = () => FthTweak.RepairedByPavise,
                Fix = delegate
                {
                    FthTweak.Repair();
                },
                Revert = delegate
                {
                    FthTweak.Restore();
                },
                ConfirmKey = "audit.fth.confirm"
            });
            return dictionary;
        }
        private void BuildEnvironmentTimerOptions(Control parent, ref int y)
        {
            AddEnvironmentPerformanceOption(parent, ref y, "AutoGlobalTimer", "set.gtimer", "env.gtimer.manual");
            SyncEnvironmentPerformanceOptions();
        }
        private void BuildEnvironmentRecordingOption(Control parent, ref int y)
        {
            swEnvironmentGameDvr = MakeSwitch(gameMode.KillGameDvr, OnEnvironmentGameDvrToggle);
            swEnvironmentGameDvr.Name = "environmentGameDvrOff";
            swEnvironmentGameDvr.AccessibleName = Lang.T("set.dvr");
            string accessibleDescriptionValue = PerformanceRecordingDetail();
            swEnvironmentGameDvr.AccessibleDescription = accessibleDescriptionValue;
            int used;
            cardEnvironmentGameDvr = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(accessibleDescriptionValue, ScrollContentW, swEnvironmentGameDvr, 88), Lang.T("set.dvr"), accessibleDescriptionValue, swEnvironmentGameDvr, out used);
            cardEnvironmentGameDvr.Height += Theme.S(17);
            y += used + 17 + 8;
            SyncEnvironmentGameDvr();
        }
        private void BuildPerformanceTuningEnvironment(Control parent, ref int y)
        {
            swPerformanceEnvironmentBundle = MakeSwitch(false, ChangePerformanceTuningBundle);
            swPerformanceEnvironmentBundle.Name = "environmentEnvPerformanceBundleV1";
            swPerformanceEnvironmentBundle.AccessibleName = PerformanceTuning.EnvironmentBundleTitle;
            string desc = EnvironmentBundlePerformanceDetail();
            swPerformanceEnvironmentBundle.AccessibleDescription = desc;
            int used;
            cardPerformanceEnvironmentBundle = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(desc, ScrollContentW, swPerformanceEnvironmentBundle, 100), PerformanceTuning.EnvironmentBundleTitle, desc, swPerformanceEnvironmentBundle, out used);
            cardPerformanceEnvironmentBundle.Height += Theme.S(17);
            y += used + 17 + 8;
            SyncPerformanceTuningEnvironment();
        }
        private void BuildSettingsPage()
        {
            Interlocked.Increment(ref slowVersion);
            InitializeSettingsWorkspace();
            Control scroll = settingsTabPanels[0];
            swAuto = MakeSwitch(TaskHelper.IsStartupTaskEnabledCached(), OnAutoToggle);
            AddSettingsTile(scroll, "SettingsStartup", Lang.T("set.autostart"), Lang.T("set.autostart.n"), "startup", swAuto).Summary = AutoText("登录后在托盘启动", "Start in the tray after sign-in");
            swAutoHide = MakeSwitch(Settings.Load("AutoHideOnGame", true), OnAutoHideToggle);
            AddSettingsTile(scroll, "SettingsAutoHide", Lang.T("set.autohide"), Lang.T("set.autohide.n"), "hide", swAutoHide).Summary = Lang.T("set.autohide.n");
            int[] exitGraceChoices = new int[6] { 0, 2, 5, 8, 15, 30 };
            TierPicker pickExitGrace = new TierPicker
            {
                Name = "GameExitGracePicker"
            };
            string[] labelsValue = new string[exitGraceChoices.Length];
            for (int i = 0; i < exitGraceChoices.Length; i++)
            {
                labelsValue[i] = Lang.F("set.exitgrace.seconds", exitGraceChoices[i]);
            }
            pickExitGrace.Labels = labelsValue;
            pickExitGrace.Index = Array.IndexOf(exitGraceChoices, gameMode.ExitGraceSeconds);
            pickExitGrace.Size = new Size(Theme.S(404), Theme.S(40));
            pickExitGrace.IndexChanged = delegate(int index)
            {
                if (index >= 0 && index < exitGraceChoices.Length && !gameMode.TrySetExitGraceSeconds(exitGraceChoices[index]))
                {
                    pickExitGrace.Index = Array.IndexOf(exitGraceChoices, gameMode.ExitGraceSeconds);
                    PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("set.exitgrace.fail"));
                }
            };
            AddSettingsTile(scroll, "SettingsExitGrace", Lang.T("set.exitgrace"), Lang.T("set.exitgrace.n"), "clock", pickExitGrace).Summary = AutoText("默认 8 秒 · 修改后立即生效", "Default: 8 s · Changes apply immediately");
            TierPicker tierPicker = new TierPicker();
            tierPicker.Name = "SettingsLanguagePicker";
            TierPicker tierPicker2 = tierPicker;
            tierPicker2.Labels = new string[3]
            {
                Lang.T("lang.zh"),
                Lang.T("lang.en"),
                "PT-BR"
            };
            tierPicker2.Index = Lang.Cur == Lang.Portuguese ? 2 : Lang.Cur;
            tierPicker2.Size = new Size(Theme.S(232), Theme.S(40));
            tierPicker2.IndexChanged = delegate(int index)
            {
                Lang.Set(index == 2 ? Lang.Portuguese : index);
                BeginInvoke(new MethodInvoker(RebuildUi));
            };
            AddSettingsTile(scroll, "SettingsLanguage", Lang.T("set.lang"), Lang.T("set.lang.n"), "language", tierPicker2).Summary = AutoText("界面与新日志一并切换", "Switch the UI and new log messages together");
            BuildAiTuningSettingsCard(scroll);
            scroll = settingsTabPanels[1];
            PillButton pillButton = new PillButton(Lang.T("workflow.attention.action.logs"));
            pillButton.Name = "settingsOpenLogs";
            pillButton.Bg = Theme.Card;
            pillButton.Size = new Size(Theme.S(136), Theme.S(40));
            PillButton pillButton2 = pillButton;
            pillButton2.Click += delegate
            {
                nav.Select(7);
            };
            AddSettingsTile(scroll, "SettingsLogs", Lang.T("nav.log"), Lang.Ui("v20.log.sub"), "logs", pillButton2).Summary = AutoText("查看运行记录与故障详情", "View activity and failure details");
            btnWipeAll = new PillButton(Lang.T("btn.wipe"), BtnKind.Normal)
            {
                Bg = Theme.Card,
                Size = new Size(Theme.S(136), Theme.S(40))
            };
            btnWipeAll.Click += delegate
            {
                OnWipeAll();
            };
            AddSettingsTile(scroll, "SettingsReset", Lang.T("btn.wipe"), Lang.T("set.reset.desc"), "reset", btnWipeAll).Summary = AutoText("恢复本软件修改并清除配置", "Restore Pavise changes and clear its settings");
            btnUninstall = new PillButton(Lang.T("btn.uninstall"), BtnKind.Danger)
            {
                Bg = Theme.Card,
                Size = new Size(Theme.S(128), Theme.S(40))
            };
            btnUninstall.Click += delegate
            {
                OnUninstall();
            };
            AddSettingsTile(scroll, "SettingsUninstall", Lang.T("btn.uninstall"), Lang.T("set.uninstall.desc"), "uninstall", btnUninstall).Summary = AutoText("恢复修改后移除 Pavise", "Restore changes, then remove Pavise");
            btnShaderGo = new PillButton(Lang.T("btn.clean"))
            {
                Size = new Size(Theme.S(112), Theme.S(40))
            };
            btnShaderGo.Click += delegate(object s, EventArgs e)
            {
                OnShaderClean((PillButton)s);
            };
            cardShader = AddSettingsTile(scroll, "SettingsShader", Lang.T("btn.shader"), Lang.T("set.shader.n"), "shader", btnShaderGo);
            ((SettingsWorkspaceTile)cardShader).Summary = AutoText("清理后首次启动游戏需重新编译", "Games recompile shaders on their next launch");
            cardShader.Value = " ";
            SyncShaderCleanupPresentation();
            scroll = settingsTabPanels[2];
            BuildUiScaleSetting(scroll);
            BuildAppearanceSection(scroll);
            LayoutSettingsWorkspace();
        }
        private void BuildUiScaleSetting(Control parent)
        {
            TechSelect picker = new TechSelect
            {
                Name = "SettingsUiScalePicker",
                Size = new Size(Theme.S(220), Theme.S(34)),
                AccessibleName = Lang.T("set.scale")
            };
            string[] choices = new string[10];
            choices[0] = Lang.T("set.scale.windows");
            for (int i = 1; i < choices.Length; i++)
            {
                choices[i] = ((75 + i * 25).ToString(System.Globalization.CultureInfo.InvariantCulture)) + "%";
            }
            picker.SetItems(choices);
            int currentPercent = Dpi.ScalePreferencePercent;
            picker.SetSilently(currentPercent == 0 ? 0 : 1 + (currentPercent - 100) / 25);
            picker.SelectionChanged += delegate(int index)
            {
                int percent = index == 0 ? 0 : 100 + (index - 1) * 25;
                string storedValue = percent.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!Settings.SaveStr("UiScalePercent", storedValue))
                {
                    picker.SetSilently(Dpi.ScalePreferencePercent == 0 ? 0 : 1 + (Dpi.ScalePreferencePercent - 100) / 25);
                    PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("set.scale.fail"));
                    return;
                }
                Dpi.UpdateWindowEnvironment(base.Handle);
                if (Dpi.SetScalePreference(percent))
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        if (base.IsDisposed || base.Disposing || runtimeShutdownBusy) return;
                        Dpi.UpdateWindow(base.Handle);
                        RebuildUiForScale();
                    }));
                }
            };
            AddSettingsTile(parent, "SettingsUiScale", Lang.T("set.scale"), Lang.T("set.scale.n"), "appearance", picker);
        }

    }
}

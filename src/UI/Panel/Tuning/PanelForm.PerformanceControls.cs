using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm : Form
    {
        private static string AutoText(string zh, string en)
        {
            return PerformancePolicy.Text(zh, en);
        }
        internal static PerformanceRestartScope PerformanceRestartFor(string key)
        {
            if (PerformancePolicy.IsIndependentManual(key))
            {
                return PerformanceRestartScope.SystemRestart;
            }
            switch (key)
            {
            case "GameNvRebarV1":
            case "GameDisableFullscreenOptimizationsV1":
            case PolicyCatalog.KeyGameControlFlowGuard:
            case "NvMaxPerf":
            case "NvShaderCache":
                return PerformanceRestartScope.ProcessRestart;
            default:
                return PerformanceRestartScope.None;
            }
        }
        internal static PerformanceLifetime PerformanceLifetimeFor(string key)
        {
            switch (key)
            {
            case "GameDvrOff":
                return PerformanceLifetime.Protection;
            case PolicyCatalog.KeyGameScheduling:
            case "GmBoost":
            case "GmThreadBoostStrategyV1":
            case "GmPauseUpdate":
            case "GmPauseDl":
            case PolicyCatalog.KeyStandbyCleaner:
            case "GmAwake":
            case "NvMaxPerf":
            case "NvShaderCache":
            case "LabEppYieldV1":
            case "LabGpuClockMaxV1":
            case "LabGpuPowerMaxV1":
            case "LabCpuIdleStrategyV1":
                return PerformanceLifetime.Session;
            default:
                return PerformanceLifetime.Persistent;
            }
        }
        private static void ConfigurePerformanceCards(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                PerformanceFeatureCard performanceFeatureCard = control as PerformanceFeatureCard;
                if (performanceFeatureCard != null && performanceFeatureCard.Hosted != null)
                {
                    string performanceFeatureKeyResult = PerformanceFeatureKey(performanceFeatureCard.Hosted.Name);
                    performanceFeatureCard.RestartScope = ((performanceFeatureKeyResult == "EnvPerformanceBundleV1") ? PerformanceRestartScope.SystemRestart : PerformanceRestartFor(performanceFeatureKeyResult));
                    performanceFeatureCard.Lifetime = PerformanceLifetimeFor(performanceFeatureKeyResult);
                    performanceFeatureCard.RestartText = "";
                    performanceFeatureCard.Title = performanceFeatureCard.Title.Replace(" · 需重启", "").Replace(" · restart required", "");
                    ConfigurePerformanceConsole(performanceFeatureCard, performanceFeatureKeyResult);
                    performanceFeatureCard.ArrangePresentation();
                }
                ConfigurePerformanceCards(control);
            }
        }
        private void BuildStandbyCleanerTuningCard(PerformanceFeatureGrid grid, ref int y)
        {
            string title = Lang.T("gm.standbycleaner");
            var options = new PerformanceActionButton(Lang.T("standbycleaner.options"))
            {
                Name = "StandbyCleanerOptions",
                AccessibleName = Lang.T("standbycleaner.options.title"),
                Size = new Size(Theme.S(94), Theme.S(30)),
                TabIndex = 0
            };
            options.Click += delegate { EditStandbyCleanerOptions(); };
            swPolicyStandbyCleaner = MakeSwitch(gameMode.StandbyCleanerEnabled, null);
            swPolicyStandbyCleaner.Name = "environmentStandbyCleaner";
            swPolicyStandbyCleaner.AccessibleName = title;
            swPolicyStandbyCleaner.TabIndex = 1;
            swPolicyStandbyCleaner.CheckedChanged += delegate { OnStandbyCleanerToggle(swPolicyStandbyCleaner.Checked); };

            var actions = new StandbyCleanerActionHost { Name = "manualPerformance_" + PolicyCatalog.KeyStandbyCleaner };
            actions.Controls.Add(options);
            actions.Controls.Add(swPolicyStandbyCleaner);
            int used;
            cardPolicyStandbyCleaner = (PerformanceFeatureCard)MakeAutoCard(grid, 6, y, ScrollContentW, 88,
                title, StandbyCleanerDetail(), actions, out used);
            cardPolicyStandbyCleaner.Name = "TuningStandbyCleanerCard";
            cardPolicyStandbyCleaner.TrackChildHover(options);
            cardPolicyStandbyCleaner.TrackChildHover(swPolicyStandbyCleaner);
            y += used + 8;
            RefreshStandbyCleanerPresentation();
            automationSync.Add(RefreshStandbyCleanerPresentation);
        }

        private void AddManualPolicyControl(Control parent, ref int y, PolicyItem item)
        {
            string key = item.Key;
            if (key == BackgroundGovernancePolicy.SettingKey || key == "GmSuppress") return;
            // 游戏调度与资源优先级由游戏库中的逐游戏配置管理
            if (key == PolicyCatalog.KeyGameScheduling || key == PolicyCatalog.KeyBoost) return;
            string autoTextResult = key == "GmBoost" ? Lang.T("gm.scheduling.resources.title") : ((key == "GmSuppress") ? AutoText("后台治理", "Background protection") : ((key == "GmThreadBoostStrategyV1") ? AutoText("游戏线程提优", "Game thread boost") : Lang.T(item.LangKey)));
            string manualPolicyDescriptionResult = ManualPolicyDescription(key);
            if (item.Kind == PolicyValueKind.Bool)
            {
                AddManualPerformanceToggle(parent, ref y, key, autoTextResult, manualPolicyDescriptionResult, () => PolicyResolver.RawGlobalValue(key) == "1", (bool value) => SaveManualPolicy(item, value ? "1" : "0"), () => ManualPolicySupported(key), () => gameMode.AutomationPolicySnapshot.ValueOf(key) == "1");
                return;
            }
            if (key == "GmThreadBoostStrategyV1")
            {
                AddManualPerformanceToggle(parent, ref y, key, autoTextResult, manualPolicyDescriptionResult, () => ThreadBoostSelection.IsOn(PolicyResolver.RawGlobalValue(key)), (bool value) => SaveManualPolicy(item, value ? "on" : "off"), null, null, gameMode.ThreadBoostExecutionState);
                return;
            }
            string[] choices = (string[])item.Choices.Clone();
            string[] textEntries = new string[choices.Length];
            for (int i = 0; i < choices.Length; i++)
            {
                textEntries[i] = ((choices[i] == "off") ? AutoText("关闭", "Off") : ((choices[i] == "on") ? AutoText("开启", "On") : choices[i]));
            }
            TierPicker picker = new TierPicker
            {
                Name = "manualPerformance_" + key,
                Labels = textEntries,
                AccessibleName = autoTextResult,
                AccessibleDescription = manualPolicyDescriptionResult,
                Size = new Size(Theme.S(180), Theme.S(30))
            };
            int used;
            SettingCard card = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(manualPolicyDescriptionResult, ScrollContentW, picker, 82), autoTextResult, manualPolicyDescriptionResult, picker, out used);
            y += used + 8;
            Action sync = delegate
            {
                string rawGlobalValueResult = PolicyResolver.RawGlobalValue(key);
                int matchIndex = Array.IndexOf(choices, rawGlobalValueResult);
                picker.Index = ((matchIndex >= 0) ? matchIndex : 0);
                bool enabled = rawGlobalValueResult != "0" && rawGlobalValueResult != "off";
                bool manualPolicySupportedSucceeded = ManualPolicySupported(key);
                picker.Enabled = manualPolicySupportedSucceeded || enabled;
                SyncManualPerformanceStatus(card, key, enabled, manualPolicySupportedSucceeded, null);
            };
            picker.IndexChanged = delegate(int index)
            {
                if (!automationUpdating)
                {
                    if (index < 0 || index >= choices.Length)
                    {
                        sync();
                    }
                    else
                    {
                        bool saveManualPolicySucceeded = SaveManualPolicy(item, choices[index]);
                        sync();
                        if (!saveManualPolicySucceeded)
                        {
                            ShowManualPerformanceSaveFailure();
                        }
                    }
                }
            };
            automationSync.Add(sync);
        }
        private bool SaveManualPolicy(PolicyItem item, string value)
        {
            string canonicalResult = PolicyCatalog.Canonical(item.Key, value);
            if (canonicalResult == null)
            {
                return false;
            }
            bool saveSucceeded = ((item.Key == "GmThreadBoostStrategyV1") ? ThreadBoostSelection.Save(canonicalResult) : ((item.Kind == PolicyValueKind.Bool) ? Settings.Save(item.Key, canonicalResult == "1") : Settings.SaveStr(item.Key, canonicalResult)));
            if (saveSucceeded)
            {
                gameMode.RefreshAutomaticPreferences();
                RefreshAutomationPage();
            }
            return saveSucceeded;
        }
        private void AddManualPerformanceToggle(Control parent, ref int y, string key, string title, string detail, Func<bool> read, Func<bool, bool> write, Func<bool> supported = null, Func<bool> automaticValue = null, Func<string> execution = null)
        {
            Toggle toggle = MakeSwitch(read(), null);
            toggle.Name = "manualPerformance_" + key;
            toggle.AccessibleName = title;
            toggle.AccessibleDescription = detail;
            int used;
            SettingCard card = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(detail, ScrollContentW, toggle, 82), title, detail, toggle, out used);
            y += used + 8;
            Action sync = delegate
            {
                bool readSucceeded = read();
                bool available = supported == null || supported();
                toggle.SetSilently(readSucceeded);
                toggle.Enabled = available || readSucceeded;
                SyncManualPerformanceStatus(card, key, readSucceeded, available, execution);
            };
            toggle.CheckedChanged += delegate
            {
                if (!automationUpdating)
                {
                    if (toggle.Checked && supported != null && !supported())
                    {
                        sync();
                    }
                    else
                    {
                        bool writeSucceeded = write(toggle.Checked);
                        sync();
                        if (!writeSucceeded)
                        {
                            ShowManualPerformanceSaveFailure();
                        }
                    }
                }
            };
            automationSync.Add(sync);
        }
        private bool ManualPolicySupported(string key)
        {
            switch (key)
            {
            case "NvMaxPerf":
            case "NvShaderCache":
                return NvApi.Available;
            case "PowerPlanOn":
                return !PowerPlan.NoSwitchSelected;
            default:
                return true;
            }
        }
        private static string ManualPolicyDescription(string key)
        {
            switch (key)
            {
            case PolicyCatalog.KeyGameScheduling:
                return Lang.T("gm.scheduling.detail");
            case "GmSuppress":
                return Lang.T("background.governance.detail");
            case "GmBoost":
                return Lang.T("gm.scheduling.resources.detail");
            case "GmBoostCpuV1":
                return AutoText("对局期间保持游戏 CPU 高优先级，持续满载或无线程提优也不降档。由资源优先级总开关控制，退局恢复原值。", "Keeps game CPU priority High during the session, including sustained full load or inactive thread boosting. Requires game resource priorities; restores the original priority on exit.");
            case "GmBoostIoV1":
                return AutoText("请求游戏磁盘 I/O 高优先级，需权限及读回确认。失败不影响其他优先级。", "Requests High game disk I/O priority after privilege and readback checks. Failure leaves other priorities unchanged.");
            case "GmBoostGpuV1":
                return AutoText("提高游戏 GPU 调度优先级，需驱动支持及读回确认。退局恢复原值。", "Raises game GPU scheduling priority with driver support and readback checks. Restores on exit.");
            case "GmThreadBoostStrategyV1":
                return AutoText("手动开启后优先照顾渲染关键线程，无法识别时改选最忙线程，退局恢复。可叠加逐游戏绑核，组合效果待实测。", "When manually enabled, prioritizes render-critical threads or the busiest thread if unrecognized. Restores on game exit. Can combine with per-game affinity; combined effects are untested.");
            case "PowerPlanOn":
                return AutoText("对局使用所选电源计划，退局恢复；“不切换”和掌机停用。托管参数按供电与硬件适配，智能／竞技共用；已有计划只切换，实验室设置单独恢复。", "Uses the selected plan during matches and restores afterwards. Disabled on handhelds or with Don't switch. Smart and Competitive share managed parameters adapted to power source and hardware. Existing plans are only activated; laboratory settings restore separately.");
            case "GmPauseDl":
                return AutoText("对局暂停 Windows 传递优化下载，结束恢复。", "Pauses Windows Delivery Optimization downloads during a match, then restores.");
            case "GmPauseUpdate":
                return AutoText("对局暂停 Windows 更新活动，结束恢复。", "Pauses Windows Update activity during a match, then restores.");
            case "GmAwake":
                return AutoText("游戏前台时阻止屏幕与电脑自动休眠。", "Prevents automatic display and system sleep while the game is foreground.");
            case "NvMaxPerf":
                return AutoText("为游戏配置 NVIDIA 最高性能偏好，下次启动核实。", "Configures NVIDIA maximum performance for games; verify on next launch.");
            case "NvShaderCache":
                return AutoText("配置 NVIDIA 着色器缓存容量，下次启动核实。", "Configures NVIDIA shader cache capacity; verify on next launch.");
            default:
                return AutoText("由你决定是否启用，按适用条件执行。", "Choose whether to enable this feature; it runs when eligible.");
            }
        }
        private static string PerformanceRecordingDetail()
        {
            return AutoText("守护期间关闭 Game DVR/Xbox 后台录制，即时回放与录制热键不可用。关闭开关、停守护或退出后还原；需要录屏时请关闭此项。", "Disables Game DVR/Xbox capture, instant replay and recording hotkeys while the guard runs. Off, guard stop or exit restores originals; turn this off to record.");
        }
        internal static string ConcisePerformanceDetail(string original, string explanation, bool keepStates = true)
        {
            int matchIndex = ((original == null) ? (-1) : original.IndexOf('\n'));
            if (matchIndex > 0 && original[matchIndex - 1] == '\r')
            {
                matchIndex--;
            }
            return explanation + ((keepStates && matchIndex >= 0) ? original.Substring(matchIndex) : "");
        }
        private void ChangePerformanceTuningBundle(object sender, EventArgs args)
        {
            if (!RequireElevationFor(swPerformanceEnvironmentBundle, PerformanceTuning.EnvironmentBundleEnabled))
            {
                return;
            }
            bool @checked = swPerformanceEnvironmentBundle.Checked;
            bool setEnvironmentBundleSucceeded = PerformanceTuning.SetEnvironmentBundle(@checked, delegate
            {
                try
                {
                    if (!base.IsDisposed && base.IsHandleCreated)
                    {
                        BeginInvoke((Action)delegate
                        {
                            RefreshPerformanceEnvironmentObservation();
                        });
                    }
                }
                catch
                {
                }
            });
            if (setEnvironmentBundleSucceeded)
            {
                environmentBundleOverride = @checked;
                gameMode.Poke();
            }
            RefreshPerformanceEnvironmentObservation();
            if (!setEnvironmentBundleSucceeded)
            {
                PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.savefail"));
            }
        }
        private static string PerformanceFeatureKey(string name)
        {
            string nameOffset = name ?? "";
            string[] textEntries = new string[3] { "manualPerformance_", "environment", "laboratory_" };
            foreach (string entryText in textEntries)
            {
                if (nameOffset.StartsWith(entryText, StringComparison.Ordinal))
                {
                    nameOffset = nameOffset.Substring(entryText.Length);
                    break;
                }
            }
            switch (nameOffset)
            {
            case "laboratoryCpuIdle":
                return "LabCpuIdleStrategyV1";
            case "backgroundGovernanceMode":
                return "GmSuppress";
            default:
                return nameOffset;
            }
        }
        private static void ConfigurePerformanceConsole(PerformanceFeatureCard card, string key)
        {
            card.IconKey = key;
            switch (key)
            {
            case "GmAwake":
                card.DisplayTitle = AutoText("对局保持唤醒", "Keep the match awake");
                card.Summary = AutoText("游戏前台时阻止息屏与睡眠", "Prevent display sleep while the game is foreground");
                break;
            case "GmThreadBoostStrategyV1":
                card.Summary = AutoText("按游戏自动提优渲染或最忙线程", "Boosts the render or busiest thread per game");
                break;
            case LaptopPerfMode.PreferenceKey:
                card.Summary = AutoText("插电对局切换厂商性能档，退局恢复原档", "Use the vendor performance profile on AC; restore after matches");
                break;
            case "GmSuppress":
                card.Summary = Lang.T("background.governance.summary");
                break;
            case "GmPauseDl":
                card.Summary = AutoText("对局期间暂停支持的后台下载", "Pause supported background downloads during matches");
                break;
            case "GmPauseUpdate":
                card.Summary = AutoText("对局期间暂缓更新，退局恢复", "Pause updates during matches, restore on exit");
                break;
            case "AutoBackgroundFpsV1":
                card.Summary = AutoText("限制合格后台渲染，不限制游戏前台", "Cap eligible background rendering, leave foreground uncapped");
                break;
            case "NvMaxPerf":
                card.Summary = AutoText("可在启动前预设电源偏好，退局恢复", "May preset power preferences before launch; restore after matches");
                break;
            case "NvShaderCache":
                card.Summary = AutoText("可在启动前预设着色器缓存，退局恢复", "May preset shader caching before launch; restore after matches");
                break;
            case "GameDvrOff":
                card.Summary = AutoText("保护期间关闭录制，关闭保护后恢复", "Disable capture while protection is on; restore when it stops");
                break;
            case "EnvPerformanceBundleV1":
                card.Summary = AutoText("关闭 VBS 与适用的 CPU 软件缓解，降低相应防护", "Disable VBS and applicable CPU mitigations, reducing protection");
                break;
            case "AutoGlobalTimer":
                card.Summary = AutoText("系统配置退局保留；精度请求仅限对局", "Keep system configuration after matches; request timer resolution only in matches");
                break;
            case "LabEppYieldV1":
                card.Summary = AutoText("插电笔记本按功耗与瓶颈验证，失败即恢复", "Verify laptop power and bottlenecks on AC; revert on failure");
                break;
            case "LabGpuClockMaxV1":
                card.Summary = AutoText("退局时 AMD 恢复原频率下限，NVIDIA 恢复自动调频", "On exit, AMD restores the clock floor and NVIDIA returns to auto");
                break;
            case "LabGpuPowerMaxV1":
                card.Summary = AutoText("AMD、NVIDIA 请求最高功耗上限，退局恢复原值", "AMD and NVIDIA request the maximum power limit and restore it on exit");
                break;
            case "LabCpuIdleStrategyV1":
                card.Summary = AutoText("插电时作用当前计划，退局恢复", "Current plan on AC; restore on exit");
                break;
            }
        }

    }
}

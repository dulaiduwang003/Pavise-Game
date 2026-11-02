using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class Official3106UiChecks
    {
        private static void PumpPrompt(Func<bool> done)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!done() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Check(done(), "AI prompt worker did not complete");
        }

        private static void RunAiTuningPromptChecks(string output)
        {
            int initial = checks;
            Settings.ResetUiFixtureMemory(); Logger.WritesEnabled = false;
            string folder = Path.Combine(output, "state-ai-prompt"); Directory.CreateDirectory(folder);
            Paths.Data = folder;
            var mode = new GameMode(folder, new SuppressionCore());
            string features = AiTuningFeatureCatalog.Describe(mode);
            var rows = Regex.Matches(features, @"(?m)^\d+\.[^\r\n]+\[([^\]]+)\]：[^\r\n]+。用：[^\r\n]+。操作：[^\r\n]+。当前：[^\r\n]+\r?$");
            var described = new HashSet<string>(rows.Cast<Match>().Select(m => m.Groups[1].Value));
            Check(rows.Count == 110 && described.Count == rows.Count && !described.Contains("MatchVisualEffectsV1"), "Compact prompt includes a retired feature, loses an entry or repeats an ID: rows=" + rows.Count + " unique=" + described.Count);
            Check(!features.Contains("详细用途未核验"), "An active capability has no useful purpose/scene description");
            foreach (PolicyItem item in PolicyCatalog.All)
                if (!PerformancePolicy.IsRetired(item.Key)) Check(described.Contains(item.Key), "AI feature description omitted policy " + item.Key);
            foreach (PerformanceAction item in PerformanceActionCatalog.All)
                Check(described.Contains(item.Key), "AI feature description omitted action " + item.Key);
            foreach (PrivacyAction item in PrivacyActionCatalog.All)
                Check(described.Contains(item.Key), "AI feature description omitted privacy action " + item.Key);
            foreach (string key in new[] { LaboratoryPower.EppKey, LaboratoryGpu.ClockKey, LaboratoryGpu.PowerKey, LaboratoryAudio.Key })
                Check(features.Contains(key), "AI feature description omitted experiment " + key);
            foreach (string term in new[] { "智能", "竞技", "托管", "掌机", "白名单", "反作弊", "恢复" })
                Check(features.Contains(term), "AI description omits an entire capability: " + term);
            string text = AiTuningPrompt.Build("Fixture hardware", features, "CS2 / 1440p");
            Check(text.Contains(App.Version) && text.Contains("Fixture hardware") && text.Contains("CS2 / 1440p"), "Prompt lost its version, hardware or user goals");
            // Budget applies to the complete default catalog, never a runtime truncation of user data.
            // Allow the new updater's integrity, safe-exit, cleanup and manual-fallback coverage;
            // keep the complete catalog instead of truncating capabilities to the previous limit.
            Check(text.Length < 15500, "Default prompt has grown beyond the compact all-features budget: " + text.Length);
            Check(features.Contains("无需托管") && features.Contains("不要求SMT") && features.Contains("通用电源路由"), "Compact wording lost important eligibility distinctions");
            var byId = rows.Cast<Match>().ToDictionary(m => m.Groups[1].Value, m => m.Value);
            Check(byId.ContainsKey("SoftwareUpdate") && byId["SoftwareUpdate"].Contains("验签") && byId["SoftwareUpdate"].Contains("删除旧版")
                && byId["SoftwareUpdate"].Contains("禁止退出") && byId["SoftwareUpdate"].Contains("下载可取消") && byId["SoftwareUpdate"].Contains("启动入口")
                && byId["SoftwareUpdate"].Contains("同一弹窗") && byId["SoftwareUpdate"].Contains("解除关闭限制")
                && byId["SoftwareUpdate"].Contains("夸克") && byId["SoftwareUpdate"].Contains("GitHub"), "In-app update and manual fallback capabilities are missing from the AI catalog");
            Check(byId["SettingsAppearance"].Contains("16款") && byId["SettingsAppearance"].Contains("选中即应用/恢复默认")
                && byId["SettingsAppearance"].Contains("两模式可同选")
                && byId["SettingsAppearance"].Contains("开合/旋转/蓄能脉冲")
                && byId["SettingsAppearance"].Contains("配色随主题") && byId["SettingsAppearance"].Contains("失焦/隐藏/离页/对局停播"),
                "Visual customization omits the unified animation library, per-mode application, accent palette or inactivity gate");
            foreach(string motion in CoreMotionCatalog.ChineseChoices.Split('/'))Check(byId["SettingsAppearance"].Contains(motion),"Visual customization omitted a built-in motion: "+motion);
            Check(byId.ContainsKey("ConfigurationPresets"), "AI feature description omitted configuration presets");
            string presets = byId["ConfigurationPresets"];
            Check(presets.Contains("作者同款/极限配置") && presets.Contains("34项") && presets.Contains("手动修改后显示自定义")
                && presets.Contains("点击配置即应用") && presets.Contains("完成提醒重启电脑"), "Configuration guide choices, application, restart reminder or custom state are missing");
            Check(presets.Contains("不可用项跳过") && presets.Contains("失败简要提示可重试")
                && presets.Contains("不改智能/竞技运行策略") && presets.Contains("作者同款不是Windows出厂恢复"),
                "Configuration presets claim unsupported application, hide failures, or alter runtime policies");
            Check(presets.Contains("当前：自定义/未应用预设"), "Absent preset identity was exported as an applied preset");
            var presetState = new ConfigurationPresetCoordinator(new ConfigurationPresetBackend(null));
            string fingerprint = presetState.Fingerprint();
            Check(fingerprint != null, "Saved preset fingerprint could not be read without hardware inspection");
            Settings.SaveStrDurable(ConfigurationPresetRuntime.IdentityKey, "1|default|" + fingerprint);
            Check(AiTuningFeatureCatalog.Describe(mode).Contains("当前：作者同款（保存配置匹配，实际生效未核验）"),
                "A matching preset identity was not exported as saved configuration");
            Settings.Save("GmAwake", false);
            Check(AiTuningFeatureCatalog.Describe(mode).Contains("当前：自定义/未应用预设"),
                "Changing a covered preference leaves a stale preset in the exported catalog");
            Settings.Remove("GmAwake");
            Settings.Remove(ConfigurationPresetRuntime.IdentityKey);
            foreach (string key in new[] { "Hags", "WindowedOptimizations", "VariableRefreshRate", "AmdSam" })
                Check(byId[key].Contains("开启/关闭") && byId[key].Contains("原配置按记录恢复") && byId[key].Contains("状态可读"),
                    "Direct graphics switch omits explicit off, recorded restoration or eligibility: " + key);
            foreach (string key in new[] { "PerfMonitorEnabledV2", "MonitoringFrameMetrics", "MonitoringHardwareMetrics", "PerfMonitorLayoutV1", "PerfMonitorDisplayIntervalMsV2", "PerfMonitorTextStyleV2", "PerfMonitorAppearanceV1", "PerfMonitorHotkeyV1", "RtssComponent", "MonitoringRunReports", "MonitoringReportDiagnostics" })
                Check(!described.Contains(key), "AI description advertises retired monitoring capability: " + key);
            foreach (string key in new[] { "AutoLaptopPerformanceV2", "GameModeOn", "OverviewTelemetry", "GameForceTrigger", "LogWritesEnabled", "DiagnosticCopy",
                "AuditGpuIrq", "AuditGpuIrqRestore", "AuditMsi", "AuditSlowDrivers", "AuditMemoryCompression", "AuditNetworkThrottle",
                "AuditLinkMetric", "AuditFth", "AuditQuantum", "AuditFixAll", "AuditPlatform", "AuditCpu", "AuditMemory",
                "AuditGraphics", "AuditPower", "AuditInput", "AuditStorageNetwork", "AuditResults" })
                Check(described.Contains(key), "AI description omitted a UI/checkup capability: " + key);
            Check(byId["GameModeOn"].Contains("已压制进程/已暂停服务数量") && byId["GameModeOn"].Contains("去重")
                && byId["GameModeOn"].Contains("退局或停守护收起"), "Guard action counts or lifecycle are missing from the catalog");
            Check(byId["Whitelist"].Contains("浏览器、聊天/开发工具")
                && byId["Whitelist"].Contains("最小化/托盘应用") && byId["Whitelist"].Contains("排除系统与反作弊"),
                "Whitelist discovery scope is missing from the AI feature catalog");
            Check(byId["GameLibrary"].Contains("最小化/托盘游戏"),
                "Minimized/tray game discovery is missing from the AI feature catalog");
            Check(byId["OverviewTelemetry"].Contains("游戏中停止概览读取") && byId["OverviewTelemetry"].Contains("退出后恢复")
                && byId["OverviewTelemetry"].Contains("不采集游戏帧率"), "Overview game pause/resume or sampling ownership is missing from the catalog");
            Check(byId["PowerPlanOn"].Contains("不切换") && byId["PowerPlanOn"].Contains("指定已有计划")
                && byId["PowerPlanOn"].Contains("退局返回原计划") && !byId["PowerPlanOn"].Contains("每秒守护")
                && !byId["PowerPlanOn"].Contains("分批回读"), "Power-plan choices, restoration or retired parameter-guard boundary is incorrect");
            Check(!byId.ContainsKey("PowerPlanSourceV1"), "Retired managed-source selector remains in the AI catalog");
            foreach (string source in new[] { "AMD通用", "Intel Non-K", "Intel K系列性能", "AMD X3D", "AMD低延迟", "笔记本台式通用" })
                Check(byId["ManagedPower2223"].Contains(source), "AI description omitted an automatic source: " + source);
            Check(byId["ManagedPower2223"].Contains("无来源选择器") && byId["ManagedPower2223"].Contains("旧手选无效"),
                "Automatic power source selection or retirement is missing");
            Check(byId["ManagedPower2223"].Contains("完整来源") && byId["ManagedPower2223"].Contains("CPU/IO/空闲/自主模式")
                && byId["ManagedPower2223"].Contains("全部DC基准") && byId["ManagedPower2223"].Contains("非对称缓存")
                && !byId["ManagedPower2223"].Contains("白名单"), "AI description still documents the retired partial parameter table");
            Check(byId["LabCpuIdleStrategyV1"].Contains("来源生效后覆盖") && byId["LabCpuIdleStrategyV1"].Contains("恢复该来源原值")
                && byId["LabEppYieldV1"].Contains("实际自主性能模式支持") && byId["LabEppYieldV1"].Contains("先恢复后重采样"),
                "Source-relative idle restoration or EPP eligibility is missing");
            Check(AppDataReset.IsUserPreference("PowerPlanSourceV1"), "Software reset retains the managed-source preference");
            Check(!described.Contains("HandheldDeviceMode") && !described.Contains("ManagedPowerHandheld"), "AI catalog still offers retired handheld controls");
            foreach (string term in new[] { "移动端智能", "混合核心AMD移动端", "普通同构AMD移动端竞技", "线程撕裂者", "至强", "EPYC", "多处理器组", "多插槽", "通用计划", "退局恢复", "不切换/指定已有计划优先" })
                Check(byId["ManagedPowerUniversal"].Contains(term), "Universal/mobile policy omitted: " + term);
            foreach (string key in new[] { "OverviewTelemetry", "AuditSlowDrivers", "AuditPlatform", "AuditCpu", "AuditMemory",
                "AuditGraphics", "AuditPower", "AuditInput", "AuditStorageNetwork", "AuditResults" })
                Check(byId[key].Contains("操作：只读检测"), "Read-only diagnosis is exported as a system switch: " + key);
            Check(byId["AuditGpuIrq"].Contains("ISR/DPC") && byId["AuditGpuIrq"].Contains("重启后验证")
                && byId["AuditGpuIrqRestore"].Contains("操作：手动恢复") && byId["AuditMsi"].Contains("MSISupported=0"),
                "GPU interrupt placement, verification, restoration and MSI repair are not separately explained");
            Check(byId["AuditMemoryCompression"].Contains("可启用") && byId["AuditNetworkThrottle"].Contains("修复到10")
                && byId["AuditLinkMetric"].Contains("自动metric") && byId["AuditFth"].Contains("移除"), "Audit repair capabilities lost their actual actions");
            foreach (string key in new[] { "EnvVbsDisabledV1", PerformanceActionCatalog.CpuMitigations, PerformanceActionCatalog.LastAccess, PerformanceActionCatalog.AudioIdle })
                Check(byId[key].Contains("随「系统性能增强」组合，无独立开关"), "Prompt invents an independent system-bundle control: " + key);
            foreach (string key in new[] { PerformanceActionCatalog.StorageIdle, PerformanceActionCatalog.StorageRuntime, PerformanceActionCatalog.StorageCold })
                Check(byId[key].Contains("随「存储设备电源管理」组合，无独立开关"), "Prompt invents an independent storage-bundle control: " + key);
            Check(byId["ModeSmart"].Contains("模式行为说明") && byId["ManagedPower2223"].Contains("电源行为说明")
                && byId["AntiCheatWeakCoreAffinity"].Contains("内置，无独立开关") && byId["LolColumnEnabled"].Contains("专属模块说明"), "Behavior documentation is presented as another switch");
            Check(byId["Checkup"].Contains("不测FPS、1% low、帧时间或输入延迟"), "System checkup is presented as a gaming benchmark");
            Check(features.Contains("恢复仅撤回工具记录，非强制开启/出厂重置"), "Restoring owned settings is presented as forcing system defaults");
            Check(byId["EnvVbsDisabledV1"].Contains("未保存禁用VBS/内存完整性/Hypervisor偏好（旧修改/执行状态未知）"), "Missing VBS preference is mistaken for an explicit off preference");
            Check(features.Contains("HAGS/SAM/窗口化优化/系统VRR关闭则请求关闭") && features.Contains("另走恢复维护"), "Restore semantics are incorrectly imposed on direct system toggles");
            Settings.Save("EnvVbsDisabledV1", true);
            Settings.Save(PerformanceActionCatalog.CpuMitigations, true);
            string disabling = AiTuningFeatureCatalog.Describe(mode);
            Check(disabling.Contains("请求禁用VBS/内存完整性/Hypervisor") && disabling.Contains("请求关闭Spectre V2/Meltdown缓解")
                && !disabling.Contains("VBS=开启") && !disabling.Contains("CPU缓解=开启"), "Negative switches export the opposite system meaning");
            Settings.Save("EnvVbsDisabledV1", false);
            Settings.Save(PerformanceActionCatalog.CpuMitigations, false);
            string restoring = AiTuningFeatureCatalog.Describe(mode);
            Check(restoring.Contains("不请求禁用VBS/内存完整性/Hypervisor（已有修改按原值恢复）"), "Off preference claims VBS has been enabled");
            Check(AiTuningPrompt.Build(null, features, null).Contains("未知"), "Hardware failure was not represented as unknown");
            string nulPrompt = AiTuningPrompt.Build("hardware\0AFTER-HARDWARE", features + "\0AFTER-FEATURES", "goal\0AFTER-GOAL");
            Check(nulPrompt.IndexOf('\0') < 0 && nulPrompt.Contains("AFTER-HARDWARE") && nulPrompt.Contains("AFTER-FEATURES")
                && nulPrompt.Contains("AFTER-GOAL"), "Embedded NUL would terminate native preview or clipboard content");
            bool rejected = false;
            try { AiTuningPrompt.Build("hardware", "", ""); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "An incomplete feature description was allowed");
            RunAutomaticPowerUiChecks(output);

            // Large prompts must survive the native text control and clipboard path.
            string complete = features + "\n" + new string('测', 70000) + "\nEND-OF-COMPLETE-FEATURES";
            foreach (int dpi in new[] { 96, 144, 192 })
            foreach (bool light in new[] { false, true })
            {
                Dpi.Scale = dpi / 96f; Dpi.NoFit = true; Theme.DropFontCache(); Theme.SetLight(light); Lang.Cur = 0;
                int reads = 0, writes = 0; string copied = null;
                Button feedbackButton = null;
                using (var dialog = new AiTuningPromptDialog(complete, "fixture-display",
                    delegate(string display) { Interlocked.Increment(ref reads); return "Fixture hardware"; },
                    delegate(string value) {
                        Check(feedbackButton != null && !feedbackButton.Enabled && feedbackButton.Text.Contains("正在复制"), "Copy click lacks immediate busy feedback");
                        if (++writes == 1) throw new InvalidOperationException("Clipboard busy");
                        copied = writes == 2 ? value.Substring(0, value.Length / 2) : value;
                    }, delegate { return copied; }))
                {
                    dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-32000, -32000);
                    dialog.ShowInTaskbar = false; dialog.Show(); Application.DoEvents();
                    var preview = (TextBox)Desc(dialog).Single(c => c.Name == "AiPromptPreview");
                    var copy = (Button)dialog.Controls["AiPromptCopy"];
                    feedbackButton = copy;
                    var promptStatus = (Label)dialog.Controls["AiPromptStatus"];
                    var goals = (TextBox)Desc(dialog).Single(c => c.Name == "AiPromptContext");
                    PumpPrompt(delegate { return copy.Enabled; });
                    Check(reads == 1 && preview.Text.Contains("END-OF-COMPLETE-FEATURES") && preview.TextLength > 70000, "Native preview truncated the full prompt");
                    goals.Text = "Fixture game / 165 Hz"; Application.DoEvents();
                    Check(reads == 1 && preview.Text.Contains(goals.Text), "Editing user goals re-read hardware or failed to update the prompt");
                    string expectedCopy = preview.Text;
                    // A shortened native view must never shorten the generated copy source.
                    preview.Text = "SHORTENED VIEW";
                    copy.PerformClick();
                    Check(copied == null && copy.Enabled && copy.Text.Contains("重试"), "Clipboard failure lost retry capability or feedback");
                    copy.PerformClick();
                    Check(copy.Enabled && copy.Text.Contains("重试") && promptStatus.Text.Contains("校验失败"), "Truncated readback was incorrectly reported as a complete copy");
                    copy.PerformClick();
                    Check(copied == expectedCopy && copied.Contains("END-OF-COMPLETE-FEATURES") && copied.Contains("[AuditResults]"), "Copy did not publish the generated original including the last checkup entry");
                    Check(copy.Text.Contains("已复制") && promptStatus.Text.Contains(copied.Length.ToString()) && promptStatus.Text.Contains(rows.Count.ToString()), "Successful copy lacks verified length/count feedback");
                    if (dpi == 96 && !light) PumpPrompt(delegate { return copy.Text == "复制全部"; });
                    goals.Text = "Updated game goal"; Application.DoEvents();
                    Check(copy.Text == "复制全部" && !promptStatus.Text.Contains("已核对完整复制") && preview.Text.Contains(goals.Text), "Editing the prompt leaves stale copied feedback");
                    foreach (string key in new[] { "AiPromptCopy", "AiPromptClose", "AiPromptRefresh" })
                        Check(dialog.ClientRectangle.Contains(dialog.Controls[key].Bounds), "Prompt control extends outside the screen-fitted dialog: " + key);
                    Check(preview.Parent.Bottom <= dialog.Controls["AiPromptStatus"].Top && dialog.Controls["AiPromptStatus"].Bottom <= copy.Top, "Prompt text overlaps fixed actions");
                    Check(((ScrollableControl)preview.Parent).AutoScrollMinSize.Height >= preview.Bottom, "Prompt preview cannot be reached by scrolling");
                    if (dpi == 96 || (dpi == 144 && light)) Screenshot(dialog, Path.Combine(output, "ai-prompt-" + dpi + "-" + light + ".png"));
                    dialog.Close();
                }
            }
            Dpi.Scale = 1; Theme.DropFontCache(); Theme.SetLight(false); Lang.Cur = 0;
            using (var release = new ManualResetEvent(false))
            {
                int calls = 0;
                using (var dialog = new AiTuningPromptDialog(features, "fixture", delegate(string display) {
                    Interlocked.Increment(ref calls); release.WaitOne(2000); return "late result";
                }, delegate(string value) { throw new InvalidOperationException("Closed dialog copied text"); }, delegate { return ""; }))
                {
                    dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-32000, -32000);
                    dialog.Show(); Application.DoEvents(); PumpPrompt(delegate { return Volatile.Read(ref calls) == 1; });
                    dialog.Close(); release.Set();
                }
                Application.DoEvents();
            }
            File.WriteAllText(Path.Combine(output, "ai-prompt-example.txt"), text);
            Console.WriteLine("PASS AI prompt assertions=" + (checks - initial) + " prompt_length=" + text.Length + " all_catalogs=true automatic_sources=6 long_copy=true hardware=fake clipboard=fake product_workers=false");
        }

        private static void RunAutomaticPowerUiChecks(string output)
        {
            var active = typeof(GameMode).GetField("active", Hidden);
            var enabled = typeof(GameMode).GetField("enabled", Hidden);
            float savedScale = Dpi.Scale; bool savedNoFit = Dpi.NoFit; int savedLanguage = Lang.Cur;
            try
            {
                foreach (int sourceDpi in new[] { 96, 144, 192 })
                foreach (int sourceLanguage in new[] { 0, 1 })
                {
                    Dpi.Scale = sourceDpi / 96f; Dpi.NoFit = true; Theme.DropFontCache(); Lang.Cur = sourceLanguage;
                    Settings.SaveStr("PowerPlanChoice", "v2:managed"); Settings.SaveStr("PowerPlanSourceV1", "intel-kf");
                    var core = new SuppressionCore(); var mode = new GameMode(Paths.Data, core);
                    using (var icon = IconArt.MakeIcon(24))
                    using (var form = new PanelForm(new Tamer(core), mode, icon, false))
                    {
                        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000);
                        form.ShowInTaskbar = false; form.Show(); Application.DoEvents();
                        Call(form, "SetModeFlyout", true);
                        var flyout = Field<StrategyFlyout>(form, "modeFlyout");
                        Check(flyout.Controls.Count == 2 && flyout.Scene.Visible && flyout.Power.Visible,
                            "Strategy popup must contain only scene and match-power choices");
                        Check(!flyout.Controls.Cast<Control>().Any(c => c.Name.Contains("ManagedSource")),
                            "Retired source selector or source description remains in the popup");
                        foreach (int width in new[] { 452, 320 })
                        {
                            flyout.FitViewport(new Size(Theme.S(width), Theme.S(220)));
                            flyout.ScrollControlIntoView(flyout.Power);
                            Check(flyout.Power.Right <= flyout.ClientSize.Width && flyout.Scene.Bottom <= flyout.Power.Top,
                                "Two-row strategy layout overlaps or exceeds its viewport");
                            Check(flyout.Power.Bottom > 0 && flyout.Power.Top < flyout.ClientSize.Height,
                                "Constrained strategy popup cannot reach its power selector");
                        }
                        flyout.AutoScrollPosition = Point.Empty; flyout.FitViewport(new Size(Theme.S(452), Theme.S(700)));
                        using (var preview = new Bitmap(flyout.Width, flyout.Height))
                        {
                            flyout.DrawToBitmap(preview, new Rectangle(Point.Empty, flyout.Size));
                            preview.Save(Path.Combine(output, "automatic-power-" + sourceDpi + "-" + sourceLanguage + ".png"));
                        }
                        Settings.SaveStr("PowerPlanChoice", "v2:off"); Call(form, "RefreshStrategyControls");
                        Check(PowerPlan.EffectivePlanId == "off" && flyout.Controls.Count == 2,
                            "Removing source selection changed no-switch behavior");
                        Settings.SaveStr("PowerPlanChoice", "v2:managed");
                        active.SetValue(mode, true); enabled.SetValue(mode, true);
                        try { Call(form, "RefreshStrategyControls"); Check(!flyout.Power.Enabled, "Game session accepts a power-plan change"); }
                        finally { active.SetValue(mode, false); enabled.SetValue(mode, false); }
                        Call(form, "RefreshStrategyControls"); Call(form, "SetModeFlyout", false);
                        Check(!flyout.Power.DropDownVisible && !mode.Enabled, "Closing strategy leaves an orphan dropdown or activates product workers");
                        form.Close();
                    }
                }
            }
            finally
            {
                Settings.Remove("PowerPlanSourceV1"); Settings.Remove("PowerPlanChoice");
                Dpi.Scale = savedScale; Dpi.NoFit = savedNoFit; Lang.Cur = savedLanguage; Theme.DropFontCache();
            }
        }
    }
}

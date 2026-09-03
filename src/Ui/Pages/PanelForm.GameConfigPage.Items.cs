// @author bdth 2074055628@qq.com
// 文件用途 逐游戏配置项的取值 文案与可用性判定
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private void BuildGameConfigContent()
        {
            cfgRowSync.Clear();
            cfgCardByKey.Clear();
            cfgJumpCursor = 0;
            cfgCoreManualPicked = false;
            var old = new List<Control>();
            foreach (Control c in pageGameConfig.Controls) old.Add(c);
            pageGameConfig.Controls.Clear();
            foreach (Control c in old) c.Dispose();

            var back = new PillButton(Lang.T("cfg.back"));
            back.SetBounds(Theme.S(ContentX), Theme.S(8), Theme.S(118), Theme.S(32));
            back.Click += delegate { CloseGameConfigPage(); };
            pageGameConfig.Controls.Add(back);

            var clear = new PillButton(Lang.T("cfg.clear"), BtnKind.Danger);
            clear.SetBounds(Theme.S(ContentX + ContentW - 100), Theme.S(8), Theme.S(100), Theme.S(32));
            clear.Click += delegate { ClearAllCfgOverrides(); };
            pageGameConfig.Controls.Add(clear);

            lblCfgCount = null;
            lblCfgSub = null;
            cfgBanner = new ModuleBanner();
            cfgBanner.SetBounds(Theme.S(ContentX), Theme.S(50), Theme.S(ContentW), Theme.S(72));
            cfgBanner.Code = "GAME PROFILE // 07";
            cfgBanner.TitleText = Lang.F("cfg.title", cfgProfile.Name);
            cfgBanner.Detail = Lang.T("cfg.sub");
            cfgBanner.State = "PROFILE READY";
            cfgBanner.StateColor = Theme.Green;
            cfgBanner.Glyph = "tiles";
            cfgBanner.Cursor = Cursors.Hand;
            cfgBanner.Click += delegate { JumpToNextCfgOverride(); };
            pageGameConfig.Controls.Add(cfgBanner);

            int modeY = 134;
            AddCfgModeRow(pageGameConfig, ref modeY);

            cfgTabs = new TechTabs();
            cfgTabs.SetBounds(Theme.S(ContentX), Theme.S(modeY + 2), Theme.S(ContentW), Theme.S(38));
            cfgTabs.SetTabs(
                new[] { Lang.T("cfg.tab.bg"), Lang.T("cfg.tab.core"),
                    Lang.T("cfg.tab.env"), Lang.T("cfg.tab.gpu") },
                new[] { Lang.T("cfg.tab.bg.sub"), Lang.T("cfg.tab.core.sub"),
                    Lang.T("cfg.tab.env.sub"), Lang.T("cfg.tab.gpu.sub") });
            pageGameConfig.Controls.Add(cfgTabs);

            int panelTop = modeY + 2 + 48;
            cfgTabPanels = new DBPanel[4];
            for (int i = 0; i < cfgTabPanels.Length; i++)
            {
                var panel = new WorkspacePanel();
                panel.SetBounds(Theme.S(20), Theme.S(panelTop), Theme.S(PageW - 40),
                    Theme.S(PageH - panelTop - 8));
                panel.BackColor = Theme.Bg; panel.AutoScroll = true; Native.Dark(panel);
                panel.Visible = i == 0;
                pageGameConfig.Controls.Add(panel);
                cfgTabPanels[i] = panel;
            }
            cfgTabs.IndexChanged = delegate(int index)
            {
                for (int i = 0; i < cfgTabPanels.Length; i++)
                    if (i != index) cfgTabPanels[i].Visible = false;
                cfgTabPanels[index].Visible = true;
            };

            int ty = 2;
            AddCfgSection(cfgTabPanels[0], Lang.T("cfg.sub.range"), ref ty,
                new[] { PolicyCatalog.KeySuppress, PolicyCatalog.KeyAggressive,
                    PolicyCatalog.KeyGpuDemote });
            AddCfgSection(cfgTabPanels[0], Lang.T("cfg.sub.boost"), ref ty,
                new[] { PolicyCatalog.KeyBoost,
                    PolicyCatalog.KeyRenderLane });
            EnableCardCollapse(cfgTabPanels[0]);

            BuildCfgCoreTab(cfgTabPanels[1]);

            ty = 2;
            AddCfgSection(cfgTabPanels[2], Lang.T("cfg.group.mempower"), ref ty,
                new[] { PolicyCatalog.KeyPowerPlan, PolicyCatalog.KeyPowerYield, PolicyCatalog.KeyLaptopPerf,
                    PolicyCatalog.KeyDisableCpuIdle, PolicyCatalog.KeyStandbyCleaner,
                    PolicyCatalog.KeyCacheWarm });
            AddCfgSection(cfgTabPanels[2], Lang.T("cfg.sub.net"), ref ty,
                new[] { PolicyCatalog.KeyPauseDl, PolicyCatalog.KeyPauseUpdate, PolicyCatalog.KeyPauseMaintenance });
            AddCfgSection(cfgTabPanels[2], Lang.T("cfg.group.env"), ref ty,
                new[] { PolicyCatalog.KeyPauseServices });
            AddCfgSection(cfgTabPanels[2], Lang.T("cfg.sub.presence"), ref ty,
                new[] { PolicyCatalog.KeyAwake });
            AddCfgSection(cfgTabPanels[2], Lang.T("cfg.sub.input"), ref ty,
                new[] { PolicyCatalog.KeyEnglishInput });
            EnableCardCollapse(cfgTabPanels[2]);

            ty = 2;
            AddCfgSection(cfgTabPanels[3], Lang.T("cfg.sub.vram"), ref ty,
                new[] { PolicyCatalog.KeyVramShield });
            AddCfgSection(cfgTabPanels[3], "NVIDIA", ref ty,
                new[] { PolicyCatalog.KeyNvMaxPerf, PolicyCatalog.KeyNvLowLat,
                    PolicyCatalog.KeyNvSmoothMotion, PolicyCatalog.KeyNvShaderCache,
                    PolicyCatalog.KeyNvDlss, PolicyCatalog.KeyNvRebar });
            AddCfgSection(cfgTabPanels[3], "AMD", ref ty,
                new[] { PolicyCatalog.KeyAmdAntiLag, PolicyCatalog.KeyAmdAfmf });
            AddCfgSection(cfgTabPanels[3], "Intel", ref ty,
                new[] { PolicyCatalog.KeyIntelLowLatency, PolicyCatalog.KeyIntelEndurance });
            AddCfgFsoRow(cfgTabPanels[3], ref ty);
            AddCfgDpiRow(cfgTabPanels[3], ref ty);
            EnableCardCollapse(cfgTabPanels[3]);

            cfgTabKeys = new[]
            {
                new[] { PolicyCatalog.KeySuppress, PolicyCatalog.KeyAggressive,
                    PolicyCatalog.KeyGpuDemote,
                    PolicyCatalog.KeyBoost,
                    PolicyCatalog.KeyRenderLane },
                new[] { PolicyCatalog.KeyStrictCores, PolicyCatalog.KeyCoreDomainAlt,
                    PolicyCatalog.KeyCoreMask },
                new[] { PolicyCatalog.KeyPowerPlan, PolicyCatalog.KeyPowerYield, PolicyCatalog.KeyLaptopPerf,
                    PolicyCatalog.KeyDisableCpuIdle, PolicyCatalog.KeyStandbyCleaner,
                    PolicyCatalog.KeyCacheWarm,
                    PolicyCatalog.KeyPauseDl, PolicyCatalog.KeyPauseUpdate, PolicyCatalog.KeyPauseMaintenance,
                    PolicyCatalog.KeyPauseServices, PolicyCatalog.KeyAwake,
                    PolicyCatalog.KeyEnglishInput },
                new[] { PolicyCatalog.KeyVramShield, PolicyCatalog.KeyNvMaxPerf,
                    PolicyCatalog.KeyNvLowLat, PolicyCatalog.KeyNvSmoothMotion,
                    PolicyCatalog.KeyNvShaderCache,
                    PolicyCatalog.KeyNvDlss, PolicyCatalog.KeyNvRebar,
                    PolicyCatalog.KeyAmdAntiLag, PolicyCatalog.KeyAmdAfmf, PolicyCatalog.KeyIntelLowLatency,
                    PolicyCatalog.KeyIntelEndurance },
            };

            SyncCfgRows();
        }

        private static string[] CfgOptionValues(PolicyItem item)
        {
            return item.Kind == PolicyValueKind.Bool ? new[] { "0", "1" } : item.Choices;
        }

        private static string[] CfgOptionLabels(PolicyItem item)
        {
            switch (item.Key)
            {
                case PolicyCatalog.KeyPreset:
                    // 顺序对齐 Choices 的 0 1 5 4 2
                    return new[] { Lang.T("preset.standard"), Lang.T("preset.competitive"),
                        Lang.T("preset.extreme"), Lang.T("preset.handheld"), Lang.T("preset.custom") };
                case PolicyCatalog.KeyNvLowLat:
                    return new[] { Lang.T("frl.off"), Lang.T("nvll.on"), Lang.T("nvll.ultra") };
                case PolicyCatalog.KeyNvDlss:
                    return new[] { Lang.T("frl.off"), Lang.T("dlss.latest"), "J", "K" };
                default:
                    return new[] { Lang.T("cfg.off"), Lang.T("cfg.on") };
            }
        }

        private static string CfgValueLabel(PolicyItem item, string value)
        {
            string[] values = CfgOptionValues(item);
            string[] labels = CfgOptionLabels(item);
            for (int i = 0; i < values.Length && i < labels.Length; i++)
                if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase)) return labels[i];
            return value;
        }

        private static string CfgDescKey(PolicyItem item)
        {
            switch (item.Key)
            {
                case PolicyCatalog.KeyPreset: return "cfg.mode.sub";
                case PolicyCatalog.KeySuppress: return "v14.bg.master.sub";
                case PolicyCatalog.KeyBoost: return "v15.boost.sub";
                case PolicyCatalog.KeyAggressive: return "gm.aggressive.sub";
                case PolicyCatalog.KeyGpuDemote: return "gm.gpudemote.sub";
                case PolicyCatalog.KeyRenderLane: return "gm.lane.sub";
                case PolicyCatalog.KeyPowerPlan: return "cfg.plan.sub";
                case PolicyCatalog.KeyPowerYield: return "gm.poweryield.sub";
                case PolicyCatalog.KeyDisableCpuIdle: return "gm.disablecpuidle.sub";
                case PolicyCatalog.KeyStandbyCleaner: return "gm.standbycleaner.cfgsub";
                case PolicyCatalog.KeyVramShield: return "gm.vramshield.sub";
                case PolicyCatalog.KeyCacheWarm: return "gm.cachewarm.sub";
                case PolicyCatalog.KeyPauseDl: return "gm.pausedl.sub";
                case PolicyCatalog.KeyPauseUpdate: return "gm.pausewu.sub";
                case PolicyCatalog.KeyPauseMaintenance: return "gm.pausemaint.sub";
                case PolicyCatalog.KeyPauseServices: return "gm.pausesvc.sub";
                case PolicyCatalog.KeyAwake: return "set.awake.n";
                case PolicyCatalog.KeyEnglishInput: return "gm.englishinput.sub";
                case PolicyCatalog.KeyNvMaxPerf: return "set.nvmax.n";
                case PolicyCatalog.KeyNvLowLat: return "set.nvll.n";
                case PolicyCatalog.KeyNvSmoothMotion: return "set.nvsmooth.n";
                case PolicyCatalog.KeyNvShaderCache: return "set.nvshader.n";
                case PolicyCatalog.KeyNvRebar: return "set.nvrebar.n";
                case PolicyCatalog.KeyNvDlss: return "set.nvdlss.n";
                case PolicyCatalog.KeyAmdAntiLag: return "set.amdalag.n";
                case PolicyCatalog.KeyAmdAfmf: return "set.amdafmf.n";
                case PolicyCatalog.KeyIntelLowLatency: return "set.intel.lowlatency.n";
                case PolicyCatalog.KeyIntelEndurance: return "set.intel.endurance.n";
                case PolicyCatalog.KeyLaptopPerf: return "gm.laptopperf.sub";
                default: return null;
            }
        }

        private bool CfgItemSupported(PolicyItem item, out string reasonKey)
        {
            reasonKey = null;
#if PAVISE_SELFTEST
            if (CfgVendorGraphicsItem(item.Key) && VendorGraphicsSupportForTest != null)
            {
                bool supported = VendorGraphicsSupportForTest(item.Key);
                if (!supported) reasonKey = "set.amd.nosup";
                return supported;
            }
#endif
            bool nvOk = cfgSyncNvOk ?? NvApi.Available;
            bool amdOk = cfgSyncAmdOk ?? AdlxTweaks.Available;
            switch (item.Key)
            {
                // 门槛跟优化策略页那份一模一样 缺哪条就说哪条 别让人在这里开了之后干等着不生效
                case PolicyCatalog.KeyPowerYield:
                    reasonKey = PowerYieldUnavailableReasonKey();
                    return reasonKey == null;
                case PolicyCatalog.KeyNvMaxPerf:
                case PolicyCatalog.KeyNvLowLat:
                case PolicyCatalog.KeyNvShaderCache:
                case PolicyCatalog.KeyNvRebar:
                    if (!nvOk) reasonKey = "set.nv.none";
                    return nvOk;
                case PolicyCatalog.KeyNvSmoothMotion:
                    if (!nvOk) { reasonKey = "set.nv.none"; return false; }
                    if (!NvDrsTweaks.SmoothMotionSupported()) { reasonKey = "set.amd.nosup"; return false; }
                    return true;
                case PolicyCatalog.KeyNvDlss:
                    if (!nvOk) { reasonKey = "set.nv.none"; return false; }
                    if (!NvDrsTweaks.DlssGpuCapable() || !NvDrsTweaks.DlssDriverSupported())
                    { reasonKey = "set.amd.nosup"; return false; }
                    return true;
                case PolicyCatalog.KeyAmdAntiLag:
                    if (!amdOk) { reasonKey = "set.amd.none"; return false; }
                    if (!AdlxTweaks.AntiLagSupported()) { reasonKey = "set.amd.nosup"; return false; }
                    return true;
                case PolicyCatalog.KeyAmdAfmf:
                    if (!amdOk) { reasonKey = "set.amd.none"; return false; }
                    if (!AdlxTweaks.AfmfSupported()) { reasonKey = "set.amd.nosup"; return false; }
                    return true;
                case PolicyCatalog.KeyIntelLowLatency:
                    if (!IntelGraphicsTweaks.HasAvailable) { reasonKey = "set.intel.none"; return false; }
                    if (!IntelGraphicsTweaks.LowLatencySupported)
                    { reasonKey = "set.intel.lowlatency.unsupported"; return false; }
                    return true;
                case PolicyCatalog.KeyIntelEndurance:
                    if (!IntelGraphicsTweaks.HasAvailable) { reasonKey = "set.intel.none"; return false; }
                    if (!Native.HasSystemBattery()) { reasonKey = "set.intel.endurance.desktop"; return false; }
                    return true;
                case PolicyCatalog.KeyLaptopPerf:
                    if (!Native.HasSystemBattery() || !LaptopPerfMode.SupportedCached())
                    { reasonKey = "laptopperf.unsupported"; return false; }
                    return true;
                default:
                    return true;
            }
        }

        private int CfgRowIndexOf(PolicyItem item, string[] values)
        {
            string ov;
            if (cfgProfile == null || !cfgProfile.Overrides.TryGetValue(item.Key, out ov)) return 0;
            for (int i = 0; i < values.Length; i++)
                if (string.Equals(values[i], ov, StringComparison.OrdinalIgnoreCase)) return i + 1;
            return 0;
        }

        private static bool CfgVendorGraphicsItem(string key)
        {
            switch (key)
            {
                case PolicyCatalog.KeyNvMaxPerf:
                case PolicyCatalog.KeyNvLowLat:
                case PolicyCatalog.KeyNvSmoothMotion:
                case PolicyCatalog.KeyNvShaderCache:
                case PolicyCatalog.KeyNvRebar:
                case PolicyCatalog.KeyNvDlss:
                case PolicyCatalog.KeyAmdAntiLag:
                case PolicyCatalog.KeyAmdAfmf:
                    return true;
                default:
                    return false;
            }
        }

        private static bool CfgGraphicsOff(PolicyItem item, string value)
        {
            return item.Kind == PolicyValueKind.Bool ? value == "0" : value == "off";
        }

        private bool CfgCanTurnOff(PolicyItem item)
        {
            string current = PolicyResolver.Read(cfgProfile, item.Key);
            if (CfgVendorGraphicsItem(item.Key)) return !CfgGraphicsOff(item, current);
            return (item.Key == PolicyCatalog.KeyIntelLowLatency || item.Key == PolicyCatalog.KeyPowerYield)
                && current == "1";
        }

        private bool CfgVendorGraphicsChoiceAllowed(string key, string value)
        {
            if (!CfgVendorGraphicsItem(key)) return true;
            PolicyItem item = PolicyCatalog.ItemOf(key);
            string reason;
            return CfgGraphicsOff(item, value ?? PolicyResolver.GlobalValue(key))
                || CfgItemSupported(item, out reason);
        }
    }
}

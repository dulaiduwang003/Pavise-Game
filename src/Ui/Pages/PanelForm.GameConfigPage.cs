// @author bdth 2074055628@qq.com
// 文件用途 构建单个游戏的独立配置二级页 标签内分节平铺 逐核分配与覆盖编辑
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private DBPanel pageGameConfig;
        private string cfgProfileId;
        private GameProfile cfgProfile;
        private Label lblCfgCount;
        private Label lblCfgSub;
        private ModuleBanner cfgBanner;
        private TechTabs cfgTabs;
        private DBPanel[] cfgTabPanels;
        private string[][] cfgTabKeys;
        private readonly List<Action> cfgRowSync = new List<Action>();
        private readonly Dictionary<string, SettingCard> cfgCardByKey
            = new Dictionary<string, SettingCard>(StringComparer.Ordinal);
        private int cfgJumpCursor;
#if PAVISE_SELFTEST
        internal Func<string, bool> VendorGraphicsSupportForTest;
#endif

        private PerformancePreset cfgEffMode;
        // 仅一轮 SyncCfgRows 内有效的厂商可用性快照 为 null 时实查
        private bool? cfgSyncNvOk, cfgSyncAmdOk;

        private void ShowGameConfigPage(string profileId)
        {
            cfgProfileId = profileId;
            if (!RefreshCfgProfile()) return;
            BuildGameConfigContent();
            SetModeFlyout(false);
            SetSearchFlyout(false);
            SetPowerFlyout(false);
            pageBaseLeft = Theme.S(RailW);
            pageGameConfig.Left = pageBaseLeft;
            bool revealing = PreparePageReveal(pageGameConfig);
            bool ready = false;
            root.SuspendLayout();
            try
            {
                foreach (var p in pages) p.Visible = false;
                pageGameConfig.Visible = true;
                curPage = pageGameConfig;
                if (UiActive) UiClock.Wake();
                NotifyPageActivation();
                ready = true;
            }
            finally
            {
                try
                {
                    root.ResumeLayout(true);
                    if (revealing)
                    {
                        if (ready) StartPageReveal();
                        else StopPageReveal();
                    }
                }
                catch { StopPageReveal(); throw; }
            }
        }

        private void CloseGameConfigPage()
        {
            nav.Select((int)PageId.Library);
        }

        private bool RefreshCfgProfile()
        {
            if (string.IsNullOrEmpty(cfgProfileId)) return false;
            foreach (GameProfile p in gameMode.GetProfiles())
                if (string.Equals(p.Id, cfgProfileId, StringComparison.OrdinalIgnoreCase))
                {
                    cfgProfile = p;
                    return true;
                }
            cfgProfile = null;
            return false;
        }

        private static bool CfgPresetForces(string key, PerformancePreset mode, out bool effective)
        {
            bool competitive = mode == PerformancePreset.Competitive
                || mode == PerformancePreset.Handheld;
            bool custom = mode == PerformancePreset.Custom;
            switch (key)
            {
                case PolicyCatalog.KeyAggressive:
                case PolicyCatalog.KeyPauseDl:
                    effective = competitive;
                    return !custom;
                default:
                    effective = false;
                    return false;
            }
        }

        private void SyncCfgRows()
        {
            if (!RefreshCfgProfile()) return;
            string presetOverride;
            int presetParsed;
            cfgEffMode = cfgProfile.Overrides.TryGetValue(PolicyCatalog.KeyPreset, out presetOverride)
                && int.TryParse(presetOverride, out presetParsed)
                && PresetValue.IsValid(presetParsed)
                ? (PerformancePreset)presetParsed : gameMode.Preset;
            // 一轮同步内各行共享厂商可用性探测 驱动状态不会在一轮里变
            cfgSyncNvOk = NvApi.Available;
            cfgSyncAmdOk = AdlxTweaks.Available;
            try { foreach (Action sync in cfgRowSync) sync(); }
            finally { cfgSyncNvOk = cfgSyncAmdOk = null; }
            if (cfgTabs != null && cfgTabKeys != null)
            {
                var hotFlags = new bool[cfgTabKeys.Length];
                for (int i = 0; i < cfgTabKeys.Length; i++)
                    foreach (string key in cfgTabKeys[i])
                        if (cfgProfile.Overrides.ContainsKey(key)) { hotFlags[i] = true; break; }
                cfgTabs.SetHot(hotFlags);
            }
            if (lblCfgCount != null)
            {
                int n = GameLibraryRow.OrdinaryOverrideCount(cfgProfile);
                lblCfgCount.Text = CfgOverrideSummary(cfgProfile);
                lblCfgCount.ForeColor = n > 0 ? Theme.Accent : Theme.Faint;
                lblCfgCount.Cursor = n > 0 ? Cursors.Hand : Cursors.Default;
            }
            if (lblCfgSub != null)
            {
                bool frozen = cfgProfileId != null
                    && string.Equals(gameMode.SessionPolicyProfileId, cfgProfileId, StringComparison.OrdinalIgnoreCase);
                lblCfgSub.Text = frozen ? Lang.T("cfg.frozen") : Lang.T("cfg.sub");
                lblCfgSub.ForeColor = frozen ? Theme.Accent : Theme.Dim;
            }
            if (cfgBanner != null)
            {
                int count = GameLibraryRow.OrdinaryOverrideCount(cfgProfile);
                bool frozen = cfgProfileId != null
                    && string.Equals(gameMode.SessionPolicyProfileId, cfgProfileId, StringComparison.OrdinalIgnoreCase);
                cfgBanner.State = CfgOverrideSummary(cfgProfile);
                cfgBanner.StateColor = count > 0 ? Theme.Accent : Theme.Green;
                cfgBanner.Cursor = count > 0 ? Cursors.Hand : Cursors.Default;
                cfgBanner.Detail = frozen ? Lang.T("cfg.frozen") : Lang.T("cfg.sub");
            }
        }

        private void AddCfgSection(Control panel, string title, ref int y, string[] keys)
        {
            Section(panel, title, 6, y);
            y += 24;
            foreach (string key in keys)
                AddCfgPickerRow(panel, ref y, PolicyCatalog.ItemOf(key));
            y += 6;
        }

        // 逐游戏禁用全屏优化 不是对局临时下发 而是立即持久写该 exe 的兼容层
        //   所以不进 PolicyCatalog 不走 Overrides 直接读写 HKCU 兼容层字符串 拨开即写 拨关即删
        private string CfgExePath()
        {
            if (cfgProfile == null) return null;
            return cfgProfile.PreferredExecutablePath;
        }

        private void AddCfgFsoRow(Control parent, ref int y)
        {
            Section(parent, Lang.T("cfg.fso.group"), 6, y);
            y += 24;
            string exe = CfgExePath();
            bool hasExe = !string.IsNullOrEmpty(exe);
            Toggle sw = MakeSwitch(hasExe && FsoTweak.IsDisabledForExe(exe), null);
            sw.Enabled = hasExe;
            int cardH;
            MakeAutoCard(parent, 6, y, ScrollContentW, 56, Lang.T("cfg.fso"),
                hasExe ? Lang.T("cfg.fso.sub") : Lang.T("cfg.fso.noexe"), sw, out cardH);
            y += cardH + 8;
            sw.CheckedChanged += delegate
            {
                string p = CfgExePath();
                if (string.IsNullOrEmpty(p)) { sw.SetSilently(false); return; }
                bool want = sw.Checked;
                bool ok = IrqMutationBoundary.Run<bool>(delegate
                {
                    return FsoTweak.SetForExe(p, want);
                });
                sw.SetSilently(FsoTweak.IsDisabledForExe(p));
                // 兼容层写不进去只有日志里有 界面上开关自己弹回来 看着像开关坏了
                if (!ok) PaviseDialog.Warn(this, App.DisplayName, Lang.T("cfg.fso.fail"));
            };
            cfgRowSync.Add(delegate
            {
                string p = CfgExePath();
                bool ok = !string.IsNullOrEmpty(p);
                sw.Enabled = ok;
                sw.SetSilently(ok && FsoTweak.IsDisabledForExe(p));
            });
        }

        private void JumpToNextCfgOverride()
        {
            if (cfgProfile == null || cfgTabKeys == null || GameLibraryRow.OrdinaryOverrideCount(cfgProfile) == 0) return;
            var cards = new List<SettingCard>();
            SettingCard presetCard;
            if (cfgProfile.Overrides.ContainsKey(PolicyCatalog.KeyPreset)
                && cfgCardByKey.TryGetValue(PolicyCatalog.KeyPreset, out presetCard))
                cards.Add(presetCard);
            foreach (string[] tabKeys in cfgTabKeys)
                foreach (string key in tabKeys)
                {
                    if (!cfgProfile.Overrides.ContainsKey(key)) continue;
                    SettingCard card;
                    if (!cfgCardByKey.TryGetValue(key, out card)) card = cfgCoreCard;
                    if (card != null && !card.IsDisposed && !cards.Contains(card)) cards.Add(card);
                }
            if (cards.Count == 0) return;
            SettingCard target = cards[cfgJumpCursor % cards.Count];
            cfgJumpCursor++;
            RevealTabFor(cfgTabs, cfgTabPanels, target);
            ScrollCardIntoView(target);
        }

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
                new[] { PolicyCatalog.KeyPowerPlan, PolicyCatalog.KeyPowerYield,
                    PolicyCatalog.KeyDisableCpuIdle, PolicyCatalog.KeyStandbyCleaner,
                    PolicyCatalog.KeyMemShield, PolicyCatalog.KeyCacheWarm });
            AddCfgSection(cfgTabPanels[2], Lang.T("cfg.sub.net"), ref ty,
                new[] { PolicyCatalog.KeyPauseDl, PolicyCatalog.KeyPauseUpdate,
                    PolicyCatalog.KeyWlanGuard });
            AddCfgSection(cfgTabPanels[2], Lang.T("cfg.group.env"), ref ty,
                new[] { PolicyCatalog.KeyPauseServices });
            AddCfgSection(cfgTabPanels[2], Lang.T("cfg.sub.presence"), ref ty,
                new[] { PolicyCatalog.KeyAwake, PolicyCatalog.KeyDisplaySolo });
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
                new[] { PolicyCatalog.KeyIntelLowLatency });
            AddCfgFsoRow(cfgTabPanels[3], ref ty);
            EnableCardCollapse(cfgTabPanels[3]);

            cfgTabKeys = new[]
            {
                new[] { PolicyCatalog.KeySuppress, PolicyCatalog.KeyAggressive,
                    PolicyCatalog.KeyGpuDemote,
                    PolicyCatalog.KeyBoost,
                    PolicyCatalog.KeyRenderLane },
                new[] { PolicyCatalog.KeyStrictCores, PolicyCatalog.KeyCoreDomainAlt,
                    PolicyCatalog.KeyCoreMask },
                new[] { PolicyCatalog.KeyPowerPlan, PolicyCatalog.KeyPowerYield,
                    PolicyCatalog.KeyDisableCpuIdle, PolicyCatalog.KeyStandbyCleaner,
                    PolicyCatalog.KeyMemShield, PolicyCatalog.KeyCacheWarm,
                    PolicyCatalog.KeyPauseDl, PolicyCatalog.KeyPauseUpdate,
                    PolicyCatalog.KeyWlanGuard, PolicyCatalog.KeyPauseServices, PolicyCatalog.KeyAwake,
                    PolicyCatalog.KeyDisplaySolo,
                    PolicyCatalog.KeyEnglishInput },
                new[] { PolicyCatalog.KeyVramShield, PolicyCatalog.KeyNvMaxPerf,
                    PolicyCatalog.KeyNvLowLat, PolicyCatalog.KeyNvSmoothMotion,
                    PolicyCatalog.KeyNvShaderCache,
                    PolicyCatalog.KeyNvDlss, PolicyCatalog.KeyNvRebar,
                    PolicyCatalog.KeyAmdAntiLag, PolicyCatalog.KeyAmdAfmf, PolicyCatalog.KeyIntelLowLatency },
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
                    return new[] { Lang.T("preset.standard"), Lang.T("preset.competitive"),
                        Lang.T("preset.handheld"), Lang.T("preset.custom") };
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
                case PolicyCatalog.KeyMemShield: return "gm.memshield.sub";
                case PolicyCatalog.KeyCacheWarm: return "gm.cachewarm.sub";
                case PolicyCatalog.KeyDisplaySolo: return "gm.solo.sub";
                case PolicyCatalog.KeyPauseDl: return "gm.pausedl.sub";
                case PolicyCatalog.KeyPauseUpdate: return "gm.pausewu.sub";
                case PolicyCatalog.KeyPauseServices: return "gm.pausesvc.sub";
                case PolicyCatalog.KeyWlanGuard: return "gm.wlanguard.sub";
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

        private void AddCfgModeRow(Control parent, ref int y)
        {
            PolicyItem item = PolicyCatalog.ItemOf(PolicyCatalog.KeyPreset);
            string[] values = CfgOptionValues(item);
            var strip = new ModeStrip();
            strip.Index = CfgRowIndexOf(item, values);
            strip.Size = new Size(Theme.S(360), Theme.S(38));
            int cardH;
            SettingCard card = MakeAutoCard(parent, ContentX, y, ContentW, 64,
                Lang.T(item.LangKey), Lang.T("cfg.mode.sub"), strip, out cardH);
            y += cardH + 8;
            cfgCardByKey[item.Key] = card;
            card.TrackChildHover(strip);
            cfgRowSync.Add(delegate
            {
                strip.Index = CfgRowIndexOf(item, values);
                strip.SetGlobal(gameMode.Preset);
            });
            strip.IndexChanged = delegate(int index)
            {
                if (cfgProfile == null) return;
                if (index <= 0) gameMode.ClearProfileOverride(cfgProfileId, PolicyCatalog.KeyPreset);
                else gameMode.SetProfileOverride(cfgProfileId, PolicyCatalog.KeyPreset, values[index - 1]);
                SyncCfgRows();
            };
        }

        private void AddCfgPickerRow(Control parent, ref int y, PolicyItem item)
        {
            AddCfgPickerRow(parent, ref y, item, 6, ScrollContentW, true);
        }

        private void AddCfgPickerRow(Control parent, ref int y, PolicyItem item,
            int x, int w, bool withDesc)
        {
            string[] values = CfgOptionValues(item);
            string[] optionLabels = CfgOptionLabels(item);
            var labels = new string[optionLabels.Length + 1];
            labels[0] = Lang.T("cfg.follow");
            for (int i = 0; i < optionLabels.Length; i++) labels[i + 1] = optionLabels[i];

            string reasonKey;
            bool supported = CfgItemSupported(item, out reasonKey);
            // 能力丢失不能把一个已经开着的选项困死在里面
            bool canTurnOff = CfgCanTurnOff(item);
            bool vendorGraphics = CfgVendorGraphicsItem(item.Key);
            string descKey = CfgDescKey(item);
            string adminNoticeKey = CfgEnableAdminNoticeKey(item.Key);
            bool enableNeedsAdmin = adminNoticeKey != null && !elevated;
            string desc = enableNeedsAdmin ? Lang.T(adminNoticeKey)
                : !supported ? Lang.T(reasonKey)
                : (withDesc && descKey != null ? Lang.T(descKey) : "");
            if (item.Key == PolicyCatalog.KeyStandbyCleaner && !gameMode.StandbyCleaningOptionsValid)
                desc = Lang.T("standbycleaner.config.invalid");
            if (item.Key == PolicyCatalog.KeyPowerYield && supported && PowerBudgetYield.Fused)
                desc = Lang.T("gm.poweryield.fused");

            var picker = new TierPicker();
            picker.Labels = labels;
            picker.Index = supported || canTurnOff || vendorGraphics ? CfgRowIndexOf(item, values) : 1;
            int segW = labels.Length >= 6 ? 68 : 78;
            picker.SetBounds(0, 0, Theme.S(labels.Length * segW + 12), Theme.S(30));
            bool showDisabled = adminNoticeKey != null || item.Key == PolicyCatalog.KeyIntelLowLatency
                || item.Key == PolicyCatalog.KeyPowerYield || vendorGraphics;
            picker.Enabled = (supported || canTurnOff) && (!enableNeedsAdmin
                || PolicyResolver.Read(cfgProfile, item.Key) == "1");
            picker.Visible = supported || canTurnOff || showDisabled;

            int cardH;
            int minimumH = item.Key == PolicyCatalog.KeyStandbyCleaner
                ? StandbyCleanerCardHeight(w, picker, 54, true) : 54;
            if (item.Key == PolicyCatalog.KeyEnglishInput || item.Key == PolicyCatalog.KeyIntelLowLatency)
                minimumH = FullTextCardHeight(desc, w, picker, minimumH);
            SettingCard card = MakeAutoCard(parent, x, y, w, minimumH,
                Lang.T(item.LangKey), desc, picker, out cardH);
            y += cardH + 8;
            cfgCardByKey[item.Key] = card;

            string key = item.Key;
            Action sync = delegate
            {
                if (key == PolicyCatalog.KeyPowerYield)
                {
                    supported = CfgItemSupported(item, out reasonKey);
                    card.Desc = Lang.T(!supported ? reasonKey
                        : PowerBudgetYield.Fused ? "gm.poweryield.fused" : "gm.poweryield.sub");
                }
                else if (vendorGraphics)
                {
                    supported = CfgItemSupported(item, out reasonKey);
                    card.Desc = !supported ? Lang.T(reasonKey)
                        : withDesc && descKey != null ? Lang.T(descKey) : "";
                }
                bool has = cfgProfile.Overrides.ContainsKey(key);
                string globalLabel = CfgValueLabel(item, PolicyResolver.GlobalValue(key));
                bool allowOff = CfgCanTurnOff(item);
                picker.Index = supported || allowOff || vendorGraphics ? CfgRowIndexOf(item, values) : 1;
                bool forcedEffective;
                bool forced = CfgPresetForces(key, cfgEffMode, out forcedEffective);
                bool needsAdmin = adminNoticeKey != null && !elevated;
                bool usable = (supported || allowOff) && !forced && (!needsAdmin
                    || PolicyResolver.Read(cfgProfile, key) == "1");
                picker.Enabled = usable;
                picker.Visible = usable || showDisabled;
                if (adminNoticeKey != null)
                    card.Desc = Lang.T(key == PolicyCatalog.KeyStandbyCleaner && !gameMode.StandbyCleaningOptionsValid
                        ? "standbycleaner.config.invalid" : needsAdmin ? adminNoticeKey : descKey);
                card.SetLock(!supported && !vendorGraphics ? Lang.T("v14.preset.forced.off")
                    : forced ? Lang.T(forcedEffective ? "v14.preset.forced.on" : "v14.preset.forced.off")
                    : "", supported && forcedEffective);
                card.SetValue(has ? Lang.F("cfg.state.over", globalLabel)
                    : Lang.F("cfg.state.follow", globalLabel), has ? Theme.Accent : Theme.Faint);
            };
            cfgRowSync.Add(sync);
            picker.IndexChanged = delegate(int index)
            {
                string chosen = index <= 0 ? null : values[index - 1];
                if (!ApplyCfgPolicyChoice(key, chosen))
                {
                    picker.Index = CfgRowIndexOf(item, values);
                    // 被厂商显卡门槛挡下时要说明原因 卡片描述解释不了"为什么不能跟随全局"
                    //   Visible 门槛 隔离回归在未显示的窗体上驱动该路径 不能弹窗
                    string canonical = chosen == null ? null : PolicyCatalog.Canonical(key, chosen);
                    if (Visible && (chosen == null || canonical != null)
                        && !CfgVendorGraphicsChoiceAllowed(key, canonical))
                        PaviseDialog.Info(this, Lang.T(item.LangKey), Lang.T("cfg.inherit.blocked"));
                    return;
                }
                SyncCfgRows();
            };
        }

        private bool ApplyCfgPolicyChoice(string key, string value)
        {
            if (cfgProfile == null) return false;
            string canonical = value == null ? null : PolicyCatalog.Canonical(key, value);
            if (value != null && canonical == null) return false;
            if (!CfgVendorGraphicsChoiceAllowed(key, canonical)) return false;
            bool turningOn = canonical == "1";
            bool inheritingPowerYield = value == null && key == PolicyCatalog.KeyPowerYield
                && CfgPowerYieldInheritanceNeedsConfirmation(cfgProfile);
            // 实验类布尔项的"跟随全局"与显式拨开同权 全局开着而本游戏此前生效为关时
            //   清掉覆盖就是启用 确认必须照弹 熔断也要跟着清
            bool inheritingExperimental = value == null && IsGuardedExperimentalKey(key)
                && CfgGuardedInheritanceNeedsConfirmation(cfgProfile, key);
            bool inheritingGuardedOn = value == null
                && ((key == PolicyCatalog.KeyDisableCpuIdle && CfgCpuIdleInheritanceNeedsConfirmation(cfgProfile))
                    || (key == PolicyCatalog.KeyStandbyCleaner && CfgStandbyCleanerInheritanceNeedsConfirmation(cfgProfile))
                    || (key == PolicyCatalog.KeyIntelLowLatency && CfgIntelInheritanceNeedsConfirmation(cfgProfile))
                    || inheritingPowerYield || inheritingExperimental);
            // 在这里拨到开 等同于把全局开关打开一次 实验提示必须照弹
            //   否则从这个页面能绕开优化策略页特意加的门槛 用户全程没见过警告
            if ((turningOn || inheritingGuardedOn) && !ConfirmCfgEnable(key)) return false;
            bool saved = value == null ? gameMode.ClearProfileOverride(cfgProfileId, key)
                : gameMode.SetProfileOverride(cfgProfileId, key, canonical);
            // 显存驻留在这里拨到开 也等同于全局开关重开一次 熔断要跟着清掉
            //   否则上次验不过留下的熔断会让这局直接跳过 用户在这个页面无从解除
            if (saved && (turningOn || inheritingExperimental) && key == PolicyCatalog.KeyVramShield)
                VramShield.ClearFuse();
            if (saved && (turningOn || inheritingExperimental) && key == PolicyCatalog.KeyMemShield)
                MemShield.ClearFuse();
            if (saved && key == PolicyCatalog.KeyPowerYield && (turningOn || inheritingPowerYield))
                PowerBudgetYield.ClearFuse();
            return saved;
        }

        // 全局开关带确认的项 逐游戏覆盖到开也要走同一段提示 文案共用一份
        private bool ConfirmCfgEnable(string key)
        {
            switch (key)
            {
                case PolicyCatalog.KeyVramShield:
                    return ConfirmVramShieldEnable();
                case PolicyCatalog.KeyMemShield:
                    return ConfirmMemShieldEnable();
                case PolicyCatalog.KeyCacheWarm:
                    return ConfirmCacheWarmEnable();
                case PolicyCatalog.KeyDisplaySolo:
                    return ConfirmDisplaySoloEnable();
                case PolicyCatalog.KeyPowerYield:
                    return ConfirmPowerYieldEnable();
                case PolicyCatalog.KeyDisableCpuIdle:
                    return ConfirmDisableCpuIdleEnable();
                case PolicyCatalog.KeyStandbyCleaner:
                    return ConfirmStandbyCleanerEnable();
                case PolicyCatalog.KeyIntelLowLatency:
                    return ConfirmIntelLowLatency();
                default:
                    return true;
            }
        }

        private void ClearAllCfgOverrides()
        {
            if (!RefreshCfgProfile()) return;
            string confirmation = CfgClearConfirmation(cfgProfile);
            if (confirmation == null) return;
            if (!PaviseDialog.Confirm(this, Lang.T("cfg.clear"),
                    confirmation, DlgKind.Warn))
                return;
            if (!ApplyCfgClearAllOverrides())
            {
                // 确认弹窗之后不能沉默 门槛拦下时说明是哪一项挡住了整次清除
                //   确认被用户自己取消的情况这里查不到被挡的键 维持无提示返回
                string blocked = Visible ? CfgBlockedVendorInheritKey() : null;
                if (blocked != null)
                    PaviseDialog.Info(this, Lang.T("cfg.clear"), Lang.F("cfg.clear.blocked",
                        Lang.T(PolicyCatalog.ItemOf(blocked).LangKey)));
                return;
            }
            cfgCoreManualPicked = false;
            SyncCfgRows();
        }

        // 厂商显卡键在全局仍为开且当前设备不支持时不能改回跟随全局
        //   否则等于替用户确认一个当前设备生效不了的全局开启
        private string CfgBlockedVendorInheritKey()
        {
            if (cfgProfile == null) return null;
            foreach (string key in cfgProfile.Overrides.Keys)
                if (!CfgVendorGraphicsChoiceAllowed(key, null)) return key;
            return null;
        }

        private bool ApplyCfgClearAllOverrides()
        {
            if (cfgProfile == null) return false;
            if (CfgBlockedVendorInheritKey() != null) return false;
            // 移除任何覆盖之前 先把所有开启项都确认完 第二个警告被取消时
            // 不能出现第一个已经把档案清掉一半的情况
            if (CfgCpuIdleInheritanceNeedsConfirmation(cfgProfile)
                && !ConfirmCfgEnable(PolicyCatalog.KeyDisableCpuIdle)) return false;
            if (CfgStandbyCleanerInheritanceNeedsConfirmation(cfgProfile)
                && !ConfirmCfgEnable(PolicyCatalog.KeyStandbyCleaner)) return false;
            if (CfgIntelInheritanceNeedsConfirmation(cfgProfile)
                && !ConfirmCfgEnable(PolicyCatalog.KeyIntelLowLatency)) return false;
            bool powerYieldWillEnable = CfgPowerYieldInheritanceNeedsConfirmation(cfgProfile);
            if (powerYieldWillEnable && !ConfirmCfgEnable(PolicyCatalog.KeyPowerYield)) return false;
            // 实验类布尔项同权 全部清除等同把它们中被本游戏关着的那些拨到全局的开
            bool vramWillEnable = false, memWillEnable = false;
            foreach (string guarded in GuardedExperimentalKeys)
            {
                if (!CfgGuardedInheritanceNeedsConfirmation(cfgProfile, guarded)) continue;
                if (!ConfirmCfgEnable(guarded)) return false;
                if (guarded == PolicyCatalog.KeyVramShield) vramWillEnable = true;
                if (guarded == PolicyCatalog.KeyMemShield) memWillEnable = true;
            }
            // 其它警告可能泵过界面消息 这期间驱动可用性会变
            if (CfgBlockedVendorInheritKey() != null) return false;
            bool cleared = gameMode.ClearProfileOverrides(cfgProfileId) > 0;
            if (cleared && powerYieldWillEnable) PowerBudgetYield.ClearFuse();
            if (cleared && vramWillEnable) VramShield.ClearFuse();
            if (cleared && memWillEnable) MemShield.ClearFuse();
            return cleared;
        }

        private static string CfgEnableAdminNoticeKey(string key)
        {
            if (key == PolicyCatalog.KeyDisableCpuIdle) return "gm.disablecpuidle.needadmin";
            if (key == PolicyCatalog.KeyStandbyCleaner) return "gm.standbycleaner.needadmin";
            return null;
        }

        // 带开启确认的实验类布尔项 "跟随全局/全部清除"要与显式拨开走同一道门
        private static readonly string[] GuardedExperimentalKeys =
        {
            PolicyCatalog.KeyVramShield, PolicyCatalog.KeyMemShield,
            PolicyCatalog.KeyCacheWarm, PolicyCatalog.KeyDisplaySolo,
        };

        private static bool IsGuardedExperimentalKey(string key)
        {
            foreach (string guarded in GuardedExperimentalKeys)
                if (string.Equals(guarded, key, StringComparison.Ordinal)) return true;
            return false;
        }

        internal static bool CfgGuardedInheritanceNeedsConfirmation(GameProfile profile, string key)
        {
            // 与 CpuIdle 同款判据 清掉显式的关等同选择了全局的开
            return profile != null
                && PolicyResolver.Read(profile, key) != "1"
                && PolicyResolver.GlobalValue(key) == "1";
        }

        internal static bool CfgCpuIdleInheritanceNeedsConfirmation(GameProfile profile)
        {
            // 把一条显式的关闭移掉 效果和主动选开是一样的
            return profile != null
                && PolicyResolver.Read(profile, PolicyCatalog.KeyDisableCpuIdle) != "1"
                && PolicyResolver.GlobalValue(PolicyCatalog.KeyDisableCpuIdle) == "1";
        }

        internal static bool CfgStandbyCleanerInheritanceNeedsConfirmation(GameProfile profile)
        {
            return profile != null
                && PolicyResolver.Read(profile, PolicyCatalog.KeyStandbyCleaner) != "1"
                && PolicyResolver.GlobalValue(PolicyCatalog.KeyStandbyCleaner) == "1";
        }

        internal static bool CfgIntelInheritanceNeedsConfirmation(GameProfile profile)
        {
            return profile != null
                && PolicyResolver.Read(profile, PolicyCatalog.KeyIntelLowLatency) != "1"
                && PolicyResolver.GlobalValue(PolicyCatalog.KeyIntelLowLatency) == "1";
        }

        internal static bool CfgPowerYieldInheritanceNeedsConfirmation(GameProfile profile)
        {
            return profile != null
                && PolicyResolver.Read(profile, PolicyCatalog.KeyPowerYield) != "1"
                && PolicyResolver.GlobalValue(PolicyCatalog.KeyPowerYield) == "1";
        }

        internal static string CfgOverrideSummary(GameProfile profile)
        {
            int count = GameLibraryRow.OrdinaryOverrideCount(profile);
            if (count > 0) return Lang.F("cfg.count", count);
            return Lang.T(profile != null && profile.Overrides.ContainsKey(PolicyCatalog.KeySuppressFamily)
                ? "cfg.count.none.family" : "cfg.count.none");
        }

        internal static string CfgClearConfirmation(GameProfile profile)
        {
            if (profile == null) return null;
            int count = GameLibraryRow.OrdinaryOverrideCount(profile);
            bool family = profile.Overrides.ContainsKey(PolicyCatalog.KeySuppressFamily);
            if (!family && count == 0) return null;
            if (!family) return Lang.F("cfg.clear.confirm", profile.Name, count);
            // 家族开关并不在本页出现 但“全部清除”仍会关闭它 不能当作跟随全局
            return count > 0 ? Lang.F("cfg.clear.family.confirm", profile.Name, count)
                : Lang.F("cfg.clear.family.only", profile.Name);
        }
    }
}

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
        private TechTabs cfgTabs;
        private DBPanel[] cfgTabPanels;
        private string[][] cfgTabKeys;
        private readonly List<Action> cfgRowSync = new List<Action>();
        private readonly Dictionary<string, SettingCard> cfgCardByKey
            = new Dictionary<string, SettingCard>(StringComparer.Ordinal);
        private int cfgJumpCursor;

        private PerformancePreset cfgEffMode;

        private void ShowGameConfigPage(string profileId)
        {
            cfgProfileId = profileId;
            if (!RefreshCfgProfile()) return;
            BuildGameConfigContent();
            SetModeFlyout(false);
            SetSearchFlyout(false);
            SetPowerFlyout(false);
            foreach (var p in pages) p.Visible = false;
            pageGameConfig.Visible = true;
            curPage = pageGameConfig;
            pageBaseLeft = Theme.S(RailW);
            pageGameConfig.Left = pageBaseLeft + Theme.S(16);
            pageSlide.Speed = 0.26f; pageSlide.Set(1f); pageSlide.To(0f);
            if (UiActive) UiClock.Wake();
            NotifyPageActivation();
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
            bool competitive = mode == PerformancePreset.Competitive;
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
                && presetParsed >= 0 && presetParsed <= 2
                ? (PerformancePreset)presetParsed : gameMode.Preset;
            foreach (Action sync in cfgRowSync) sync();
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
                int n = cfgProfile.Overrides.Count;
                lblCfgCount.Text = n > 0 ? Lang.F("cfg.count", n) : Lang.T("cfg.count.none");
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
        }

        private void AddCfgSection(Control panel, string title, ref int y, string[] keys)
        {
            Section(panel, title, 6, y);
            y += 24;
            foreach (string key in keys)
                AddCfgPickerRow(panel, ref y, PolicyCatalog.ItemOf(key));
            y += 6;
        }

        private void JumpToNextCfgOverride()
        {
            if (cfgProfile == null || cfgTabKeys == null || cfgProfile.Overrides.Count == 0) return;
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

            var title = new Label();
            title.Text = Lang.F("cfg.title", cfgProfile.Name);
            title.ForeColor = Theme.Fg; title.BackColor = Theme.Bg;
            title.Font = Theme.UI(13.5f, true);
            title.UseCompatibleTextRendering = false;
            title.AutoEllipsis = true;
            title.SetBounds(Theme.S(ContentX), Theme.S(46), Theme.S(ContentW - 300), Theme.S(24));
            pageGameConfig.Controls.Add(title);

            lblCfgCount = new Label();
            lblCfgCount.BackColor = Theme.Bg;
            lblCfgCount.Font = Theme.UI(8.4f, true);
            lblCfgCount.UseCompatibleTextRendering = false;
            lblCfgCount.TextAlign = ContentAlignment.MiddleRight;
            lblCfgCount.SetBounds(Theme.S(ContentX + ContentW - 300), Theme.S(51), Theme.S(300), Theme.S(20));
            lblCfgCount.Click += delegate { JumpToNextCfgOverride(); };
            pageGameConfig.Controls.Add(lblCfgCount);

            var copy = new PillButton(Lang.T("cfg.copy"));
            copy.SetBounds(Theme.S(ContentX + ContentW - 268), Theme.S(8), Theme.S(160), Theme.S(32));
            copy.Click += delegate { ShowCopyOverridesDialog(); };
            pageGameConfig.Controls.Add(copy);

            var clear = new PillButton(Lang.T("cfg.clear"), BtnKind.Danger);
            clear.SetBounds(Theme.S(ContentX + ContentW - 100), Theme.S(8), Theme.S(100), Theme.S(32));
            clear.Click += delegate { ClearAllCfgOverrides(); };
            pageGameConfig.Controls.Add(clear);

            lblCfgSub = new Label();
            lblCfgSub.BackColor = Theme.Bg;
            lblCfgSub.Font = Theme.UI(8.4f, false);
            lblCfgSub.UseCompatibleTextRendering = false;
            lblCfgSub.AutoEllipsis = true;
            lblCfgSub.SetBounds(Theme.S(ContentX + 1), Theme.S(71), Theme.S(ContentW - 2), Theme.S(17));
            pageGameConfig.Controls.Add(lblCfgSub);

            int modeY = 100;
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
                var panel = new DBPanel();
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
                    if (i != index) { Fx.Settle(cfgTabPanels[i]); cfgTabPanels[i].Visible = false; }
                cfgTabPanels[index].Visible = true;
                Fx.SlideIn(cfgTabPanels[index]);
            };

            int ty = 2;
            AddCfgSection(cfgTabPanels[0], Lang.T("cfg.sub.range"), ref ty,
                new[] { PolicyCatalog.KeySuppress, PolicyCatalog.KeyAggressive,
                    PolicyCatalog.KeySqueezeBg, PolicyCatalog.KeyGpuDemote });
            AddCfgSection(cfgTabPanels[0], Lang.T("cfg.sub.boost"), ref ty,
                new[] { PolicyCatalog.KeyBoost, PolicyCatalog.KeyIfeoBoost,
                    PolicyCatalog.KeyRenderLane });
            EnableCardCollapse(cfgTabPanels[0]);

            BuildCfgCoreTab(cfgTabPanels[1]);

            ty = 2;
            AddCfgSection(cfgTabPanels[2], Lang.T("cfg.group.mempower"), ref ty,
                new[] { PolicyCatalog.KeyPowerPlan });
            AddCfgSection(cfgTabPanels[2], Lang.T("cfg.sub.net"), ref ty,
                new[] { PolicyCatalog.KeyPauseDl, PolicyCatalog.KeyPauseUpdate,
                    PolicyCatalog.KeyWlanGuard });
            AddCfgSection(cfgTabPanels[2], Lang.T("cfg.sub.presence"), ref ty,
                new[] { PolicyCatalog.KeyAwake });
            EnableCardCollapse(cfgTabPanels[2]);

            ty = 2;
            AddCfgSection(cfgTabPanels[3], "NVIDIA", ref ty,
                new[] { PolicyCatalog.KeyNvMaxPerf, PolicyCatalog.KeyNvLowLat,
                    PolicyCatalog.KeyNvSmoothMotion, PolicyCatalog.KeyNvShaderCache,
                    PolicyCatalog.KeyNvDlss, PolicyCatalog.KeyNvRebar,
                    PolicyCatalog.KeyNvAnselOff });
            EnableCardCollapse(cfgTabPanels[3]);

            cfgTabKeys = new[]
            {
                new[] { PolicyCatalog.KeySuppress, PolicyCatalog.KeyAggressive,
                    PolicyCatalog.KeySqueezeBg, PolicyCatalog.KeyGpuDemote,
                    PolicyCatalog.KeyBoost,
                    PolicyCatalog.KeyIfeoBoost, PolicyCatalog.KeyRenderLane },
                new[] { PolicyCatalog.KeyStrictCores, PolicyCatalog.KeyCoreDomainAlt,
                    PolicyCatalog.KeyCoreMask },
                new[] { PolicyCatalog.KeyPowerPlan,
                    PolicyCatalog.KeyPauseDl, PolicyCatalog.KeyPauseUpdate,
                    PolicyCatalog.KeyWlanGuard, PolicyCatalog.KeyAwake },
                new[] { PolicyCatalog.KeyNvMaxPerf,
                    PolicyCatalog.KeyNvLowLat, PolicyCatalog.KeyNvSmoothMotion,
                    PolicyCatalog.KeyNvShaderCache,
                    PolicyCatalog.KeyNvDlss, PolicyCatalog.KeyNvRebar,
                    PolicyCatalog.KeyNvAnselOff },
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
                        Lang.T("preset.custom") };
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
                case PolicyCatalog.KeySqueezeBg: return "gm.squeezebg.sub";
                case PolicyCatalog.KeyGpuDemote: return "gm.gpudemote.sub";
                case PolicyCatalog.KeyIfeoBoost: return "gm.ifeo.sub";
                case PolicyCatalog.KeyRenderLane: return "gm.lane.sub";
                case PolicyCatalog.KeyPowerPlan: return "cfg.plan.sub";
                case PolicyCatalog.KeyPauseDl: return "gm.pausedl.sub";
                case PolicyCatalog.KeyPauseUpdate: return "gm.pausewu.sub";
                case PolicyCatalog.KeyWlanGuard: return "gm.wlanguard.sub";
                case PolicyCatalog.KeyAwake: return "set.awake.n";
                case PolicyCatalog.KeyNvMaxPerf: return "set.nvmax.n";
                case PolicyCatalog.KeyNvLowLat: return "set.nvll.n";
                case PolicyCatalog.KeyNvSmoothMotion: return "set.nvsmooth.n";
                case PolicyCatalog.KeyNvShaderCache: return "set.nvshader.n";
                case PolicyCatalog.KeyNvAnselOff: return "set.nvansel.n";
                case PolicyCatalog.KeyNvRebar: return "set.nvrebar.n";
                case PolicyCatalog.KeyNvDlss: return "set.nvdlss.n";
                default: return null;
            }
        }

        private static bool CfgItemSupported(PolicyItem item, out string reasonKey)
        {
            bool nvOk = NvApi.Available;
            reasonKey = null;
            switch (item.Key)
            {
                case PolicyCatalog.KeySqueezeBg:
                    if (!CpuTopology.SqueezeSupported) reasonKey = "gm.squeezebg.smallcpu";
                    return CpuTopology.SqueezeSupported;
                case PolicyCatalog.KeyNvMaxPerf:
                case PolicyCatalog.KeyNvLowLat:
                case PolicyCatalog.KeyNvShaderCache:
                case PolicyCatalog.KeyNvAnselOff:
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
            string descKey = CfgDescKey(item);
            string desc = !supported ? Lang.T(reasonKey)
                : (withDesc && descKey != null ? Lang.T(descKey) : "");

            var picker = new TierPicker();
            picker.Labels = labels;
            picker.Index = supported ? CfgRowIndexOf(item, values) : 1;
            int segW = labels.Length >= 6 ? 68 : 78;
            picker.SetBounds(0, 0, Theme.S(labels.Length * segW + 12), Theme.S(30));
            bool showDisabled = item.Key == PolicyCatalog.KeySqueezeBg && !supported;
            picker.Enabled = supported;
            picker.Visible = supported || showDisabled;

            int cardH;
            SettingCard card = MakeAutoCard(parent, x, y, w, 54,
                Lang.T(item.LangKey), desc, picker, out cardH);
            y += cardH + 8;
            cfgCardByKey[item.Key] = card;

            string key = item.Key;
            Action sync = delegate
            {
                bool has = cfgProfile.Overrides.ContainsKey(key);
                string globalLabel = CfgValueLabel(item, PolicyResolver.GlobalValue(key));
                picker.Index = supported ? CfgRowIndexOf(item, values) : 1;
                bool forcedEffective;
                bool forced = CfgPresetForces(key, cfgEffMode, out forcedEffective);
                bool usable = supported && !forced;
                picker.Enabled = usable;
                picker.Visible = usable || showDisabled;
                card.SetLock(!supported ? Lang.T("v14.preset.forced.off")
                    : forced ? Lang.T(forcedEffective ? "v14.preset.forced.on" : "v14.preset.forced.off")
                    : "", supported && forcedEffective);
                card.SetValue(has ? Lang.F("cfg.state.over", globalLabel)
                    : Lang.F("cfg.state.follow", globalLabel), has ? Theme.Accent : Theme.Faint);
            };
            cfgRowSync.Add(sync);
            picker.IndexChanged = delegate(int index)
            {
                if (cfgProfile == null) return;
                if (index <= 0) gameMode.ClearProfileOverride(cfgProfileId, key);
                else gameMode.SetProfileOverride(cfgProfileId, key, values[index - 1]);
                SyncCfgRows();
            };
        }

        private void ClearAllCfgOverrides()
        {
            if (cfgProfile == null) return;
            int n = cfgProfile.Overrides.Count;
            if (n == 0) return;
            if (!PaviseDialog.Confirm(this, Lang.T("cfg.clear"),
                    Lang.F("cfg.clear.confirm", cfgProfile.Name, n), DlgKind.Warn))
                return;
            gameMode.ClearProfileOverrides(cfgProfileId);
            cfgCoreManualPicked = false;
            SyncCfgRows();
        }

        private sealed class CfgCopyTarget
        {
            public readonly string Id;
            private readonly string name;
            public CfgCopyTarget(string id, string profileName) { Id = id; name = profileName; }
            public override string ToString() { return name; }
        }

        private void ShowCopyOverridesDialog()
        {
            if (cfgProfile == null) return;
            var targets = new List<CfgCopyTarget>();
            foreach (GameProfile p in gameMode.GetProfiles())
                if (!string.Equals(p.Id, cfgProfileId, StringComparison.OrdinalIgnoreCase))
                    targets.Add(new CfgCopyTarget(p.Id, p.Name));
            if (targets.Count == 0)
            {
                PaviseDialog.Info(this, Lang.T("cfg.copy"), Lang.T("cfg.copy.none"));
                return;
            }

            var list = new CheckedListBox();
            list.BackColor = Theme.Card;
            list.ForeColor = Theme.Fg;
            list.BorderStyle = BorderStyle.FixedSingle;
            list.Font = Theme.UI(9.2f, false);
            list.CheckOnClick = true;
            list.IntegralHeight = false;
            list.Size = new Size(Theme.S(416), Theme.S(Math.Min(220, 30 + targets.Count * 26)));
            foreach (CfgCopyTarget t in targets) list.Items.Add(t);

            if (!PaviseDialog.Confirm(this, Lang.T("cfg.copy"),
                    Lang.F("cfg.copy.sub", cfgProfile.Name), DlgKind.Info, list, 468))
                return;
            int applied = 0;
            foreach (object checkedItem in list.CheckedItems)
            {
                var target = checkedItem as CfgCopyTarget;
                if (target == null) continue;
                gameMode.CopyProfileOverrides(cfgProfileId, target.Id);
                applied++;
            }
            if (applied > 0)
            {
                Logger.Log(Lang.F("cfg.copy.done", applied));
                cfgCoreManualPicked = false;
                SyncCfgRows();
            }
        }
    }
}

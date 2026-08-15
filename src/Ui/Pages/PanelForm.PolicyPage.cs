// @author bdth 2074055628@qq.com
// 文件用途 构建优化策略页 并按当前预设锁定或放开自定义项

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Label lblPolicyMode;
        private TechTabs policyTabs;
        private DBPanel[] policyTabPanels;
        private TierPicker pickPolicyCores;
        private Toggle swPolicyBackground, swPolicyAggressive;
        private Toggle swPolicyPauseDl, swPolicyDvr;
        private Toggle swPolicyGpuDemote, swPolicyBoost, swPolicyIfeo, swPolicyLane;
        private Toggle swPolicyPauseWu, swPolicyWlan, swPolicyAwake;
        private SettingCard cardPolicyCores, cardPolicyAggressive;
        private SettingCard cardPolicyPauseDl, cardPolicyDvr;
        private SettingCard cardPolicyBackground, cardPolicyGpuDemote, cardPolicyBoost, cardPolicyIfeo, cardPolicyLane;
        private SettingCard cardPolicyPauseWu, cardPolicyWlan, cardPolicyAwake;
        private readonly List<Action> policySync = new List<Action>();

        private void BuildPolicyPage()
        {
            policySync.Clear();
            int y = PageHeader(pagePolicy, Lang.T("nav.policy"), Lang.T("v15.policy.sub"), 2);
            var banner = new RoundPanel();
            banner.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(62));
            banner.BackColor = Theme.Bg; banner.Fill = Theme.Card; banner.Border = Theme.Stroke; banner.Radius = Theme.S(12);
            banner.AccentEdge = true;
            lblPolicyMode = CardLabel(banner, "", 18, 10, 300, 22, 9.5f, true, Theme.Accent);
            CardLabel(banner, Lang.T("v15.policy.mode.hint"), 18, 33, ContentW - 36, 18, 7.8f, false, Theme.Dim);
            pagePolicy.Controls.Add(banner); y += 74;

            policyTabs = new TechTabs();
            policyTabs.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(38));
            policyTabs.SetTabs(
                new[] { Lang.T("policy.tab.core"), Lang.T("policy.tab.cores"),
                    Lang.T("policy.tab.custom"), Lang.T("policy.tab.extras") },
                new[] { Lang.T("v15.policy.core"), Lang.T("v15.policy.cores"),
                    Lang.T("v15.policy.custom"), Lang.T("v15.policy.extras") });
            pagePolicy.Controls.Add(policyTabs);
            y += 48;

            policyTabPanels = MakeTabPanels(pagePolicy, policyTabs, 4, y);

            Control scroll = policyTabPanels[0];
            int sy = 2;
            swPolicyBackground = AddPolicyToggle(scroll, ref sy, Lang.T("v14.bg.master"), Lang.T("v14.bg.master.sub"),
                delegate { return gameMode.SuppressBackground; }, delegate(bool v) { gameMode.SuppressBackground = v; });
            cardPolicyBackground = (SettingCard)swPolicyBackground.Parent;
            swPolicyGpuDemote = AddPolicyToggle(scroll, ref sy, Lang.T("gm.gpudemote"), Lang.T("gm.gpudemote.sub"),
                delegate { return gameMode.GpuDemote; }, delegate(bool v) { gameMode.GpuDemote = v; });
            cardPolicyGpuDemote = (SettingCard)swPolicyGpuDemote.Parent;
            swPolicyBoost = AddPolicyToggle(scroll, ref sy, Lang.T("gm.boost"), Lang.T("v15.boost.sub"),
                delegate { return gameMode.BoostGame; }, delegate(bool v) { gameMode.BoostGame = v; });
            cardPolicyBoost = (SettingCard)swPolicyBoost.Parent;
            swPolicyIfeo = AddPolicyToggle(scroll, ref sy, Lang.T("gm.ifeo"), Lang.T("gm.ifeo.sub"),
                delegate { return gameMode.IfeoBoostFallback; }, delegate(bool v) { gameMode.IfeoBoostFallback = v; });
            cardPolicyIfeo = (SettingCard)swPolicyIfeo.Parent;
            swPolicyLane = AddPolicyToggle(scroll, ref sy, Lang.T("gm.lane"), Lang.T("gm.lane.sub"),
                delegate { return gameMode.RenderLaneOn; }, delegate(bool v) { gameMode.RenderLaneOn = v; });
            cardPolicyLane = (SettingCard)swPolicyLane.Parent;
            // 对局电源计划选择器已移到主窗口标题栏(PowerFlyout)优化策略页不再重复放

            BuildCorePage(policyTabPanels[1]);

            scroll = policyTabPanels[2]; sy = 2;
            swPolicyAggressive = AddPolicyToggle(scroll, ref sy, Lang.T("gm.aggressive"), Lang.T("gm.aggressive.sub"),
                delegate { return gameMode.AggressiveSuppression; }, delegate(bool v) { gameMode.AggressiveSuppression = v; });
            cardPolicyAggressive = (SettingCard)swPolicyAggressive.Parent;
            swPolicyAggressive.CheckedChanged += delegate { RefreshPolicyPresentation(); };
            swPolicyPauseDl = AddPolicyToggle(scroll, ref sy, Lang.T("gm.pausedl"), Lang.T("gm.pausedl.sub"), delegate { return gameMode.PauseDownloads; }, delegate(bool v) { gameMode.PauseDownloads = v; });
            cardPolicyPauseDl = (SettingCard)swPolicyPauseDl.Parent;
            swPolicyDvr = AddPolicyToggle(scroll, ref sy, Lang.T("set.dvr"), Lang.T("set.dvr.sub"), delegate { return gameMode.KillGameDvr; }, delegate(bool v) { gameMode.KillGameDvr = v; });
            cardPolicyDvr = (SettingCard)swPolicyDvr.Parent;
            scroll = policyTabPanels[3]; sy = 2;
            AddPolicyToggle(scroll, ref sy, Lang.T("gm.standby"), Lang.T("gm.standby.sub"),
                delegate { return gameMode.PurgeStandby; }, delegate(bool v) { gameMode.PurgeStandby = v; });
            swPolicyPauseWu = AddPolicyToggle(scroll, ref sy, Lang.T("gm.pausewu"), Lang.T("gm.pausewu.sub"),
                delegate { return gameMode.PauseWindowsUpdate; }, delegate(bool v) { gameMode.PauseWindowsUpdate = v; });
            cardPolicyPauseWu = (SettingCard)swPolicyPauseWu.Parent;
            swPolicyWlan = AddPolicyToggle(scroll, ref sy, Lang.T("gm.wlanguard"), Lang.T("gm.wlanguard.sub"),
                delegate { return gameMode.WlanScanGuard; }, delegate(bool v) { gameMode.WlanScanGuard = v; });
            cardPolicyWlan = (SettingCard)swPolicyWlan.Parent;
            swPolicyAwake = AddPolicyToggle(scroll, ref sy, Lang.T("set.awake"), Lang.T("set.awake.n"),
                delegate { return gameMode.KeepAwake; }, delegate(bool v) { gameMode.KeepAwake = v; });
            cardPolicyAwake = (SettingCard)swPolicyAwake.Parent;

            EnableCardCollapse(policyTabPanels[0], cardPolicyBackground, cardPolicyBoost);
            EnableCardCollapse(policyTabPanels[2]);
            EnableCardCollapse(policyTabPanels[3]);

            RefreshPolicyPresentation();
        }

        private const int CollapsedCardH = 52;

        private readonly Dictionary<Control, Dictionary<Control, int>> stackBase
            = new Dictionary<Control, Dictionary<Control, int>>();

        private void EnableCardCollapse(Control panel, params SettingCard[] keepOpen)
        {
            var dead = new List<Control>();
            foreach (Control key in stackBase.Keys) if (key.IsDisposed) dead.Add(key);
            foreach (Control key in dead) stackBase.Remove(key);

            var map = new Dictionary<Control, int>();
            foreach (Control c in panel.Controls) map[c] = c.Top;
            stackBase[panel] = map;

            var open = new List<SettingCard>(keepOpen ?? new SettingCard[0]);
            foreach (Control c in panel.Controls)
            {
                var card = c as SettingCard;
                if (card == null || card.Desc.Length == 0) continue;
                card.ExpandedHeight = card.Height;
                card.CollapsedHeight = Theme.S(CollapsedCardH)
                    + (card.HasStatus ? Theme.S(SettingCard.StatusLineH) : 0);
                if (card.CollapsedHeight >= card.ExpandedHeight) continue;

                card.Collapsible = true;
                card.SnapExpanded(open.Contains(card));
                Control owner = panel;
                card.ExpandedChanged = delegate { RestackCards(owner); };
                card.Height = card.Expanded ? card.ExpandedHeight : card.CollapsedHeight;
            }
            RestackCards(panel);
        }

        private void RestackCards(Control panel)
        {
            Dictionary<Control, int> map;
            if (!stackBase.TryGetValue(panel, out map)) return;

            var shifts = new List<int[]>();
            foreach (Control c in panel.Controls)
            {
                var card = c as SettingCard;
                if (card == null || !card.Collapsible) continue;
                int delta = card.Height - card.ExpandedHeight;
                int baseTop;
                if (delta != 0 && map.TryGetValue(card, out baseTop))
                    shifts.Add(new[] { baseTop, delta });
            }

            var scrollable = panel as ScrollableControl;
            int origin = scrollable == null ? 0 : scrollable.AutoScrollPosition.Y;
            panel.SuspendLayout();
            foreach (Control c in panel.Controls)
            {
                int baseTop;
                if (!map.TryGetValue(c, out baseTop)) continue;
                int shift = 0;
                foreach (int[] s in shifts) if (s[0] < baseTop) shift += s[1];
                int want = baseTop + shift + origin;
                if (c.Top != want) c.Top = want;
            }
            panel.ResumeLayout();
        }

        private void EnsureTabFor(Control card)
        {
            RevealTabFor(policyTabs, policyTabPanels, card);
            RevealTabFor(envTabs, envTabPanels, card);
            RevealTabFor(gfxTabs, gfxTabPanels, card);
            RevealTabFor(colTabs, colTabPanels, card);
        }

        private static void RevealTabFor(TechTabs tabs, DBPanel[] panels, Control card)
        {
            if (panels == null || tabs == null || card == null) return;
            for (Control c = card; c != null; c = c.Parent)
                for (int i = 0; i < panels.Length; i++)
                    if (c == panels[i]) { tabs.Index = i; return; }
        }

        private int coreManualIndex;
        private bool coreThreeWay;
        private bool coreManualPicked;

        private TierPicker AddCorePlacementPicker(Control parent, ref int y)
        {
            bool partition = CpuTopology.HasSafeBackgroundPartition();
            coreThreeWay = partition && CpuTopology.HasAltPartition();

            var labels = new List<string> { Lang.T("cpu.place.all") };
            if (partition)
            {
                labels.Add(coreThreeWay ? PrimaryDomainLabel() : CorePartitionLabel());
                if (coreThreeWay) labels.Add(AltDomainLabel());
            }
            labels.Add(Lang.T("cpu.place.manual"));
            coreManualIndex = labels.Count - 1;

            var picker = new TierPicker();
            picker.Labels = labels.ToArray();
            picker.SetBounds(0, 0, Theme.S(labels.Count * 88 + 12), Theme.S(34));
            picker.Index = CorePlacementIndex();
            picker.IndexChanged = delegate(int index) { ApplyCorePlacement(index); };

            int cardH;
            cardPolicyCores = MakeAutoCard(parent, 6, y, ScrollContentW, 88,
                Lang.T("cpu.place.title"), CorePartitionDescription(), picker,
                Theme.S(labels.Count * 88 + 24), out cardH);
            y += cardH + 8;
            policySync.Add(delegate
            {
                picker.Index = CorePlacementIndex();
                if (cardPolicyCores != null)
                    cardPolicyCores.SetValue(gameMode.CoreDomainSwitchPending
                        ? Lang.T("cpu.place.pending") : "", Theme.Accent);
            });
            return picker;
        }

        private void ApplyCorePlacement(int index)
        {
            if (index == coreManualIndex)
            {
                coreManualPicked = true;
                if (corePending == 0) corePending = CpuTopology.AllMask;
                if (coreMatrix != null) coreMatrix.Selected = corePending;
                SyncCorePage();
                return;
            }
            coreManualPicked = false;
            gameMode.CustomCoreMask = 0;
            corePending = CpuTopology.AllMask;
            if (coreMatrix != null) coreMatrix.Selected = corePending;
            gameMode.CorePartitionEnabled = index > 0;
            if (index > 0 && coreThreeWay) gameMode.CoreDomainAlt = index == 2;
            SyncCorePage();
        }

        private int CorePlacementIndex()
        {
            if (gameMode.CustomCoreMask != 0 || coreManualPicked) return coreManualIndex;
            if (!gameMode.CorePartitionEnabled || !CpuTopology.HasSafeBackgroundPartition()) return 0;
            return CpuTopology.HasAltPartition() && gameMode.CoreDomainAlt ? 2 : 1;
        }

        private static string PrimaryDomainLabel()
        {
            if (CpuTopology.AsymCache) return Lang.T("cpu.place.cache");
            int die = CpuTopology.AltDomainActive ? CpuTopology.AltDomainIndex : CpuTopology.GameDomainIndex;
            return die >= 0 ? "CCD " + die : Lang.T("cpu.place.partition");
        }

        private static string AltDomainLabel()
        {
            if (CpuTopology.AsymCache) return Lang.T("cpu.place.freq");
            int die = CpuTopology.AltDomainActive ? CpuTopology.GameDomainIndex : CpuTopology.AltDomainIndex;
            return die >= 0 ? "CCD " + die : Lang.T("cpu.place.partition");
        }

        private static string CorePartitionLabel()
        {
            if (CpuTopology.AsymCache || CpuTopology.PartitionTag == "symmetric-ccd")
                return Lang.T("cpu.place.ccd");
            if (CpuTopology.Hybrid) return Lang.T("cpu.place.performance");
            return Lang.T("cpu.place.partition");
        }

        private static string CorePartitionDescription()
        {
            if (CpuTopology.AsymCache)
                return Lang.T(CpuTopology.HasAltPartition() ? "cpu.place.x3d3.desc" : "cpu.place.x3d.desc");
            if (CpuTopology.PartitionTag == "symmetric-ccd")
                return Lang.T(CpuTopology.HasAltPartition() ? "cpu.place.ccd3.desc" : "cpu.place.ccd.desc");
            if (CpuTopology.Hybrid) return Lang.T("cpu.place.hybrid.desc");
            return CpuTopology.HasSafeBackgroundPartition()
                ? Lang.T("cpu.place.generic.desc") : Lang.T("cpu.place.unavailable.desc");
        }

        private Toggle AddPolicyToggle(Control parent, ref int y, string title, string desc, Func<bool> read, Action<bool> write)
        {
            return AddPolicyToggle(parent, ref y, title, desc, read, write, 0);
        }

        private Toggle AddPolicyToggle(Control parent, ref int y, string title, string desc, Func<bool> read, Action<bool> write,
            int valueReserve)
        {
            Toggle sw = MakeSwitch(read(), null);
            sw.CheckedChanged += delegate { write(sw.Checked); };

            int cardH;
            SettingCard card = MakeAutoCard(parent, 6, y, ScrollContentW, 78, title, desc, sw, valueReserve, out cardH);
            y += cardH + 8;
            policySync.Add(delegate { sw.SetSilently(read()); });
            return sw;
        }

        private void RefreshPolicyPresentation()
        {
            if (lblPolicyMode != null) lblPolicyMode.Text = Lang.F("mode.policy.active", ModeButton.ModeName(gameMode.ActivePreset));
            PerformancePreset mode = gameMode.ActivePreset;
            bool competitive = mode == PerformancePreset.Competitive;
            bool custom = mode == PerformancePreset.Custom;
            ApplyPresetPolicy(swPolicyBackground, cardPolicyBackground, Lang.T("v14.bg.master"), false, true);
            ApplyPresetPolicy(swPolicyGpuDemote, cardPolicyGpuDemote, Lang.T("gm.gpudemote"), false, true);
            ApplyPresetPolicy(swPolicyBoost, cardPolicyBoost, Lang.T("gm.boost"), false, true);
            ApplyPresetPolicy(swPolicyIfeo, cardPolicyIfeo, Lang.T("gm.ifeo"), false, true);
            ApplyPresetPolicy(swPolicyLane, cardPolicyLane, Lang.T("gm.lane"), false, true);
            if (cardPolicyCores != null) cardPolicyCores.Title = Lang.T("cpu.place.title");
            ApplyPresetPolicy(swPolicyAggressive, cardPolicyAggressive, Lang.T("gm.aggressive"), !custom, competitive);
            ApplyPresetPolicy(swPolicyPauseDl, cardPolicyPauseDl, Lang.T("gm.pausedl"), !custom, competitive);
            ApplyPresetPolicy(swPolicyDvr, cardPolicyDvr, Lang.T("set.dvr"), !custom, competitive);
            ApplyPresetPolicy(swPolicyPauseWu, cardPolicyPauseWu, Lang.T("gm.pausewu"), false, true);
            ApplyPresetPolicy(swPolicyWlan, cardPolicyWlan, Lang.T("gm.wlanguard"), false, true);
            ApplyPresetPolicy(swPolicyAwake, cardPolicyAwake, Lang.T("set.awake"), false, true);
        }

        private static void ApplyPresetPolicy(Toggle toggle, SettingCard card, string title, bool forced, bool effective)
        {
            if (toggle != null)
            {
                toggle.Enabled = !forced;
                if (forced) toggle.SetSilently(effective);
            }
            if (card == null) return;
            card.Title = title;
            card.SetLock(forced
                ? Lang.T(effective ? "v14.preset.forced.on" : "v14.preset.forced.off")
                : "", effective);
        }

    }
}

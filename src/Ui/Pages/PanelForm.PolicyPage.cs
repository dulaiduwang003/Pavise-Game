// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
#if PAVISE_SELFTEST
        private ModuleBanner policyBanner;
        private TechTabs policyTabs;
        private DBPanel[] policyTabPanels;
        private Toggle swPolicyBackground, swPolicyAggressive;
        private Toggle swPolicyPauseDl, swPolicyDvr;
        private Toggle swPolicyGpuDemote, swPolicyBoost, swPolicyLane;
        private Toggle swPolicyAdaptive;
        private SettingCard cardPolicyAdaptive;
        private SettingCard cardPolicyEnglishInput;
        private Toggle swPolicyEnglishInput;
        private Toggle swPolicyDwmBoost, swPolicyWsTrim, swPolicyCacheWarm;
        private SettingCard cardPolicyCacheWarm;
        private SettingCard cardPolicyDwmBoost, cardPolicyWsTrim;
        private Toggle swPolicyPauseWu, swPolicyPauseServices, swPolicyAwake;
        private Toggle swPolicyPauseMaint;
        private SettingCard cardPolicyPauseMaint;
        private SettingCard cardPolicyAggressive;
        private SettingCard cardPolicyPauseDl, cardPolicyDvr;
        private SettingCard cardPolicyBackground, cardPolicyGpuDemote, cardPolicyBoost, cardPolicyLane;
        private SettingCard cardPolicyPauseWu, cardPolicyPauseServices, cardPolicyAwake;
#endif
        private readonly List<Action> policySync = new List<Action>();

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
            int gap = Theme.S(8);
            foreach (Control c in panel.Controls)
            {
                var card = c as SettingCard;
                int baseTop;
                if (card == null || !map.TryGetValue(card, out baseTop)) continue;
#if PAVISE_SELFTEST
                if (card.Suppressed)
                {
                    shifts.Add(new[] { baseTop, -((card.Collapsible ? card.ExpandedHeight : card.Height) + gap) });
                    continue;
                }
#endif
                if (!card.Collapsible) continue;
                int delta = card.Height - card.ExpandedHeight;
                if (delta != 0) shifts.Add(new[] { baseTop, delta });
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

#if PAVISE_SELFTEST
        private void EnsureTabFor(Control card)
        {
            RevealTabFor(policyTabs, policyTabPanels, card);
            RevealTabFor(gfxTabs, gfxTabPanels, card);
            RevealTabFor(envTabs, envTabPanels, card);
            RevealTabFor(settingsTabs, settingsTabPanels, card);
        }

        private static void RevealTabFor(TechTabs tabs, DBPanel[] panels, Control card)
        {
            if (panels == null || tabs == null || card == null) return;
            for (Control c = card; c != null; c = c.Parent)
                for (int i = 0; i < panels.Length; i++)
                    if (c == panels[i]) { tabs.Index = i; return; }
        }

        private SettingCard BuildCacheWarmPolicyCard(Control parent, ref int y)
        {
            Toggle warm = AddPolicyToggle(parent, ref y, Lang.T("gm.cachewarm"), Lang.T("gm.cachewarm.sub"),
                delegate { return gameMode.CacheWarmOn; }, delegate(bool v) { gameMode.CacheWarmOn = v; }, 0, true);
            swPolicyCacheWarm = warm;
            SettingCard card = (SettingCard)warm.Parent;
            card.SetStatus(gameMode.CacheWarmStatus, Theme.Dim);
            card.Height += Theme.S(SettingCard.StatusLineH); y += SettingCard.StatusLineH;
            policySync.Add(delegate { card.SetStatus(gameMode.CacheWarmStatus, Theme.Dim); });
            return card;
        }

        private Toggle AddPolicyToggle(Control parent, ref int y, string title, string desc, Func<bool> read, Action<bool> write)
        {
            return AddPolicyToggle(parent, ref y, title, desc, read, write, 0);
        }

        private Toggle AddPolicyToggle(Control parent, ref int y, string title, string desc, Func<bool> read, Action<bool> write,
            int valueReserve, bool fullText = false)
        {
            Toggle sw = MakeSwitch(read(), null);
            sw.CheckedChanged += delegate { write(sw.Checked); };

            int cardH;
            SettingCard card = MakeAutoCard(parent, 6, y, ScrollContentW,
                fullText ? FullTextCardHeight(desc, ScrollContentW, sw, 78) : 78,
                title, desc, sw, valueReserve, out cardH);
            y += cardH + 8;
            policySync.Add(delegate { sw.SetSilently(read()); });
            return sw;
        }

#endif
        private void RefreshPolicyPresentation()
        {
#if PAVISE_SELFTEST
            if (policyBanner != null)
            {
                policyBanner.State = Lang.F("mode.policy.active", ModeButton.ModeName(gameMode.ActivePreset));
                policyBanner.StateColor = Theme.Accent;
            }
            PerformancePreset mode = gameMode.ActivePreset;
            bool competitive = PresetValue.IsCompetitive(mode);
            bool presetForcesOn = competitive || mode == PerformancePreset.Handheld;
            ApplyPresetPolicy(swPolicyBackground, cardPolicyBackground, Lang.T("v14.bg.master"), false, true);
            ApplyPresetPolicy(swPolicyGpuDemote, cardPolicyGpuDemote, Lang.T("gm.gpudemote"), false, true);
            bool smart = mode == PerformancePreset.Standard;
            if (cardPolicyAdaptive != null)
            {
                cardPolicyAdaptive.Visible = smart;
                cardPolicyAdaptive.Suppressed = !smart;
            }
            if (smart) ApplyPresetPolicy(swPolicyAdaptive, cardPolicyAdaptive, Lang.T("gm.adaptive"), false, true);
            ReflowPolicyCoreCards();
            ApplyPresetPolicy(swPolicyBoost, cardPolicyBoost, Lang.T("gm.boost"), false, true);
            SyncPolicyLane();
            ApplyPresetPolicy(swPolicyAggressive, cardPolicyAggressive, Lang.T("gm.aggressive"), true, presetForcesOn);
            ApplyPresetPolicy(swPolicyPauseDl, cardPolicyPauseDl, Lang.T("gm.pausedl"), true, presetForcesOn);
            ApplyPresetPolicy(swPolicyDvr, cardPolicyDvr, Lang.T("set.dvr"), false, true);
            bool handheld = mode == PerformancePreset.Handheld;
            ApplyPresetPolicy(swPolicyCacheWarm, cardPolicyCacheWarm, Lang.T("gm.cachewarm"), handheld, false);
            ApplyPresetPolicy(swPolicyPauseWu, cardPolicyPauseWu, Lang.T("gm.pausewu"), false, true);
            ApplyPresetPolicy(swPolicyPauseMaint, cardPolicyPauseMaint, Lang.T("gm.pausemaint"), false, true);
            ApplyPresetPolicy(swPolicyPauseServices, cardPolicyPauseServices, Lang.T("gm.pausesvc"), false, true);
            ApplyPresetPolicy(swPolicyAwake, cardPolicyAwake, Lang.T("set.awake"), false, true);
            ApplyPresetPolicy(swPolicyEnglishInput, cardPolicyEnglishInput, Lang.T("gm.englishinput"), false, true);
            ApplyPresetPolicy(swPolicyWsTrim, cardPolicyWsTrim, Lang.T("gm.wstrim"), false, true);
            ApplyPresetPolicy(swPolicyDwmBoost, cardPolicyDwmBoost, Lang.T("gm.dwmboost"), false, true);
#endif
            RefreshStandbyCleanerPresentation();
        }

#if PAVISE_SELFTEST
        private void SyncPolicyLane()
        {
            PerformancePreset mode = gameMode.ActivePreset;
            bool supported = GameMode.LaneSupported(mode);
            ApplyPresetPolicy(swPolicyLane, cardPolicyLane, Lang.T("gm.lane"), false, true);
            if (swPolicyLane != null)
            {
                swPolicyLane.Enabled = supported;
                swPolicyLane.SetSilently(supported && gameMode.RenderLaneOn);
            }
            if (cardPolicyLane != null)
            {
                cardPolicyLane.Desc = Lang.T(supported ? "gm.lane.sub" : "gm.lane.unsupported");
                if (!supported) cardPolicyLane.SetLock(Lang.T("lock.na"), false);
            }
        }

        private void ReflowPolicyCoreCards()
        {
            if (policyTabPanels == null || policyTabPanels.Length == 0 || policyTabPanels[0] == null) return;
            RestackCards(policyTabPanels[0]);
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
#endif

    }
}

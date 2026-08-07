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
        private Toggle swPolicyBackground, swPolicyStrict, swPolicyAggressive, swPolicyFreeze;
        private Toggle swPolicyPauseDl, swPolicyPauseSvc, swPolicyDvr;
        private SettingCard cardPolicyStrict, cardPolicyAggressive, cardPolicyFreeze;
        private SettingCard cardPolicyPauseDl, cardPolicyPauseSvc, cardPolicyDvr;
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

            var scroll = new DBPanel();
            scroll.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            scroll.BackColor = Theme.Bg; scroll.AutoScroll = true; Native.Dark(scroll); pagePolicy.Controls.Add(scroll);
            int sy = 2;
            Section(scroll, Lang.T("v15.policy.core"), 6, sy); sy += 24;
            swPolicyBackground = AddPolicyToggle(scroll, ref sy, Lang.T("v14.bg.master"), Lang.T("v14.bg.master.sub"),
                delegate { return gameMode.SuppressBackground; }, delegate(bool v) { gameMode.SuppressBackground = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("gm.gpudemote"), Lang.T("gm.gpudemote.sub"),
                delegate { return gameMode.GpuDemote; }, delegate(bool v) { gameMode.GpuDemote = v; });
            swPolicyFreeze = AddPolicyConfirmToggle(scroll, ref sy, Lang.T("gm.freeze"), Lang.T("gm.freeze.sub"), Lang.T("gm.freeze.warn"),
                delegate { return gameMode.FreezeBackground; }, delegate(bool v) { gameMode.FreezeBackground = v; });
            cardPolicyFreeze = (SettingCard)swPolicyFreeze.Parent;
            AddPolicyToggle(scroll, ref sy, Lang.T("gm.boost"), Lang.T("v15.boost.sub"),
                delegate { return gameMode.BoostGame; }, delegate(bool v) { gameMode.BoostGame = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("gm.ifeo"), Lang.T("gm.ifeo.sub"),
                delegate { return gameMode.IfeoBoostFallback; }, delegate(bool v) { gameMode.IfeoBoostFallback = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("gm.lane"), Lang.T("gm.lane.sub"),
                delegate { return gameMode.RenderLaneOn; }, delegate(bool v) { gameMode.RenderLaneOn = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("set.plan"), Lang.T("v15.plan.sub"),
                delegate { return gameMode.PowerPlanSwitch; }, delegate(bool v) { gameMode.PowerPlanSwitch = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("set.notif"), Lang.T("v15.notif.sub"),
                delegate { return gameMode.NotifQuiet; }, delegate(bool v) { gameMode.NotifQuiet = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("set.hz"), Lang.T("v15.hz.sub"),
                delegate { return gameMode.HzGuard; }, delegate(bool v) { gameMode.HzGuard = v; });

            sy += 10; Section(scroll, Lang.T("v15.policy.custom"), 6, sy); sy += 24;
            swPolicyStrict = AddPolicyToggle(scroll, ref sy, Lang.T("v14.cpu.adaptive"), Lang.T("v14.cpu.adaptive.sub2"),
                delegate { return gameMode.StrictCoreIsolation; }, delegate(bool v) { gameMode.StrictCoreIsolation = v; });
            cardPolicyStrict = (SettingCard)swPolicyStrict.Parent;
            swPolicyStrict.CheckedChanged += delegate { RefreshPolicyPresentation(); };
            swPolicyAggressive = AddPolicyToggle(scroll, ref sy, Lang.T("gm.aggressive"), Lang.T("gm.aggressive.sub"),
                delegate { return gameMode.AggressiveSuppression; }, delegate(bool v) { gameMode.AggressiveSuppression = v; });
            cardPolicyAggressive = (SettingCard)swPolicyAggressive.Parent;
            swPolicyAggressive.CheckedChanged += delegate { RefreshPolicyPresentation(); };
            swPolicyPauseDl = AddPolicyToggle(scroll, ref sy, Lang.T("gm.pausedl"), Lang.T("v15.custom.override"), delegate { return gameMode.PauseDownloads; }, delegate(bool v) { gameMode.PauseDownloads = v; });
            cardPolicyPauseDl = (SettingCard)swPolicyPauseDl.Parent;
            swPolicyPauseSvc = AddPolicyToggle(scroll, ref sy, Lang.T("gm.pausesvc"), Lang.T("v15.custom.override"), delegate { return gameMode.PauseSvcIndex; }, delegate(bool v) { gameMode.PauseSvcIndex = v; });
            cardPolicyPauseSvc = (SettingCard)swPolicyPauseSvc.Parent;
            swPolicyDvr = AddPolicyToggle(scroll, ref sy, Lang.T("set.dvr"), Lang.T("v15.custom.override"), delegate { return gameMode.KillGameDvr; }, delegate(bool v) { gameMode.KillGameDvr = v; });
            cardPolicyDvr = (SettingCard)swPolicyDvr.Parent;
            sy += 10; Section(scroll, Lang.T("v15.policy.extras"), 6, sy); sy += 24;
            Toggle swPolicyIdle = AddPolicyConfirmToggle(scroll, ref sy, Lang.T("gm.idledisable"), Lang.T("gm.idledisable.sub"),
                Lang.T("gm.idledisable.confirm"),
                delegate { return gameMode.IdleStateDisable; }, delegate(bool v) { gameMode.IdleStateDisable = v; },
                ValueTextWidth(Lang.T("gm.idledisable.warn")));
            ((SettingCard)swPolicyIdle.Parent).SetValue(Lang.T("gm.idledisable.warn"), Theme.Danger);
            AddPolicyToggle(scroll, ref sy, Lang.T("gm.visualfx"), Lang.T("gm.visualfx.sub"),
                delegate { return gameMode.VisualFxDowngrade; }, delegate(bool v) { gameMode.VisualFxDowngrade = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("set.trim"), Lang.T("v15.trim.sub"),
                delegate { return gameMode.TrimWorkingSet; }, delegate(bool v) { gameMode.TrimWorkingSet = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("gm.standby"), Lang.T("gm.standby.sub"),
                delegate { return gameMode.PurgeStandby; }, delegate(bool v) { gameMode.PurgeStandby = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("gm.pausewu"), Lang.T("gm.pausewu.sub"),
                delegate { return gameMode.PauseWindowsUpdate; }, delegate(bool v) { gameMode.PauseWindowsUpdate = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("set.pqos"), Lang.T("set.pqos.n"),
                delegate { return gameMode.PresenceQosOff; }, delegate(bool v) { gameMode.PresenceQosOff = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("set.awake"), Lang.T("set.awake.n"),
                delegate { return gameMode.KeepAwake; }, delegate(bool v) { gameMode.KeepAwake = v; });
            RefreshPolicyPresentation();
        }

        private Toggle AddPolicyConfirmToggle(
            Control parent, ref int y, string title, string desc, string warning, Func<bool> read, Action<bool> write)
        {
            return AddPolicyConfirmToggle(parent, ref y, title, desc, warning, read, write, 0);
        }

        private Toggle AddPolicyConfirmToggle(
            Control parent, ref int y, string title, string desc, string warning, Func<bool> read, Action<bool> write,
            int valueReserve)
        {
            Toggle sw = MakeSwitch(read(), null);
            sw.CheckedChanged += delegate
            {
                if (!sw.Checked) { write(false); return; }
                if (MessageBox.Show(this, warning, "Pavise",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.OK)
                {
                    sw.SetSilently(false);
                    return;
                }
                write(true);
            };
            int cardH;
            MakeAutoCard(parent, 6, y, ScrollContentW, 78, title, desc, sw, valueReserve, out cardH);
            y += cardH + 8;
            policySync.Add(delegate { sw.SetSilently(read()); });
            return sw;
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
            ApplyPresetPolicy(swPolicyStrict, cardPolicyStrict, Lang.T("v14.cpu.adaptive"), false, true);
            ApplyPresetPolicy(swPolicyAggressive, cardPolicyAggressive, Lang.T("gm.aggressive"), !custom, competitive);
            ApplyPresetPolicy(swPolicyPauseDl, cardPolicyPauseDl, Lang.T("gm.pausedl"), !custom, competitive);
            ApplyPresetPolicy(swPolicyPauseSvc, cardPolicyPauseSvc, Lang.T("gm.pausesvc"), !custom, false);
            ApplyPresetPolicy(swPolicyDvr, cardPolicyDvr, Lang.T("set.dvr"), !custom, competitive);
            bool freezeScope = GameMode.IsAggressive(mode, gameMode.AggressiveSuppression);
            bool freezeDepth = GameMode.ResolveBackgroundLevel(mode, gameMode.StrictCoreIsolation,
                SuppressionLevel.None, false) >= SuppressionLevel.Isolated;
            ApplyGatedPolicy(swPolicyFreeze, cardPolicyFreeze, Lang.T("gm.freeze"), freezeScope && freezeDepth,
                Lang.T(freezeScope ? "v17.freeze.needstrict" : "v17.freeze.gated"));
        }

        private static void ApplyGatedPolicy(Toggle toggle, SettingCard card, string title, bool usable, string reason)
        {
            if (toggle != null)
            {
                toggle.Enabled = usable;
                if (!usable) toggle.SetSilently(false);
            }
            if (card != null) card.Title = title + (usable ? "" : " · " + reason);
        }

        private static void ApplyPresetPolicy(Toggle toggle, SettingCard card, string title, bool forced, bool effective)
        {
            if (toggle != null)
            {
                toggle.Enabled = !forced;
                if (forced) toggle.SetSilently(effective);
            }
            if (card != null) card.Title = title + (forced ? " · " + Lang.T("v14.preset.forced") : "");
        }

    }
}

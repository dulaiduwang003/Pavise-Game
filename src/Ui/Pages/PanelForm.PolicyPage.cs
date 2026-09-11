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
        private ModuleBanner policyBanner;
        private TechTabs policyTabs;
        private DBPanel[] policyTabPanels;
        private Toggle swPolicyBackground, swPolicyAggressive;
        private Toggle swPolicyPauseDl, swPolicyDvr;
        private Toggle swPolicyGpuDemote, swPolicyBoost, swPolicyLane, swPolicyMmcss;
        private Toggle swPolicyAdaptive;
        private SettingCard cardPolicyAdaptive;
        private Toggle swPolicyVramShield;
        private SettingCard cardPolicyVramShield, cardPolicyEnglishInput;
        private Toggle swPolicyEnglishInput;
        private Toggle swPolicyDwmBoost, swPolicyWsTrim, swPolicyAudioLat, swPolicyIdlePolicy, swPolicyCacheWarm;
        private SettingCard cardPolicyCacheWarm;
        private SettingCard cardPolicyDwmBoost, cardPolicyWsTrim, cardPolicyAudioLat, cardPolicyIdlePolicy;
        private Toggle swPolicyPowerYield;
        private Toggle swPolicyDisableCpuIdle;
        private Toggle swPolicyPauseWu, swPolicyPauseServices, swPolicyAwake;
        private Toggle swPolicyPauseMaint;
        private SettingCard cardPolicyPauseMaint;
        private SettingCard cardPolicyAggressive;
        private SettingCard cardPolicyPauseDl, cardPolicyDvr;
        private SettingCard cardPolicyBackground, cardPolicyGpuDemote, cardPolicyBoost, cardPolicyLane, cardPolicyMmcss;
        private SettingCard cardPolicyPowerYield;
        private SettingCard cardPolicyDisableCpuIdle;
        private SettingCard cardPolicyPauseWu, cardPolicyPauseServices, cardPolicyAwake;
        private readonly List<Action> policySync = new List<Action>();
#if PAVISE_SELFTEST
        internal Func<bool> DisableCpuIdleConfirmationForTest;
        internal Func<bool> PowerYieldConfirmationForTest;
        internal Func<bool> VramShieldConfirmationForTest;
#endif

        private void BuildPolicyPage()
        {
            policySync.Clear();
            int y = PageHeader(pagePolicy, Lang.T("nav.policy"), Lang.T("v15.policy.sub"), 2);
            policyBanner = new ModuleBanner();
            policyBanner.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(72));
            policyBanner.Code = "POLICY CORE // 01";
            policyBanner.TitleText = Lang.T("nav.policy");
            policyBanner.Detail = Lang.T("v15.policy.mode.hint");
            policyBanner.Glyph = "settings";
            pagePolicy.Controls.Add(policyBanner); y += 84;

            policyTabs = new TechTabs();
            policyTabs.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(38));
            policyTabs.SetTabs(
                new[] { Lang.T("policy.tab.core"),
                    Lang.T("policy.tab.custom"), Lang.T("policy.tab.extras") },
                new[] { Lang.T("v15.policy.core"),
                    Lang.T("v15.policy.custom"), Lang.T("v15.policy.extras") });
            pagePolicy.Controls.Add(policyTabs);
            y += 48;

            policyTabPanels = MakeTabPanels(pagePolicy, policyTabs, 3, y);

            Control scroll = policyTabPanels[0];
            int sy = 2;
            swPolicyBackground = AddPolicyToggle(scroll, ref sy, Lang.T("v14.bg.master"), Lang.T("v14.bg.master.sub"),
                delegate { return gameMode.SuppressBackground; }, delegate(bool v) { gameMode.SuppressBackground = v; });
            cardPolicyBackground = (SettingCard)swPolicyBackground.Parent;
            swPolicyGpuDemote = AddPolicyToggle(scroll, ref sy, Lang.T("gm.gpudemote"), Lang.T("gm.gpudemote.sub"),
                delegate { return gameMode.GpuDemote; }, delegate(bool v) { gameMode.GpuDemote = v; });
            cardPolicyGpuDemote = (SettingCard)swPolicyGpuDemote.Parent;
            // 游戏选核和核心隔离在独立的核心调度页保存。
            swPolicyAdaptive = AddPolicyToggle(scroll, ref sy, Lang.T("gm.adaptive"), Lang.T("gm.adaptive.sub"),
                delegate { return gameMode.AdaptiveEscalateOn; }, delegate(bool v) { gameMode.AdaptiveEscalateOn = v; });
            cardPolicyAdaptive = (SettingCard)swPolicyAdaptive.Parent;
            swPolicyBoost = AddPolicyToggle(scroll, ref sy, Lang.T("gm.boost"), Lang.T("v15.boost.sub"),
                delegate { return gameMode.BoostGame; }, delegate(bool v) { gameMode.BoostGame = v; });
            cardPolicyBoost = (SettingCard)swPolicyBoost.Parent;
            swPolicyLane = AddPolicyToggle(scroll, ref sy, Lang.T("gm.lane"), Lang.T("gm.lane.sub"),
                delegate { return gameMode.RenderLaneOn; }, delegate(bool v) { gameMode.RenderLaneOn = v; });
            cardPolicyLane = (SettingCard)swPolicyLane.Parent;
            policySync.Add(SyncPolicyLane);
            swPolicyVramShield = AddPolicyToggle(scroll, ref sy, Lang.T("gm.vramshield"), Lang.T("gm.vramshield.sub"),
                delegate { return gameMode.VramShieldOn; }, delegate(bool v) { OnVramShieldToggle(v); });
            cardPolicyVramShield = (SettingCard)swPolicyVramShield.Parent;
            swPolicyLane.CheckedChanged += delegate { RefreshPolicyPresentation(); };

            scroll = policyTabPanels[1]; sy = 2;
            swPolicyAggressive = AddPolicyToggle(scroll, ref sy, Lang.T("gm.aggressive"), Lang.T("gm.aggressive.sub"),
                delegate { return gameMode.AggressiveSuppression; }, delegate(bool v) { gameMode.AggressiveSuppression = v; });
            cardPolicyAggressive = (SettingCard)swPolicyAggressive.Parent;
            swPolicyAggressive.CheckedChanged += delegate { RefreshPolicyPresentation(); };
            swPolicyPauseDl = AddPolicyToggle(scroll, ref sy, Lang.T("gm.pausedl"), Lang.T("gm.pausedl.sub"), delegate { return gameMode.PauseDownloads; }, delegate(bool v) { gameMode.PauseDownloads = v; });
            cardPolicyPauseDl = (SettingCard)swPolicyPauseDl.Parent;
            swPolicyDvr = AddPolicyToggle(scroll, ref sy, Lang.T("set.dvr"), Lang.T("set.dvr.sub"), delegate { return gameMode.KillGameDvr; }, delegate(bool v) { gameMode.KillGameDvr = v; });
            cardPolicyDvr = (SettingCard)swPolicyDvr.Parent;
            swPolicyMmcss = AddPolicyToggle(scroll, ref sy, Lang.T("gm.mmcss"), Lang.T("gm.mmcss.sub"),
                delegate { return gameMode.MmcssOn; }, delegate(bool v) { gameMode.MmcssOn = v; });
            cardPolicyMmcss = (SettingCard)swPolicyMmcss.Parent;
            swPolicyDwmBoost = AddPolicyToggle(scroll, ref sy, Lang.T("gm.dwmboost"), Lang.T("gm.dwmboost.sub"),
                delegate { return gameMode.DwmBoostOn; }, delegate(bool v) { gameMode.DwmBoostOn = v; });
            cardPolicyDwmBoost = (SettingCard)swPolicyDwmBoost.Parent;
            swPolicyPowerYield = AddPolicyToggle(scroll, ref sy,
                Lang.T("gm.poweryield"), Lang.T("gm.poweryield.sub"),
                delegate { return Settings.Load(PowerBudgetYieldRunner.EnabledKey, false); },
                delegate(bool v) { OnPowerYieldToggle(v); });
            cardPolicyPowerYield = (SettingCard)swPolicyPowerYield.Parent;
            scroll = policyTabPanels[2]; sy = 2;
            swPolicyEnglishInput = AddPolicyToggle(scroll, ref sy, Lang.T("gm.englishinput"), Lang.T("gm.englishinput.sub"),
                delegate { return gameMode.EnglishInputEnabled; },
                delegate(bool v) { gameMode.EnglishInputEnabled = v; }, 0, true);
            cardPolicyEnglishInput = (SettingCard)swPolicyEnglishInput.Parent;
            swPolicyAudioLat = AddPolicyToggle(scroll, ref sy, Lang.T("gm.audiolat"), Lang.T("gm.audiolat.sub"),
                delegate { return gameMode.AudioLowLatOn; }, delegate(bool v) { gameMode.AudioLowLatOn = v; }, 0, true);
            cardPolicyAudioLat = (SettingCard)swPolicyAudioLat.Parent;
            swPolicyDisableCpuIdle = AddPolicyToggle(scroll, ref sy,
                Lang.T("gm.disablecpuidle"), Lang.T("gm.disablecpuidle.sub"),
                delegate { return gameMode.DisableCpuIdle; },
                delegate(bool v) { OnDisableCpuIdleToggle(v); });
            cardPolicyDisableCpuIdle = (SettingCard)swPolicyDisableCpuIdle.Parent;
            BuildStandbyCleanerPolicyCard(scroll, ref sy);
            cardPolicyCacheWarm = BuildCacheWarmPolicyCard(scroll, ref sy);
            swPolicyWsTrim = AddPolicyToggle(scroll, ref sy, Lang.T("gm.wstrim"), Lang.T("gm.wstrim.sub"),
                delegate { return gameMode.WsTrimOn; }, delegate(bool v) { gameMode.WsTrimOn = v; }, 0, true);
            cardPolicyWsTrim = (SettingCard)swPolicyWsTrim.Parent;
            swPolicyIdlePolicy = AddPolicyToggle(scroll, ref sy,
                Lang.T("gm.idlepolicy"), Lang.T("gm.idlepolicy.sub"),
                delegate { return gameMode.IdlePolicyOn; },
                delegate(bool v) { gameMode.IdlePolicyOn = v; }, 0, true);
            cardPolicyIdlePolicy = (SettingCard)swPolicyIdlePolicy.Parent;
            swPolicyPauseWu = AddPolicyToggle(scroll, ref sy, Lang.T("gm.pausewu"), Lang.T("gm.pausewu.sub"),
                delegate { return gameMode.PauseWindowsUpdate; }, delegate(bool v) { gameMode.PauseWindowsUpdate = v; });
            cardPolicyPauseWu = (SettingCard)swPolicyPauseWu.Parent;
            swPolicyPauseMaint = AddPolicyToggle(scroll, ref sy, Lang.T("gm.pausemaint"), Lang.T("gm.pausemaint.sub"),
                delegate { return gameMode.PauseMaintenance; }, delegate(bool v) { gameMode.PauseMaintenance = v; });
            cardPolicyPauseMaint = (SettingCard)swPolicyPauseMaint.Parent;
            swPolicyPauseServices = AddPolicyToggle(scroll, ref sy, Lang.T("gm.pausesvc"), Lang.T("gm.pausesvc.sub"),
                delegate { return gameMode.PauseServices; }, delegate(bool v) { gameMode.PauseServices = v; });
            cardPolicyPauseServices = (SettingCard)swPolicyPauseServices.Parent;
            swPolicyAwake = AddPolicyToggle(scroll, ref sy, Lang.T("set.awake"), Lang.T("set.awake.n"),
                delegate { return gameMode.KeepAwake; }, delegate(bool v) { gameMode.KeepAwake = v; });
            cardPolicyAwake = (SettingCard)swPolicyAwake.Parent;

            EnableCardCollapse(policyTabPanels[0], cardPolicyBackground, cardPolicyBoost);
            EnableCardCollapse(policyTabPanels[1]);
            EnableCardCollapse(policyTabPanels[2]);

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
            int gap = Theme.S(8);
            foreach (Control c in panel.Controls)
            {
                var card = c as SettingCard;
                int baseTop;
                if (card == null || !map.TryGetValue(card, out baseTop)) continue;
                // 按档位整张藏起来的卡 连同它下面的间距一起让出来 折叠与否不管
                if (card.Suppressed)
                {
                    shifts.Add(new[] { baseTop, -((card.Collapsible ? card.ExpandedHeight : card.Height) + gap) });
                    continue;
                }
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

        private void EnsureTabFor(Control card)
        {
            RevealTabFor(policyTabs, policyTabPanels, card);
            RevealTabFor(envTabs, envTabPanels, card);
            RevealTabFor(gfxTabs, gfxTabPanels, card);
            RevealTabFor(settingsTabs, settingsTabPanels, card);
        }

        private static void RevealTabFor(TechTabs tabs, DBPanel[] panels, Control card)
        {
            if (panels == null || tabs == null || card == null) return;
            for (Control c = card; c != null; c = c.Parent)
                for (int i = 0; i < panels.Length; i++)
                    if (c == panels[i]) { tabs.Index = i; return; }
        }

        private void AddPolicyLink(Control parent, ref int y, string title, string desc, Action go)
        {
            var btn = new PillButton(Lang.T("gm.goto"), BtnKind.Normal);
            btn.SetBounds(0, 0, Theme.S(120), Theme.S(30));
            btn.Click += delegate { go(); };
            int cardH;
            MakeAutoCard(parent, 6, y, ScrollContentW, 64, title, desc, btn, out cardH);
            y += cardH + 8;
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

        private void RefreshPolicyPresentation()
        {
            if (policyBanner != null)
            {
                policyBanner.State = Lang.F("mode.policy.active", ModeButton.ModeName(gameMode.ActivePreset));
                policyBanner.StateColor = Theme.Accent;
            }
            PerformancePreset mode = gameMode.ActivePreset;
            bool competitive = mode == PerformancePreset.Competitive;
            bool custom = mode == PerformancePreset.Custom;
            // 掌机档跟电竞一样锁死这两项 差别在功耗侧 不在这里
            bool presetForcesOn = competitive || mode == PerformancePreset.Handheld;
            ApplyPresetPolicy(swPolicyBackground, cardPolicyBackground, Lang.T("v14.bg.master"), false, true);
            ApplyPresetPolicy(swPolicyGpuDemote, cardPolicyGpuDemote, Lang.T("gm.gpudemote"), false, true);
            // 只有智能档会用到 其它档位本来就是电竞口径 整张卡藏起来 下面的卡上移补位
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
            ApplyPresetPolicy(swPolicyAggressive, cardPolicyAggressive, Lang.T("gm.aggressive"), !custom, presetForcesOn);
            ApplyPresetPolicy(swPolicyPauseDl, cardPolicyPauseDl, Lang.T("gm.pausedl"), !custom, presetForcesOn);
            ApplyPresetPolicy(swPolicyDvr, cardPolicyDvr, Lang.T("set.dvr"), false, true);
            ApplyPresetPolicy(swPolicyMmcss, cardPolicyMmcss, Lang.T("gm.mmcss"), false, true);
            if (swPolicyMmcss != null && !elevated)
            {
                swPolicyMmcss.Enabled = false;
                if (cardPolicyMmcss != null)
                {
                    cardPolicyMmcss.Desc = Lang.T("vbs.needadmin");
                    cardPolicyMmcss.SetLock(Lang.T("lock.na"), false);
                }
            }
            ApplyPresetPolicy(swPolicyPowerYield, cardPolicyPowerYield,
                Lang.T("gm.poweryield"), false, true);
            if (swPolicyPowerYield != null && cardPolicyPowerYield != null)
            {
                // 硬件或权限失败只挡住新的开启 永远不挡关闭
                // 熔断是一张重试通知 显式关一次再开可能重新验证通过
                string reasonKey = PowerYieldUnavailableReasonKey();
                bool watts = EnergyMeter.Available;
                bool fused = watts ? PowerBudgetYield.Fused : PowerBudgetYield.FreqFused;
                // 读不到瓦数时把设备实际上报的内容一并显示 分得出"没接口"还是"名字不认识"
                //   通道命名各家不同 认不出来的名字要让用户看得见 才好反馈回来补进 ClassifyRail
                string why = reasonKey == null ? null : Lang.T(reasonKey);
                if (reasonKey == "gm.poweryield.nowatt") why += " " + EnergyMeter.Describe();
                swPolicyPowerYield.Enabled = PowerBudgetYieldRunner.EnabledSetting || reasonKey == null;
                cardPolicyPowerYield.Desc = why ?? Lang.T(fused ? "gm.poweryield.fused"
                    : watts ? "gm.poweryield.sub" : "gm.poweryield.proxysub");
                cardPolicyPowerYield.SetLock(swPolicyPowerYield.Enabled ? "" : Lang.T("lock.na"), false);
            }
            // 掌机档不提供的四项 开关锁死标预设强制关 已开着的照样锁 对局中本来就不生效
            bool handheld = mode == PerformancePreset.Handheld;
            ApplyPresetPolicy(swPolicyDisableCpuIdle, cardPolicyDisableCpuIdle,
                Lang.T("gm.disablecpuidle"), handheld, false);
            if (!handheld) RefreshDisableCpuIdlePresentation();
            RefreshStandbyCleanerPresentation();
            ApplyPresetPolicy(swPolicyCacheWarm, cardPolicyCacheWarm, Lang.T("gm.cachewarm"), handheld, false);
            ApplyPresetPolicy(swPolicyPauseWu, cardPolicyPauseWu, Lang.T("gm.pausewu"), false, true);
            ApplyPresetPolicy(swPolicyPauseMaint, cardPolicyPauseMaint, Lang.T("gm.pausemaint"), false, true);
            ApplyPresetPolicy(swPolicyPauseServices, cardPolicyPauseServices, Lang.T("gm.pausesvc"), false, true);
            ApplyPresetPolicy(swPolicyAwake, cardPolicyAwake, Lang.T("set.awake"), false, true);
            ApplyPresetPolicy(swPolicyVramShield, cardPolicyVramShield, Lang.T("gm.vramshield"), handheld, false);
            ApplyPresetPolicy(swPolicyEnglishInput, cardPolicyEnglishInput, Lang.T("gm.englishinput"), false, true);
            // 没有档位会强制这三项 显示一律听用户自己的开关
            ApplyPresetPolicy(swPolicyWsTrim, cardPolicyWsTrim, Lang.T("gm.wstrim"), false, true);
            ApplyPresetPolicy(swPolicyIdlePolicy, cardPolicyIdlePolicy, Lang.T("gm.idlepolicy"), handheld, false);
            if (!handheld) RefreshIdlePolicyPresentation();
            ApplyPresetPolicy(swPolicyAudioLat, cardPolicyAudioLat, Lang.T("gm.audiolat"), false, true);
            ApplyPresetPolicy(swPolicyDwmBoost, cardPolicyDwmBoost, Lang.T("gm.dwmboost"), false, true);
        }

        // 空闲旋钮只写托管方案 targetOwned 为假时 TuneTarget 整个不跑
        //   手选了自己的电源方案或关掉方案总开关 这个开关就打不出任何效果
        //   禁止 CPU 空闲不一样 它写当前活动方案 用户自己的方案也会被改 所以两者不能共用一套提示
        //   永远留一条关闭的路 已经开着的不许因为不适用而锁死
        private void RefreshIdlePolicyPresentation()
        {
            if (swPolicyIdlePolicy == null || cardPolicyIdlePolicy == null) return;
            bool managed = PowerPlan.EffectivePlanId == PowerPlan.ManagedChoice;
            bool applies = managed && gameMode.PowerPlanSwitch;
            swPolicyIdlePolicy.Enabled = applies || gameMode.IdlePolicyOn;
            cardPolicyIdlePolicy.Desc = applies
                ? Lang.T("gm.idlepolicy.sub") : Lang.T("gm.idlepolicy.needmanaged");
            cardPolicyIdlePolicy.SetLock(applies ? "" : Lang.T("lock.na"), false);
        }

        private void RefreshDisableCpuIdlePresentation()
        {
            if (swPolicyDisableCpuIdle != null)
                swPolicyDisableCpuIdle.Enabled = elevated || gameMode.DisableCpuIdle;
            if (cardPolicyDisableCpuIdle != null)
                cardPolicyDisableCpuIdle.Desc = Lang.T(elevated
                    ? "gm.disablecpuidle.sub" : "gm.disablecpuidle.needadmin");
        }

        private bool ConfirmDisableCpuIdleEnable()
        {
            if (!elevated) return false;
#if PAVISE_SELFTEST
            if (DisableCpuIdleConfirmationForTest != null) return DisableCpuIdleConfirmationForTest();
#endif
            if (PowerPlan.CpuIdleVendorBlocked)
            {
                PaviseDialog.Info(this, Lang.T("gm.disablecpuidle"), Lang.T("disablecpuidle.amd"));
                return false;
            }
            return PaviseDialog.Confirm(this, Lang.T("gm.disablecpuidle"),
                Lang.T("disablecpuidle.warn"), DlgKind.Warn);
        }

        private void OnDisableCpuIdleToggle(bool on)
        {
            if (on && !ConfirmDisableCpuIdleEnable())
            {
                if (swPolicyDisableCpuIdle != null) swPolicyDisableCpuIdle.SetSilently(false);
                return;
            }
            gameMode.DisableCpuIdle = on;
            RefreshDisableCpuIdlePresentation();
        }

        private string PowerYieldUnavailableReasonKey()
        {
            if (!Native.HasSystemBattery()) return "gm.poweryield.desktop";
            // 读不到瓦数还可以走频率代理的降级验证 两条证据链都没有才算不可用
            if (!EnergyMeter.Available && !PowerBudgetYieldRunner.FreqProxyAvailable)
                return "gm.poweryield.nowatt";
            if (!elevated) return "vbs.needadmin";
            return null;
        }

        private bool ConfirmPowerYieldEnable()
        {
            if (PowerYieldUnavailableReasonKey() != null) return false;
            bool confirmed;
#if PAVISE_SELFTEST
            if (PowerYieldConfirmationForTest != null) confirmed = PowerYieldConfirmationForTest();
            else
#endif
                confirmed = PaviseDialog.Confirm(this, Lang.T("gm.poweryield"),
                    Lang.T("poweryield.warn"), DlgKind.Warn);
            return confirmed && PowerYieldUnavailableReasonKey() == null;
        }

        private void OnPowerYieldToggle(bool on)
        {
            if (on && !ConfirmPowerYieldEnable())
            {
                if (swPolicyPowerYield != null)
                    swPolicyPowerYield.SetSilently(PowerBudgetYieldRunner.EnabledSetting);
                RefreshPolicyPresentation();
                return;
            }
            if (Settings.Save(PowerBudgetYieldRunner.EnabledKey, on) && on) PowerBudgetYield.ClearFuse();
            if (swPolicyPowerYield != null)
                swPolicyPowerYield.SetSilently(PowerBudgetYieldRunner.EnabledSetting);
            RefreshPolicyPresentation();
        }

        private bool ConfirmVramShieldEnable()
        {
#if PAVISE_SELFTEST
            if (VramShieldConfirmationForTest != null) return VramShieldConfirmationForTest();
#endif
            return PaviseDialog.Confirm(this, Lang.T("gm.vramshield"), Lang.T("vramshield.warn"), DlgKind.Warn);
        }

        // 开启前先把话说在前面 体感不对就关掉
        private void OnVramShieldToggle(bool on)
        {
            if (on && !ConfirmVramShieldEnable())
            {
                if (swPolicyVramShield != null) swPolicyVramShield.SetSilently(gameMode.VramShieldOn);
                return;
            }
            gameMode.VramShieldOn = on;
            if (swPolicyVramShield != null) swPolicyVramShield.SetSilently(gameMode.VramShieldOn);
        }

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

        // 第一栏有按档位整张藏起来的卡 藏与现和折叠展开走同一套摞法 两套各算各的会互相覆盖
        //   摞法按 Suppressed 判 不看 Visible 建页和换档常发生在这页还没显示的时候 那会儿所有卡的 Visible 都是假
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

    }
}

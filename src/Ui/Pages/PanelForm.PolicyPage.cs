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
        private TierPicker pickPolicyCores;
        private Toggle swPolicyBackground, swPolicyAggressive;
        private Toggle swPolicyPauseDl, swPolicyDvr;
        private Toggle swPolicyGpuDemote, swPolicyBoost, swPolicyLane, swPolicyMmcss;
        private Toggle swPolicyHeavySqueeze;
        private SettingCard cardPolicyHeavySqueeze;
        private Toggle swPolicyAdaptive;
        private SettingCard cardPolicyAdaptive;
        private Toggle swPolicyVramShield;
        private SettingCard cardPolicyVramShield, cardPolicyEnglishInput;
        private Toggle swPolicyEnglishInput;
        private Toggle swPolicyPowerYield;
        private Toggle swPolicyDisableCpuIdle;
        private Toggle swPolicyPauseWu, swPolicyPauseServices, swPolicyAwake;
        private Toggle swPolicyPauseMaint, swPolicyLaptopPerf;
        private SettingCard cardPolicyPauseMaint, cardPolicyLaptopPerf;
        private SettingCard cardPolicyCores, cardPolicyAggressive;
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
        internal Func<bool> HeavySqueezeConfirmationForTest;
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
            swPolicyHeavySqueeze = AddPolicyToggle(scroll, ref sy, Lang.T("gm.squeeze"), Lang.T("gm.squeeze.sub"),
                delegate { return gameMode.HeavySqueezeOn; }, delegate(bool v) { OnHeavySqueezeToggle(v); });
            cardPolicyHeavySqueeze = (SettingCard)swPolicyHeavySqueeze.Parent;
            swPolicyAdaptive = AddPolicyToggle(scroll, ref sy, Lang.T("gm.adaptive"), Lang.T("gm.adaptive.sub"),
                delegate { return gameMode.AdaptiveEscalateOn; }, delegate(bool v) { gameMode.AdaptiveEscalateOn = v; });
            cardPolicyAdaptive = (SettingCard)swPolicyAdaptive.Parent;
            swPolicyBoost = AddPolicyToggle(scroll, ref sy, Lang.T("gm.boost"), Lang.T("v15.boost.sub"),
                delegate { return gameMode.BoostGame; }, delegate(bool v) { gameMode.BoostGame = v; });
            cardPolicyBoost = (SettingCard)swPolicyBoost.Parent;
            swPolicyLane = AddPolicyToggle(scroll, ref sy, Lang.T("gm.lane"), Lang.T("gm.lane.sub"),
                delegate { return gameMode.RenderLaneOn; }, delegate(bool v) { gameMode.RenderLaneOn = v; });
            cardPolicyLane = (SettingCard)swPolicyLane.Parent;
            swPolicyVramShield = AddPolicyToggle(scroll, ref sy, Lang.T("gm.vramshield"), Lang.T("gm.vramshield.sub"),
                delegate { return gameMode.VramShieldOn; }, delegate(bool v) { OnVramShieldToggle(v); });
            cardPolicyVramShield = (SettingCard)swPolicyVramShield.Parent;
            swPolicyLane.CheckedChanged += delegate { RefreshPolicyPresentation(); };

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
            swPolicyMmcss = AddPolicyToggle(scroll, ref sy, Lang.T("gm.mmcss"), Lang.T("gm.mmcss.sub"),
                delegate { return gameMode.MmcssOn; }, delegate(bool v) { gameMode.MmcssOn = v; });
            cardPolicyMmcss = (SettingCard)swPolicyMmcss.Parent;
            swPolicyPowerYield = AddPolicyToggle(scroll, ref sy,
                Lang.T("gm.poweryield"), Lang.T("gm.poweryield.sub"),
                delegate { return Settings.Load(PowerBudgetYieldRunner.EnabledKey, false); },
                delegate(bool v) { OnPowerYieldToggle(v); });
            cardPolicyPowerYield = (SettingCard)swPolicyPowerYield.Parent;
            // 只有带电池且厂商接口在的机器才有这一项 台式机不显示
            if (Native.HasSystemBattery())
            {
                swPolicyLaptopPerf = AddPolicyToggle(scroll, ref sy, Lang.T("gm.laptopperf"),
                    LaptopPerfMode.SupportedCached() ? Lang.T("gm.laptopperf.sub") : Lang.T("laptopperf.unsupported"),
                    delegate { return gameMode.LaptopPerf; }, delegate(bool v) { gameMode.LaptopPerf = v; });
                cardPolicyLaptopPerf = (SettingCard)swPolicyLaptopPerf.Parent;
                if (!LaptopPerfMode.SupportedCached() && !gameMode.LaptopPerf) swPolicyLaptopPerf.Enabled = false;
            }
            scroll = policyTabPanels[3]; sy = 2;
            swPolicyEnglishInput = AddPolicyToggle(scroll, ref sy, Lang.T("gm.englishinput"), Lang.T("gm.englishinput.sub"),
                delegate { return gameMode.EnglishInputEnabled; },
                delegate(bool v) { gameMode.EnglishInputEnabled = v; }, 0, true);
            cardPolicyEnglishInput = (SettingCard)swPolicyEnglishInput.Parent;
            swPolicyDisableCpuIdle = AddPolicyToggle(scroll, ref sy,
                Lang.T("gm.disablecpuidle"), Lang.T("gm.disablecpuidle.sub"),
                delegate { return gameMode.DisableCpuIdle; },
                delegate(bool v) { OnDisableCpuIdleToggle(v); });
            cardPolicyDisableCpuIdle = (SettingCard)swPolicyDisableCpuIdle.Parent;
            BuildStandbyCleanerPolicyCard(scroll, ref sy);
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
            RevealTabFor(settingsTabs, settingsTabPanels, card);
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

        private void AddPolicyLink(Control parent, ref int y, string title, string desc, Action go)
        {
            var btn = new PillButton(Lang.T("gm.goto"), BtnKind.Normal);
            btn.SetBounds(0, 0, Theme.S(120), Theme.S(30));
            btn.Click += delegate { go(); };
            int cardH;
            MakeAutoCard(parent, 6, y, ScrollContentW, 64, title, desc, btn, out cardH);
            y += cardH + 8;
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
            bool competitive = mode == PerformancePreset.Competitive
                || mode == PerformancePreset.Extreme;
            bool custom = mode == PerformancePreset.Custom;
            // 掌机档跟电竞一样锁死这两项 差别在功耗侧 不在这里 极限同口径
            bool presetForcesOn = competitive || mode == PerformancePreset.Handheld;
            // 极限档强制的清单项在这里显示锁定 否则开关显示用户配置值 与实际生效相反
            bool extremeTier = mode == PerformancePreset.Extreme;
            ApplyPresetPolicy(swPolicyBackground, cardPolicyBackground, Lang.T("v14.bg.master"), false, true);
            ApplyPresetPolicy(swPolicyGpuDemote, cardPolicyGpuDemote, Lang.T("gm.gpudemote"), extremeTier, true);
            ApplyPresetPolicy(swPolicyHeavySqueeze, cardPolicyHeavySqueeze, Lang.T("gm.squeeze"), false, true);
            if (swPolicyHeavySqueeze != null && cardPolicyHeavySqueeze != null && !GameMode.HeavySqueezeSupported())
            {
                // 拓扑没有落点只挡新开启 已经开着的永远能关
                swPolicyHeavySqueeze.Enabled = gameMode.HeavySqueezeOn;
                cardPolicyHeavySqueeze.Desc = Lang.T("gm.squeeze.unsupported");
                cardPolicyHeavySqueeze.SetLock(swPolicyHeavySqueeze.Enabled ? "" : Lang.T("lock.na"), false);
            }
            // 只有智能档会用到 其它档位本来就是电竞口径 开关留着但标为不生效
            ApplyPresetPolicy(swPolicyAdaptive, cardPolicyAdaptive, Lang.T("gm.adaptive"), false,
                mode == PerformancePreset.Standard);
            ApplyPresetPolicy(swPolicyBoost, cardPolicyBoost, Lang.T("gm.boost"), false, true);
            ApplyPresetPolicy(swPolicyLane, cardPolicyLane, Lang.T("gm.lane"), extremeTier, true);
            if (cardPolicyCores != null) cardPolicyCores.Title = Lang.T("cpu.place.title");
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
                Lang.T("gm.poweryield"), extremeTier, true);
            if (swPolicyPowerYield != null && cardPolicyPowerYield != null && !extremeTier)
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
            ApplyPresetPolicy(swPolicyDisableCpuIdle, cardPolicyDisableCpuIdle,
                Lang.T("gm.disablecpuidle"), false, true);
            RefreshDisableCpuIdlePresentation();
            RefreshStandbyCleanerPresentation();
            ApplyPresetPolicy(swPolicyPauseWu, cardPolicyPauseWu, Lang.T("gm.pausewu"), extremeTier, true);
            ApplyPresetPolicy(swPolicyPauseMaint, cardPolicyPauseMaint, Lang.T("gm.pausemaint"), extremeTier, true);
            if (swPolicyLaptopPerf != null)
            {
                ApplyPresetPolicy(swPolicyLaptopPerf, cardPolicyLaptopPerf, Lang.T("gm.laptopperf"), false, gameMode.LaptopPerf);
                if (!LaptopPerfMode.SupportedCached() && !gameMode.LaptopPerf)
                {
                    swPolicyLaptopPerf.Enabled = false;
                    if (cardPolicyLaptopPerf != null) cardPolicyLaptopPerf.SetLock(Lang.T("lock.na"), false);
                }
            }
            ApplyPresetPolicy(swPolicyPauseServices, cardPolicyPauseServices, Lang.T("gm.pausesvc"), extremeTier, true);
            ApplyPresetPolicy(swPolicyAwake, cardPolicyAwake, Lang.T("set.awake"), false, true);
            ApplyPresetPolicy(swPolicyVramShield, cardPolicyVramShield, Lang.T("gm.vramshield"), extremeTier, true);
            ApplyPresetPolicy(swPolicyEnglishInput, cardPolicyEnglishInput, Lang.T("gm.englishinput"), extremeTier, true);
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

        private bool ConfirmHeavySqueezeEnable()
        {
#if PAVISE_SELFTEST
            if (HeavySqueezeConfirmationForTest != null) return HeavySqueezeConfirmationForTest();
#endif
            return PaviseDialog.Confirm(this, Lang.T("gm.squeeze"), Lang.T("squeeze.warn"), DlgKind.Warn);
        }

        private void OnHeavySqueezeToggle(bool on)
        {
            if (on && !ConfirmHeavySqueezeEnable())
            {
                if (swPolicyHeavySqueeze != null) swPolicyHeavySqueeze.SetSilently(gameMode.HeavySqueezeOn);
                return;
            }
            gameMode.HeavySqueezeOn = on;
            if (swPolicyHeavySqueeze != null) swPolicyHeavySqueeze.SetSilently(gameMode.HeavySqueezeOn);
            RefreshPolicyPresentation();
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

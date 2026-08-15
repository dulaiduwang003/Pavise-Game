// @author bdth 2074055628@qq.com
// 文件用途 维护主窗口状态和主要交互事件

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal enum AutoHideAction { None, Schedule, Cancel }

    internal enum PageId
    {
        Overview = 0,
        Library = 1,
        Policy = 2,
        AntiCheat = 3,
        Graphics = 4,
        Environment = 5,
        Audit = 6,
        Log = 7,
        Settings = 8,
        About = 9,
        Whitelist = 10,
        Column = 11,
        Count = 12
    }

    internal partial class PanelForm : Form
    {
        private readonly Tamer tamer;
        private readonly GameMode gameMode;
        private readonly bool elevated;

        private DBPanel pageOverview, pagePolicy, pageAntiCheat, pageLibrary, pageLog, pageSettings, pageAbout;
        private DBPanel pageGraphics, pageEnvironment, pageWhitelist;
        private DBPanel[] pages;
        private NavRail nav;
        private ModeButton modeButton;
        private ThemeSwitch themeSwitch;
        private SearchButton searchButton;
        private PowerButton powerButton;
        private PowerFlyout powerFlyout;
        private ModePickerPanel modeFlyout;
        private PerformancePreset visualMode;
        private bool visualEnabled;
        private bool modeVisualInitialized;
        private Label lblSub;
        private int builtLang;
        private System.Windows.Forms.Timer uiTimer;
        private volatile bool uiActive;
        private bool uiActivityKnown;
        private bool formFrameAttached;
        private DBPanel curPage;
        private int pageBaseLeft;
        private Motion pageSlide;
        private Icon appIcon;
        public bool RealExit;
        public Action ExitApp;

        private DBPanel root;
        private System.Windows.Forms.Timer fitTimer;
        private bool fitting;
        private bool moveSizeLoop;
        private bool fitDeferredByDrag;

        private const int WinW = 1196, WinH = 768, RailW = 208, TopH = 54;
        private const int PageW = WinW - RailW, PageH = WinH - TopH;
        private const int ContentX = 26, ContentW = PageW - ContentX * 2;
        private const int ScrollContentW = PageW - 40 - 12 - 20;

        public PanelForm(Tamer t, GameMode gm, Icon icon, bool isElevated)
            : this(t, gm, icon, isElevated, new LolOptimizationService())
        {
        }

        public PanelForm(Tamer t, GameMode gm, Icon icon, bool isElevated, LolOptimizationService leagueService)
        {
            tamer = t; gameMode = gm; elevated = isElevated; lolService = leagueService; appIcon = (Icon)icon.Clone();
            visualMode = gameMode.ActivePreset; visualEnabled = gameMode.Enabled;
            Theme.SetMode(visualMode, false);
            BuildUi(appIcon);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x10;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.EnableElevatedFileDrop(Handle);
            AttachFormFrame();
            Native.RoundCorners(Handle);
            uiActivityKnown = false;
            CenterRoot();
            ScheduleFit();
            SyncUiActivity();
            if (UiActive) RefreshSlowStateAsync();
        }

        private void BuildUi(Icon appIcon)
        {
            builtLang = Lang.Cur;
            Text = App.DisplayName;
            Icon = appIcon;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            if (!fitting)
            {
                if (Dpi.SetDesignSize(WinW, WinH)) Theme.DropFontCache();
                ClientSize = new Size(Theme.S(WinW), Theme.S(WinH));
            }
            BackColor = Theme.Bg;
            Font = Theme.UI(9.5f, false);
            AttachFormFrame();

            nav = new NavRail(
                new[] { Lang.T("nav.overview"), Lang.T("nav.library"), Lang.T("nav.policy"),
                        Lang.T("v14.anticheat"), Lang.T("nav.graphics"), Lang.T("nav.env"), Lang.T("nav.audit"),
                        Lang.T("nav.log"), Lang.T("nav.set"), Lang.T("nav.about"), Lang.T("nav.white"),
                        Lang.T("nav.column") },
                new[] { "game", "tiles", "settings", "acshield", "gpu", "chip", "chart", "log", "gear", "info", "white", "gamepad" },
                new[] { (int)PageId.Overview, (int)PageId.Library, (int)PageId.Column, (int)PageId.Whitelist,
                        (int)PageId.Policy, (int)PageId.AntiCheat, (int)PageId.Log, (int)PageId.Graphics,
                        (int)PageId.Environment, (int)PageId.Audit, (int)PageId.Settings, (int)PageId.About },
                new[] { 7 }, new[] { Lang.T("nav.hardware") }, 2);
            AssertNavMatchesPageIds(nav);
            nav.SetBounds(0, 0, Theme.S(RailW), Theme.S(WinH));
            nav.SelectionChanged = ShowPage;
            nav.SetMode(visualMode, visualEnabled);

            var topBar = new DBPanel();
            topBar.SetBounds(Theme.S(RailW), 0, Theme.S(WinW - RailW), Theme.S(TopH));
            topBar.BackColor = Theme.Bg;
            topBar.MouseDown += DragMove;
            topBar.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var p = new Pen(Theme.Stroke)) e.Graphics.DrawLine(p, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
                using (var p = new Pen(Theme.Accent)) e.Graphics.DrawLine(p, 0, topBar.Height - 1, Theme.S(72), topBar.Height - 1);
            };

            lblSub = new Label();
            lblSub.Text = elevated ? Lang.T("title.admin") + " " + Lang.T("title.idle") : Lang.T("title.noelev");
            lblSub.ForeColor = elevated ? Theme.Faint : Theme.Danger;
            lblSub.BackColor = Theme.Bg;
            lblSub.Font = Theme.UI(8.25f, false);
            lblSub.UseCompatibleTextRendering = false;
            lblSub.TextAlign = ContentAlignment.MiddleLeft;
            lblSub.SetBounds(Theme.S(28), 0, Theme.S(300), Theme.S(TopH));
            lblSub.MouseDown += DragMove;

            modeButton = new ModeButton();
            modeButton.SetBounds(Theme.S(PageW - 340), Theme.S(4), Theme.S(232), Theme.S(46));
            modeButton.Clicked = ToggleModeFlyout;
            modeButton.SetMode(gameMode.ActivePreset);

            themeSwitch = new ThemeSwitch(Theme.LightMode);
            themeSwitch.SetBounds(Theme.S(PageW - 430), Theme.S(4), Theme.S(78), Theme.S(46));
            themeSwitch.Toggled = OnThemeToggled;

            searchButton = new SearchButton();
            searchButton.SetBounds(Theme.S(PageW - 484), Theme.S(4), Theme.S(46), Theme.S(46));
            searchButton.Clicked = ToggleSearchFlyout;

            powerButton = new PowerButton();
            powerButton.SetBounds(Theme.S(PageW - 652), Theme.S(4), Theme.S(160), Theme.S(46));
            powerButton.Clicked = TogglePowerFlyout;
            powerButton.SetState(gameMode.PowerPlanSwitch, PowerPlanButtonLabel());

            int tw = Theme.S(WinW - RailW);
            var btnMin = new CaptionButton(false);
            btnMin.SetBounds(tw - Theme.S(92), 0, Theme.S(44), Theme.S(TopH));
            btnMin.Bg = Theme.Bg;
            btnMin.Click += (s, e) => WindowState = FormWindowState.Minimized;
            var btnClose = new CaptionButton(true);
            btnClose.SetBounds(tw - Theme.S(48), 0, Theme.S(44), Theme.S(TopH));
            btnClose.Bg = Theme.Bg;
            btnClose.Click += (s, e) => Hide();

            topBar.Controls.AddRange(new Control[] { lblSub, powerButton, searchButton, themeSwitch, modeButton, btnMin, btnClose });

            pages = new DBPanel[(int)PageId.Count];
            pages[(int)PageId.Overview] = pageOverview = MakePage();
            pages[(int)PageId.Library] = pageLibrary = MakePage();
            pages[(int)PageId.Whitelist] = pageWhitelist = MakePage();
            pages[(int)PageId.Policy] = pagePolicy = MakePage();
            pages[(int)PageId.AntiCheat] = pageAntiCheat = MakePage();
            pages[(int)PageId.Graphics] = pageGraphics = MakePage();
            pages[(int)PageId.Environment] = pageEnvironment = MakePage();
            pages[(int)PageId.Audit] = pageAudit = MakePage();
            pages[(int)PageId.Log] = pageLog = MakePage();
            pages[(int)PageId.Settings] = pageSettings = MakePage();
            pages[(int)PageId.About] = pageAbout = MakePage();
            pages[(int)PageId.Column] = pageColumn = MakePage();
            BuildOverviewPage();
            BuildLibraryPage();
            BuildWhitelistPage();
            BuildPolicyPage();
            BuildAntiCheatPage();
            BuildGraphicsPage();
            BuildEnvironmentPage();
            BuildAuditPage();
            BuildLogPage();
            BuildSettingsPage();
            BuildAboutPage();
            BuildColumnPage();
            pageGameConfig = MakePage();
            RegisterPages();

            root = new DBPanel();
            root.SetBounds(0, 0, Theme.S(WinW), Theme.S(WinH));
            root.BackColor = Theme.Bg;

            root.Controls.Add(topBar);
            foreach (var p in pages) root.Controls.Add(p);
            root.Controls.Add(pageGameConfig);
            root.Controls.Add(nav);

            modeFlyout = new ModePickerPanel();
            modeFlyout.SetBounds(Theme.S(WinW - 420), Theme.S(56), Theme.S(396), Theme.S(286));
            modeFlyout.Visible = false;
            modeFlyout.ModeChosen = ChooseGlobalMode;
            root.Controls.Add(modeFlyout);
            modeFlyout.BringToFront();

            powerFlyout = new PowerFlyout();
            powerFlyout.SetBounds(Theme.S(WinW - 420), Theme.S(56), Theme.S(396), Theme.S(220));
            powerFlyout.Visible = false;
            powerFlyout.Chosen = ChoosePowerPlan;
            root.Controls.Add(powerFlyout);
            powerFlyout.BringToFront();

            searchFlyout = new SearchFlyout();
            searchFlyout.SetBounds(Theme.S(WinW - 420), Theme.S(56), Theme.S(396), Theme.S(432));
            searchFlyout.Visible = false;
            searchFlyout.Query = QuerySettingCards;
            searchFlyout.Chosen = OnSearchHitChosen;
            searchFlyout.Dismiss = delegate { SetSearchFlyout(false); };
            root.Controls.Add(searchFlyout);
            searchFlyout.BringToFront();

            Controls.Add(root);
            CenterRoot();

            KeyPreview = true;
            KeyDown -= OnEscHide;
            KeyDown += OnEscHide;

            nav.Select((int)PageId.Overview);

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 1200;
            uiTimer.Tick += OnUiTick;
            uiActivityKnown = false;
            SyncUiActivity();
        }

        private static void AssertNavMatchesPageIds(NavRail rail)
        {
            int expected = (int)PageId.Count;
            if (rail.ItemCount != expected)
                throw new InvalidOperationException("导航项数量 " + rail.ItemCount + " 与 PageId.Count " + expected + " 不一致");
            var seen = new bool[expected];
            for (int slot = 0; slot < expected; slot++)
            {
                int item = rail.ItemAtSlot(slot);
                if (item < 0 || item >= expected) throw new InvalidOperationException("导航视觉排序含越界项 " + item);
                if (seen[item]) throw new InvalidOperationException("导航视觉排序重复了 " + (PageId)item);
                seen[item] = true;
            }
        }

        private DBPanel MakePage()
        {
            var p = new DBPanel();
            p.SetBounds(Theme.S(RailW), Theme.S(TopH), Theme.S(WinW - RailW), Theme.S(WinH - TopH));
            p.BackColor = Theme.Bg;
            p.Visible = false;
            return p;
        }

        private sealed class PageHook
        {
            public readonly DBPanel Panel;
            public readonly Action<bool> OnActiveChanged;
            public readonly Action OnTick;

            public PageHook(DBPanel panel, Action<bool> onActiveChanged, Action onTick)
            {
                Panel = panel; OnActiveChanged = onActiveChanged; OnTick = onTick;
            }
        }

        private PageHook[] pageHooks;

        private void RegisterPages()
        {
            pageHooks = new PageHook[(int)PageId.Count];
            pageHooks[(int)PageId.Overview] = new PageHook(pageOverview,
                delegate(bool active) { if (paviseCore != null) paviseCore.SetAnimationEnabled(active); }, null);
            pageHooks[(int)PageId.Library] = new PageHook(pageLibrary,
                delegate(bool active) { if (active) { RefreshGames(); RefreshGameRunningStates(true); } },
                delegate { RefreshGameRunningStates(); });
            pageHooks[(int)PageId.Whitelist] = new PageHook(pageWhitelist,
                delegate(bool active) { if (active) RefreshWhitelist(true); }, null);
            pageHooks[(int)PageId.Policy] = new PageHook(pagePolicy, null, null);
            pageHooks[(int)PageId.AntiCheat] = new PageHook(pageAntiCheat, null, RefreshAcGroupStates);
            pageHooks[(int)PageId.Graphics] = new PageHook(pageGraphics, null, null);
            pageHooks[(int)PageId.Environment] = new PageHook(pageEnvironment,
                delegate(bool active) { if (active) RefreshEnvironmentStateAsync(); }, null);
            pageHooks[(int)PageId.Audit] = new PageHook(pageAudit,
                null, null);
            pageHooks[(int)PageId.Log] = new PageHook(pageLog,
                delegate(bool active) { if (active) RefreshLog(); }, RefreshLog);
            pageHooks[(int)PageId.Settings] = new PageHook(pageSettings,
                delegate(bool active) { if (active) RefreshSlowStateAsync(); }, null);
            pageHooks[(int)PageId.About] = new PageHook(pageAbout, null, null);
            pageHooks[(int)PageId.Column] = new PageHook(pageColumn,
                delegate(bool active)
                {
                    if (!active) return;
                    if (lolService != null) lolService.RequestDiscovery();
                    RefreshLolColumn();
                }, null);
        }

        private void NotifyPageActivation()
        {
            if (pageHooks == null) return;
            for (int i = 0; i < pageHooks.Length; i++)
            {
                PageHook hook = pageHooks[i];
                if (hook == null || hook.OnActiveChanged == null) continue;
                hook.OnActiveChanged(UiActive && hook.Panel == curPage);
            }
        }

        internal void ShowPageForShot(PageId id)
        {
            nav.Select((int)id);
            if (curPage != null) curPage.Left = Theme.S(RailW);
        }

        internal void ShowPolicyTabForShot(int index)
        {
            if (policyTabs != null) policyTabs.Index = index;
        }

        internal void SetCoreModeForShot(bool manual)
        {
            coreManualPicked = manual;
            if (pickPolicyCores != null)
                pickPolicyCores.Index = manual ? coreManualIndex : 0;
            SyncCorePage();
        }

        internal ulong SetCoreSelectionForShot(ulong mask)
        {
            if (coreMatrix == null) return ulong.MaxValue;
            gameMode.CustomCoreMask = 0;
            SetCoreModeForShot(true);
            corePending = mask;
            coreMatrix.Selected = mask;
            SyncCorePage();
            coreMatrix.Refresh();
            return coreMatrix.Selected;
        }

        internal void CommitCoreSelectionForShot()
        {
            ulong clean = CpuTopology.SanitizeCustomMask(corePending, CpuTopology.AllMask);
            if (clean == 0) return;
            gameMode.CustomCoreMask = clean == CpuTopology.AllMask ? 0 : clean;
            SyncCorePage();
        }

        private void ShowPage(int index)
        {
            SetModeFlyout(false);
            SetSearchFlyout(false);
            SetPowerFlyout(false);
            if (pageGameConfig != null && pageGameConfig.Visible)
            {
                pageGameConfig.Visible = false;
                cfgProfileId = null;
                cfgProfile = null;
                RefreshGames();
            }
            var page = pages[index];
            foreach (var p in pages) p.Visible = (p == page);
            curPage = page;
            pageBaseLeft = Theme.S(RailW);
            page.Left = pageBaseLeft + Theme.S(16);
            pageSlide.Speed = 0.26f; pageSlide.Set(1f); pageSlide.To(0f);
            SlideInActiveTab(page);
            if (UiActive) UiClock.Wake();
            NotifyPageActivation();
        }

        // 进入标签页时让当前活动内容面板滑入一次 单标签页(如游戏专栏)靠这个补上过渡动画
        private void SlideInActiveTab(Control page)
        {
            DBPanel[] panels;
            if (pageTabPanels == null || !pageTabPanels.TryGetValue(page, out panels)) return;
            foreach (DBPanel panel in panels)
                if (panel != null && panel.Visible) { Fx.SlideIn(panel); break; }
        }

        private void AttachFormFrame()
        {
            if (formFrameAttached) return;
            UiClock.Frame += OnFormFrame;
            formFrameAttached = true;
        }

        private void DetachFormFrame()
        {
            if (!formFrameAttached) return;
            UiClock.Frame -= OnFormFrame;
            formFrameAttached = false;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { CenterRoot(); ScheduleFit(); }
            else if (introActive) FinishIntro();
            SyncUiActivity();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            CenterRoot();
            ScheduleFit();
            SyncUiActivity();
        }

        private void CenterRoot()
        {
            if (root == null || root.IsDisposed) return;
            int x = (ClientSize.Width - root.Width) / 2;
            int y = (ClientSize.Height - root.Height) / 2;
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (root.Left != x || root.Top != y) root.Location = new Point(x, y);
        }

        private void ScheduleFit()
        {
            if (fitting || IsDisposed || !IsHandleCreated) return;
            if (WindowState == FormWindowState.Minimized) return;
            if (!Dpi.FitDiffers(ClientSize.Width, ClientSize.Height)) return;
            if (moveSizeLoop) { fitDeferredByDrag = true; return; }
            if (fitTimer == null)
            {
                fitTimer = new System.Windows.Forms.Timer();
                fitTimer.Interval = 220;
                fitTimer.Tick += OnFitTick;
            }
            fitTimer.Stop();
            fitTimer.Start();
        }

        private void OnFitTick(object sender, EventArgs e)
        {
            if (fitTimer != null) fitTimer.Stop();
            if (fitting || IsDisposed || !IsHandleCreated) return;
            if (moveSizeLoop) { fitDeferredByDrag = true; return; }
            if (WindowState == FormWindowState.Minimized) return;
            int w = ClientSize.Width, h = ClientSize.Height;
            if (!Dpi.FitDiffers(w, h)) return;
            float target = Dpi.FitScale(w, h);
            fitting = true;
            try
            {
                Dpi.Scale = target;
                Theme.DropFontCache();
                Logger.Log("界面按窗口尺寸重排 客户区 " + w + "x" + h
                    + " 缩放 " + target.ToString("F2"));
                RebuildUi();
            }
            finally { fitting = false; }
            CenterRoot();
        }

        internal bool UiActive
        {
            get { return uiActive; }
        }

        internal static bool ShouldRunUi(bool visible, FormWindowState windowState)
        {
            return visible && windowState != FormWindowState.Minimized;
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            SyncUiForeground(true);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            SyncUiForeground(false);
        }

        private void SyncUiForeground(bool foreground)
        {
            if (!foreground || !UiActive) return;
            UiClock.Wake();
            UiClock.WakeSlow();
        }

        private void SyncUiActivity()
        {
            bool next = ShouldRunUi(IsHandleCreated && !IsDisposed && Visible, WindowState);
            if (uiActivityKnown && uiActive == next) return;

            uiActivityKnown = true;
            uiActive = next;
            bool gameActive = gameMode != null && gameMode.Enabled && gameMode.IsActive;
            SyncAutoHideBaseline(gameActive, ref lastGameActive, ref autoHideArmed);

            if (!next)
            {
                if (uiTimer != null) uiTimer.Stop();
                CancelAutoHide();
                UiClock.Suspended = true;
                if (paviseCore != null) paviseCore.SetAnimationEnabled(false);
                return;
            }

            if (builtLang != Lang.Cur) { RebuildUi(); return; }

            RefreshLightweightUiState();
            SyncToggleValues();

            UiClock.Suspended = false;
            if (uiTimer != null) uiTimer.Start();
            UiClock.Wake();
            UiClock.WakeSlow();
            NotifyPageActivation();
        }

        private void RefreshLightweightUiState()
        {
            if (gameMode == null) return;
            if (lblStatus != null) lblStatus.Text = gameMode.StatusText;
            bool act = gameMode.Enabled && gameMode.IsActive;
            UiClock.Frozen = act;
            if (statusDot != null)
            {
                statusDot.Color = !gameMode.Enabled ? Theme.Dim : (act ? Theme.Green : Theme.Accent);
                statusDot.Pulse = act;
            }
            if (paviseCore != null) paviseCore.SetState(gameMode.ActivePreset, gameMode.Enabled, act);
            if (lblSub != null && elevated)
            {
                string game = gameMode.ActiveGame;
                string state = Lang.T("title.admin") + " "
                    + (game != null ? Lang.F("title.guard", game) : Lang.T("title.idle"));
                if (lblSub.Text != state) lblSub.Text = state;
                lblSub.ForeColor = game != null ? Theme.Green : Theme.Faint;
            }
            RefreshBoostPresentation();
        }

        private void ToggleModeFlyout()
        {
            if (lolDiscoveringUi) { SetModeFlyout(false); return; }
            SetModeFlyout(modeFlyout == null || !modeFlyout.Visible);
        }

        private void TogglePowerFlyout()
        {
            SetPowerFlyout(powerFlyout == null || !powerFlyout.Visible);
        }

        private void SetPowerFlyout(bool visible)
        {
            if (powerFlyout == null) return;
            if (visible)
            {
                SetSearchFlyout(false);
                SetModeFlyout(false);
                powerFlyout.Open(gameMode.PowerPlanSwitch, PowerPlan.EffectivePlanId);
            }
            if (!visible) Fx.Settle(powerFlyout);
            powerFlyout.Visible = visible;
            if (visible)
            {
                powerFlyout.BringToFront();
                Fx.DropIn(powerFlyout);
            }
        }

        private void ChoosePowerPlan(string id)
        {
            if (id == null) gameMode.PowerPlanSwitch = false;
            else { PowerPlan.SelectPlan(id); gameMode.PowerPlanSwitch = true; }
            SetPowerFlyout(false);
            if (powerButton != null) powerButton.SetState(gameMode.PowerPlanSwitch, PowerPlanButtonLabel());
            for (int i = 0; i < policySync.Count; i++) policySync[i]();
            if (pageGameConfig != null && pageGameConfig.Visible) SyncCfgRows();
        }

        private string PowerPlanButtonLabel()
        {
            if (!gameMode.PowerPlanSwitch) return Lang.T("plan.pick.off");
            if (PowerPlan.ManagedSelected) return PowerPlan.ManagedPlanTitle;
            string id = PowerPlan.EffectivePlanId;
            foreach (PowerPlanEntry entry in PowerPlan.ListUserPlans())
                if (string.Equals(entry.Id.ToString(), id, StringComparison.OrdinalIgnoreCase))
                    return entry.Name;
            return PowerPlan.ManagedPlanTitle;
        }

        private void SetModeFlyout(bool visible)
        {
            if (modeFlyout == null) return;
            if (visible)
            {
                modeFlyout.Sync(gameMode.Preset);
                SetSearchFlyout(false);
                SetPowerFlyout(false);
            }
            if (!visible) Fx.Settle(modeFlyout);
            modeFlyout.Visible = visible;
            if (visible)
            {
                modeFlyout.BringToFront();
                Fx.DropIn(modeFlyout);
            }
        }

        private void ChooseGlobalMode(PerformancePreset mode)
        {
            if (lolDiscoveringUi) { SetModeFlyout(false); return; }
            gameMode.Preset = mode;
            SetModeFlyout(false);
            UpdateModePresentation(true);
            SyncAllToggles();
        }

        private void UpdateModePresentation(bool animate)
        {
            PerformancePreset effective = gameMode.ActivePreset;
            bool enabled = gameMode.Enabled;
            bool visualChanged = !modeVisualInitialized || effective != visualMode || enabled != visualEnabled;
            if (modeButton != null) modeButton.SetMode(effective);
            if (lblHeroMode != null) lblHeroMode.Text = ModeButton.ModeName(effective);
            if (lblHeroSource != null)
            {
                string policySource = gameMode.SessionPolicySourceName;
                lblHeroSource.Text = policySource != null
                    ? Lang.F("mode.source.game", policySource) : Lang.T("mode.source.global");
            }
            if (lblPolicyMode != null) lblPolicyMode.Text = Lang.F("mode.policy.active", ModeButton.ModeName(effective));
            if (paviseCore != null) paviseCore.SetState(effective, enabled, gameMode.IsActive);
            if (effective != visualMode)
            {
                visualMode = effective;
                Theme.SetMode(effective, animate);
            }
            visualEnabled = enabled;
            modeVisualInitialized = true;
            if (nav != null) nav.SetMode(effective, enabled);
            if (visualChanged)
                using (Icon icon = IconArt.MakeMultiIcon(effective, enabled)) SetRuntimeIcon(icon);
            RefreshPolicyPresentation();
            if (pageGameConfig != null && pageGameConfig.Visible) SyncCfgRows();
        }

        private void OnThemeToggled(bool light)
        {
            Settings.Save("UiLight", light);
            Theme.SetLight(light);
            Logger.Log("界面主题切换 " + (light ? "亮色" : "暗色"));
            BeginInvoke((MethodInvoker)delegate { if (!IsDisposed) RebuildUi(); });
        }

        public void SetRuntimeIcon(Icon value)
        {
            if (value == null) return;
            if (InvokeRequired) { Icon copy = (Icon)value.Clone(); BeginInvoke((MethodInvoker)delegate { using (copy) SetRuntimeIcon(copy); }); return; }
            Icon next = (Icon)value.Clone();
            Icon old = appIcon;
            appIcon = next; Icon = next;
            if (old != null) old.Dispose();
        }

        private void RebuildUi()
        {
            CancelOutro();
            if (uiTimer != null) { uiTimer.Stop(); uiTimer.Dispose(); uiTimer = null; }
            uiActive = false;
            uiActivityKnown = false;
            UiClock.Suspended = true;
            if (paviseCore != null) paviseCore.SetAnimationEnabled(false);
            DetachFormFrame();
            var old = new List<Control>();
            int keep = nav != null ? nav.Selected : 0;
            string keepCfg = pageGameConfig != null && !pageGameConfig.IsDisposed
                && pageGameConfig.Visible ? cfgProfileId : null;
            foreach (Control c in Controls) old.Add(c);
            Controls.Clear();
            foreach (var c in old) c.Dispose();
            acGroups.Clear(); acCards.Clear(); acToggles.Clear();
            BuildUi(appIcon);
            nav.Select(keep);
            if (keepCfg != null) ShowGameConfigPage(keepCfg);
            if (UiActive) RefreshSlowStateAsync();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DROPFILES)
            {
                AddDroppedGames(Native.ReadDroppedFiles(m.WParam));
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == Native.WM_ENTERSIZEMOVE) BeginMoveSizeLoop();
            else if (m.Msg == Native.WM_EXITSIZEMOVE) EndMoveSizeLoop();
            base.WndProc(ref m);
        }

        private void ApplyPendingDpiRebuild()
        {
            if (IsDisposed) return;
            int dpi = Dpi.WindowDpi(Handle);
            if (dpi <= 0 || !Dpi.WouldChange(dpi)) return;
            Dpi.Update(dpi);
            Theme.DropFontCache();
            Logger.Log("界面缩放校正后重建 DPI " + dpi);
            RebuildUi();
        }

        private PageHook CurrentPageHook()
        {
            if (pageHooks == null || curPage == null) return null;
            for (int i = 0; i < pageHooks.Length; i++)
                if (pageHooks[i] != null && pageHooks[i].Panel == curPage) return pageHooks[i];
            return null;
        }

        private void BeginMoveSizeLoop()
        {
            if (moveSizeLoop) return;
            moveSizeLoop = true;
            if (introActive)
            {
                introActive = false;
                introPending = false;
                DropLayeredStyle();
            }
            if (uiTimer != null) uiTimer.Stop();
            if (fitTimer != null) fitTimer.Stop();
        }

        private void EndMoveSizeLoop()
        {
            if (!moveSizeLoop) return;
            moveSizeLoop = false;
            if (IsDisposed || !UiActive) return;
            if (uiTimer != null) uiTimer.Start();
            OnUiTick(null, EventArgs.Empty);
            if (fitDeferredByDrag) { fitDeferredByDrag = false; ScheduleFit(); }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            CancelAutoHide();
            outroActive = false;
            outroWatch.Reset();
            if (outroTimer != null) { outroTimer.Stop(); outroTimer.Dispose(); outroTimer = null; }
            if (uiTimer != null) uiTimer.Stop();
            if (fitTimer != null) { fitTimer.Stop(); fitTimer.Dispose(); fitTimer = null; }
            uiActive = false;
            uiActivityKnown = true;
            UiClock.Suspended = true;
            if (paviseCore != null) paviseCore.SetAnimationEnabled(false);
            DetachFormFrame();
            foreach (Bitmap bitmap in gameIconCache.Values) try { bitmap.Dispose(); } catch { }
            gameIconCache.Clear();
            foreach (Bitmap bitmap in whiteIconCache.Values)
                if (bitmap != null) try { bitmap.Dispose(); } catch { }
            whiteIconCache.Clear();
            whiteNameCache.Clear();
            if (appIcon != null) { appIcon.Dispose(); appIcon = null; }
            base.OnHandleDestroyed(e);
        }

        private void DragMove(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, (IntPtr)Native.HT_CAPTION, IntPtr.Zero);
            }
        }

        public void ShowPanel()
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)ShowPanel); return; }
            if (IsDisposed) return;
            CancelOutro();
            ApplyPendingDpiRebuild();
            bool wasVisible = Visible && WindowState != FormWindowState.Minimized;
            if (!wasVisible) BeginIntro();
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            StartIntro();
            SyncUiActivity();
            if (wasVisible) SyncAllToggles();
        }

        public void SyncAllToggles()
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)SyncAllToggles); return; }
            if (IsDisposed || !UiActive) return;
            if (builtLang != Lang.Cur) { RebuildUi(); return; }
            SyncToggleValues();
            RefreshSlowStateAsync();
            RefreshEnvironmentStateAsync();
            RefreshLolColumn();
        }

        private void SyncToggleValues()
        {
            if (gameMode == null || tamer == null) return;
            if (swGame != null) swGame.SetSilently(gameMode.Enabled);
            if (swAcMaster != null) swAcMaster.SetSilently(!tamer.Paused);
            if (swAutoHide != null) swAutoHide.SetSilently(Settings.Load(AutoHideKey, AutoHideDefault));
            if (swPolicyBackground != null) swPolicyBackground.SetSilently(gameMode.SuppressBackground);
            for (int i = 0; i < policySync.Count; i++) policySync[i]();
            if (powerButton != null) powerButton.SetState(gameMode.PowerPlanSwitch, PowerPlanButtonLabel());
            SyncGraphicsToggles();
            SyncEnvironmentToggles();
            UpdateModePresentation(false);
            for (int i = 0; i < acGroups.Count && i < acToggles.Count; i++)
                acToggles[i].SetSilently(tamer.IsGroupEnabled(acGroups[i].Key));
        }

        public void RenderTo(string path, int pageIndex, bool showAntiCheat = false, bool showModePicker = false, string previewMode = null)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-20000, -20000);
            Show();
            if (pageIndex >= 0 && pageIndex < pages.Length)
            {
                nav.Select(pageIndex);
                nav.SnapToSelection();
                pageSlide.Set(0f);
                if (curPage != null) curPage.Left = pageBaseLeft;
            }
            OnUiTick(null, EventArgs.Empty);
            if (showAntiCheat) { nav.Select((int)PageId.AntiCheat); nav.SnapToSelection(); if (curPage != null) curPage.Left = pageBaseLeft; }
            PerformancePreset? preview = previewMode == "competitive" ? PerformancePreset.Competitive
                : previewMode == "custom" ? PerformancePreset.Custom
                : previewMode == "standard" ? PerformancePreset.Standard : (PerformancePreset?)null;
            if (preview.HasValue)
            {
                Theme.SetMode(preview.Value, false);
                modeButton.SetMode(preview.Value); nav.SetMode(preview.Value, true);
                if (lblHeroMode != null) { lblHeroMode.Text = ModeButton.ModeName(preview.Value); lblHeroMode.ForeColor = Theme.Accent; }
                if (paviseCore != null) paviseCore.SetState(preview.Value, true, false);
            }
            if (previewMode == "gameconfig" || previewMode == "gameconfig-core" || previewMode == "gameconfig-gpu")
            {
                List<GameProfile> shotProfiles = gameMode.GetProfiles();
                if (shotProfiles.Count > 0)
                {
                    ShowGameConfigPage(shotProfiles[0].Id);
                    pageSlide.Set(0f);
                    if (curPage != null) curPage.Left = pageBaseLeft;
                    if (previewMode == "gameconfig-core" && cfgTabs != null) cfgTabs.Index = 1;
                    if (previewMode == "gameconfig-gpu" && cfgTabs != null) cfgTabs.Index = 3;
                }
            }
            if (showModePicker && modeButton != null) modeButton.PerformClick();
            if (previewMode == "power" && powerFlyout != null) SetPowerFlyout(true);
            if (previewMode == "search" && searchFlyout != null) SetSearchFlyout(true);
            if (previewMode == "search-hit" && searchFlyout != null)
            {
                SetSearchFlyout(true);
                searchFlyout.SetQuery("后台");
            }
            if (previewMode == "audit" && pageIndex == (int)PageId.Audit)
            {
                try { RenderAudit(SystemAudit.Collect(400)); } catch { }
                if (lblAuditStatus != null) lblAuditStatus.Text = "";
            }
            Application.DoEvents();
            using (var bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
            {
                DrawToBitmap(bmp, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
                if (showModePicker && modeFlyout != null && modeFlyout.Visible)
                    using (var overlay = new Bitmap(modeFlyout.Width, modeFlyout.Height))
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        modeFlyout.DrawToBitmap(overlay, new Rectangle(0, 0, overlay.Width, overlay.Height));
                        g.DrawImageUnscaled(overlay, modeFlyout.Left, modeFlyout.Top);
                    }
                if (searchFlyout != null && searchFlyout.Visible)
                    using (var overlay = new Bitmap(searchFlyout.Width, searchFlyout.Height))
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        searchFlyout.DrawToBitmap(overlay, new Rectangle(0, 0, overlay.Width, overlay.Height));
                        g.DrawImageUnscaled(overlay, searchFlyout.Left, searchFlyout.Top);
                    }
                if (powerFlyout != null && powerFlyout.Visible)
                    using (var overlay = new Bitmap(powerFlyout.Width, powerFlyout.Height))
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        powerFlyout.DrawToBitmap(overlay, new Rectangle(0, 0, overlay.Width, overlay.Height));
                        g.DrawImageUnscaled(overlay, powerFlyout.Left, powerFlyout.Top);
                    }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!RealExit && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.ApplicationExitCall)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }

}

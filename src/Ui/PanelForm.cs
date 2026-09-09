// @author bdth 2074055628@qq.com
// 文件用途 主面板构建 页面注册与页面切换
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
        Interrupt = 11,
        CoreScheduling = 12,
        Count = 13
    }

    internal partial class PanelForm : Form
    {
        // 截图渲染期间置位 离屏窗口不抢焦点 不打扰正在操作的用户
        //   不要在这里改 ShowInTaskbar 会触发句柄重建 图标已被释放会抛异常
        private bool shotMode;

        private readonly Tamer tamer;
        private readonly GameMode gameMode;
        private readonly bool elevated;

        private DBPanel pageOverview, pagePolicy, pageAntiCheat, pageLibrary, pageLog, pageSettings, pageAbout;
        private PictureBox aboutIcon;
        private DBPanel pageGraphics, pageEnvironment, pageWhitelist, pageCoreScheduling;
        private DBPanel[] pages;
        private NavRail nav;
        private AdvancedBackBar advBackBar;
        private ModeButton modeButton;
        private ThemeSwitch themeSwitch;
        private SearchButton searchButton;
        private PowerButton powerButton;
        private PowerFlyout powerFlyout;
        private ModePickerPanel modeFlyout;
        private PerformancePreset visualMode;
        private bool visualEnabled;
        private bool visualActive;
        private string visualPolicySource;
        private bool modeVisualInitialized;
        private Label lblSub;
        private int builtLang;
        private System.Windows.Forms.Timer uiTimer;
        private volatile bool uiActive;
        private bool uiActivityKnown;
        private bool formFrameAttached;
        private DBPanel curPage;
        private int pageBaseLeft;
        private Icon appIcon;
        public bool RealExit;
        public Action ExitApp;
        public Action ResetApp;
        public Action UninstallApp;

        private DBPanel root;
        private System.Windows.Forms.Timer fitTimer;
        private bool fitting;
        private bool moveSizeLoop;
        private bool fitDeferredByDrag;

        private const int WinW = 1220, WinH = 760, RailW = 224, TopH = 64;
        private const int PageW = WinW - RailW, PageH = WinH - TopH;
        private const int ContentX = 30, ContentW = PageW - ContentX * 2;
        private const int ScrollContentW = PageW - 40 - 12 - 20;
        private const string DeepTuningWarningSuppressedKey = "DeepTuningWarningSuppressed";

        public PanelForm(Tamer t, GameMode gm, Icon icon, bool isElevated)
        {
            tamer = t; gameMode = gm; elevated = isElevated; appIcon = (Icon)icon.Clone();
            lastAdvancedPage = LoadLastAdvancedPage();
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
            RegisterPowerSchemeNotifications();
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
            // Rebuild 会复用 PanelForm 实例 新控件得强制拿一份完整的呈现状态
            modeVisualInitialized = false;
            visualPolicySource = null;
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

            BuildNavigation();
            pageTabPanels.Clear();
            tabScrollPositions.Clear();

            var topBar = new WorkspacePanel();
            topBar.SetBounds(Theme.S(RailW), 0, Theme.S(WinW - RailW), Theme.S(TopH));
            topBar.BackColor = Theme.Bg;
            topBar.MouseDown += DragMove;
            topBar.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var p = new Pen(Theme.Stroke)) e.Graphics.DrawLine(p, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
            };

            lblSub = new Label();
            lblSub.Text = elevated ? Lang.T("title.admin") + " " + Lang.T("v20.admin.ready") : Lang.T("title.noelev");
            lblSub.ForeColor = elevated ? Theme.Faint : Theme.Danger;
            lblSub.BackColor = Color.Transparent;
            lblSub.Font = Theme.UI(9f, false);
            lblSub.UseCompatibleTextRendering = false;
            lblSub.TextAlign = ContentAlignment.MiddleLeft;
            lblSub.SetBounds(Theme.S(32), 0, Theme.S(250), Theme.S(TopH));
            lblSub.MouseDown += DragMove;

            modeButton = new ModeButton();
            modeButton.SetBounds(Theme.S(PageW - 334), Theme.S(12), Theme.S(210), Theme.S(46));
            modeButton.Clicked = ToggleModeFlyout;
            modeButton.SetMode(gameMode.ActivePreset);
            modeButton.SetSource(ModeSourceText(gameMode.SessionPolicySourceName, true));

            themeSwitch = new ThemeSwitch(Theme.LightMode);
            themeSwitch.SetBounds(Theme.S(PageW - 442), Theme.S(12), Theme.S(94), Theme.S(46));
            themeSwitch.Toggled = OnThemeToggled;

            searchButton = new SearchButton();
            searchButton.SetBounds(Theme.S(PageW - 502), Theme.S(12), Theme.S(46), Theme.S(46));
            searchButton.Clicked = ToggleSearchFlyout;
            powerButton = new PowerButton();
            powerButton.SetBounds(Theme.S(PageW - 700), Theme.S(12), Theme.S(184), Theme.S(46));
            powerButton.Clicked = TogglePowerFlyout;
            powerButton.SetState(gameMode.PowerPlanSwitch, PowerPlanButtonLabel());

            int tw = Theme.S(WinW - RailW);
            var btnMin = new CaptionButton(false);
            btnMin.SetBounds(tw - Theme.S(92), 0, Theme.S(44), Theme.S(TopH));
            btnMin.Click += (s, e) => WindowState = FormWindowState.Minimized;
            var btnClose = new CaptionButton(true);
            btnClose.SetBounds(tw - Theme.S(48), 0, Theme.S(44), Theme.S(TopH));
            btnClose.Click += (s, e) => BeginOutro();

            topBar.Controls.AddRange(new Control[] { lblSub, powerButton, searchButton, themeSwitch, modeButton, btnMin, btnClose });

            pages = new DBPanel[(int)PageId.Count];
            pages[(int)PageId.Overview] = pageOverview = MakePage();
            pages[(int)PageId.Library] = pageLibrary = MakePage();
            pages[(int)PageId.Whitelist] = pageWhitelist = MakePage();
            pages[(int)PageId.Policy] = pagePolicy = MakePage();
            pages[(int)PageId.CoreScheduling] = pageCoreScheduling = MakePage();
            pages[(int)PageId.AntiCheat] = pageAntiCheat = MakePage();
            pages[(int)PageId.Graphics] = pageGraphics = MakePage();
            pages[(int)PageId.Environment] = pageEnvironment = MakePage();
            pages[(int)PageId.Audit] = pageAudit = MakePage();
            pages[(int)PageId.Interrupt] = pageIrq = MakePage();
            pages[(int)PageId.Log] = pageLog = MakePage();
            pages[(int)PageId.Settings] = pageSettings = MakePage();
            pages[(int)PageId.About] = pageAbout = MakePage();
            BuildOverviewPage();
            BuildLibraryPage();
            BuildWhitelistPage();
            BuildPolicyPage();
            BuildCoreSchedulingPage();
            BuildAntiCheatPage();
            BuildGraphicsPage();
            BuildEnvironmentPage();
            BuildIrqPage();
            BuildGuardVeils();
            BuildAuditPage();
            BuildLogPage();
            BuildSettingsPage();
            BuildAboutPage();
            pageGameConfig = MakePage();
            RegisterPages();

            RegisterThemeRefresh(delegate { if (pageGameConfig != null && pageGameConfig.Visible) SyncCfgRows(); });

            root = new DBPanel();
            root.SetBounds(0, 0, Theme.S(WinW), Theme.S(WinH));
            root.BackColor = Theme.Bg;

            root.Controls.Add(topBar);
            foreach (var p in pages) root.Controls.Add(p);
            root.Controls.Add(pageGameConfig);
            root.Controls.Add(nav);

            root.Controls.Add(tuningNav);

            modeFlyout = new ModePickerPanel();
            // 高度随可见档位数走 极限解锁后多一格
            modeFlyout.SetBounds(Theme.S(WinW - 420), Theme.S(TopH + 8), Theme.S(396),
                Theme.S(66 + PresetValue.VisibleOrder().Length * 70 + 10));
            modeFlyout.Visible = false;
            modeFlyout.ModeChosen = ChooseGlobalMode;
            root.Controls.Add(modeFlyout);
            modeFlyout.BringToFront();

            powerFlyout = new PowerFlyout();
            powerFlyout.SetBounds(Theme.S(WinW - 420), Theme.S(TopH + 8), Theme.S(396), Theme.S(220));
            powerFlyout.Visible = false;
            powerFlyout.Chosen = ChoosePowerPlan;
            root.Controls.Add(powerFlyout);
            powerFlyout.BringToFront();

            searchFlyout = new SearchFlyout();
            searchFlyout.SetBounds(Theme.S(WinW - 420), Theme.S(TopH + 8), Theme.S(396), Theme.S(432));
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
            if (Backdrop.Active) ApplyBackdropLabels(this);

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 1200;
            uiTimer.Tick += OnUiTick;
            uiActivityKnown = false;
            SyncUiActivity();
        }

        // 换图换档后整窗重画 卡片和各控件取的是同一张图的不同块 少刷一个就露馅
        private void RefreshBackdrop()
        {
            StopPageReveal();
            ApplyBackdropLabels(this);
            Invalidate(true);
            Update();
        }

        // 标签自己顶着一块纯色底 有封面时就成了补丁 改成透明交给父控件把图画进来
        private static void ApplyBackdropLabels(Control host)
        {
            if (!Backdrop.AppliesTo(host)) return;
            foreach (Control c in host.Controls)
            {
                var lb = c as Label;
                if (lb != null && lb.BackColor != Color.Transparent) lb.BackColor = Color.Transparent;
                if (c.HasChildren) ApplyBackdropLabels(c);
            }
        }

        private static void AssertNavMatchesPageIds(NavRail rail)
        {
            int expected = (int)PageId.Count;
            if (rail.ItemCount != expected)
                throw new InvalidOperationException(Lang.T("t.panelform.1") + rail.ItemCount + Lang.T("t.panelform.2") + expected + Lang.T("t.panelform.3"));
            var seen = new bool[expected];
            int visible = 0;
            for (int slot = 0; slot < expected; slot++)
            {
                int item = rail.ItemAtSlot(slot);
                if (item < 0) break;
                if (item < 0 || item >= expected) throw new InvalidOperationException(Lang.T("t.panelform.4") + item);
                if (seen[item]) throw new InvalidOperationException(Lang.T("t.panelform.5") + (PageId)item);
                seen[item] = true;
                visible++;
            }
            if (visible == 0) throw new InvalidOperationException(Lang.T("t.panelform.1") + "0");
        }

        private DBPanel MakePage()
        {
            var p = new WorkspacePanel();
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
                delegate(bool active) { if (active) RefreshGames(); },
                delegate { RefreshGameRunningStates(); });
            pageHooks[(int)PageId.Whitelist] = new PageHook(pageWhitelist,
                delegate(bool active) { if (active) RefreshWhitelist(true); }, null);
            pageHooks[(int)PageId.Policy] = new PageHook(pagePolicy,
                delegate(bool active) { if (active) RefreshPolicyPresentation(); },
                RefreshPolicyPresentation);
            pageHooks[(int)PageId.CoreScheduling] = new PageHook(pageCoreScheduling,
                delegate(bool active) { if (active) SyncCorePage(); }, SyncCorePage);
            pageHooks[(int)PageId.AntiCheat] = new PageHook(pageAntiCheat, null, RefreshAcGroupStates);
            pageHooks[(int)PageId.Graphics] = new PageHook(pageGraphics, null, null);
            pageHooks[(int)PageId.Environment] = new PageHook(pageEnvironment,
                delegate(bool active) { if (active) RefreshEnvironmentStateAsync(); }, null);
            pageHooks[(int)PageId.Audit] = new PageHook(pageAudit,
                null, null);
            pageHooks[(int)PageId.Interrupt] = new PageHook(pageIrq,
                delegate(bool active) { if (active) RefreshIrqPage(); }, null);
            pageHooks[(int)PageId.Log] = new PageHook(pageLog,
                delegate(bool active) { if (active) RefreshLog(); }, RefreshLog);
            pageHooks[(int)PageId.Settings] = new PageHook(pageSettings,
                delegate(bool active) { if (active) RefreshSlowStateAsync(); }, null);
            pageHooks[(int)PageId.About] = new PageHook(pageAbout,
                delegate(bool active) { if (active) RefreshAboutIcon(); }, null);
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
            StopPageReveal();
            nav.SnapToSelection();
            tuningNav.SnapToSelection();
            if (curPage != null) curPage.Left = Theme.S(RailW);
        }

        internal void ShowPolicyTabForShot(int index)
        {
            if (policyTabs != null) { policyTabs.Index = index; policyTabs.SnapToSelection(); }
        }

        // 分配与独占已合成一页 截图不再需要切子页 保留入口让调用方不必改
        internal void ShowCoreTabForShot(int index)
        {
        }

        internal ulong SetCoreSelectionForShot(ulong mask)
        {
            if (coreSchedulingPanel == null) return ulong.MaxValue;
            ShowCoreTabForShot(0);
            coreSchedulingPanel.SelectMask(mask);
            SyncCorePage();
            coreSchedulingPanel.Matrix.Refresh();
            return coreSchedulingPanel.Matrix.Selected;
        }

        internal void CommitCoreSelectionForShot()
        {
            if (coreSchedulingPanel != null) coreSchedulingPanel.SaveDraft();
            SyncCorePage();
        }

        private void ShowPage(int index)
        {
            if (index < 0 || index >= pages.Length || pages[index] == null) return;
            SetModeFlyout(false);
            SetSearchFlyout(false);
            SetPowerFlyout(false);
            var page = pages[index];
            // 重复点击已激活项不重新刷新整页 也不重启尚未结束的过渡
            if (curPage == page && page.Visible) return;
            SavePagePosition(curPage);
            pageBaseLeft = Theme.S(RailW);
            page.Left = pageBaseLeft;
            bool revealing = curPage != null && !curPage.IsDisposed && PreparePageReveal(page);
            bool ready = false;
            root.SuspendLayout();
            page.SuspendLayout();
            try
            {
                if (pageGameConfig != null && pageGameConfig.Visible)
                {
                    pageGameConfig.Visible = false;
                    cfgProfileId = null;
                    cfgProfile = null;
                    RefreshGames();
                }
                foreach (var p in pages) p.Visible = (p == page);
                curPage = page;
                bool advanced = IsAdvancedPage(index);
                nav.Visible = !advanced;
                tuningNav.Visible = advanced;
                if (advanced)
                {
                    tuningNav.SelectSilently(index);
                    if (lastAdvancedPage != index)
                    {
                        lastAdvancedPage = index;
                        Settings.SaveStr(LastAdvancedPageKey, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
                else mainReturnPage = index;
                SyncGuardVeils();
                RestorePagePosition(index);
                if (UiActive) UiClock.Wake();
                NotifyPageActivation();
                ready = true;
            }
            finally
            {
                try
                {
                    try { page.ResumeLayout(true); }
                    finally { root.ResumeLayout(true); }
                    if (revealing)
                    {
                        if (ready)
                        {
                            (nav.Visible ? nav : tuningNav).Update();
                            StartPageReveal();
                        }
                        else StopPageReveal();
                    }
                }
                catch { StopPageReveal(); throw; }
            }
        }

    }
}

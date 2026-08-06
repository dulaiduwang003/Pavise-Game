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
        Count = 11
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
        private ModePickerPanel modeFlyout;
        private PerformancePreset visualMode;
        private bool visualEnabled;
        private bool modeVisualInitialized;
        private Motion modeFlyoutMotion;
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

        private Motion introMotion;
        private bool introActive, introPending;
        private int introBaseTop;
        private System.Windows.Forms.Timer autoHideTimer;
        private bool autoHideArmed, lastGameActive;
        private DBPanel root;
        private System.Windows.Forms.Timer fitTimer;
        private bool fitting;

        private const string AutoHideKey = "AutoHideOnGame";
        private const int AutoHideDelayMs = 10000;
        private const int IntroRise = 18;

        private const int WinW = 1196, WinH = 768, RailW = 208, TopH = 54;
        private const int PageW = WinW - RailW, PageH = WinH - TopH;
        private const int ContentX = 26, ContentW = PageW - ContentX * 2;
        private const int ScrollContentW = PageW - 40 - 12 - 20;

        public PanelForm(Tamer t, GameMode gm, Icon icon, bool isElevated)
        {
            tamer = t; gameMode = gm; elevated = isElevated; appIcon = (Icon)icon.Clone();
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
                Dpi.SetDesignSize(WinW, WinH);
                ClientSize = new Size(Theme.S(WinW), Theme.S(WinH));
            }
            BackColor = Theme.Bg;
            Font = Theme.UI(9.5f, false);
            AttachFormFrame();

            nav = new NavRail(
                new[] { Lang.T("nav.overview"), Lang.T("nav.library"), Lang.T("nav.policy"),
                        Lang.T("v14.anticheat"), Lang.T("nav.graphics"), Lang.T("nav.env"), Lang.T("nav.audit"),
                        Lang.T("nav.log"), Lang.T("nav.set"), Lang.T("nav.about"), Lang.T("nav.white") },
                new[] { "game", "white", "settings", "shield", "gpu", "chip", "chart", "log", "gear", "info", "shield" },
                new[] { (int)PageId.Overview, (int)PageId.Library, (int)PageId.Whitelist, (int)PageId.Policy,
                        (int)PageId.AntiCheat, (int)PageId.Log, (int)PageId.Graphics, (int)PageId.Environment,
                        (int)PageId.Audit, (int)PageId.Settings, (int)PageId.About },
                new[] { 6 }, new[] { Lang.T("nav.hardware") }, 2);
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
            lblSub.Text = elevated ? Lang.T("title.admin") + " · " + Lang.T("title.idle") : Lang.T("title.noelev");
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

            int tw = Theme.S(WinW - RailW);
            var btnMin = new CaptionButton(false);
            btnMin.SetBounds(tw - Theme.S(92), 0, Theme.S(44), Theme.S(TopH));
            btnMin.Bg = Theme.Bg;
            btnMin.Click += (s, e) => WindowState = FormWindowState.Minimized;
            var btnClose = new CaptionButton(true);
            btnClose.SetBounds(tw - Theme.S(48), 0, Theme.S(44), Theme.S(TopH));
            btnClose.Bg = Theme.Bg;
            btnClose.Click += (s, e) => Hide();

            topBar.Controls.AddRange(new Control[] { lblSub, themeSwitch, modeButton, btnMin, btnClose });

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
            RegisterPages();

            root = new DBPanel();
            root.SetBounds(0, 0, Theme.S(WinW), Theme.S(WinH));
            root.BackColor = Theme.Bg;

            root.Controls.Add(topBar);
            foreach (var p in pages) root.Controls.Add(p);
            root.Controls.Add(nav);

            modeFlyout = new ModePickerPanel();
            modeFlyout.SetBounds(Theme.S(WinW - 420), Theme.S(56), Theme.S(396), Theme.S(282));
            modeFlyout.Visible = false;
            modeFlyout.ModeChosen = ChooseGlobalMode;
            root.Controls.Add(modeFlyout);
            modeFlyout.BringToFront();

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
                delegate(bool active) { if (active) RefreshGameRunningStates(true); },
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

        private void ShowPage(int index)
        {
            SetModeFlyout(false);
            var page = pages[index];
            foreach (var p in pages) p.Visible = (p == page);
            curPage = page;
            pageBaseLeft = Theme.S(RailW);
            page.Left = pageBaseLeft + Theme.S(16);
            pageSlide.Speed = 0.26f; pageSlide.Set(1f); pageSlide.To(0f);
            if (UiActive) UiClock.Wake();
            NotifyPageActivation();
        }

        private void OnFormFrame(object s, EventArgs e)
        {
            if (Theme.StepTheme())
            {
                if (lblHeroMode != null) lblHeroMode.ForeColor = Theme.Accent;
                if (lblPolicyMode != null) lblPolicyMode.ForeColor = Theme.Accent;
                Invalidate(true);
            }
            if (curPage != null && pageSlide.Step())
                curPage.Left = pageBaseLeft + (int)(pageSlide.Value * Theme.S(16));
            if (modeFlyout != null && modeFlyout.Visible && modeFlyoutMotion.Step())
                modeFlyout.Top = Theme.S(56) + (int)(modeFlyoutMotion.Value * Theme.S(10));
            StepIntro();
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

        private void StepIntro()
        {
            if (!introActive) return;
            if (introMotion.Step())
            {
                Top = introBaseTop + (int)(introMotion.Value * Theme.S(IntroRise));
                Opacity = 1.0 - introMotion.Value;
            }
            else FinishIntro();
        }

        private void FinishIntro()
        {
            introActive = false;
            Top = introBaseTop;
            if (Opacity < 1.0) Opacity = 1.0;
        }

        private void BeginIntro()
        {
            if (introActive) { introActive = false; Top = introBaseTop; }
            introPending = true;
            Opacity = 0.0;
        }

        private void StartIntro()
        {
            if (!introPending) { if (Opacity < 1.0) Opacity = 1.0; return; }
            introPending = false;
            introBaseTop = Top;
            introMotion.Speed = 0.24f;
            introMotion.Set(1f);
            introMotion.To(0f);
            introActive = true;
            Top = introBaseTop + Theme.S(IntroRise);
            PaintTree(this);
            UiClock.Wake(90);
            if (!UiClock.Running) FinishIntro();
        }

        private static void PaintTree(Control c)
        {
            if (!c.IsHandleCreated || !c.Visible) return;
            c.Update();
            for (int i = 0; i < c.Controls.Count; i++) PaintTree(c.Controls[i]);
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
            if (WindowState == FormWindowState.Minimized) return;
            int w = ClientSize.Width, h = ClientSize.Height;
            if (!Dpi.FitDiffers(w, h)) return;
            float target = Dpi.FitScale(w, h);
            fitting = true;
            try
            {
                Dpi.Scale = target;
                Theme.DropFontCache();
                Logger.Log("界面按窗口尺寸重排：客户区 " + w + "x" + h
                    + "，缩放 " + target.ToString("F2"));
                RebuildUi();
            }
            finally { fitting = false; }
            CenterRoot();
        }

        internal bool UiActive
        {
            get { return uiActive; }
        }

        internal bool UiTimerEnabled
        {
            get { return uiTimer != null && uiTimer.Enabled; }
        }

        internal static bool ShouldRunUi(bool visible, FormWindowState windowState)
        {
            return visible && windowState != FormWindowState.Minimized;
        }

        internal static void SyncAutoHideBaseline(bool gameActive, ref bool lastActive, ref bool armed)
        {
            lastActive = gameActive;
            armed = gameActive;
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
            if (statusDot != null)
            {
                statusDot.Color = !gameMode.Enabled ? Theme.Dim : (act ? Theme.Green : Theme.Accent);
                statusDot.Pulse = act;
            }
            if (paviseCore != null) paviseCore.SetState(gameMode.ActivePreset, gameMode.Enabled, act);
            if (lblSub != null && elevated)
            {
                string game = gameMode.ActiveGame;
                string state = Lang.T("title.admin") + " · "
                    + (game != null ? Lang.F("title.guard", game) : Lang.T("title.idle"));
                if (lblSub.Text != state) lblSub.Text = state;
                lblSub.ForeColor = game != null ? Theme.Green : Theme.Faint;
            }
            RefreshBoostPresentation();
        }

        private void ToggleModeFlyout()
        {
            SetModeFlyout(modeFlyout == null || !modeFlyout.Visible);
        }

        private void SetModeFlyout(bool visible)
        {
            if (modeFlyout == null) return;
            if (visible) modeFlyout.Sync(gameMode.Preset);
            modeFlyout.Visible = visible;
            if (visible)
            {
                modeFlyoutMotion.Speed = 0.24f; modeFlyoutMotion.Set(-1f); modeFlyoutMotion.To(0f);
                modeFlyout.BringToFront(); UiClock.Wake();
            }
        }

        private void ChooseGlobalMode(PerformancePreset mode)
        {
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
            if (lblHeroSource != null) lblHeroSource.Text = Lang.T("mode.source.global");
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
        }

        private void OnThemeToggled(bool light)
        {
            Settings.Save("UiLight", light);
            Theme.SetLight(light);
            Logger.Log("界面主题切换：" + (light ? "亮色" : "暗色"));
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
            if (uiTimer != null) { uiTimer.Stop(); uiTimer.Dispose(); uiTimer = null; }
            uiActive = false;
            uiActivityKnown = false;
            UiClock.Suspended = true;
            if (paviseCore != null) paviseCore.SetAnimationEnabled(false);
            DetachFormFrame();
            var old = new List<Control>();
            int keep = nav != null ? nav.Selected : 0;
            foreach (Control c in Controls) old.Add(c);
            Controls.Clear();
            foreach (var c in old) c.Dispose();
            acGroups.Clear(); acCards.Clear(); acToggles.Clear();
            BuildUi(appIcon);
            nav.Select(keep);
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
            base.WndProc(ref m);
        }

        private void ApplyPendingDpiRebuild()
        {
            if (IsDisposed) return;
            int dpi = Dpi.WindowDpi(Handle);
            if (dpi <= 0 || !Dpi.WouldChange(dpi)) return;
            Dpi.Update(dpi);
            Theme.DropFontCache();
            Logger.Log("界面缩放校正后重建：DPI " + dpi);
            RebuildUi();
        }


        private PageHook CurrentPageHook()
        {
            if (pageHooks == null || curPage == null) return null;
            for (int i = 0; i < pageHooks.Length; i++)
                if (pageHooks[i] != null && pageHooks[i].Panel == curPage) return pageHooks[i];
            return null;
        }

        private void OnUiTick(object s, EventArgs e)
        {
            if (!UiActive) return;
            UpdateAutoHide(gameMode.Enabled && gameMode.IsActive);
            RefreshLightweightUiState();
            UpdateModePresentation(true);
            PageHook hook = CurrentPageHook();
            if (hook != null && hook.OnTick != null) hook.OnTick();
        }

        internal static AutoHideAction NextAutoHide(bool gameActive, ref bool lastActive, ref bool armed,
            bool settingOn, bool visible)
        {
            if (gameActive == lastActive) return AutoHideAction.None;
            lastActive = gameActive;
            if (!gameActive) { armed = false; return AutoHideAction.Cancel; }
            if (armed) return AutoHideAction.None;
            armed = true;
            if (!settingOn || !visible) return AutoHideAction.None;
            return AutoHideAction.Schedule;
        }

        private void UpdateAutoHide(bool gameActive)
        {
            AutoHideAction action = NextAutoHide(gameActive, ref lastGameActive, ref autoHideArmed,
                Settings.Load(AutoHideKey, false), UiActive);
            if (action == AutoHideAction.Cancel) { CancelAutoHide(); return; }
            if (action != AutoHideAction.Schedule) return;
            CancelAutoHide();
            autoHideTimer = new System.Windows.Forms.Timer();
            autoHideTimer.Interval = AutoHideDelayMs;
            autoHideTimer.Tick += OnAutoHideTick;
            autoHideTimer.Start();
        }

        private void OnAutoHideTick(object s, EventArgs e)
        {
            CancelAutoHide();
            if (IsDisposed || !UiActive) return;
            if (AnyDialogOpen()) return;
            Hide();
        }

        private void CancelAutoHide()
        {
            if (autoHideTimer == null) return;
            autoHideTimer.Stop();
            autoHideTimer.Tick -= OnAutoHideTick;
            autoHideTimer.Dispose();
            autoHideTimer = null;
        }

        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);

        private bool AnyDialogOpen()
        {
            try
            {
                if (IsHandleCreated && !IsWindowEnabled(Handle)) return true;
                foreach (Form f in Application.OpenForms)
                    if (!ReferenceEquals(f, this) && f.Visible) return true;
            }
            catch { }
            return false;
        }

        private void OnAutoHideToggle(object s, EventArgs e)
        {
            Settings.Save(AutoHideKey, swAutoHide.Checked);
            if (!swAutoHide.Checked) CancelAutoHide();
            swAutoHide.SetSilently(Settings.Load(AutoHideKey, false));
        }

        private void OnEscHide(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                if (modeFlyout != null && modeFlyout.Visible) SetModeFlyout(false);
                else Hide();
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            CancelAutoHide();
            if (uiTimer != null) uiTimer.Stop();
            if (fitTimer != null) { fitTimer.Stop(); fitTimer.Dispose(); fitTimer = null; }
            uiActive = false;
            uiActivityKnown = true;
            UiClock.Suspended = true;
            if (paviseCore != null) paviseCore.SetAnimationEnabled(false);
            DetachFormFrame();
            foreach (Bitmap bitmap in gameIconCache.Values) try { bitmap.Dispose(); } catch { }
            gameIconCache.Clear();
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
        }

        private void SyncToggleValues()
        {
            if (gameMode == null || tamer == null) return;
            if (swGame != null) swGame.SetSilently(gameMode.Enabled);
            if (swAcMaster != null) swAcMaster.SetSilently(!tamer.Paused);
            if (swAutoHide != null) swAutoHide.SetSilently(Settings.Load(AutoHideKey, false));
            if (swPolicyBackground != null) swPolicyBackground.SetSilently(gameMode.SuppressBackground);
            for (int i = 0; i < policySync.Count; i++) policySync[i]();
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
            if (showModePicker && modeButton != null) modeButton.PerformClick();
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

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

namespace AegisApp
{
    internal enum AutoHideAction { None, Schedule, Cancel }

    // 导航项与 pages 数组共用这一套序号；新增页面必须同时在 BuildUi 的三个并列数组里补位
    internal enum PageId
    {
        Overview = 0,
        League = 1,
        Library = 2,
        Policy = 3,
        AntiCheat = 4,
        Graphics = 5,
        Environment = 6,
        Reports = 7,
        Settings = 8,
        About = 9,
        Count = 10
    }

    internal partial class PanelForm : Form
    {
        private readonly Tamer tamer;
        private readonly GameMode gameMode;
        private readonly bool elevated;

        // 各页自己的控件字段声明在对应的 Pages\PanelForm.*Page.cs 里
        private DBPanel pageOverview, pagePolicy, pageAntiCheat, pageLibrary, pageReports, pageSettings, pageAbout;
        private DBPanel pageGraphics, pageEnvironment;
        private DBPanel[] pages;
        private NavRail nav;
        private ModeButton modeButton;
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

        private const string AutoHideKey = "AutoHideOnGame";
        private const int AutoHideDelayMs = 10000;
        private const int IntroRise = 18;

        private const int WinW = 1040, WinH = 720, RailW = 208, TopH = 54;
        private const int PageW = WinW - RailW, PageH = WinH - TopH;
        private const int ContentX = 26, ContentW = PageW - ContentX * 2;
        private const int ScrollContentW = ContentW - 24;

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
            // 启动时用的是系统 DPI；窗口若开在缩放不同的另一块屏上这里会不一致。
            // 不在句柄刚创建时重建（那会在构造途中拆掉刚建好的控件），只记下目标 DPI，
            // 由 ShowPanel 在真正要显示之前连同重建一起处理——这里同样不能只改
            // Dpi.Scale 就走，那会让整个界面按旧缩放建、按新缩放画。
            int handleDpi = Dpi.WindowDpi(Handle);
            if (handleDpi > 0 && Dpi.WouldChange(handleDpi)) pendingDpi = handleDpi;
            uiActivityKnown = false;
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
            ClientSize = new Size(Theme.S(WinW), Theme.S(WinH));
            BackColor = Theme.Bg;
            Font = Theme.UI(9.5f, false);
            AttachFormFrame();

            // 前两个数组按 PageId 顺序排列；第三个是视觉排序，显卡/系统环境与英雄联盟各自成组
            nav = new NavRail(
                new[] { Lang.T("nav.overview"), LolText("英雄联盟"), Lang.T("nav.library"), Lang.T("nav.policy"),
                        Lang.T("v14.anticheat"), Lang.T("nav.graphics"), Lang.T("nav.env"), Lang.T("nav.reports"),
                        Lang.T("nav.set"), Lang.T("nav.about") },
                new[] { "game", "lol", "white", "settings", "shield", "gpu", "chip", "log", "gear", "info" },
                new[] { (int)PageId.Overview, (int)PageId.Library, (int)PageId.Policy, (int)PageId.AntiCheat,
                        (int)PageId.Reports, (int)PageId.Graphics, (int)PageId.Environment, (int)PageId.League,
                        (int)PageId.Settings, (int)PageId.About },
                new[] { 5, 7 }, new[] { Lang.T("nav.hardware"), Lang.T("nav.columns") }, 2);
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

            int tw = Theme.S(WinW - RailW);
            var btnMin = new CaptionButton(false);
            btnMin.SetBounds(tw - Theme.S(92), 0, Theme.S(44), Theme.S(TopH));
            btnMin.Bg = Theme.Bg;
            btnMin.Click += (s, e) => WindowState = FormWindowState.Minimized;
            var btnClose = new CaptionButton(true);
            btnClose.SetBounds(tw - Theme.S(48), 0, Theme.S(44), Theme.S(TopH));
            btnClose.Bg = Theme.Bg;
            btnClose.Click += (s, e) => Hide();

            topBar.Controls.AddRange(new Control[] { lblSub, modeButton, btnMin, btnClose });

            pages = new DBPanel[(int)PageId.Count];
            pages[(int)PageId.Overview] = pageOverview = MakePage();
            pages[(int)PageId.League] = pageLol = MakePage();
            pages[(int)PageId.Library] = pageLibrary = MakePage();
            pages[(int)PageId.Policy] = pagePolicy = MakePage();
            pages[(int)PageId.AntiCheat] = pageAntiCheat = MakePage();
            pages[(int)PageId.Graphics] = pageGraphics = MakePage();
            pages[(int)PageId.Environment] = pageEnvironment = MakePage();
            pages[(int)PageId.Reports] = pageReports = MakePage();
            pages[(int)PageId.Settings] = pageSettings = MakePage();
            pages[(int)PageId.About] = pageAbout = MakePage();
            BuildOverviewPage();
            BuildLolPage();
            BuildLibraryPage();
            BuildPolicyPage();
            BuildAntiCheatPage();
            BuildGraphicsPage();
            BuildEnvironmentPage();
            BuildReportsPage();
            BuildSettingsPage();
            BuildAboutPage();
            RegisterPages();

            Controls.Add(topBar);
            foreach (var p in pages) Controls.Add(p);
            Controls.Add(nav);

            modeFlyout = new ModePickerPanel();
            modeFlyout.SetBounds(Theme.S(WinW - 420), Theme.S(56), Theme.S(396), Theme.S(282));
            modeFlyout.Visible = false;
            modeFlyout.ModeChosen = ChooseGlobalMode;
            Controls.Add(modeFlyout);
            modeFlyout.BringToFront();

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

        // 标题/图标数组必须按 PageId 顺序逐一对应，视觉排序必须恰好是每个 PageId 各出现一次。
        // 漏一项在运行时只会表现成点错页面，所以在这里直接炸掉，自测构造窗体时就能撞上。
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

        // 页面钩子：换页与休眠/唤醒时每页各被告知一次自己是否当前活动页，
        // 活动页另外按 UI 心跳收到 OnTick。新增页面只在 RegisterPages 里补一行。
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
                delegate(bool active) { if (aegisCore != null) aegisCore.SetAnimationEnabled(active); }, null);
            pageHooks[(int)PageId.League] = new PageHook(pageLol,
                delegate(bool active) { if (active) RefreshLolPage(); }, null);
            pageHooks[(int)PageId.Library] = new PageHook(pageLibrary,
                delegate(bool active) { if (active) RefreshGameRunningStates(true); },
                delegate { RefreshGameRunningStates(); });
            pageHooks[(int)PageId.Policy] = new PageHook(pagePolicy, null, null);
            pageHooks[(int)PageId.AntiCheat] = new PageHook(pageAntiCheat, null, RefreshAcGroupStates);
            pageHooks[(int)PageId.Graphics] = new PageHook(pageGraphics, null, null);
            pageHooks[(int)PageId.Environment] = new PageHook(pageEnvironment,
                delegate(bool active) { if (active) RefreshEnvironmentStateAsync(); }, null);
            pageHooks[(int)PageId.Reports] = new PageHook(pageReports,
                delegate(bool active) { if (active) RefreshReports(); }, RefreshReports);
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
                double shown = 1d - introMotion.Value;
                if (shown < 0d) shown = 0d; else if (shown > 1d) shown = 1d;
                try { Opacity = shown; } catch { }
                Top = introBaseTop + (int)(introMotion.Value * Theme.S(IntroRise));
            }
            else
            {
                introActive = false;
                try { Opacity = 1d; } catch { }
                Top = introBaseTop;
            }
        }

        private void BeginIntro()
        {

            if (introActive) { introActive = false; Top = introBaseTop; }
            introPending = true;
            try { Opacity = 0d; } catch { }
        }

        private void StartIntro()
        {
            if (!introPending) return;
            introPending = false;
            introBaseTop = Top;
            introMotion.Speed = 0.24f;
            introMotion.Set(1f);
            introMotion.To(0f);
            introActive = true;
            Top = introBaseTop + Theme.S(IntroRise);
            UiClock.Wake(90);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            SyncUiActivity();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SyncUiActivity();
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
                if (aegisCore != null) aegisCore.SetAnimationEnabled(false);
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
            if (aegisCore != null) aegisCore.SetState(gameMode.ActivePreset, gameMode.Enabled, act);
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
            if (aegisCore != null) aegisCore.SetState(effective, enabled, gameMode.IsActive);
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
            if (aegisCore != null) aegisCore.SetAnimationEnabled(false);
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

        private const int WM_DPICHANGED = 0x02E0;
        private const int DpiRebuildCooldownMs = 3000;
        // 重试要落在冷却窗口之外，否则第一次重试必然又判成"距上次重建过近"
        private const int DpiRetryMs = 3200;
        // 待重建的目标 DPI；0 表示没有积压。存 DPI 而不是 bool，是因为推迟时
        // 一律不动 Dpi.Scale，真正要重建时还得知道目标值是多少。
        private int pendingDpi;
        private int dpiHandling;
        private int lastDpiRebuildTicks;
        private bool haveRebuiltForDpi;
        private System.Windows.Forms.Timer dpiRetry;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DROPFILES)
            {
                AddDroppedGames(Native.ReadDroppedFiles(m.WParam));
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_DPICHANGED)
            {
                ApplyDpiChange(m.WParam, m.LParam);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        // 推迟重建的理由。抽成静态纯函数是为了能自测——这一段的分支组合正是
        // 整个 DPI 处理里唯一有风险的地方，靠界面没法覆盖。
        internal static string DpiDeferReason(bool disposed, bool visible, bool minimized,
            bool gameActive, bool withinCooldown)
        {
            if (disposed) return "面板已销毁";
            if (!visible || minimized) return "面板不可见";
            // 对局期间即使面板开着也不能重建：游戏切换全屏会改变有效 DPI，
            // 在这里动窗口等于反复把游戏挤出全屏。
            if (gameActive) return "对局进行中";
            // 兜底：拖窗过屏是一次性的用户动作，几秒内连续到达只可能是反馈震荡
            if (withinCooldown) return "距上次重建过近";
            return null;
        }

        // Environment.TickCount 每约 49.7 天回绕成负数，补码减法在回绕处依然给出
        // 正确的间隔，所以只能用差值比较，不能直接比大小。用它而不是 DateTime 是
        // 因为系统时间被 NTP 或用户往前拨会让墙上时钟的差值变负，那会把冷却判成
        // 永远成立，界面就再也不重建了。
        internal static bool WithinCooldown(int now, int last, bool everRebuilt, int cooldownMs)
        {
            if (!everRebuilt) return false;
            int elapsed = unchecked(now - last);
            return elapsed >= 0 && elapsed < cooldownMs;
        }

        private string CurrentDeferReason()
        {
            return DpiDeferReason(IsDisposed, Visible,
                WindowState == FormWindowState.Minimized,
                gameMode != null && gameMode.Enabled && gameMode.IsActive,
                WithinCooldown(Environment.TickCount, lastDpiRebuildTicks,
                    haveRebuiltForDpi, DpiRebuildCooldownMs));
        }

        // PerMonitorV2 下切换显示器或改缩放时，系统只按新 DPI 放大顶层窗口，
        // 子控件的像素坐标仍是用旧缩放算死的，于是内容缩在左上角、其余是空背景。
        //
        // 但重建绝不能无条件立刻做。重建会改变窗口尺寸和位置，跨到另一块缩放不同的屏上
        // 就会再收到一条 WM_DPICHANGED，两块屏之间来回弹、每次都整窗重建；而游戏切换全屏
        // 本身就会改变有效 DPI，于是在对局期间反复把游戏挤出全屏。实测 45 秒内弹了 10 次。
        //
        // 关键是：推迟必须是"什么都没发生"。改了 Dpi.Scale 或丢了字体缓存却不重建，
        // 界面就停在布局旧、自绘文字新的混排状态，而且没有任何东西会来纠正它——
        // 实测过一次，导航栏字号放大到只剩四项、模式下拉框文字溢出红框。
        private void ApplyDpiChange(IntPtr wParam, IntPtr lParam)
        {
            int dpi = (int)((uint)wParam.ToInt64() & 0xFFFF);
            if (dpi <= 0) return;
            if (Interlocked.Exchange(ref dpiHandling, 1) == 1) return;
            try
            {
                if (!Dpi.WouldChange(dpi)) return;
                string defer = CurrentDeferReason();
                if (defer != null)
                {
                    if (pendingDpi != dpi)
                        Logger.Log("界面缩放已变化（DPI " + dpi + "），" + defer + "，推迟重建");
                    pendingDpi = dpi;
                    ScheduleDpiRetry();
                    return;
                }
                RebuildForDpi(dpi);
            }
            finally { Interlocked.Exchange(ref dpiHandling, 0); }
        }

        // 改缩放的唯一入口：Scale、字体缓存和整窗重建必须一起发生，分开就是混排。
        // 不移动窗口：系统在发这条消息前已经把窗口摆到新位置了，我们只需要按新缩放
        // 重设自己的尺寸并重建内容。再去写 Location 会把窗口推向另一块屏，
        // 正是之前来回震荡的起因。
        private void RebuildForDpi(int dpi)
        {
            Dpi.Update(dpi);
            Theme.DropFontCache();
            pendingDpi = 0;
            lastDpiRebuildTicks = Environment.TickCount;
            haveRebuiltForDpi = true;
            Logger.Log("界面缩放随显示器变化重建：DPI " + dpi);
            RebuildUi();
        }

        // 推迟的理由迟早会自己消失（冷却到期、对局结束），但没有任何外部事件会来
        // 通知，而面板已经显示着的话 ShowPanel 也不会再被调用——不自己排一次重试，
        // 积压的缩放就永远落不了地。重试里重新判断，条件还不满足就再排一次。
        private void ScheduleDpiRetry()
        {
            if (IsDisposed || pendingDpi <= 0) return;
            if (dpiRetry == null)
            {
                dpiRetry = new System.Windows.Forms.Timer();
                dpiRetry.Interval = DpiRetryMs;
                dpiRetry.Tick += OnDpiRetryTick;
            }
            dpiRetry.Stop();
            dpiRetry.Start();
        }

        private void OnDpiRetryTick(object sender, EventArgs e)
        {
            if (dpiRetry != null) dpiRetry.Stop();
            if (IsDisposed) return;
            int dpi = pendingDpi;
            if (dpi <= 0) return;
            // 期间可能已经被别的路径重建过（比如用户重开了面板），那就没得可做
            if (!Dpi.WouldChange(dpi)) { pendingDpi = 0; return; }
            if (CurrentDeferReason() != null) { ScheduleDpiRetry(); return; }
            RebuildForDpi(dpi);
        }

        // 面板即将显示：先按窗口当前所在的显示器校正，再把积压的缩放落地。
        // 这条路径不看可见性和冷却——用户主动打开面板本身就要重建窗口内容，
        // 而且此刻 Show() 还没执行，用可见性判断会把自己永久挡在门外。
        private void ApplyPendingDpiRebuild()
        {
            if (IsDisposed) return;
            int dpi = Dpi.WindowDpi(Handle);
            if (dpi > 0 && Dpi.WouldChange(dpi)) pendingDpi = dpi;
            if (pendingDpi <= 0) return;
            RebuildForDpi(pendingDpi);
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
            if (dpiRetry != null) { dpiRetry.Stop(); dpiRetry.Dispose(); dpiRetry = null; }
            uiActive = false;
            uiActivityKnown = true;
            UiClock.Suspended = true;
            if (aegisCore != null) aegisCore.SetAnimationEnabled(false);
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
            RefreshLolPage();
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
                if (aegisCore != null) aegisCore.SetState(preview.Value, true, false);
            }
            if (showModePicker && modeButton != null) modeButton.PerformClick();
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

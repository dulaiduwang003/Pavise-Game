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

    internal partial class PanelForm : Form
    {
        private readonly Tamer tamer;
        private readonly GameMode gameMode;
        private readonly bool elevated;

        private DBPanel pageGame, pageTame, pageAnti, pageWhite, pageReports, pageSettings, pageAbout;
        private DBPanel[] pages;
        private DBPanel tameList;
        private NavRail nav;
        private Toggle swGame, swTame, swAuto, swGpu, swFso, swVbs, swHags, swIrqAffinity, swNetAffinity, swUsbAffinity, swMpo;
        private Label lblOverviewBoost, lblEvidenceLive;
        private Label lblHeroMode, lblHeroSource, lblPolicyMode;
        private Toggle swPolicyBackground, swPolicyStrict;
        private Toggle swPolicyNet, swPolicyFg, swPolicyMmcss, swPolicyPauseDl, swPolicyPauseSvc, swPolicyDvr;
        private Toggle swPolicyAggressive;
        private ModeButton modeButton;
        private ModePickerPanel modeFlyout;
        private PerformancePreset visualMode;
        private bool visualEnabled;
        private bool modeVisualInitialized;
        private Motion modeFlyoutMotion;
        private TextBox tbReports;
        private ListBox lstGames;
        private EmptyStatePanel gameListPanel;
        private readonly Dictionary<string, Bitmap> gameIconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private Label lblStatus;
        private Label lblSub;
        private SettingCard cardVbs;
        private SettingCard cardShader;
        private SettingCard cardPolicyStrict, cardPolicyNet, cardPolicyFg, cardPolicyMmcss;
        private SettingCard cardPolicyPauseDl, cardPolicyPauseSvc, cardPolicyDvr;
        private SettingCard cardPolicyAggressive;
        private readonly List<Action> policySync = new List<Action>();

        private static volatile bool shaderCleaning;
        private int slowBusy;
        private int restoreBusy;
        private int runningBusy;
        private long nextRunningProbeTicks;
        private int netQosBusy;
        private string netQosSignature;
        private static readonly object netQosSync = new object();
        private int builtLang;
        private StatusDot statusDot;
        private AegisCore aegisCore;
        private readonly List<AcGroup> tameGroups = new List<AcGroup>();
        private readonly List<SettingCard> tameCards = new List<SettingCard>();
        private readonly List<Toggle> tameToggles = new List<Toggle>();
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
        private Toggle swAutoHide;
        private System.Windows.Forms.Timer autoHideTimer;
        private bool autoHideArmed, lastGameActive;

        private const string AutoHideKey = "AutoHideOnGame";
        private const int AutoHideDelayMs = 10000;
        private const int IntroRise = 18;
        private static readonly long RunningProbeIntervalTicks = TimeSpan.FromSeconds(5).Ticks;

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
                // WS_EX_ACCEPTFILES 必须进 CreateParams：手动 DragAcceptFiles 设的位
                // 会被 WinForms 后续按 CreateParams 重写 ExStyle 时抹掉
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

            nav = new NavRail(
                new[] { Lang.T("nav.overview"), LolText("英雄联盟"), Lang.T("nav.library"), Lang.T("nav.policy"), Lang.T("v14.anticheat"), Lang.T("nav.reports"), Lang.T("nav.set"), Lang.T("nav.about") },
                new[] { "game", "lol", "white", "settings", "shield", "log", "gear", "info" },
                new[] { 0, 2, 3, 4, 5, 1, 6, 7 }, 5, Lang.T("nav.columns"), 2);
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

            pageGame = MakePage();
            pageTame = MakePage();
            pageWhite = MakePage();
            pageReports = MakePage();
            pageSettings = MakePage();
            pageAbout = MakePage();
            pageAnti = MakePage();
            pageLol = MakePage();
            pages = new[] { pageGame, pageLol, pageWhite, pageTame, pageAnti, pageReports, pageSettings, pageAbout };
            BuildOverviewPageV14();
            BuildLolPage();
            BuildLibraryPageV14();
            BuildPolicyPageV14();
            BuildAntiCheatPageV14();
            BuildReportsPageV14();
            BuildSettingsPage();
            BuildAboutPage();

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

            nav.Select(0);

            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 1200;
            uiTimer.Tick += OnUiTick;
            uiActivityKnown = false;
            SyncUiActivity();
        }

        private DBPanel MakePage()
        {
            var p = new DBPanel();
            p.SetBounds(Theme.S(RailW), Theme.S(TopH), Theme.S(WinW - RailW), Theme.S(WinH - TopH));
            p.BackColor = Theme.Bg;
            p.Visible = false;
            return p;
        }

        private static void OwnedImage(PictureBox pb, Image img)
        {
            pb.Image = img;
            pb.Disposed += delegate { try { img.Dispose(); } catch { } };
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
            if (aegisCore != null) aegisCore.SetAnimationEnabled(UiActive && page == pageGame);
            if (UiActive && page == pageLol) RefreshLolPage();
            if (UiActive && page == pageWhite) RefreshGameRunningStates(true);
            if (UiActive && page == pageReports) RefreshReportsV14();
            if (UiActive && page == pageSettings) RefreshSlowStateAsync();
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
            if (curPage == pageLol) RefreshLolPage();

            UiClock.Suspended = false;
            if (aegisCore != null) aegisCore.SetAnimationEnabled(curPage == pageGame);
            if (uiTimer != null) uiTimer.Start();
            UiClock.Wake();
            UiClock.WakeSlow();
            if (curPage == pageSettings) RefreshSlowStateAsync();
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
            tameGroups.Clear(); tameCards.Clear(); tameToggles.Clear();
            BuildUi(appIcon);
            nav.Select(keep);
            if (UiActive) RefreshSlowStateAsync();
        }

        private void OnAutoToggle(object s, EventArgs e)
        {
            int rc = swAuto.Checked ? TaskHelper.CreateStartupTask() : TaskHelper.DeleteStartupTask();
            if (rc != 0)
            {
                MessageBox.Show(this, Lang.T("msg.taskfail"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                swAuto.SetSilently(TaskHelper.TaskExists());
            }
        }

        private void OnHagsToggle(object s, EventArgs e)
        {
            if (!elevated)
            {
                MessageBox.Show(this, Lang.T("vbs.needadmin"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                swHags.SetSilently(HagsTweak.EnabledByAegis || HagsTweak.CurrentlyOn());
                return;
            }
            bool ok = swHags.Checked ? HagsTweak.Enable() : HagsTweak.Disable();
            if (ok) MessageBox.Show(this, Lang.T("hags.reboot"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swHags.SetSilently(HagsTweak.EnabledByAegis || HagsTweak.CurrentlyOn());
        }

        private void OnUsbAffinityToggle(object s, EventArgs e)
        {
            if (!elevated)
            {
                MessageBox.Show(this, Lang.T("vbs.needadmin"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                swUsbAffinity.SetSilently(UsbInterruptAffinityTweak.EnabledByAegis);
                return;
            }
            bool ok = swUsbAffinity.Checked ? UsbInterruptAffinityTweak.Enable() : UsbInterruptAffinityTweak.Disable();
            if (ok) MessageBox.Show(this, Lang.T("irqaffinity.reboot"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swUsbAffinity.SetSilently(UsbInterruptAffinityTweak.EnabledByAegis);
        }

        private void OnMpoToggle(object s, EventArgs e)
        {
            if (!elevated)
            {
                MessageBox.Show(this, Lang.T("vbs.needadmin"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                swMpo.SetSilently(MpoTweak.DisabledByAegis || MpoTweak.CurrentlyDisabled());
                return;
            }
            bool ok = swMpo.Checked ? MpoTweak.Disable() : MpoTweak.Restore();
            if (ok) MessageBox.Show(this, Lang.T("mpo.reboot"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swMpo.SetSilently(MpoTweak.DisabledByAegis || MpoTweak.CurrentlyDisabled());
        }

        private void OnIrqAffinityToggle(object s, EventArgs e)
        {
            if (!elevated)
            {
                MessageBox.Show(this, Lang.T("vbs.needadmin"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                swIrqAffinity.SetSilently(InterruptAffinityTweak.EnabledByAegis);
                return;
            }
            bool ok = swIrqAffinity.Checked ? InterruptAffinityTweak.Enable() : InterruptAffinityTweak.Disable();
            if (ok) MessageBox.Show(this, Lang.T("irqaffinity.reboot"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swIrqAffinity.SetSilently(InterruptAffinityTweak.EnabledByAegis);
        }

        private void OnNetAffinityToggle(object s, EventArgs e)
        {
            if (!elevated)
            {
                MessageBox.Show(this, Lang.T("vbs.needadmin"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                swNetAffinity.SetSilently(NetworkAffinityTweak.EnabledByAegis);
                return;
            }
            bool ok;
            lock (netQosSync)
            {
                ok = swNetAffinity.Checked
                    ? NetworkAffinityTweak.Enable(gameMode.GetProfiles())
                    : NetworkAffinityTweak.Disable();
            }
            if (ok) MessageBox.Show(this, Lang.T("netaffinity.reboot"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            swNetAffinity.SetSilently(NetworkAffinityTweak.EnabledByAegis);
        }

        private void OnVbsToggle(object s, EventArgs e)
        {
            if (swVbs.Checked)
            {
                if (!elevated)
                {
                    MessageBox.Show(this, Lang.T("vbs.needadmin"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    swVbs.SetSilently(false); return;
                }
                var r = MessageBox.Show(this, Lang.T("vbs.warn"), "Aegis", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                if (r != DialogResult.OK || !VbsTweak.Disable())
                {
                    swVbs.SetSilently(false); RefreshVbsState(); return;
                }
                RefreshVbsState();
                MessageBox.Show(this, Lang.T("vbs.done"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                if (!elevated)
                {
                    MessageBox.Show(this, Lang.T("vbs.needadmin"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    swVbs.SetSilently(true); return;
                }
                if (!VbsTweak.Restore())
                {
                    swVbs.SetSilently(VbsTweak.DisabledByAegis);
                    RefreshVbsState();
                    MessageBox.Show(this, Lang.T("vbs.restorefail"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                RefreshVbsState();
                MessageBox.Show(this, Lang.T("vbs.restored"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void OnShaderClean(PillButton btn)
        {
            if (shaderCleaning)
            {
                if (cardShader != null) cardShader.Value = Lang.T("shader.busy");
                return;
            }
            if (MessageBox.Show(this, Lang.T("shader.confirm"), "Aegis", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            btn.Enabled = false;
            shaderCleaning = true;
            if (cardShader != null) cardShader.Value = Lang.T("shader.busy");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                CacheSweep.Result cr = ShaderCache.Clean();
                long left = ShaderCache.MeasureBytes();
                Logger.Log("着色器缓存清理：释放 " + CacheSweep.FmtBytes(cr.FreedBytes)
                    + (cr.FailedFiles > 0 ? "，" + cr.FailedFiles + " 个文件被占用已跳过" : ""));
                shaderCleaning = false;
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (IsDisposed) return;
                        if (!btn.IsDisposed) btn.Enabled = true;
                        if (cardShader != null && !cardShader.IsDisposed)
                            cardShader.Value = CacheSweep.FmtBytes(left);
                        string msg = Lang.F("shader.freed", CacheSweep.FmtBytes(cr.FreedBytes))
                            + (cr.FailedFiles > 0 ? "\r\n" + Lang.F("shader.skip", cr.FailedFiles) : "")
                            + "\r\n\r\n" + Lang.T("shader.note");
                        MessageBox.Show(this, msg, "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
                catch { }
            });
        }

        private void RefreshVbsState()
        {
            if (cardVbs == null) return;
            ApplyVbsState(VbsTweak.Query());
        }

        private void ApplyVbsState(VbsTweak.State st)
        {
            if (cardVbs == null) return;
            string key;
            if (VbsTweak.DisabledByAegis && (!st.WmiOk || st.VbsRunning)) key = "vbs.state.pending";
            else if (!st.WmiOk) key = "vbs.state.unknown";
            else if (st.VbsRunning) key = "vbs.state.on";
            else key = "vbs.state.off";
            cardVbs.Desc = Lang.T(key);
        }

        private void RefreshSlowStateAsync()
        {
            if (!UiActive) return;
            if (Interlocked.Exchange(ref slowBusy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool task = false;
                var st = new VbsTweak.State();
                long shaderBytes = -1;
                try { task = TaskHelper.TaskExists(); st = VbsTweak.Query(); } catch { }
                try { if (!shaderCleaning) shaderBytes = ShaderCache.MeasureBytes(); } catch { }
                Interlocked.Exchange(ref slowBusy, 0);
                if (!UiActive) return;
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (IsDisposed || !UiActive) return;
                        if (swAuto != null) swAuto.SetSilently(task);
                        if (swVbs != null) swVbs.SetSilently(VbsTweak.DisabledByAegis);
                        ApplyVbsState(st);
                        if (cardShader != null && !shaderCleaning && shaderBytes >= 0)
                            cardShader.Value = CacheSweep.FmtBytes(shaderBytes);
                    }));
                }
                catch { }
            });
        }

        private DialogResult ShowDim(Form dlg)
        {
            Opacity = 0.55;
            try { return dlg.ShowDialog(this); }
            finally { Opacity = 1.0; }
        }

        private void BrowseGameExecutable()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = Lang.T("ofd.game");
                dlg.Filter = Lang.T("ofd.filter");
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string error;
                    if (!gameMode.AddGameFile(dlg.FileName, out error))
                        MessageBox.Show(this, error, "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    RefreshGames();
                }
            }
        }

        private void RefreshGames()
        {
            if (lstGames == null) return;
            List<GameProfile> profiles = gameMode.GetProfiles();
            var paths = new List<string>();
            foreach (GameProfile profile in profiles) paths.Add(profile.ExecutablePath);
            Dictionary<string, bool> states = ProbeRunning(paths);
            lstGames.BeginUpdate();
            lstGames.Items.Clear();
            foreach (GameProfile profile in profiles)
                lstGames.Items.Add(new GameLibraryItem(profile, RunningIn(states, profile.ExecutablePath)));
            lstGames.EndUpdate();
            bool empty = lstGames.Items.Count == 0;
            lstGames.Visible = !empty;
            if (gameListPanel != null) { gameListPanel.ShowEmpty = empty; gameListPanel.Invalidate(); }
            SyncNetQosPolicies(profiles);
        }

        private void RefreshGameRunningStates(bool force = false)
        {
            if (!UiActive || lstGames == null) return;
            long now = DateTime.UtcNow.Ticks;
            if (!force && now < Interlocked.Read(ref nextRunningProbeTicks)) return;
            if (Interlocked.Exchange(ref runningBusy, 1) == 1) return;
            Interlocked.Exchange(ref nextRunningProbeTicks, now + RunningProbeIntervalTicks);
            var paths = new List<string>();
            foreach (object value in lstGames.Items)
            {
                GameLibraryItem item = value as GameLibraryItem;
                if (item != null && item.Profile != null) paths.Add(item.Profile.ExecutablePath);
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Dictionary<string, bool> states = null;
                try { states = ProbeRunning(paths); }
                catch { }
                Interlocked.Exchange(ref runningBusy, 0);
                if (states == null || !UiActive) return;
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (IsDisposed || !UiActive || lstGames == null) return;
                        ApplyRunningStates(states);
                    }));
                }
                catch { }
            });
        }

        private void ApplyRunningStates(Dictionary<string, bool> states)
        {
            bool changed = false;
            foreach (object value in lstGames.Items)
            {
                GameLibraryItem item = value as GameLibraryItem;
                if (item == null || item.Profile == null) continue;
                string path = item.Profile.ExecutablePath;
                bool running;
                if (string.IsNullOrEmpty(path) || !states.TryGetValue(path, out running)) continue;
                if (running != item.Running) { item.Running = running; changed = true; }
            }
            if (changed) lstGames.Invalidate();
        }

        private void SyncNetQosPolicies(List<GameProfile> profiles)
        {
            var sb = new System.Text.StringBuilder();
            foreach (GameProfile profile in profiles)
            {
                if (string.IsNullOrEmpty(profile.ExecutablePath)) continue;
                sb.Append(profile.Name).Append('>').Append(profile.ExecutablePath).Append('|');
            }
            string signature = sb.ToString();
            if (netQosSignature == null || netQosSignature == signature) { netQosSignature = signature; return; }
            netQosSignature = signature;
            if (!NetworkAffinityTweak.EnabledByAegis) return;
            if (Interlocked.Exchange(ref netQosBusy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (netQosSync)
                {
                    Interlocked.Exchange(ref netQosBusy, 0);
                    try { NetworkAffinityTweak.Enable(gameMode.GetProfiles()); }
                    catch { }
                }
            });
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

        private void AddDroppedGames(string[] files)
        {
            if (files == null) return;
            string error = null;
            foreach (string file in files)
                if (!gameMode.AddGameFile(file, out error) && error != "该游戏已经在列表中") break;
            if (!string.IsNullOrEmpty(error) && error != "该游戏已经在列表中")
                MessageBox.Show(this, error, "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            RefreshGames();
        }

        private static bool RunningIn(Dictionary<string, bool> states, string executablePath)
        {
            bool running;
            return !string.IsNullOrEmpty(executablePath)
                && states.TryGetValue(executablePath, out running) && running;
        }

        private static Dictionary<string, bool> ProbeRunning(List<string> paths)
        {
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var wanted = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path) || result.ContainsKey(path)) continue;
                result[path] = false;
                string name;
                try { name = Path.GetFileNameWithoutExtension(path); }
                catch { continue; }
                if (string.IsNullOrEmpty(name)) continue;
                List<string> bucket;
                if (!wanted.TryGetValue(name, out bucket)) { bucket = new List<string>(); wanted[name] = bucket; }
                bucket.Add(path);
            }
            if (wanted.Count == 0) return result;
            Process[] all = null;
            try
            {
                all = Process.GetProcesses();
                foreach (Process process in all)
                {
                    List<string> bucket = null;
                    int pid = 0;
                    try { wanted.TryGetValue(process.ProcessName, out bucket); pid = process.Id; }
                    catch { continue; }
                    if (bucket == null) continue;
                    IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (handle == IntPtr.Zero) continue;
                    string image;
                    try { image = Native.ImagePath(handle); }
                    finally { Native.CloseHandle(handle); }
                    if (string.IsNullOrEmpty(image)) continue;
                    foreach (string path in bucket)
                        if (string.Equals(path, image, StringComparison.OrdinalIgnoreCase)) result[path] = true;
                }
            }
            catch { }
            finally { if (all != null) foreach (Process process in all) process.Dispose(); }
            return result;
        }

        private Bitmap GameIcon(string executablePath)
        {
            string key = executablePath ?? "";
            Bitmap bitmap;
            if (gameIconCache.TryGetValue(key, out bitmap)) return bitmap;
            try
            {
                using (Icon icon = Icon.ExtractAssociatedIcon(executablePath)) bitmap = icon.ToBitmap();
            }
            catch { bitmap = appIcon == null ? new Bitmap(32, 32) : appIcon.ToBitmap(); }
            gameIconCache[key] = bitmap;
            return bitmap;
        }

        private sealed class GameLibraryItem
        {
            public readonly GameProfile Profile;
            public bool Running;
            public GameLibraryItem(GameProfile profile, bool running) { Profile = profile; Running = running; }
            public override string ToString() { return Profile == null ? "" : Profile.Name; }
        }

        private void OnUiTick(object s, EventArgs e)
        {
            if (!UiActive) return;
            UpdateAutoHide(gameMode.Enabled && gameMode.IsActive);
            lblStatus.Text = gameMode.StatusText;
            bool act = gameMode.Enabled && gameMode.IsActive;
            statusDot.Color = !gameMode.Enabled ? Theme.Dim : (act ? Theme.Green : Theme.Accent);
            statusDot.Pulse = act;
            if (aegisCore != null) aegisCore.SetState(gameMode.ActivePreset, gameMode.Enabled, act);
            if (lblSub != null && elevated)
            {
                string g = gameMode.ActiveGame;
                string st = Lang.T("title.admin") + " · " + (g != null ? Lang.F("title.guard", g) : Lang.T("title.idle"));
                if (lblSub.Text != st) lblSub.Text = st;
                lblSub.ForeColor = g != null ? Theme.Green : Theme.Faint;
            }
            RefreshBoostPresentation();
            UpdateModePresentation(true);
            if (pageReports.Visible) RefreshReportsV14();
            if (pageWhite.Visible) RefreshGameRunningStates();
            if (pageAnti.Visible)
                for (int i = 0; i < tameGroups.Count; i++)
                {
                    string key = tameGroups[i].Key;
                    int state = tamer.GroupState(key);
                    tameCards[i].SetValue(tamer.GroupStatus(key),
                        state == 1 ? Theme.Green : state == 0 ? Theme.Dim : Theme.Accent);
                }
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
        }

        private void SyncToggleValues()
        {
            if (gameMode == null || tamer == null) return;
            if (swGame != null) swGame.SetSilently(gameMode.Enabled);
            if (swTame != null) swTame.SetSilently(!tamer.Paused);
            if (swGpu != null) swGpu.SetSilently(gameMode.GpuHighPerf);
            if (swFso != null) swFso.SetSilently(gameMode.DisableFso);
            if (swVbs != null) swVbs.SetSilently(VbsTweak.DisabledByAegis);
            if (swAutoHide != null) swAutoHide.SetSilently(Settings.Load(AutoHideKey, false));
            if (swPolicyBackground != null) swPolicyBackground.SetSilently(gameMode.SuppressBackground);
            for (int i = 0; i < policySync.Count; i++) policySync[i]();
            UpdateModePresentation(false);
            for (int i = 0; i < tameGroups.Count && i < tameToggles.Count; i++)
                tameToggles[i].SetSilently(tamer.IsGroupEnabled(tameGroups[i].Key));
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
            if (showAntiCheat) { nav.Select(4); nav.SnapToSelection(); if (curPage != null) curPage.Left = pageBaseLeft; }
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

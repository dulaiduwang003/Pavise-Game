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
        private Toggle swGame, swTame, swAuto, swGpu, swFso, swVbs, swHags, swIrqAffinity, swNetAffinity;
        private Label lblOverviewBoost, lblEvidenceLive;
        private Label lblHeroMode, lblHeroSource, lblPolicyMode;
        private Toggle swPolicyBackground, swPolicyStrict, swPolicyFreeze;
        private Toggle swPolicyNet, swPolicyFg, swPolicyMmcss, swPolicyPauseDl, swPolicyPauseSvc, swPolicyDvr;
        private Toggle swPolicyAggressive;
        private ModeButton modeButton;
        private ModePickerPanel modeFlyout;
        private PerformancePreset visualMode;
        private bool visualEnabled;
        private bool modeVisualInitialized;
        private Motion modeFlyoutMotion;
        private TextBox tbReports;
        private ListBox lstGames, lstWhite;
        private EmptyStatePanel gameListPanel;
        private readonly Dictionary<string, Bitmap> gameIconCache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private Label lblStatus;
        private Label lblSub;
        private SettingCard cardVbs;
        private SettingCard cardShader;
        private SettingCard cardLol;
        private SettingCard cardPolicyStrict, cardPolicyNet, cardPolicyFg, cardPolicyMmcss;
        private SettingCard cardPolicyPauseDl, cardPolicyPauseSvc, cardPolicyDvr;
        private SettingCard cardPolicyAggressive;
        private readonly List<Action> policySync = new List<Action>();
        private volatile bool shaderCleaning;
        private volatile bool lolCleaning;
        private string lolDir;
        private int slowBusy;
        private int restoreBusy;
        private int builtLang;
        private StatusDot statusDot;
        private AegisCore aegisCore;
        private readonly List<AcGroup> tameGroups = new List<AcGroup>();
        private readonly List<SettingCard> tameCards = new List<SettingCard>();
        private readonly List<Toggle> tameToggles = new List<Toggle>();
        private System.Windows.Forms.Timer uiTimer;
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

        private const int WinW = 1040, WinH = 720, RailW = 208, TopH = 54;
        private const int PageW = WinW - RailW, PageH = WinH - TopH;
        private const int ContentX = 26, ContentW = PageW - ContentX * 2;
        private const int ScrollContentW = ContentW - 24;

        public PanelForm(Tamer t, GameMode gm, Icon icon, bool isElevated)
        {
            tamer = t; gameMode = gm; elevated = isElevated; appIcon = (Icon)icon.Clone();
            visualMode = gameMode.ActivePreset; visualEnabled = gameMode.Enabled;
            Theme.SetMode(visualMode, false);
            BuildUi(appIcon);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
            RefreshSlowStateAsync();
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
            UiClock.Frame += OnFormFrame;

            nav = new NavRail(
                new[] { Lang.T("nav.overview"), Lang.T("nav.library"), Lang.T("nav.policy"), Lang.T("v14.anticheat"), Lang.T("nav.reports"), Lang.T("nav.set"), Lang.T("nav.about") },
                new[] { "game", "white", "settings", "shield", "log", "gear", "info" });
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
            pages = new[] { pageGame, pageWhite, pageTame, pageAnti, pageReports, pageSettings, pageAbout };
            BuildOverviewPageV14();
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
            uiTimer.Start();
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
            if (Visible) UiClock.Wake();
            if (aegisCore != null) aegisCore.SetAnimationEnabled(Visible && page == pageGame);
            if (page == pageReports) RefreshReportsV14();
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

        // 开场动画：窗口从略低处淡入并上浮到位，避免"啪"地直接出现。
        // introMotion.Value 从 1（完全未就位）走到 0（就位），透明度取 1-Value。
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
            // 上一次动画没走完就又被显示：先把位置还原，避免每次都往下漂一截
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
            if (Visible) UiClock.Wake(); else UiClock.Running = false;
            if (aegisCore != null) aegisCore.SetAnimationEnabled(Visible && curPage == pageGame);
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
            UiClock.Frame -= OnFormFrame;
            var old = new List<Control>();
            int keep = nav != null ? nav.Selected : 0;
            foreach (Control c in Controls) old.Add(c);
            Controls.Clear();
            foreach (var c in old) c.Dispose();
            tameGroups.Clear(); tameCards.Clear(); tameToggles.Clear();
            BuildUi(appIcon);
            nav.Select(keep);
            RefreshSlowStateAsync();
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
            bool ok = swNetAffinity.Checked
                ? NetworkAffinityTweak.Enable(gameMode.GetProfiles())
                : NetworkAffinityTweak.Disable();
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

        private void OnLolCrossClean(PillButton btn)
        {
            if (lolDir == null) return;
            if (LolCross.AnyLolProcessAlive(lolDir))
            {
                MessageBox.Show(this, Lang.T("lol.running"), "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (MessageBox.Show(this, Lang.T("lol.confirm"), "Aegis", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            btn.Enabled = false;
            lolCleaning = true;
            if (cardLol != null) cardLol.Value = Lang.T("shader.busy");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                CacheSweep.Result cr = LolCross.Clean(lolDir);
                long left = LolCross.MeasureBytes(lolDir);
                Logger.Log("LOL Cross 清理：释放 " + CacheSweep.FmtBytes(cr.FreedBytes)
                    + (cr.FailedFiles > 0 ? "，" + cr.FailedFiles + " 个文件被占用已跳过" : ""));
                lolCleaning = false;
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (IsDisposed) return;
                        if (!btn.IsDisposed) btn.Enabled = true;
                        if (cardLol != null && !cardLol.IsDisposed)
                            cardLol.Value = CacheSweep.FmtBytes(left);
                        string msg = Lang.F("lol.freed", CacheSweep.FmtBytes(cr.FreedBytes))
                            + (cr.FailedFiles > 0 ? "\r\n" + Lang.F("shader.skip", cr.FailedFiles) : "")
                            + "\r\n\r\n" + Lang.T("lol.note");
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
            if (Interlocked.Exchange(ref slowBusy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool task = false;
                var st = new VbsTweak.State();
                long shaderBytes = -1;
                try { task = TaskHelper.TaskExists(); st = VbsTweak.Query(); } catch { }
                try { if (!shaderCleaning) shaderBytes = ShaderCache.MeasureBytes(); } catch { }
                long lolBytes = -1;
                try { if (lolDir != null && !lolCleaning) lolBytes = LolCross.MeasureBytes(lolDir); } catch { }
                Interlocked.Exchange(ref slowBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (IsDisposed) return;
                        if (swAuto != null) swAuto.SetSilently(task);
                        if (swVbs != null) swVbs.SetSilently(VbsTweak.DisabledByAegis);
                        ApplyVbsState(st);
                        if (cardShader != null && !shaderCleaning && shaderBytes >= 0)
                            cardShader.Value = CacheSweep.FmtBytes(shaderBytes);
                        if (cardLol != null && !lolCleaning && lolBytes >= 0)
                            cardLol.Value = CacheSweep.FmtBytes(lolBytes);
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

        private void PickInto(bool toGames)
        {
            using (var dlg = new ProcessPickerDialog())
            {
                if (ShowDim(dlg) == DialogResult.OK && dlg.SelectedName != null)
                {
                    if (toGames) { gameMode.AddGameExecutable(dlg.SelectedName, dlg.SelectedPath); RefreshGames(); }
                    else { gameMode.AddWhitelist(dlg.SelectedName); RefreshWhite(); }
                }
            }
        }

        private void BrowseInto(bool toGames)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = toGames ? Lang.T("ofd.game") : Lang.T("ofd.white");
                dlg.Filter = toGames ? Lang.T("ofd.filter") : "Programs (*.exe)|*.exe";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string n = Path.GetFileNameWithoutExtension(dlg.FileName);
                    if (toGames)
                    {
                        string error;
                        if (!gameMode.AddGameFile(dlg.FileName, out error))
                            MessageBox.Show(this, error, "Aegis", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        RefreshGames();
                    }
                    else { gameMode.AddWhitelist(n); RefreshWhite(); }
                }
            }
        }

        private void RefreshGames()
        {
            if (lstGames == null) return;
            lstGames.BeginUpdate();
            lstGames.Items.Clear();
            foreach (GameProfile profile in gameMode.GetProfiles())
                lstGames.Items.Add(new GameLibraryItem(profile, IsExecutableRunning(profile.ExecutablePath)));
            lstGames.EndUpdate();
            bool empty = lstGames.Items.Count == 0;
            lstGames.Visible = !empty;
            if (gameListPanel != null) { gameListPanel.ShowEmpty = empty; gameListPanel.Invalidate(); }
        }

        private void RefreshGameRunningStates()
        {
            if (lstGames == null) return;
            bool changed = false;
            foreach (object value in lstGames.Items)
            {
                GameLibraryItem item = value as GameLibraryItem;
                if (item == null) continue;
                bool running = IsExecutableRunning(item.Profile.ExecutablePath);
                if (running != item.Running) { item.Running = running; changed = true; }
            }
            if (changed) lstGames.Invalidate();
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

        private static bool IsExecutableRunning(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath)) return false;
            string name = Path.GetFileNameWithoutExtension(executablePath);
            Process[] matches = null;
            try
            {
                matches = Process.GetProcessesByName(name);
                foreach (Process process in matches)
                {
                    IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
                    if (handle == IntPtr.Zero) continue;
                    try
                    {
                        if (string.Equals(Native.ImagePath(handle), executablePath, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    finally { Native.CloseHandle(handle); }
                }
            }
            catch { }
            finally { if (matches != null) foreach (Process process in matches) process.Dispose(); }
            return false;
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

        private void RefreshWhite()
        {
            lstWhite.BeginUpdate();
            lstWhite.Items.Clear();
            foreach (string w in gameMode.GetWhitelist()) lstWhite.Items.Add(w);
            lstWhite.EndUpdate();
        }

        private void OnUiTick(object s, EventArgs e)
        {
            // 放在可见性判断之前：窗口收起后也要继续跟踪对局起止，否则下一局无法重新武装
            UpdateAutoHide(gameMode.Enabled && gameMode.IsActive);
            if (!Visible) return;
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

        // 每局对局只自动收起一次：收起后用户再手动打开就不会又被收走，
        // 直到这局结束（IsActive 落回 false）才为下一局重新武装，避免窗口来回乱跳。
        // 抽成静态纯函数是为了能脱离窗口单测——"只收一次"正是最容易写错的一条。
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
                Settings.Load(AutoHideKey, false), Visible);
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
            if (IsDisposed || !Visible) return;
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

        // 有子对话框开着就不收——否则会把对话框的父窗口从它底下抽走
        private bool AnyDialogOpen()
        {
            try
            {
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
            UiClock.Frame -= OnFormFrame;
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
            SyncAllToggles();
        }

        public void SyncAllToggles()
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)SyncAllToggles); return; }
            if (IsDisposed || !Visible) return;
            if (builtLang != Lang.Cur) { RebuildUi(); return; }
            if (swGame != null) swGame.SetSilently(gameMode.Enabled);
            if (swTame != null) swTame.SetSilently(!tamer.Paused);
            if (swGpu != null) swGpu.SetSilently(gameMode.GpuHighPerf);
            if (swFso != null) swFso.SetSilently(gameMode.DisableFso);
            if (swVbs != null) swVbs.SetSilently(VbsTweak.DisabledByAegis);
            if (swAutoHide != null) swAutoHide.SetSilently(Settings.Load(AutoHideKey, false));
            if (swPolicyBackground != null) swPolicyBackground.SetSilently(gameMode.SuppressBackground);
            if (swPolicyFreeze != null) swPolicyFreeze.SetSilently(gameMode.DeepFreeze);
            for (int i = 0; i < policySync.Count; i++) policySync[i]();
            UpdateModePresentation(false);
            for (int i = 0; i < tameGroups.Count && i < tameToggles.Count; i++)
                tameToggles[i].SetSilently(tamer.IsGroupEnabled(tameGroups[i].Key));
            RefreshSlowStateAsync();
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
            if (showAntiCheat) { nav.Select(3); nav.SnapToSelection(); if (curPage != null) curPage.Left = pageBaseLeft; }
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

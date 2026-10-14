// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swAuto, swAutoHide;
        private SettingCard cardShader;
        private PillButton btnShaderGo;
        private PillButton btnBackdrop, btnBackdropReset;
        private TierPicker pickBackdropDim;
        private BackdropPreview backdropPreview;
        private Label lblBackdropState;
        private readonly Label[] modeAccentLabels = new Label[7];

        private static readonly Color[] AccentPalette =
        {
            Color.FromArgb(239, 190, 66), Color.FromArgb(255, 140, 40), Color.FromArgb(255, 61, 82),
            Color.FromArgb(240, 90, 170), Color.FromArgb(178, 118, 255), Color.FromArgb(48, 180, 255),
            Color.FromArgb(40, 200, 200), Color.FromArgb(69, 224, 154), Color.FromArgb(150, 214, 80),
            Color.FromArgb(120, 140, 255),
        };
        private readonly List<ColorSwatch>[] modeSwatches =
            { new List<ColorSwatch>(), new List<ColorSwatch>(), new List<ColorSwatch>(),
              new List<ColorSwatch>(), new List<ColorSwatch>(), new List<ColorSwatch>(), new List<ColorSwatch>() };
        private static int shaderCleaning;
        private static int shaderCleanupVersion;
        private static bool ShaderCleanupBusy { get { return Volatile.Read(ref shaderCleaning) != 0; } }
        private int slowBusy;
        private int slowVersion, slowPending;
        private int wipeBusy;
        private bool runtimeShutdownBusy;
        private PillButton btnWipeAll, btnUninstall;

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal Func<bool> StartupTaskQueryForTest;
        internal Func<bool, int> StartupTaskChangeForTest;
        internal Func<long> ShaderMeasureForTest;
        internal Action<Action> UiStateWorkQueueForTest;
        internal Action<Action> UiStatePostForTest;
        internal Action<string> StartupTaskWarningForTest;
        internal Func<CacheSweep.Result> ShaderCleanForTest;
        internal Func<string, bool> SettingsConfirmationForTest;
        internal Action<bool, string> ShaderCleanupMessageForTest;
        internal Action<string> SettingsOperationWarningForTest;
#endif

        private bool QueryStartupTaskState()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (StartupTaskQueryForTest == null) throw new InvalidOperationException("Startup task query was not mocked");
            return StartupTaskQueryForTest();
#else
            return TaskHelper.IsStartupTaskEnabled();
#endif
        }

        private int ChangeStartupTask(bool enabled)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (StartupTaskChangeForTest == null) throw new InvalidOperationException("Startup task change was not mocked");
            return StartupTaskChangeForTest(enabled);
#else
            return enabled ? TaskHelper.CreateStartupTask() : TaskHelper.DeleteStartupTask();
#endif
        }

        private long MeasureSettingsShaderCache()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (ShaderMeasureForTest == null) throw new InvalidOperationException("Shader cache query was not mocked");
            return ShaderMeasureForTest();
#else
            return ShaderCache.MeasureBytes();
#endif
        }

        private CacheSweep.Result CleanSettingsShaderCache()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (ShaderCleanForTest == null) throw new InvalidOperationException("Shader cache cleanup was not mocked");
            return ShaderCleanForTest();
#else
            return ShaderCache.Clean();
#endif
        }

        private bool ConfirmSettingsAction(string key, DlgKind kind)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (SettingsConfirmationForTest == null) throw new InvalidOperationException("Settings confirmation was not mocked");
            return SettingsConfirmationForTest(key);
#else
            return PaviseDialog.Confirm(this, App.DisplayName, Lang.T(key), kind);
#endif
        }

        private void ShowShaderCleanupMessage(bool failed, string message)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (ShaderCleanupMessageForTest == null) throw new InvalidOperationException("Shader result dialog was not mocked");
            ShaderCleanupMessageForTest(failed, message);
#else
            if (failed) PaviseDialog.Warn(this, App.DisplayName, message);
            else PaviseDialog.Info(this, App.DisplayName, message);
#endif
        }

        private void QueueUiStateWork(Action work)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (UiStateWorkQueueForTest != null) { UiStateWorkQueueForTest(work); return; }
#endif
            if (!ThreadPool.QueueUserWorkItem(delegate { work(); }))
                throw new InvalidOperationException("UI state work could not be queued");
        }

        private void PostUiStateResult(Action result)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (UiStatePostForTest != null) { UiStatePostForTest(result); return; }
#endif
            BeginInvoke((MethodInvoker)delegate { result(); });
        }

        private void WarnStartupTaskFailure(string reason)
        {
            string message = Lang.T("msg.taskfail")
                + (string.IsNullOrEmpty(reason) ? "" : "\r\n\r\n" + reason);
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (StartupTaskWarningForTest == null) throw new InvalidOperationException("Startup task warning was not mocked");
            StartupTaskWarningForTest(message);
#else
            PaviseDialog.Warn(this, App.DisplayName, message);
#endif
        }

        private TechTabs settingsTabs;
        private DBPanel[] settingsTabPanels;

        private void BuildSettingsPage()
        {
            Interlocked.Increment(ref slowVersion);
            int y = PageHeader(pageSettings, Lang.T("nav.set"), Lang.T("set.hint"), 2);

            settingsTabs = new TechTabs();
            settingsTabs.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(38));
            settingsTabs.SetTabs(
                new[] { Lang.T("sec.app"), Lang.T("sec.maint"), Lang.T("sec.appearance"), AutomaticPolicy.Text("性能监测", "Monitoring") },
                new[] { Lang.T("set.tab.app.h"), Lang.T("set.tab.maint.h"), Lang.T("set.tab.appearance.h"), AutomaticPolicy.Text("游戏内指标与显示布局", "In-game metrics and overlay layout") });
            pageSettings.Controls.Add(settingsTabs);
            y += 48;
            settingsTabPanels = MakeTabPanels(pageSettings, settingsTabs, 4, y);

            Control scroll = settingsTabPanels[0];
            int sy = 2, cardH;
            swAuto = MakeSwitch(TaskHelper.IsStartupTaskEnabledCached(), OnAutoToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.autostart"), Lang.T("set.autostart.n"), swAuto, out cardH);
            sy += cardH + 8;

            swAutoHide = MakeSwitch(Settings.Load(AutoHideKey, AutoHideDefault), OnAutoHideToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.autohide"), Lang.T("set.autohide.n"), swAutoHide, out cardH);
            sy += cardH + 8;

            var laptopPerformance = MakeSwitch(gameMode.LaptopPerformanceEnabled, delegate(object sender, EventArgs args)
            {
                var toggle = (Toggle)sender;
                if (!gameMode.TrySetLaptopPerformanceEnabled(toggle.Checked))
                {
                    toggle.SetSilently(gameMode.LaptopPerformanceEnabled);
                    PaviseDialog.Warn(this, App.DisplayName, Lang.T("set.laptopperf.fail"));
                }
            });
            laptopPerformance.Name = "LaptopPerformanceSwitch";
            MakeAutoCard(scroll, 6, sy, ScrollContentW,
                FullTextCardHeight(Lang.T("set.laptopperf.n"), ScrollContentW, laptopPerformance, 88),
                Lang.T("set.laptopperf"), Lang.T("set.laptopperf.n"), laptopPerformance, out cardH);
            sy += cardH + 8;

            int[] exitGraceChoices = { 0, 2, 5, 8, 15, 30 };
            var pickExitGrace = new TierPicker();
            pickExitGrace.Name = "GameExitGracePicker";
            var exitGraceLabels = new string[exitGraceChoices.Length];
            for (int i = 0; i < exitGraceChoices.Length; i++)
                exitGraceLabels[i] = Lang.F("set.exitgrace.seconds", exitGraceChoices[i]);
            pickExitGrace.Labels = exitGraceLabels;
            pickExitGrace.Index = Array.IndexOf(exitGraceChoices, gameMode.ExitGraceSeconds);
            pickExitGrace.Size = new Size(Theme.S(336), Theme.S(30));
            pickExitGrace.IndexChanged = delegate(int index)
            {
                if (index < 0 || index >= exitGraceChoices.Length) return;
                if (!gameMode.TrySetExitGraceSeconds(exitGraceChoices[index]))
                {
                    pickExitGrace.Index = Array.IndexOf(exitGraceChoices, gameMode.ExitGraceSeconds);
                    PaviseDialog.Warn(this, App.DisplayName, Lang.T("set.exitgrace.fail"));
                }
            };
            MakeAutoCard(scroll, 6, sy, ScrollContentW,
                FullTextCardHeight(Lang.T("set.exitgrace.n"), ScrollContentW, pickExitGrace, 96), Lang.T("set.exitgrace"),
                Lang.T("set.exitgrace.n"), pickExitGrace, out cardH);
            sy += cardH + 8;

            var pickLang = new TierPicker();
            pickLang.Labels = new[] { Lang.T("lang.zh"), Lang.T("lang.en") };
            pickLang.Index = Lang.Cur;
            pickLang.Size = new Size(Theme.S(208), Theme.S(30));
            pickLang.IndexChanged = delegate(int index)
            {
                Lang.Set(index);
                BeginInvoke((MethodInvoker)RebuildUi);
            };
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.lang"), Lang.T("set.lang.n"), pickLang, out cardH);
            sy += cardH + 8;
            EnableCardCollapse(scroll);

            scroll = settingsTabPanels[1]; sy = 2;
            BuildRecoveryMaintenance(scroll, ref sy);
            btnWipeAll = new PillButton(Lang.T("btn.wipe"), BtnKind.Normal);
            btnWipeAll.Bg = Theme.Card;
            btnWipeAll.Size = new Size(Theme.S(136), Theme.S(32));
            btnWipeAll.Click += delegate { OnWipeAll(); };
            MakeAutoCard(scroll, 6, sy, ScrollContentW,
                FullTextCardHeight(Lang.T("set.reset.desc"), ScrollContentW, btnWipeAll, 78),
                Lang.T("btn.wipe"), Lang.T("set.reset.desc"), btnWipeAll, out cardH);
            sy += cardH + 8;

            btnUninstall = new PillButton(Lang.T("btn.uninstall"), BtnKind.Danger);
            btnUninstall.Bg = Theme.Card;
            btnUninstall.Size = new Size(Theme.S(128), Theme.S(32));
            btnUninstall.Click += delegate { OnUninstall(); };
            MakeAutoCard(scroll, 6, sy, ScrollContentW,
                FullTextCardHeight(Lang.T("set.uninstall.desc"), ScrollContentW, btnUninstall, 78),
                Lang.T("btn.uninstall"), Lang.T("set.uninstall.desc"), btnUninstall, out cardH);
            sy += cardH + 8;

            btnShaderGo = new PillButton(Lang.T("btn.clean"));
            btnShaderGo.Size = new Size(Theme.S(88), Theme.S(30));
            btnShaderGo.Click += (s, e) => OnShaderClean((PillButton)s);
            cardShader = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("btn.shader"), Lang.T("set.shader.n"), btnShaderGo, out cardH);
            cardShader.Value = " ";
            SyncShaderCleanupPresentation();
            sy += cardH + 8;

            var lblAbout = new Label();
            lblAbout.Text = Lang.F("set.about", App.VersionTag, Paths.Data);
            lblAbout.ForeColor = Theme.Faint; lblAbout.BackColor = Theme.Bg;
            lblAbout.Font = Theme.UI(8.25f, false);
            lblAbout.SetBounds(Theme.S(10), Theme.S(sy + 6), Theme.S(ScrollContentW - 10), Theme.S(18));
            scroll.Controls.Add(lblAbout);
            EnableCardCollapse(scroll);

            scroll = settingsTabPanels[2]; sy = 2;
            BuildAppearanceSection(scroll, ref sy);

            BuildPerformanceMonitoringSettings(settingsTabPanels[3]);

        }

        private void BuildAppearanceSection(Control scroll, ref int sy)
        {

            int panelH = 184 + PresetValue.VisibleOrder().Length * 34 + 4;
            var panel = MakeConsolePanel(scroll, 6, sy, ScrollContentW, panelH, true);
            int w = ScrollContentW;

            backdropPreview = new BackdropPreview();
            backdropPreview.EmptyText = Lang.T("backdrop.preview.empty");
            backdropPreview.SetBounds(Theme.S(18), Theme.S(18), Theme.S(194), Theme.S(104));
            panel.Controls.Add(backdropPreview);

            CardLabel(panel, Lang.T("backdrop.title"), 232, 18, w - 500, 22, 9.75f, true, Theme.Fg);
            lblBackdropState = CardLabel(panel, "", 232, 43, w - 500, 20, 8.2f, false, Theme.Dim);

            btnBackdrop = new PillButton(Lang.T(Backdrop.Active ? "btn.backdrop.change" : "btn.backdrop.set"));
            btnBackdrop.Size = new Size(Theme.S(116), Theme.S(30));
            btnBackdrop.Click += delegate { OnBackdropPick(); };
            btnBackdrop.SetBounds(Theme.S(w - 134), Theme.S(18), Theme.S(116), Theme.S(30));
            panel.Controls.Add(btnBackdrop);

            btnBackdropReset = new PillButton(Lang.T("btn.backdrop.reset"));
            btnBackdropReset.Size = new Size(Theme.S(104), Theme.S(30));
            btnBackdropReset.Click += delegate { OnBackdropReset(); };
            btnBackdropReset.SetBounds(Theme.S(w - 246), Theme.S(18), Theme.S(104), Theme.S(30));
            panel.Controls.Add(btnBackdropReset);

            CardLabel(panel, Lang.T("backdrop.dim"), 232, 78, 120, 30, 8.6f, true, Theme.Fg);

            pickBackdropDim = new TierPicker();
            pickBackdropDim.Labels = new[] { Lang.T("backdrop.dim.off"), Lang.T("backdrop.dim.1"),
                Lang.T("backdrop.dim.2"), Lang.T("backdrop.dim.3") };
            pickBackdropDim.Index = Backdrop.Active ? Backdrop.Dim + 1 : 0;
            pickBackdropDim.Size = new Size(Theme.S(276), Theme.S(30));
            pickBackdropDim.IndexChanged = OnBackdropDim;
            pickBackdropDim.SetBounds(Theme.S(360), Theme.S(78), Theme.S(276), Theme.S(30));
            panel.Controls.Add(pickBackdropDim);

            var separator = new Panel();
            separator.BackColor = Theme.Stroke;
            separator.SetBounds(Theme.S(18), Theme.S(137), Theme.S(w - 36), Math.Max(1, Theme.S(1)));
            panel.Controls.Add(separator);

            CardLabel(panel, Lang.T("sec.theme"), 18, 151, 150, 22, 9.2f, true, Theme.Fg);
            CardLabel(panel, Lang.T("theme.hint"), 174, 153, w - 192, 18, 8.0f, false, Theme.Dim);

            BuildThemeColorRows(panel, 184);
            SyncBackdropAppearance();
            sy += panelH + 8;
        }

        private void OnBackdropPick()
        {
            if (!Backdrop.Choose(this)) return;
            SyncBackdropAppearance();
            RefreshBackdrop();
        }

        private void OnBackdropReset()
        {
            if (!Backdrop.Active) return;
            Backdrop.Clear();
            SyncBackdropAppearance();
            RefreshBackdrop();
        }

        private void OnBackdropDim(int index)
        {
            if (index <= 0)
            {
                Backdrop.Clear();
            }
            else Backdrop.Dim = index - 1;
            SyncBackdropAppearance();
            RefreshBackdrop();
        }

        private void SyncBackdropAppearance()
        {
            bool active = Backdrop.Active;
            if (btnBackdrop != null) btnBackdrop.Text = Lang.T(active ? "btn.backdrop.change" : "btn.backdrop.set");
            if (btnBackdropReset != null) btnBackdropReset.Enabled = active;
            if (pickBackdropDim != null)
            {
                pickBackdropDim.Enabled = active;
                pickBackdropDim.Index = active ? Backdrop.Dim + 1 : 0;
            }
            if (lblBackdropState != null)
            {
                string strength = active && pickBackdropDim != null && pickBackdropDim.Labels != null
                    ? pickBackdropDim.Labels[Backdrop.Dim + 1] : "";
                lblBackdropState.Text = active ? Lang.F("backdrop.status.on", strength) : Lang.T("backdrop.status.off");
                lblBackdropState.ForeColor = active ? Theme.Accent : Theme.Dim;
            }
            if (backdropPreview != null) backdropPreview.Invalidate();
        }

        private void BuildThemeColorRows(Control parent, int startY)
        {
            PerformancePreset[] modes = PresetValue.VisibleOrder();
            int row = 0;
            foreach (PerformancePreset mode in modes)
            {
                PerformancePreset capMode = mode;
                modeSwatches[(int)mode].Clear();
                int y = startY + row * 34;
                bool current = mode == gameMode.ActivePreset;

                var lbl = new Label();
                lbl.Text = ModeName(mode);
                lbl.ForeColor = current ? Theme.ModeColor(mode) : Theme.Fg;
                lbl.BackColor = Color.Transparent;
                lbl.Font = Theme.UI(8.6f, current);
                lbl.TextAlign = ContentAlignment.MiddleLeft;
                lbl.SetBounds(Theme.S(18), Theme.S(y), Theme.S(82), Theme.S(26));
                parent.Controls.Add(lbl);
                modeAccentLabels[(int)mode] = lbl;

                int sx = 108;
                bool overridden = Theme.HasModeColorOverride(mode);

                var def = new ColorSwatch(Theme.ModeColorBuiltin(mode));
                def.IsDefault = true; def.Selected = !overridden;
                def.SetBounds(Theme.S(sx), Theme.S(y), Theme.S(26), Theme.S(26));
                def.Picked = delegate { OnModeColorPick(capMode, Color.Empty); };
                parent.Controls.Add(def); modeSwatches[(int)mode].Add(def);
                sx += 32;

                Color eff = Theme.ModeColor(mode);
                foreach (Color paletteColor in AccentPalette)
                {
                    Color cc = paletteColor;
                    var sw = new ColorSwatch(cc);
                    sw.Selected = overridden && SameRgb(eff, cc);
                    sw.SetBounds(Theme.S(sx), Theme.S(y), Theme.S(26), Theme.S(26));
                    sw.Picked = delegate { OnModeColorPick(capMode, cc); };
                    parent.Controls.Add(sw); modeSwatches[(int)mode].Add(sw);
                    sx += 32;
                }
                row++;
            }
        }

        private void RefreshModeAccentLabels()
        {
            for (int i = 0; i < modeAccentLabels.Length; i++)
            {
                Label lbl = modeAccentLabels[i];
                if (lbl == null || lbl.IsDisposed) continue;
                PerformancePreset mode = (PerformancePreset)i;
                bool current = mode == visualMode;
                lbl.ForeColor = current ? Theme.ModeColor(mode) : Theme.Fg;
                lbl.Font = Theme.UI(8.6f, current);
            }
        }

        private static string ModeName(PerformancePreset m)
        {
            return m == PerformancePreset.Extreme ? Lang.T("preset.extreme")
                : m == PerformancePreset.Competitive ? Lang.T("preset.competitive")
                : m == PerformancePreset.Handheld ? Lang.T("preset.handheld")
                : Lang.T("preset.standard");
        }

        private static bool SameRgb(Color a, Color b) { return a.R == b.R && a.G == b.G && a.B == b.B; }

        private void OnModeColorPick(PerformancePreset mode, Color color)
        {
            if (color.IsEmpty)
            {
                Theme.ClearModeColorOverride(mode);
                Settings.SaveStr("ModeAccent" + (int)mode, "");
            }
            else
            {
                Theme.SetModeColorOverride(mode, color);
                Settings.SaveStr("ModeAccent" + (int)mode, Col.ToHex(color));
            }

            bool overridden = Theme.HasModeColorOverride(mode);
            Color eff = Theme.ModeColor(mode);
            foreach (ColorSwatch sw in modeSwatches[(int)mode])
            {
                sw.Selected = sw.IsDefault ? !overridden : (overridden && SameRgb(sw.Swatch, eff));
                sw.Invalidate();
            }
            RefreshModeAccentLabels();

            if (modeVisualInitialized && mode == visualMode)
            {
                using (Icon icon = IconArt.MakeMultiIcon(visualMode, visualEnabled)) SetRuntimeIcon(icon);
                if (nav != null) nav.RefreshLogo();
                if (tuningNav != null) tuningNav.RefreshLogo();
                if (paviseCore != null) paviseCore.RefreshVisual();
            }
            if (UiActive) UiClock.Wake();
        }

        private void OnAutoToggle(object s, EventArgs e)
        {
            if (IsDisposed || swAuto == null || swAuto.IsDisposed) return;
            // 命令前的旧查询不能覆盖新的选择结果。
            Interlocked.Increment(ref slowVersion);
            int rc = ChangeStartupTask(swAuto.Checked);
            if (rc != 0)
            {
                string reason = TaskHelper.LastSchtasksError;
                swAuto.SetSilently(QueryStartupTaskState());
                WarnStartupTaskFailure(reason);
            }
        }

        private void OnShaderClean(PillButton btn)
        {
            if (runtimeShutdownBusy || IsDisposed || btn == null || btn.IsDisposed) return;
            if (ShaderCleanupBusy)
            {
                if (cardShader != null) cardShader.Value = Lang.T("shader.busy");
                return;
            }
            if (gameMode != null && gameMode.IsActive)
            {
                PaviseDialog.Warn(this, App.DisplayName, Lang.T("shader.ingame"));
                return;
            }
            if (!ConfirmSettingsAction("shader.confirm", DlgKind.Warn)) return;
            // 确认弹窗期间可能已开始对局。
            if (gameMode != null && gameMode.IsActive)
            {
                PaviseDialog.Warn(this, App.DisplayName, Lang.T("shader.ingame"));
                return;
            }
            if (runtimeShutdownBusy || IsDisposed || btn.IsDisposed
                || Interlocked.CompareExchange(ref shaderCleaning, 1, 0) != 0) return;
            int version = Interlocked.Increment(ref shaderCleanupVersion);
            btn.Enabled = false;
            SyncShaderCleanupPresentation();
            Action<string, CacheSweep.Result, long> complete = delegate(string failure, CacheSweep.Result cr, long left)
            {
                try
                {
                    PostUiStateResult(delegate
                    {
                        if (IsDisposed || runtimeShutdownBusy || version != Volatile.Read(ref shaderCleanupVersion)) return;
                        if (!btn.IsDisposed) btn.Enabled = true;
                        SyncShaderCleanupPresentation();
                        if (cardShader != null && !cardShader.IsDisposed && left >= 0)
                            cardShader.Value = CacheSweep.FmtBytes(left);
                        else if (cardShader != null && !cardShader.IsDisposed)
                            cardShader.Value = failure == null ? "" : AutomaticPolicy.Text("清理未完成", "Cleanup incomplete");
                        string msg = failure != null ? AutomaticPolicy.Text("着色器缓存清理未完成", "Shader cache cleanup incomplete") + "\r\n\r\n" + failure
                            : Lang.F("shader.freed", CacheSweep.FmtBytes(cr.FreedBytes))
                            + (cr.FailedFiles > 0 ? "\r\n" + Lang.F("shader.skip", cr.FailedFiles) : "")
                            + "\r\n\r\n" + Lang.T("shader.note");
                        ShowShaderCleanupMessage(failure != null, msg);
                    });
                }
                catch { }
            };
            try
            {
                QueueUiStateWork(delegate
                {
                    CacheSweep.Result cr = null;
                    long left = -1;
                    string failure = null;
                    try
                    {
                        cr = CleanSettingsShaderCache();
                        if (cr == null) throw new InvalidOperationException("Shader cleanup returned no result");
                        left = MeasureSettingsShaderCache();
                        Logger.Log(Lang.T("log.panelformsettingspage.1") + CacheSweep.FmtBytes(cr.FreedBytes)
                            + (cr.FailedFiles > 0 ? " " + cr.FailedFiles + Lang.T("log.panelformsettingspage.2") : ""));
                    }
                    catch (Exception error) { failure = error.Message; }
                    finally { Interlocked.Exchange(ref shaderCleaning, 0); }
                    complete(failure, cr, left);
                });
            }
            catch (Exception error)
            {
                Interlocked.Exchange(ref shaderCleaning, 0);
                btn.Enabled = true;
                SyncShaderCleanupPresentation();
                ShowShaderCleanupMessage(true, AutomaticPolicy.Text("着色器缓存清理未完成", "Shader cache cleanup incomplete") + "\r\n\r\n" + error.Message);
            }
        }

        private void SyncShaderCleanupPresentation()
        {
            bool busy = ShaderCleanupBusy;
            if (btnShaderGo != null && !btnShaderGo.IsDisposed) btnShaderGo.Enabled = !busy;
            if (busy && cardShader != null && !cardShader.IsDisposed) cardShader.Value = Lang.T("shader.busy");
        }

        internal void BeginRuntimeShutdown(string message)
        {
            if (IsDisposed) return;
            if (!runtimeShutdownBusy)
            {
                runtimeShutdownBusy = true;
                FormClosing += PreventShutdownClose;
            }
            CancelMonitoringForShutdown();
            RealExit = true;
            auditCancelRequested = true;
            Interlocked.Increment(ref slowVersion);
            uiActive = false;
            uiActivityKnown = true;
            if (uiTimer != null) uiTimer.Stop();
            if (fitTimer != null) fitTimer.Stop();
            CancelAutoHide();
            StopPageReveal();
            if (paviseCore != null) paviseCore.SetAnimationEnabled(false);
            if (root != null) root.Enabled = false;
            Cursor = Cursors.WaitCursor;
            ShowDataOperationProgress(message);
            SetResetProgress(message);
        }

        private void PreventShutdownClose(object sender, FormClosingEventArgs args)
        {
            if (runtimeShutdownBusy && args.CloseReason != CloseReason.WindowsShutDown) args.Cancel = true;
        }

        internal void FinishRuntimeShutdown()
        {
            runtimeShutdownBusy = false;
            FormClosing -= PreventShutdownClose;
        }

        internal void SetResetProgress(string message)
        {
            if (IsDisposed) return;
            if (dataOperationPanel != null && !dataOperationPanel.IsDisposed)
                dataOperationPanel.Status.Text = message;
            if (lblSub != null && !lblSub.IsDisposed)
            {
                lblSub.Text = message;
                lblSub.ForeColor = Theme.Warning;
            }
        }

        private void SetResetActionsEnabled(bool enabled)
        {
            if (btnWipeAll != null && !btnWipeAll.IsDisposed) btnWipeAll.Enabled = enabled;
            if (btnUninstall != null && !btnUninstall.IsDisposed) btnUninstall.Enabled = enabled;
        }

        private bool CanBeginReset(string inGameMessage, string confirmation)
        {
            if (runtimeShutdownBusy || RejectDataOperationDuringMonitoringInstall()
                || Interlocked.CompareExchange(ref wipeBusy, 1, 0) != 0) return false;
            bool accepted = false;
            try
            {
                SetResetActionsEnabled(false);
                if (ShaderCleanupBusy)
                {
                    ShowSettingsOperationWarning(AutomaticPolicy.Text("着色器缓存正在清理，请完成后重试", "Shader cleanup is in progress; retry when it finishes"));
                    return false;
                }
                if (gameMode.IsActive)
                {
                    ShowSettingsOperationWarning(Lang.T(inGameMessage));
                    return false;
                }
                if (!ConfirmSettingsAction(confirmation, DlgKind.Danger)) return false;
                if (gameMode.IsActive)
                {
                    ShowSettingsOperationWarning(Lang.T(inGameMessage));
                    return false;
                }
                if (runtimeShutdownBusy || ShaderCleanupBusy || RejectDataOperationDuringMonitoringInstall()) return false;
                Cursor = Cursors.WaitCursor;
                accepted = true;
                return true;
            }
            finally
            {
                if (!accepted)
                {
                    Interlocked.Exchange(ref wipeBusy, 0);
                    if (!runtimeShutdownBusy) SetResetActionsEnabled(true);
                }
            }
        }

        private void ShowSettingsOperationWarning(string message)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (SettingsOperationWarningForTest == null) throw new InvalidOperationException("Data operation warning was not mocked");
            SettingsOperationWarningForTest(message);
#else
            PaviseDialog.Warn(this, App.DisplayName, message);
#endif
        }

        private void OnWipeAll()
        {
            Action reset = ResetApp;
            if (reset == null) return;
            if (!CanBeginReset("wipe.ingame", "wipe.confirm")) return;
            reset();
        }

        private void OnUninstall()
        {
            Action uninstall = UninstallApp;
            if (uninstall == null) return;
            if (!CanBeginReset("uninstall.ingame", "uninstall.confirm")) return;
            uninstall();
        }

        private void RefreshSlowStateAsync()
        {
            if (IsDisposed || !UiActive) return;
            RefreshMonitoringPresentation();
            if (Interlocked.CompareExchange(ref slowBusy, 1, 0) != 0)
            {
                Interlocked.Exchange(ref slowPending, 1);
                return;
            }
            Interlocked.Exchange(ref slowPending, 0);
            int version = Volatile.Read(ref slowVersion);
            Toggle auto = swAuto;
            SettingCard shader = cardShader;
            try
            {
                QueueUiStateWork(delegate
                {
                    bool task = false, taskKnown = false;
                    long shaderBytes = -1;
                    try { task = QueryStartupTaskState(); taskKnown = true; } catch { }
                    try { if (!ShaderCleanupBusy) shaderBytes = MeasureSettingsShaderCache(); } catch { }
                    try
                    {
                        PostUiStateResult(delegate
                        {
                            try
                            {
                                if (IsDisposed || !UiActive
                                    || !ReferenceEquals(auto, swAuto) || !ReferenceEquals(shader, cardShader)) return;
                                if (version == Volatile.Read(ref slowVersion)
                                    && auto != null && !auto.IsDisposed && taskKnown) auto.SetSilently(task);
                                if (shader != null && !shader.IsDisposed && !ShaderCleanupBusy && shaderBytes >= 0)
                                    shader.Value = CacheSweep.FmtBytes(shaderBytes);
                            }
                            finally { FinishSlowStateRefresh(); }
                        });
                    }
                    catch { Interlocked.Exchange(ref slowBusy, 0); }
                });
            }
            catch { Interlocked.Exchange(ref slowBusy, 0); }
        }

        private void FinishSlowStateRefresh()
        {
            // 结果被界面消费后才释放槽位。
            Interlocked.Exchange(ref slowBusy, 0);
            if (Interlocked.Exchange(ref slowPending, 0) != 0 && !IsDisposed && UiActive)
                RefreshSlowStateAsync();
        }

    }
}

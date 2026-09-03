// @author bdth 2074055628@qq.com
// 文件用途 构建设置页 只放应用自身的偏好与维护工具
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
        private PillButton btnBackdrop, btnBackdropReset;
        private TierPicker pickBackdropDim;
        private BackdropPreview backdropPreview;
        private Label lblBackdropState;
        private readonly Label[] modeAccentLabels = new Label[6];
        private Toggle swExtremeUnlock;
        private RoundPanel cardExtreme;
        private Label lblExtremeCode, lblExtremeState;

        private static readonly Color[] AccentPalette =
        {
            Color.FromArgb(239, 190, 66), Color.FromArgb(255, 140, 40), Color.FromArgb(255, 61, 82),
            Color.FromArgb(240, 90, 170), Color.FromArgb(178, 118, 255), Color.FromArgb(48, 180, 255),
            Color.FromArgb(40, 200, 200), Color.FromArgb(69, 224, 154), Color.FromArgb(150, 214, 80),
            Color.FromArgb(120, 140, 255),
        };
        private readonly List<ColorSwatch>[] modeSwatches =
            { new List<ColorSwatch>(), new List<ColorSwatch>(), new List<ColorSwatch>(),
              new List<ColorSwatch>(), new List<ColorSwatch>(), new List<ColorSwatch>() };
        private static volatile bool shaderCleaning;
        private int slowBusy;
        private int slowVersion, slowPending;
        private int wipeBusy;
        private PillButton btnWipeAll, btnUninstall;

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal Func<bool> StartupTaskQueryForTest;
        internal Func<bool, int> StartupTaskChangeForTest;
        internal Func<long> ShaderMeasureForTest;
        internal Action<Action> UiStateWorkQueueForTest;
        internal Action<Action> UiStatePostForTest;
        internal Action<string> StartupTaskWarningForTest;
#endif

        private bool QueryStartupTaskState()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (StartupTaskQueryForTest == null) throw new InvalidOperationException("Startup task query was not mocked");
            return StartupTaskQueryForTest();
#else
            return TaskHelper.TaskExists();
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
                new[] { Lang.T("set.tab.extreme"), Lang.T("sec.app"), Lang.T("sec.maint"), Lang.T("sec.appearance") },
                new[] { Lang.T("set.tab.extreme.h"), Lang.T("set.tab.app.h"), Lang.T("set.tab.maint.h"), Lang.T("set.tab.appearance.h") });
            pageSettings.Controls.Add(settingsTabs);
            y += 48;
            settingsTabPanels = MakeTabPanels(pageSettings, settingsTabs, 4, y);

            Control scroll = settingsTabPanels[0];
            int sy = 2, cardH;
            BuildExtremeCard(scroll, ref sy);

            scroll = settingsTabPanels[1]; sy = 2;
            swAuto = MakeSwitch(TaskHelper.TaskExistsCached(), OnAutoToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.autostart"), Lang.T("set.autostart.n"), swAuto, out cardH);
            sy += cardH + 8;

            swAutoHide = MakeSwitch(Settings.Load(AutoHideKey, AutoHideDefault), OnAutoHideToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.autohide"), Lang.T("set.autohide.n"), swAutoHide, out cardH);
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

            scroll = settingsTabPanels[2]; sy = 2;
            var resetActions = new Panel();
            resetActions.BackColor = Color.Transparent;
            resetActions.Size = new Size(Theme.S(274), Theme.S(32));

            btnWipeAll = new PillButton(Lang.T("btn.wipe"), BtnKind.Normal);
            btnWipeAll.Bg = Theme.Card;
            btnWipeAll.SetBounds(0, 0, Theme.S(136), Theme.S(32));
            btnWipeAll.Click += delegate { OnWipeAll(); };
            resetActions.Controls.Add(btnWipeAll);

            btnUninstall = new PillButton(Lang.T("btn.uninstall"), BtnKind.Danger);
            btnUninstall.Bg = Theme.Card;
            btnUninstall.SetBounds(Theme.S(146), 0, Theme.S(128), Theme.S(32));
            btnUninstall.Click += delegate { OnUninstall(); };
            resetActions.Controls.Add(btnUninstall);

            MakeAutoCard(scroll, 6, sy, ScrollContentW, 78, Lang.T("set.wipe.title"),
                Lang.T("set.wipe.desc"), resetActions, out cardH);
            sy += cardH + 8;

            var btnShaderGo = new PillButton(Lang.T("btn.clean"));
            btnShaderGo.Size = new Size(Theme.S(88), Theme.S(30));
            btnShaderGo.Click += (s, e) => OnShaderClean(btnShaderGo);
            cardShader = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("btn.shader"), Lang.T("set.shader.n"), btnShaderGo, out cardH);
            cardShader.Value = " ";
            sy += cardH + 8;

            var lblAbout = new Label();
            lblAbout.Text = Lang.F("set.about", App.VersionTag, Paths.Data);
            lblAbout.ForeColor = Theme.Faint; lblAbout.BackColor = Theme.Bg;
            lblAbout.Font = Theme.UI(8.25f, false);
            lblAbout.SetBounds(Theme.S(10), Theme.S(sy + 6), Theme.S(ScrollContentW - 10), Theme.S(18));
            scroll.Controls.Add(lblAbout);
            EnableCardCollapse(scroll);

            scroll = settingsTabPanels[3]; sy = 2;
            BuildAppearanceSection(scroll, ref sy);
        }

        private void BuildAppearanceSection(Control scroll, ref int sy)
        {

            // 高度随可见档位数走 极限解锁后配色区多一行
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

        // 第一档就是不要封面 连图一起删掉 关掉的东西不留残留是这套的老规矩
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

        // 极限模式解锁卡 常规卡片的加冕版 紫边 代码行 状态章 全部现成控件搭出来
        private void BuildExtremeCard(Control scroll, ref int sy)
        {
            // 强调色跟主题走 未解锁时极限不在配色行里 模式色在这儿就是改不掉的死色
            Color accent = Theme.Accent;
            var card = new RoundPanel
            {
                Fill = Theme.Card, Border = Col.Alpha(accent, 170), BackColor = Theme.Bg,
                Radius = Theme.S(14), AccentEdge = true
            };
            cardExtreme = card;
            card.SetBounds(Theme.S(6), Theme.S(sy), Theme.S(ScrollContentW), Theme.S(118));

            var code = new Label
            {
                Text = "EXTREME // GATED TIER", ForeColor = accent, BackColor = Theme.Card,
                Font = Theme.Mono(7.4f)
            };
            code.SetBounds(Theme.S(18), Theme.S(10), Theme.S(240), Theme.S(15));
            lblExtremeCode = code;

            bool visible = ExtremeMode.Visible;
            bool pending = ExtremeMode.PendingReboot;
            var state = new Label
            {
                Text = Lang.T(visible ? "extreme.card.state.ready"
                    : pending ? "extreme.card.state.pending" : "extreme.card.state.locked"),
                ForeColor = visible || pending ? accent : Theme.Dim,
                BackColor = Theme.Card, Font = Theme.Mono(7.4f),
                TextAlign = ContentAlignment.MiddleRight
            };
            state.SetBounds(card.Width - Theme.S(170), Theme.S(10), Theme.S(150), Theme.S(15));
            lblExtremeState = state;

            var title = new Label
            {
                Text = Lang.T("extreme.card.title"), ForeColor = Theme.Fg, BackColor = Theme.Card,
                Font = Theme.UI(10.6f, true), AutoEllipsis = true
            };
            title.SetBounds(Theme.S(18), Theme.S(28), Theme.S(300), Theme.S(22));

            var desc = new Label
            {
                Text = Lang.T("extreme.card.sub"), ForeColor = Theme.Dim, BackColor = Theme.Card,
                Font = Theme.UI(7.6f, false)
            };
            desc.SetBounds(Theme.S(18), Theme.S(52), card.Width - Theme.S(200), Theme.S(58));

            var sw = MakeSwitch(ExtremeMode.Unlocked, OnExtremeUnlockToggle);
            sw.Location = new Point(card.Width - Theme.S(72), Theme.S(32));
            swExtremeUnlock = sw;

            card.Controls.AddRange(new Control[] { code, state, title, desc, sw });

            // 极限档做成原子的 解锁即全开 回锁即全关 不给逐项管理入口
            //   某项熔断会自己退出集 崩溃保险丝两次异常重启自动回锁 手动逃生阀交给整档开关
            scroll.Controls.Add(card);
            sy += 118 + 8;
        }

        // 卡片颜色是构建时的快照 模式切换后主题强调色变了要跟着刷
        //   取模式色终值 与配色行标签同一惯例 不跟渐变动画逐帧走
        private void RefreshExtremeCardAccent()
        {
            if (cardExtreme == null || cardExtreme.IsDisposed) return;
            Color accent = Theme.ModeColor(gameMode.ActivePreset);
            cardExtreme.Border = Col.Alpha(accent, 170);
            if (lblExtremeCode != null) lblExtremeCode.ForeColor = accent;
            if (lblExtremeState != null && (ExtremeMode.Visible || ExtremeMode.PendingReboot))
                lblExtremeState.ForeColor = accent;
            cardExtreme.Invalidate();
        }

        private void OnExtremeUnlockToggle(object s, EventArgs e)
        {
            if (swExtremeUnlock.Checked)
            {
                if (!RequireElevationFor(swExtremeUnlock, false)) return;
                if (!PaviseDialog.Confirm(this, App.DisplayName, Lang.T("extreme.unlock.warn"), DlgKind.Warn))
                {
                    swExtremeUnlock.SetSilently(false);
                    return;
                }
                int failed = 0;
                int flipped = 0;
                Cursor = Cursors.WaitCursor;
                try
                {
                    IrqMutationBoundary.Run(delegate
                    {
                        int failedInner;
                        flipped = ExtremeMode.ApplyEnvItems(out failedInner);
                        failed = failedInner;
                    });
                }
                finally { Cursor = Cursors.Default; }
                ExtremeMode.MarkUnlocked();
                Logger.Log(Lang.T("log.extreme.1") + flipped);
                // 解锁确认时已经说明会立即重启 这里不再给拒绝的机会
                //   环境项已经写进系统 停在这个状态上 极限档既不出现也无法生效
                //   用户唯一能拿到的一致状态就是重启后 所以强制走完
                string restartNow = Lang.F("extreme.unlock.restart", flipped)
                    + (failed > 0 ? Lang.F("extreme.unlock.failed", failed) : "");
                PaviseDialog.Warn(this, App.DisplayName, restartNow);
                RestartComputer();
            }
            else
            {
                if (!PaviseDialog.Confirm(this, App.DisplayName, Lang.T("extreme.relock.confirm"), DlgKind.Warn))
                {
                    swExtremeUnlock.SetSilently(true);
                    return;
                }
                bool ok = false;
                Cursor = Cursors.WaitCursor;
                try { IrqMutationBoundary.Run(delegate { ok = ExtremeMode.RollbackEnvItems(); }); }
                finally { Cursor = Cursors.Default; }
                ExtremeMode.ClearUnlock();
                if (gameMode.Preset == PerformancePreset.Extreme)
                    gameMode.Preset = PerformancePreset.Competitive;
                Logger.Log(Lang.T("log.extreme.2"));
                PaviseDialog.Info(this, App.DisplayName, Lang.T(ok ? "extreme.relock.done" : "extreme.relock.partial"));
                BeginInvoke((MethodInvoker)RebuildUi);
            }
        }

        // 解锁向导承诺的立即重启 5 秒缓冲留给系统落盘
        // 强制重启 不给应用程序否决权
        //   不加 /f 时一个有未保存文档的程序就能把整个重启拦下来
        //   用户在解锁确认里已经同意立即重启 这里 10 秒缓冲留给他保存东西
        //   起不来就明说 别让用户以为已经重启完成
        private void RestartComputer()
        {
            int code = -1;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(
                    System.IO.Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /f /t 10");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
                {
                    if (p != null && p.WaitForExit(8000)) code = p.ExitCode;
                    else code = 0;
                }
            }
            catch { code = -1; }
            if (code == 0) return;
            Logger.Warn(Lang.T("log.extreme.5") + code);
            PaviseDialog.Warn(this, App.DisplayName, Lang.T("extreme.unlock.restartfail"));
        }

        private static string ModeName(PerformancePreset m)
        {
            return m == PerformancePreset.Competitive ? Lang.T("preset.competitive")
                : m == PerformancePreset.Extreme ? Lang.T("preset.extreme")
                : m == PerformancePreset.Handheld ? Lang.T("preset.handheld")
                : m == PerformancePreset.Custom ? Lang.T("preset.custom") : Lang.T("preset.standard");
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
            // 在这条命令之前发起的延迟读 不能推翻成功的选择
            // 也不能推翻失败路径上那次新鲜回读
            Interlocked.Increment(ref slowVersion);
            int rc = ChangeStartupTask(swAuto.Checked);
            if (rc != 0)
            {
                // 先把原因取出来 TaskExists 会再跑一次 schtasks 把它冲掉
                string reason = TaskHelper.LastSchtasksError;
                swAuto.SetSilently(QueryStartupTaskState());
                WarnStartupTaskFailure(reason);
            }
        }

        private void OnShaderClean(PillButton btn)
        {
            if (shaderCleaning)
            {
                if (cardShader != null) cardShader.Value = Lang.T("shader.busy");
                return;
            }
            if (gameMode != null && gameMode.IsActive)
            {
                PaviseDialog.Warn(this, App.DisplayName, Lang.T("shader.ingame"));
                return;
            }
            if (!PaviseDialog.Confirm(this, App.DisplayName, Lang.T("shader.confirm"), DlgKind.Warn)) return;
            btn.Enabled = false;
            shaderCleaning = true;
            if (cardShader != null) cardShader.Value = Lang.T("shader.busy");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                CacheSweep.Result cr = ShaderCache.Clean();
                long left = ShaderCache.MeasureBytes();
                Logger.Log(Lang.T("log.panelformsettingspage.1") + CacheSweep.FmtBytes(cr.FreedBytes)
                    + (cr.FailedFiles > 0 ? " " + cr.FailedFiles + Lang.T("log.panelformsettingspage.2") : ""));
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
                        PaviseDialog.Info(this, App.DisplayName, msg);
                    }));
                }
                catch { }
            });
        }

        private void SetResetActionsEnabled(bool enabled)
        {
            if (btnWipeAll != null && !btnWipeAll.IsDisposed) btnWipeAll.Enabled = enabled;
            if (btnUninstall != null && !btnUninstall.IsDisposed) btnUninstall.Enabled = enabled;
        }

        private bool CanBeginReset(string inGameMessage, string confirmation)
        {
            if (gameMode.IsActive)
            {
                PaviseDialog.Warn(this, App.DisplayName, Lang.T(inGameMessage));
                return false;
            }
            if (!PaviseDialog.Confirm(this, App.DisplayName, Lang.T(confirmation), DlgKind.Danger)) return false;
            if (Interlocked.Exchange(ref wipeBusy, 1) != 0) return false;
            SetResetActionsEnabled(false);
            Cursor = Cursors.WaitCursor;
            return true;
        }

        private void OnWipeAll()
        {
            Action reset = ResetApp;
            if (reset == null) return;
            if (!CanBeginReset("wipe.ingame", "wipe.confirm")) return;
            // 永久的停止 还原 删除 退出这套顺序归 Program 管 恐慌保持
            // 会过期 否则可能在擦除过程中把优化重新拉起来
            reset();
        }

        private void OnUninstall()
        {
            Func<string> uninstall = UninstallApp;
            if (uninstall == null) return;
            if (!CanBeginReset("uninstall.ingame", "uninstall.confirm")) return;

            string failure = null;
            try { failure = uninstall(); }
            catch (Exception ex) { failure = Lang.F("uninstall.startfailed.detail", ex.GetType().Name); }
            if (string.IsNullOrEmpty(failure)) return;

            Interlocked.Exchange(ref wipeBusy, 0);
            SetResetActionsEnabled(true);
            Cursor = Cursors.Default;
            PaviseDialog.Warn(this, App.DisplayName, failure);
        }

        private void RefreshSlowStateAsync()
        {
            if (IsDisposed || !UiActive) return;
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
                    try { if (!shaderCleaning) shaderBytes = MeasureSettingsShaderCache(); } catch { }
                    try
                    {
                        PostUiStateResult(delegate
                        {
                            try
                            {
                                if (IsDisposed || !UiActive
                                    || !ReferenceEquals(auto, swAuto) || !ReferenceEquals(shader, cardShader)) return;
                                // 启动项命令只让它自己那次任务读失效
                                // 缓存测量和这个选择无关
                                if (version == Volatile.Read(ref slowVersion)
                                    && auto != null && !auto.IsDisposed && taskKnown) auto.SetSilently(task);
                                if (shader != null && !shader.IsDisposed && !shaderCleaning && shaderBytes >= 0)
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
            // 槽位留到它的界面结果被消费掉为止 免得排队的结果
            // 互相超车 被挡住的请求合并成一次
            Interlocked.Exchange(ref slowBusy, 0);
            if (Interlocked.Exchange(ref slowPending, 0) != 0 && !IsDisposed && UiActive)
                RefreshSlowStateAsync();
        }

        private void ShowRestoreAllResult(
            bool completed, int failed, int attempted)
        {
            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    if (IsDisposed) return;
                    Cursor = Cursors.Default;
                    string message = Lang.T(
                        completed ? "panic.done" : "panic.timeout");
                    if (!completed)
                        message += "\r\n\r\n" + Lang.F(
                            "panic.failedcount", failed, attempted);
                    if (completed) PaviseDialog.Success(this, App.DisplayName, message);
                    else PaviseDialog.Warn(this, App.DisplayName, message);
                    SyncAllToggles();
                }));
            }
            catch { }
        }
    }
}

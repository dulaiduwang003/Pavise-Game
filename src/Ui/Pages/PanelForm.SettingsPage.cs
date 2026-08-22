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
        private Toggle swAuto, swAutoHide, swIrqProbe;
        private SettingCard cardShader;

        private static readonly Color[] AccentPalette =
        {
            Color.FromArgb(239, 190, 66), Color.FromArgb(255, 140, 40), Color.FromArgb(255, 61, 82),
            Color.FromArgb(240, 90, 170), Color.FromArgb(178, 118, 255), Color.FromArgb(48, 180, 255),
            Color.FromArgb(40, 200, 200), Color.FromArgb(69, 224, 154), Color.FromArgb(150, 214, 80),
            Color.FromArgb(120, 140, 255),
        };
        private readonly List<ColorSwatch>[] modeSwatches =
            { new List<ColorSwatch>(), new List<ColorSwatch>(), new List<ColorSwatch>() };
        private static volatile bool shaderCleaning;
        private int slowBusy;
        private int wipeBusy;

        private void BuildSettingsPage()
        {
            int y = PageHeader(pageSettings, Lang.T("nav.set"), Lang.T("set.hint"), 2);

            var scroll = new DBPanel();
            scroll.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            scroll.BackColor = Theme.Bg;
            scroll.AutoScroll = true;
            Native.Dark(scroll);
            pageSettings.Controls.Add(scroll);

            int sy = 2, cardH;
            Section(scroll, Lang.T("sec.app"), 6, sy); sy += 24;

            swAuto = MakeSwitch(TaskHelper.TaskExistsCached(), OnAutoToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.autostart"), Lang.T("set.autostart.n"), swAuto, out cardH);
            sy += cardH + 8;

            swAutoHide = MakeSwitch(Settings.Load(AutoHideKey, AutoHideDefault), OnAutoHideToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.autohide"), Lang.T("set.autohide.n"), swAutoHide, out cardH);
            sy += cardH + 8;

            swIrqProbe = MakeSwitch(IrqSessionProbe.EnabledSetting, OnIrqProbeToggle);
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 96, Lang.T("set.irqprobe"), Lang.T("set.irqprobe.n"), swIrqProbe, out cardH);
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

            sy += 10;
            Section(scroll, Lang.T("sec.maint"), 6, sy); sy += 24;

            var btnWipe = new PillButton(Lang.T("btn.wipe"), BtnKind.Danger);
            btnWipe.Bg = Theme.Card;
            btnWipe.Size = new Size(Theme.S(136), Theme.S(32));
            btnWipe.Click += delegate { OnWipeAll(btnWipe); };
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 78, Lang.T("set.wipe.title"), Lang.T("set.wipe.desc"), btnWipe, out cardH);
            sy += cardH + 8;

            var btnShaderGo = new PillButton(Lang.T("btn.clean"));
            btnShaderGo.Size = new Size(Theme.S(88), Theme.S(30));
            btnShaderGo.Click += (s, e) => OnShaderClean(btnShaderGo);
            cardShader = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("btn.shader"), Lang.T("set.shader.n"), btnShaderGo, out cardH);
            cardShader.Value = " ";
            sy += cardH + 8;

            sy += 10;
            BuildThemeColorSection(scroll, ref sy);
            sy += 10;

            var lblAbout = new Label();
            lblAbout.Text = Lang.F("set.about", App.VersionTag, Paths.Data);
            lblAbout.ForeColor = Theme.Faint; lblAbout.BackColor = Theme.Bg;
            lblAbout.Font = Theme.UI(8.25f, false);
            lblAbout.SetBounds(Theme.S(10), Theme.S(sy), Theme.S(ScrollContentW - 10), Theme.S(18));
            scroll.Controls.Add(lblAbout);

            EnableCardCollapse(scroll);
        }

        private void BuildThemeColorSection(Control scroll, ref int sy)
        {
            Section(scroll, Lang.T("sec.theme"), 6, sy); sy += 24;

            var hint = new Label();
            hint.Text = Lang.T("theme.hint");
            hint.ForeColor = Theme.Dim; hint.BackColor = Theme.Bg;
            hint.Font = Theme.UI(8.0f, false); hint.AutoEllipsis = true;
            hint.SetBounds(Theme.S(16), Theme.S(sy), Theme.S(ScrollContentW - 24), Theme.S(18));
            scroll.Controls.Add(hint);
            sy += 26;

            PerformancePreset[] modes =
                { PerformancePreset.Standard, PerformancePreset.Competitive, PerformancePreset.Custom };
            foreach (PerformancePreset mode in modes)
            {
                PerformancePreset capMode = mode;
                modeSwatches[(int)mode].Clear();

                var lbl = new Label();
                lbl.Text = ModeName(mode);
                lbl.ForeColor = Theme.Fg; lbl.BackColor = Theme.Bg;
                lbl.Font = Theme.UI(8.6f, false);
                lbl.TextAlign = ContentAlignment.MiddleLeft;
                lbl.SetBounds(Theme.S(16), Theme.S(sy), Theme.S(60), Theme.S(26));
                scroll.Controls.Add(lbl);

                int sx = 84;
                bool overridden = Theme.HasModeColorOverride(mode);

                var def = new ColorSwatch(Theme.ModeColorBuiltin(mode));
                def.IsDefault = true; def.Selected = !overridden;
                def.SetBounds(Theme.S(sx), Theme.S(sy), Theme.S(26), Theme.S(26));
                def.Picked = delegate { OnModeColorPick(capMode, Color.Empty); };
                scroll.Controls.Add(def); modeSwatches[(int)mode].Add(def);
                sx += 32;

                Color eff = Theme.ModeColor(mode);
                foreach (Color paletteColor in AccentPalette)
                {
                    Color cc = paletteColor;
                    var sw = new ColorSwatch(cc);
                    sw.Selected = overridden && SameRgb(eff, cc);
                    sw.SetBounds(Theme.S(sx), Theme.S(sy), Theme.S(26), Theme.S(26));
                    sw.Picked = delegate { OnModeColorPick(capMode, cc); };
                    scroll.Controls.Add(sw); modeSwatches[(int)mode].Add(sw);
                    sx += 32;
                }
                sy += 34;
            }
            sy += 10;
        }

        private static string ModeName(PerformancePreset m)
        {
            return m == PerformancePreset.Competitive ? Lang.T("preset.competitive")
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

            if (modeVisualInitialized && mode == visualMode)
            {
                using (Icon icon = IconArt.MakeMultiIcon(visualMode, visualEnabled)) SetRuntimeIcon(icon);
                if (nav != null) nav.RefreshLogo();
                if (paviseCore != null) paviseCore.RefreshVisual();
            }
            if (UiActive) UiClock.Wake();
        }

        private void OnAutoToggle(object s, EventArgs e)
        {
            int rc = swAuto.Checked ? TaskHelper.CreateStartupTask() : TaskHelper.DeleteStartupTask();
            if (rc != 0)
            {
                PaviseDialog.Warn(this, App.DisplayName, Lang.T("msg.taskfail"));
                swAuto.SetSilently(TaskHelper.TaskExists());
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

        private void OnWipeAll(PillButton btn)
        {
            if (gameMode.IsActive)
            {
                PaviseDialog.Warn(this, App.DisplayName, Lang.T("wipe.ingame"));
                return;
            }
            if (!PaviseDialog.Confirm(this, App.DisplayName, Lang.T("wipe.confirm"), DlgKind.Danger)) return;
            if (Interlocked.Exchange(ref wipeBusy, 1) != 0) return;
            btn.Enabled = false;
            Cursor = Cursors.WaitCursor;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                int files = 0;
                string unrestored = null;
                try
                {
                    gameMode.PanicRestore();
                    tamer.PanicRestore();
                    ok = LegacyPurge.WipeAll(Paths.Data, true, Lang.T("t.panelformsettingspage.3"), out files, out unrestored);
                }
                catch (Exception ex)
                {
                    Logger.LogFailure(Lang.T("t.panelformsettingspage.3"), ex);
                    unrestored = ex.Message;
                }
                Interlocked.Exchange(ref wipeBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        Cursor = Cursors.Default;
                        if (!btn.IsDisposed) btn.Enabled = true;
                        if (ok)
                        {
                            PaviseDialog.Success(this, App.DisplayName, Lang.F("wipe.done", files));
                            Action exit = ExitApp;
                            if (exit != null) exit();
                        }
                        else if (unrestored != null)
                            PaviseDialog.Warn(this, App.DisplayName, Lang.F("wipe.failed", unrestored));
                        else PaviseDialog.Warn(this, App.DisplayName, Lang.T("wipe.regfail"));
                    });
                }
                catch { }
            });
        }

        private void RefreshSlowStateAsync()
        {
            if (!UiActive) return;
            if (Interlocked.Exchange(ref slowBusy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool task = false;
                long shaderBytes = -1;
                try { task = TaskHelper.TaskExists(); } catch { }
                try { if (!shaderCleaning) shaderBytes = ShaderCache.MeasureBytes(); } catch { }
                Interlocked.Exchange(ref slowBusy, 0);
                if (!UiActive) return;
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (IsDisposed || !UiActive) return;
                        if (swAuto != null) swAuto.SetSilently(task);
                        if (cardShader != null && !shaderCleaning && shaderBytes >= 0)
                            cardShader.Value = CacheSweep.FmtBytes(shaderBytes);
                    }));
                }
                catch { }
            });
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

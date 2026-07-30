// @author bdth 2074055628@qq.com
// 文件用途 构建设置 报告和关于页面

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AegisApp
{
    internal partial class PanelForm
    {

        private int PageHeader(DBPanel page, string title, string sub, int subLines)
        {
            var rail = new AccentLine();
            rail.SetBounds(Theme.S(26), Theme.S(5), Theme.S(28), Math.Max(1, Theme.S(2)));
            page.Controls.Add(rail);

            var sys = new Label();
            sys.Text = "AEGIS  //  CONTROL";
            sys.ForeColor = Theme.Faint; sys.BackColor = Theme.Bg;
            sys.Font = Theme.Mono(6.75f);
            sys.UseCompatibleTextRendering = false;
            sys.SetBounds(Theme.S(62), 0, Theme.S(190), Theme.S(14));
            page.Controls.Add(sys);

            var t = new Label();
            t.Text = title;
            t.ForeColor = Theme.Fg; t.BackColor = Theme.Bg;
            t.Font = Theme.UI(14.5f, true);
            t.UseCompatibleTextRendering = false;
            t.SetBounds(Theme.S(26), Theme.S(17), Theme.S(ContentW - 80), Theme.S(32));
            page.Controls.Add(t);
            int y = 50;
            if (!string.IsNullOrEmpty(sub))
            {
                var s2 = new Label();
                s2.Text = sub;
                s2.ForeColor = Theme.Dim; s2.BackColor = Theme.Bg;
                s2.Font = Theme.UI(8.5f, false);
                s2.UseCompatibleTextRendering = false;
                s2.AutoEllipsis = true;
                s2.SetBounds(Theme.S(27), Theme.S(y), Theme.S(ContentW - 2), Theme.S(16 * subLines + 2));
                page.Controls.Add(s2);
                y += 16 * subLines + 8;
            }
            return y + 8;
        }

        private Label Section(Control parent, string text, int x, int y)
        {
            var mark = new AccentLine();
            mark.SetBounds(Theme.S(x + 4), Theme.S(y + 5), Theme.S(3), Theme.S(8));
            parent.Controls.Add(mark);
            var l = new Label();
            l.Text = text;
            l.ForeColor = Theme.Faint; l.BackColor = Theme.Bg;
            l.Font = Theme.UI(8.25f, true);
            l.UseCompatibleTextRendering = false;
            l.SetBounds(Theme.S(x + 14), Theme.S(y), Theme.S(400), Theme.S(18));
            parent.Controls.Add(l);
            return l;
        }

        private Toggle MakeSwitch(bool on, EventHandler handler)
        {
            var t = new Toggle();
            t.Size = new Size(Theme.S(46), Theme.S(24));
            t.Bg = Theme.Card;
            t.SetSilently(on);
            if (handler != null) t.CheckedChanged += handler;
            return t;
        }

        private int AutoCardHeight(string desc, int cardW, Control host, int minHeight)
        {
            if (string.IsNullOrEmpty(desc)) return minHeight;
            int padL = Theme.S(18);
            int reserve = padL + (host != null ? host.Width + Theme.S(14) : 0);
            int textW = Theme.S(cardW) - padL - reserve;
            if (textW <= 0) return minHeight;
            Font font = Theme.UI(8.5f, false);
            int lineH = TextRenderer.MeasureText("Ag", font).Height;
            if (lineH <= 0) return minHeight;
            int need = TextRenderer.MeasureText(
                desc, font, new Size(textW, int.MaxValue), TextFormatFlags.WordBreak).Height;
            int lines = (need + lineH - 1) / lineH;
            if (lines < 1) lines = 1;
            int scale100 = Theme.S(100);
            if (scale100 <= 0) return minHeight;
            int px = lines * lineH + Theme.S(44);
            int logical = (px * 100 + scale100 - 1) / scale100;
            return logical > minHeight ? logical : minHeight;
        }

        private SettingCard MakeAutoCard(
            Control parent, int x, int y, int w, int minH, string title, string desc, Control host, out int used)
        {
            used = AutoCardHeight(desc, w, host, minH);
            return MakeCard(parent, x, y, w, used, title, desc, host);
        }

        private SettingCard MakeCard(Control parent, int x, int y, int w, int h, string title, string desc, Control host)
        {
            var c = new SettingCard();
            c.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
            c.Title = title;
            c.Desc = desc ?? "";
            if (host != null) c.Host(host);
            parent.Controls.Add(c);
            return c;
        }

        private void RefreshTameList()
        {
            while (tameList.Controls.Count > 0) tameList.Controls[0].Dispose();
            tameGroups.Clear();
            tameCards.Clear();
            tameToggles.Clear();
            int pitch = 90, idx = 0;
            foreach (AcGroup g in AntiCheatCatalog.Groups)
            {
                string note = Lang.T("ac." + g.Key + ".d") + "  ·  " + string.Join(" / ", g.Procs);
                AddTameCard(g.Key, Lang.T("ac." + g.Key + ".n"), note, idx * pitch);
                idx++;
            }
        }

        private void AddTameCard(string key, string title, string note, int y)
        {
            var sw = MakeSwitch(tamer.IsGroupEnabled(key), null);
            sw.CheckedChanged += (s, e) => tamer.SetGroupEnabled(key, sw.Checked);

            var lvl = new TierPicker();
            lvl.Size = new Size(Theme.S(168), Theme.S(28));
            lvl.Value = tamer.GroupLevel(key);
            lvl.Changed = delegate(SuppressionLevel v) { tamer.SetGroupLevel(key, v); };

            var wrap = new DBPanel();
            wrap.Size = new Size(lvl.Width + Theme.S(12) + sw.Width, Theme.S(30));
            wrap.BackColor = Theme.Card;
            lvl.Location = new Point(0, (wrap.Height - lvl.Height) / 2);
            sw.Location = new Point(lvl.Width + Theme.S(12), (wrap.Height - sw.Height) / 2);
            wrap.Controls.Add(lvl);
            wrap.Controls.Add(sw);

            var card = new SettingCard();
            card.SetBounds(Theme.S(6), Theme.S(y), Theme.S(ScrollContentW), Theme.S(82));
            card.Title = title;
            card.Desc = note;
            card.Host(wrap);

            tameList.Controls.Add(card);
            tameGroups.Add(new AcGroup(key, title, "", false, new string[0]));
            tameCards.Add(card);
            tameToggles.Add(sw);
        }

        private void BuildSettingsPage()
        {
            int y = PageHeader(pageSettings, Lang.T("nav.set"), Lang.T("set.hint"), 1);

            var scroll = new DBPanel();
            scroll.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            scroll.BackColor = Theme.Bg;
            scroll.AutoScroll = true;
            Native.Dark(scroll);
            pageSettings.Controls.Add(scroll);

            int sy = 2;
            Section(scroll, Lang.T("sec.pergame"), 6, sy); sy += 24;

            swGpu = MakeSwitch(gameMode.GpuHighPerf, null);
            swGpu.CheckedChanged += (s, e) => gameMode.GpuHighPerf = swGpu.Checked;
            int cardH0;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.gpu"), Lang.T("set.gpu.n"), swGpu, out cardH0);
            sy += cardH0 + 8;

            swFso = MakeSwitch(gameMode.DisableFso, null);
            swFso.CheckedChanged += (s, e) => gameMode.DisableFso = swFso.Checked;
            int cardH1;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.fso"), Lang.T("set.fso.n"), swFso, out cardH1);
            sy += cardH1 + 8;

            bool nvOk = NvApi.Available;
            var swNvMax = MakeSwitch(gameMode.NvMaxPerf, null);
            swNvMax.CheckedChanged += (s, e) => gameMode.NvMaxPerf = swNvMax.Checked;
            swNvMax.Enabled = nvOk;
            int cardHNv0;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvmax"),
                Lang.T(nvOk ? "set.nvmax.n" : "set.nv.none"), swNvMax, out cardHNv0);
            sy += cardHNv0 + 8;

            var swNvLat = MakeSwitch(gameMode.NvLowLatency, null);
            swNvLat.CheckedChanged += (s, e) => gameMode.NvLowLatency = swNvLat.Checked;
            swNvLat.Enabled = nvOk;
            int cardHNv1;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvlat"),
                Lang.T(nvOk ? "set.nvlat.n" : "set.nv.none"), swNvLat, out cardHNv1);
            sy += cardHNv1 + 8;

            var frlPicker = new TierPicker();
            frlPicker.Size = new Size(Theme.S(216), Theme.S(28));
            frlPicker.Labels = new[] { Lang.T("frl.off"), "60", "120", Lang.T("frl.screen") };
            string frlMode = gameMode.NvFrlMode;
            frlPicker.Index = frlMode == "60" ? 1 : frlMode == "120" ? 2 : frlMode == "screen" ? 3 : 0;
            frlPicker.IndexChanged = delegate(int i)
            {
                gameMode.NvFrlMode = i == 1 ? "60" : i == 2 ? "120" : i == 3 ? "screen" : "off";
            };
            frlPicker.Enabled = nvOk;
            int cardHNv2;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.nvfrl"),
                Lang.T(nvOk ? "set.nvfrl.n" : "set.nv.none"), frlPicker, out cardHNv2);
            sy += cardHNv2 + 8;

            sy += 10;
            Section(scroll, Lang.T("sec.system"), 6, sy); sy += 24;

            swAuto = MakeSwitch(TaskHelper.TaskExistsCached(), OnAutoToggle);
            int cardH2;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.autostart"), Lang.T("set.autostart.n"), swAuto, out cardH2);
            sy += cardH2 + 8;

            swAutoHide = MakeSwitch(Settings.Load(AutoHideKey, false), OnAutoHideToggle);
            int cardH3;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.autohide"), Lang.T("set.autohide.n"), swAutoHide, out cardH3);
            sy += cardH3 + 8;

            swHags = MakeSwitch(HagsTweak.EnabledByAegis || HagsTweak.CurrentlyOn(), OnHagsToggle);
            int cardH4;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.hags"), Lang.T("set.hags.n"), swHags, out cardH4);
            sy += cardH4 + 8;

            swIrqAffinity = MakeSwitch(InterruptAffinityTweak.EnabledByAegis, OnIrqAffinityToggle);
            int cardH5;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.irqaffinity"), Lang.T("set.irqaffinity.n"), swIrqAffinity, out cardH5);
            sy += cardH5 + 8;

            swNetAffinity = MakeSwitch(NetworkAffinityTweak.EnabledByAegis, OnNetAffinityToggle);
            int cardH6;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.netaffinity"), Lang.T("set.netaffinity.n"), swNetAffinity, out cardH6);
            sy += cardH6 + 8;

            swUsbAffinity = MakeSwitch(UsbInterruptAffinityTweak.EnabledByAegis, OnUsbAffinityToggle);
            int cardHUsb;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.usbaffinity"), Lang.T("set.usbaffinity.n"), swUsbAffinity, out cardHUsb);
            sy += cardHUsb + 8;

            swVbs = MakeSwitch(VbsTweak.DisabledByAegis, OnVbsToggle);
            int cardH7;
            cardVbs = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.vbs"), "…", swVbs, out cardH7);
            sy += cardH7 + 8;

            swMpo = MakeSwitch(MpoTweak.DisabledByAegis || MpoTweak.CurrentlyDisabled(), OnMpoToggle);
            int cardHMpo;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.mpo"), Lang.T("set.mpo.n"), swMpo, out cardHMpo);
            sy += cardHMpo + 8;

            sy += 10;
            Section(scroll, Lang.T("sec.maint"), 6, sy); sy += 24;

            var btnRestore = new PillButton(Lang.T("btn.panic"), BtnKind.Danger);
            btnRestore.Bg = Theme.Card;
            btnRestore.Size = new Size(Theme.S(136), Theme.S(32));
            btnRestore.Click += delegate { RestoreAllNow(); };
            int cardH8;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 78, Lang.T("v15.restore.title"), Lang.T("v15.restore.desc"), btnRestore, out cardH8);
            sy += cardH8 + 8;

            var btnDefender = new PillButton(Lang.T("btn.open"), BtnKind.Normal);
            btnDefender.Bg = Theme.Card;
            btnDefender.Size = new Size(Theme.S(120), Theme.S(32));
            btnDefender.Click += delegate
            {

                Cursor = Cursors.WaitCursor;
                DefenderExclusionDialog dlg;
                try { dlg = new DefenderExclusionDialog(gameMode); }
                finally { Cursor = Cursors.Default; }
                using (dlg) dlg.ShowDialog(this);
            };
            int cardH9;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("def.open"), Lang.T("def.open.sub"), btnDefender, out cardH9);
            sy += cardH9 + 8;

            var btnShaderGo = new PillButton(Lang.T("btn.clean"));
            btnShaderGo.Size = new Size(Theme.S(88), Theme.S(30));
            btnShaderGo.Click += (s, e) => OnShaderClean(btnShaderGo);
            int cardH10;
            cardShader = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("btn.shader"), Lang.T("set.shader.n"), btnShaderGo, out cardH10);
            cardShader.Value = "…";
            sy += 64;

            sy += 10;

            var lblAbout = new Label();
            lblAbout.Text = Lang.F("set.about", App.VersionTag, Paths.Data);
            lblAbout.ForeColor = Theme.Faint; lblAbout.BackColor = Theme.Bg;
            lblAbout.Font = Theme.UI(8.25f, false);
            lblAbout.SetBounds(Theme.S(10), Theme.S(sy), Theme.S(ScrollContentW - 10), Theme.S(18));
            scroll.Controls.Add(lblAbout);
        }

        private void RestoreAllNow()
        {
            if (Interlocked.Exchange(ref restoreBusy, 1) != 0) return;
            Cursor = Cursors.WaitCursor;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool completed = false;
                int attempted = 0;
                int failed = 0;
                try
                {
                    if (lolService.HasRecordedHeadlessState())
                    {
                        attempted++;
                        if (!TryRestoreRecordedItem(
                                "英雄联盟大厅界面",
                                delegate { return lolService.RestoreNow(); }))
                            failed++;
                    }
                    attempted++;
                    if (!TryRestoreRecordedItem(
                            "游戏模式",
                            delegate { return gameMode.PanicRestore(); }))
                        failed++;
                    attempted++;
                    if (!TryRestoreRecordedItem(
                            "反作弊压制",
                            delegate { return tamer.PanicRestore(); }))
                        failed++;

                    completed = failed == 0;
                    Logger.Log("一键全部恢复：已执行 " + attempted
                        + " 项，失败 " + failed + " 项；"
                        + (completed ? "恢复流程已完成" : "未确认项保留并继续重试"));
                }
                catch (Exception ex)
                {
                    completed = false;
                    attempted++;
                    failed++;
                    Logger.LogFailure("一键全部恢复流程", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref restoreBusy, 0);
                    ShowRestoreAllResult(completed, failed, attempted);
                }
            });
        }

        private static bool TryRestoreRecordedItem(
            string name, Func<bool> restore)
        {
            try
            {
                bool restored = restore != null && restore();
                if (!restored) Logger.Log("一键全部恢复：" + name + " 未确认完成");
                return restored;
            }
            catch (Exception ex)
            {
                Logger.LogFailure("一键全部恢复：" + name, ex);
                return false;
            }
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
                    MessageBox.Show(
                        this,
                        message,
                        App.DisplayName,
                        MessageBoxButtons.OK,
                        completed
                            ? MessageBoxIcon.Information
                            : MessageBoxIcon.Warning);
                    SyncAllToggles();
                }));
            }
            catch { }
        }

        private void BuildAboutPage()
        {
            int y = PageHeader(pageAbout, Lang.T("nav.about"), "", 0);

            var hero = MakeConsolePanel(pageAbout, ContentX, y, ContentW, 134, true);
            var pbIcon = new PictureBox();
            pbIcon.SetBounds(Theme.S(24), Theme.S(20), Theme.S(76), Theme.S(76));
            pbIcon.BackColor = Color.Transparent;
            pbIcon.SizeMode = PictureBoxSizeMode.Zoom;
            OwnedImage(pbIcon, IconArt.Render(Theme.S(76)));

            CardLabel(hero, App.DisplayName, 120, 17, 250, 35, 18f, true, Theme.Fg);
            CardLabel(hero, App.VersionTag + "  //  " + Lang.T("v15.about.identity"), 122, 52, ContentW - 150, 20, 8f, true, Theme.Accent);
            CardLabel(hero, Lang.T("about.desc").Replace("\r\n", "  ·  "), 122, 75, ContentW - 150, 48, 8.2f, false, Theme.Dim);
            hero.Controls.Add(pbIcon);

            int cardsY = y + 150;
            int infoW = 476, gap = 16, updateW = ContentW - infoW - gap;
            const int cardH = 268;
            var card = MakeConsolePanel(pageAbout, ContentX, cardsY, infoW, cardH, false);
            CardLabel(card, "PROJECT // IDENTITY", 20, 15, infoW - 40, 20, 7.6f, true, Theme.Faint);

            string[] rowKeys = { "about.author", "about.repo", "about.lic" };
            string[] rowVals = { App.Author + " · " + App.AuthorEmail, App.RepoUrl.Replace("https://", ""), "MIT License" };
            for (int i = 0; i < 3; i++)
            {
                int ry = 45 + i * 56;
                CardLabel(card, Lang.T(rowKeys[i]).ToUpperInvariant(), 20, ry, 108, 18, 7.4f, true, Theme.Faint);
                var lblV = CardLabel(card, rowVals[i], 132, ry - 2, infoW - 152, 24, 9.2f, i == 1, i == 1 ? Theme.Accent : Theme.Fg);
                if (i == 1)
                {
                    lblV.Cursor = Cursors.Hand;
                    lblV.Click += (s, e) => { try { using (Process.Start(App.RepoUrl)) { } } catch { } };
                }
            }

            bool unseenNotes = ReleaseNotes.HasUnseen;
            var btnNotes = new PillButton(Lang.T("notes.open") + (unseenNotes ? "  ·  NEW" : ""),
                unseenNotes ? BtnKind.Primary : BtnKind.Normal);
            btnNotes.Bg = Theme.Card;
            btnNotes.SetBounds(Theme.S(20), Theme.S(214), Theme.S(infoW - 40), Theme.S(38));
            btnNotes.Click += delegate
            {
                using (var dlg = new ReleaseNotesDialog()) dlg.ShowDialog(this);
                btnNotes.Text = Lang.T("notes.open");
                btnNotes.Kind = BtnKind.Normal;
                btnNotes.Invalidate();
            };
            card.Controls.Add(btnNotes);

            var update = MakeConsolePanel(pageAbout, ContentX + infoW + gap, cardsY, updateW, cardH, true);
            CardLabel(update, Lang.T("v15.about.update"), 20, 16, updateW - 40, 22, 9.5f, true, Theme.Fg);
            CardLabel(update, Lang.T("v15.about.update.sub"), 20, 45, updateW - 40, 42, 7.8f, false, Theme.Dim);

            var btnCheck = new PillButton(Lang.T("btn.checkupd"), BtnKind.Primary);
            btnCheck.Bg = Theme.Card;
            btnCheck.SetBounds(Theme.S(20), Theme.S(96), Theme.S(updateW - 40), Theme.S(40));

            var btnDl = new PillButton(Lang.T("btn.download"));
            btnDl.Bg = Theme.Card;
            btnDl.SetBounds(Theme.S(20), Theme.S(144), Theme.S(updateW - 40), Theme.S(34));
            btnDl.Visible = false;

            var lblUpd = CardLabel(update, App.VersionTag, 20, 150, updateW - 40, 58, 8f, false, Theme.Faint);

            string dlUrl = null;
            btnDl.Click += (s, e) => { if (dlUrl != null && dlUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) try { using (Process.Start(dlUrl)) { } } catch { } };

            btnCheck.Click += (s, e) =>
            {
                btnCheck.Enabled = false;
                btnDl.Visible = false;
                lblUpd.Top = Theme.S(150);
                lblUpd.ForeColor = Theme.Dim;
                lblUpd.Text = Lang.T("upd.checking");
                UpdateChecker.CheckAsync(r =>
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)(() =>
                        {
                            if (btnCheck.IsDisposed) return;
                            btnCheck.Enabled = true;
                            if (!r.Ok)
                            {
                                lblUpd.ForeColor = Theme.Danger;
                                lblUpd.Text = Lang.T("upd.fail");
                                Logger.Log("检查更新失败：" + r.Error);
                            }
                            else if (r.Newer)
                            {
                                dlUrl = r.Url;
                                btnDl.Visible = true;
                                lblUpd.Top = Theme.S(184);
                                lblUpd.Height = Theme.S(36);
                                lblUpd.ForeColor = Theme.Green;
                                lblUpd.Text = Lang.F("upd.newver", r.Latest, App.VersionTag);
                                Logger.Log("检查更新：发现新版本 " + r.Latest + "（当前 " + App.VersionTag + "）");
                            }
                            else
                            {
                                lblUpd.ForeColor = Theme.Green;
                                lblUpd.Text = Lang.F("upd.latest", App.VersionTag);
                                Logger.Log("检查更新：已是最新版本（" + App.VersionTag + "）");
                            }
                        }));
                    }
                    catch { }
                });
            };

            update.Controls.AddRange(new Control[] { btnCheck, btnDl });
        }

    }
}

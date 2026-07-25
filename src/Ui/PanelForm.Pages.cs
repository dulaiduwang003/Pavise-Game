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

        private static void SplitDot(string full, out string title, out string desc)
        {
            title = full; desc = "";
            int di = full.IndexOf('·');
            if (di > 0) { title = full.Substring(0, di).Trim(); desc = full.Substring(di + 1).Trim(); }
        }


        private void BuildTamePage()
        {
            int y = PageHeader(pageTame, Lang.T("nav.tame"), Lang.T("tame.caveat"), 2);

            swTame = MakeSwitch(!tamer.Paused, (s, e) =>
            { tamer.Paused = !swTame.Checked; Settings.Save("TameOn", swTame.Checked); });
            MakeCard(pageTame, 26, y, 638, 56, Lang.T("tame.toggle"), "", swTame);
            y += 68;

            tameList = new DBPanel();
            tameList.SetBounds(Theme.S(20), Theme.S(y), Theme.S(664), Theme.S(592 - y - 32));
            tameList.BackColor = Theme.Bg;
            tameList.AutoScroll = true;
            Native.Dark(tameList);
            pageTame.Controls.Add(tameList);

            var lblCustom = new Label();
            lblCustom.Text = Lang.T("tame.footer");
            lblCustom.ForeColor = Theme.Faint; lblCustom.BackColor = Theme.Bg;
            lblCustom.Font = Theme.UI(8.25f, false);
            lblCustom.SetBounds(Theme.S(27), Theme.S(592 - 26), Theme.S(636), Theme.S(18));
            pageTame.Controls.Add(lblCustom);

            RefreshTameList();
        }

        private void RefreshTameList()
        {
            while (tameList.Controls.Count > 0) tameList.Controls[0].Dispose();
            tameGroups.Clear();
            tameCards.Clear();
            tameToggles.Clear();
            int pitch = 72, idx = 0;
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

            var card = new SettingCard();
            card.SetBounds(Theme.S(6), Theme.S(y), Theme.S(ScrollContentW), Theme.S(64));
            card.Title = title;
            card.Desc = note;
            card.Host(sw);

            tameList.Controls.Add(card);
            tameGroups.Add(new AcGroup(key, title, "", false, new string[0]));
            tameCards.Add(card);
            tameToggles.Add(sw);
        }


        private void BuildWhitePage()
        {
            int y = PageHeader(pageWhite, Lang.T("nav.white"), Lang.T("white.desc"), 2);

            int listH = 592 - y - 16;
            var listWrap = new RoundPanel();
            listWrap.SetBounds(Theme.S(26), Theme.S(y), Theme.S(438), Theme.S(listH));
            listWrap.BackColor = Theme.Bg; listWrap.Fill = Theme.Card; listWrap.Border = Theme.Stroke; listWrap.Radius = Theme.S(12);
            listWrap.Padding = new Padding(Theme.S(8));
            lstWhite = new ListBox();
            lstWhite.Dock = DockStyle.Fill;
            Theme.StyleList(lstWhite);
            lstWhite.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete && lstWhite.SelectedItem != null) { gameMode.RemoveWhitelist((string)lstWhite.SelectedItem); RefreshWhite(); }
            };
            listWrap.Controls.Add(lstWhite);

            int bx = 476, bw = 188, bh = 36;
            var btnPick = new PillButton(Lang.T("btn.pick"), BtnKind.Primary); btnPick.SetBounds(Theme.S(bx), Theme.S(y), Theme.S(bw), Theme.S(bh));
            btnPick.Click += (s, e) => PickInto(false);
            var btnBrowse = new PillButton(Lang.T("btn.browse")); btnBrowse.SetBounds(Theme.S(bx), Theme.S(y + 44), Theme.S(bw), Theme.S(bh));
            btnBrowse.Click += (s, e) => BrowseInto(false);
            var btnRemove = new PillButton(Lang.T("btn.remove")); btnRemove.SetBounds(Theme.S(bx), Theme.S(y + 104), Theme.S(bw), Theme.S(bh));
            btnRemove.Click += (s, e) => { if (lstWhite.SelectedItem != null) { gameMode.RemoveWhitelist((string)lstWhite.SelectedItem); RefreshWhite(); } };

            var btnReset = new PillButton(Lang.T("btn.reset"), BtnKind.Danger); btnReset.SetBounds(Theme.S(bx), Theme.S(y + listH - bh), Theme.S(bw), Theme.S(bh));
            btnReset.Click += (s, e) => { gameMode.ResetWhitelist(); RefreshWhite(); };

            pageWhite.Controls.AddRange(new Control[] { listWrap, btnPick, btnBrowse, btnRemove, btnReset });
            RefreshWhite();
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
            MakeCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.gpu"), Lang.T("set.gpu.n"), swGpu); sy += 64;

            swFso = MakeSwitch(gameMode.DisableFso, null);
            swFso.CheckedChanged += (s, e) => gameMode.DisableFso = swFso.Checked;
            MakeCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.fso"), Lang.T("set.fso.n"), swFso); sy += 64;

            sy += 10;
            Section(scroll, Lang.T("sec.system"), 6, sy); sy += 24;

            swAuto = MakeSwitch(TaskHelper.TaskExistsCached(), OnAutoToggle);
            MakeCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.autostart"), Lang.T("set.autostart.n"), swAuto); sy += 64;

            swAutoHide = MakeSwitch(Settings.Load(AutoHideKey, false), OnAutoHideToggle);
            MakeCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.autohide"), Lang.T("set.autohide.n"), swAutoHide); sy += 64;

            swHags = MakeSwitch(HagsTweak.EnabledByAegis || HagsTweak.CurrentlyOn(), OnHagsToggle);
            MakeCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.hags"), Lang.T("set.hags.n"), swHags); sy += 64;

            swIrqAffinity = MakeSwitch(InterruptAffinityTweak.EnabledByAegis, OnIrqAffinityToggle);
            MakeCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.irqaffinity"), Lang.T("set.irqaffinity.n"), swIrqAffinity); sy += 64;

            swNetAffinity = MakeSwitch(NetworkAffinityTweak.EnabledByAegis, OnNetAffinityToggle);
            MakeCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.netaffinity"), Lang.T("set.netaffinity.n"), swNetAffinity); sy += 64;

            swVbs = MakeSwitch(VbsTweak.DisabledByAegis, OnVbsToggle);
            cardVbs = MakeCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.vbs"), "…", swVbs); sy += 64;

            sy += 10;
            Section(scroll, Lang.T("sec.maint"), 6, sy); sy += 24;

            var btnRestore = new PillButton(Lang.T("btn.panic"), BtnKind.Danger);
            btnRestore.Bg = Theme.Card;
            btnRestore.Size = new Size(Theme.S(136), Theme.S(32));
            btnRestore.Click += delegate { RestoreAllNow(); };
            MakeCard(scroll, 6, sy, ScrollContentW, 64, Lang.T("v15.restore.title"), Lang.T("v15.restore.desc"), btnRestore);
            sy += 72;

            var btnShaderGo = new PillButton(Lang.T("btn.clean"));
            btnShaderGo.Size = new Size(Theme.S(88), Theme.S(30));
            btnShaderGo.Click += (s, e) => OnShaderClean(btnShaderGo);
            cardShader = MakeCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("btn.shader"), Lang.T("set.shader.n"), btnShaderGo);
            cardShader.Value = "…";
            sy += 64;

            lolDir = LolCross.FindLolDir();
            cardLol = null;
            if (lolDir != null)
            {
                var btnLolGo = new PillButton(Lang.T("btn.clean"));
                btnLolGo.Size = new Size(Theme.S(88), Theme.S(30));
                btnLolGo.Click += (s, e) => OnLolCrossClean(btnLolGo);
                cardLol = MakeCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("btn.lolcross"), Lang.T("set.lolcross.n"), btnLolGo);
                cardLol.Value = "…";
                sy += 64;
            }

            sy += 10;
            Section(scroll, Lang.T("set.lang"), 6, sy); sy += 24;

            string[] langNames = { "中文", "English", "日本語" };
            int lx = 6;
            for (int i = 0; i < 3; i++)
            {
                int ii = i;
                var lb = new PillButton(langNames[i], i == Lang.Cur ? BtnKind.Primary : BtnKind.Normal);
                lb.SetBounds(Theme.S(lx), Theme.S(sy), Theme.S(110), Theme.S(32));
                lb.Click += (s, e) => { if (Lang.Cur != ii) { Lang.Set(ii); BeginInvoke((MethodInvoker)RebuildUi); } };
                scroll.Controls.Add(lb);
                lx += 120;
            }
            sy += 48;

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
                bool completed = gameMode.PanicRestore();
                completed &= tamer.PanicRestore();
                Logger.Log("一键全部恢复：" + (completed ? "恢复流程已完成" : "等待游戏模式恢复超时，快照保留"));
                Interlocked.Exchange(ref restoreBusy, 0);
                try
                {
                    BeginInvoke((MethodInvoker)(() =>
                    {
                        if (IsDisposed) return;
                        Cursor = Cursors.Default;
                        MessageBox.Show(this, Lang.T(completed ? "panic.done" : "panic.timeout"), App.DisplayName,
                            MessageBoxButtons.OK, completed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                        SyncAllToggles();
                    }));
                }
                catch { }
            });
        }


        private void BuildAboutPage()
        {
            int y = PageHeader(pageAbout, Lang.T("nav.about"), Lang.T("v15.about.sub"), 2);

            var hero = MakeConsolePanel(pageAbout, ContentX, y, ContentW, 118, true);
            var pbIcon = new PictureBox();
            pbIcon.SetBounds(Theme.S(24), Theme.S(20), Theme.S(76), Theme.S(76));
            pbIcon.BackColor = Color.Transparent;
            pbIcon.SizeMode = PictureBoxSizeMode.Zoom;
            OwnedImage(pbIcon, IconArt.Render(Theme.S(76)));

            CardLabel(hero, App.DisplayName, 120, 17, 250, 35, 18f, true, Theme.Fg);
            CardLabel(hero, App.VersionTag + "  //  " + Lang.T("v15.about.identity"), 122, 52, ContentW - 150, 20, 8f, true, Theme.Accent);
            CardLabel(hero, Lang.T("about.desc").Replace("\r\n", "  ·  "), 122, 77, ContentW - 150, 24, 8.2f, false, Theme.Dim);
            hero.Controls.Add(pbIcon);

            int cardsY = y + 134;
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
                    lblV.Click += (s, e) => { try { Process.Start(App.RepoUrl); } catch { } };
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
            btnDl.Click += (s, e) => { if (dlUrl != null && dlUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) try { Process.Start(dlUrl); } catch { } };

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

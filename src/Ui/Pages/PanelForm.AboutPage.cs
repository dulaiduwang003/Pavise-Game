// @author bdth 2074055628@qq.com
// 文件用途 构建关于页 项目信息与更新检查

using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace AegisApp
{
    internal partial class PanelForm
    {
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
            string[] rowVals = { App.Author + " · " + App.AuthorEmail, App.RepoUrl.Replace("https://", ""), "GNU GPL v3.0" };
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

        private static void OwnedImage(PictureBox pb, Image img)
        {
            pb.Image = img;
            pb.Disposed += delegate { try { img.Dispose(); } catch { } };
        }
    }
}

// @author bdth 2074055628@qq.com
// 文件用途 构建关于页 项目信息与更新检查
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private void BuildAboutPage()
        {
            int y = PageHeader(pageAbout, Lang.T("nav.about"), Lang.T("v20.about.sub"), 1);

            const int heroH = 180;
            var hero = new AboutHeroPanel();
            hero.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(heroH));
            pageAbout.Controls.Add(hero);

            // 这里以前调的是写死智能档的单参重载 于是关于页的图标永远是智能档的颜色
            //   其它三处 NavRail ContactDialog 托盘 都传的是当前档位 只有这里漏了
            var pbIcon = new PictureBox();
            pbIcon.SetBounds(Theme.S(28), Theme.S(35), Theme.S(108), Theme.S(108));
            pbIcon.BackColor = Color.Transparent;
            pbIcon.SizeMode = PictureBoxSizeMode.Zoom;
            aboutIcon = pbIcon;
            OwnedImage(pbIcon, IconArt.Render(Theme.S(108), Theme.CurrentMode, true));

            CardLabel(hero, Lang.T("v20.about.system"), 164, 20, 360, 20, 7.1f, true, Theme.Faint);
            CardLabel(hero, App.DisplayName, 162, 40, 330, 43, 24f, true, Theme.Fg);
            AccentLabel(hero, "CORE CONTROL 2.0  //  " + App.VersionTag, 164, 83, 360, 22, 8f, true);
            CardLabel(hero, Lang.T("about.desc").Replace("\r\n", " "), 164, 111, 390, 30, 9f, false, Theme.Dim);
            hero.Controls.Add(pbIcon);

            AddAboutMetric(hero, Lang.T("v20.about.build"), App.VersionTag, 582, 34, 116, false);
            AddAboutMetric(hero, Lang.T("v20.about.platform"), "WINDOWS x64", 706, 34, 124, false);
            AddAboutMetric(hero, Lang.T("v20.about.state"), Lang.T("v20.about.ready"), 838, 34, 108, true);

            int cardsY = y + heroH + 14;
            int infoW = 454, gap = 14, updateW = ContentW - infoW - gap;
            int cardH = PageH - cardsY - 12;
            var card = MakeConsolePanel(pageAbout, ContentX, cardsY, infoW, cardH, false);
            CardLabel(card, "PROJECT // IDENTITY", 20, 15, infoW - 40, 20, 7.6f, true, Theme.Faint);
            CardLabel(card, "NODE 01", infoW - 88, 15, 68, 18, 6.5f, false, Theme.Faint).TextAlign = ContentAlignment.MiddleRight;

            string[] rowKeys = { "about.author", "about.wechat", "about.repo", "about.lic" };
            string[] rowVals = { App.Author + " " + App.AuthorEmail, App.WeChat,
                App.RepoUrl.Replace("https://", ""), Lang.T("about.lic.value") };
            for (int i = 0; i < 4; i++)
            {
                int ry = 51 + i * 51;
                Label lblV = AddAboutRow(card, Lang.T(rowKeys[i]).ToUpperInvariant(), rowVals[i], ry, infoW, i == 2);
                if (i == 2)
                {
                    lblV.Cursor = Cursors.Hand;
                    lblV.Click += (s, e) => { try { using (Process.Start(App.RepoUrl)) { } } catch { } };
                }
            }
            CardLabel(card, Lang.T("about.contact.hint"), 20, 263, infoW - 40, 54, 7.8f, false, Theme.Dim);

            int halfBtnW = (infoW - 40 - 16) / 2;
            var btnContact = new PillButton(Lang.T("contact.open.title"));
            btnContact.Bg = Theme.Card;
            btnContact.SetBounds(Theme.S(20), Theme.S(cardH - 58), Theme.S(halfBtnW), Theme.S(42));
            btnContact.Click += delegate
            {
                using (var dlg = new ContactDialog()) dlg.ShowDialog(this);
            };
            card.Controls.Add(btnContact);

            bool unseenNotes = ReleaseNotes.HasUnseen;
            var btnNotes = new PillButton(Lang.T("notes.open") + (unseenNotes ? " NEW" : ""),
                unseenNotes ? BtnKind.Primary : BtnKind.Normal);
            btnNotes.Bg = Theme.Card;
            btnNotes.SetBounds(Theme.S(36 + halfBtnW), Theme.S(cardH - 58), Theme.S(halfBtnW), Theme.S(42));
            btnNotes.Click += delegate
            {
                using (var dlg = new ReleaseNotesDialog()) dlg.ShowDialog(this);
                btnNotes.Text = Lang.T("notes.open");
                btnNotes.Kind = BtnKind.Normal;
                btnNotes.Invalidate();
            };
            card.Controls.Add(btnNotes);

            var update = MakeConsolePanel(pageAbout, ContentX + infoW + gap, cardsY, updateW, cardH, true);
            CardLabel(update, "RELEASE // " + Lang.T("v20.about.channel").ToUpperInvariant(), 20, 15, updateW - 126, 20, 7.6f, true, Theme.Faint);
            var channelDot = new StatusDot();
            channelDot.SetBounds(Theme.S(updateW - 126), Theme.S(12), Theme.S(22), Theme.S(22));
            channelDot.Bg = Theme.Card; channelDot.Color = Theme.Green;
            update.Controls.Add(channelDot);
            CardLabel(update, Lang.T("v20.about.stable"), updateW - 102, 12, 82, 24, 6.7f, true, Theme.Green)
                .TextAlign = ContentAlignment.MiddleRight;

            AccentLabel(update, App.VersionTag, 20, 45, 254, 43, 23f, true);
            CardLabel(update, Lang.T("v15.about.identity"), 22, 87, updateW - 44, 22, 8.2f, false, Theme.Dim);

            var divider = new Panel();
            divider.BackColor = Theme.StrokeHi;
            divider.SetBounds(Theme.S(20), Theme.S(119), Theme.S(updateW - 40), 1);
            update.Controls.Add(divider);
            CardLabel(update, Lang.T("v20.about.routes"), 20, 132, updateW - 40, 18, 7f, true, Theme.Faint);

            int routeW = (updateW - 40 - 16) / 3;
            AddAboutRoute(update, Lang.T("v20.about.direct"), "01", 20, 156, routeW, true);
            AddAboutRoute(update, Lang.T("v20.about.raw"), "02", 28 + routeW, 156, routeW, false);
            AddAboutRoute(update, Lang.T("v20.about.mirror"), "03", 36 + routeW * 2, 156, routeW, false);

            var privacy = MakeConsolePanel(update, 20, 232, updateW - 40, 70, false);
            var privacyDot = new StatusDot();
            privacyDot.SetBounds(Theme.S(14), Theme.S(22), Theme.S(22), Theme.S(22));
            privacyDot.Bg = Theme.Card; privacyDot.Color = Theme.Accent;
            privacy.Controls.Add(privacyDot);
            CardLabel(privacy, Lang.T("v20.about.privacy"), 44, 12, updateW - 116, 19, 7f, true, Theme.Faint);
            CardLabel(privacy, Lang.T("v20.about.privacy.value"), 44, 31, updateW - 116, 25, 9.4f, true, Theme.Fg);

            var btnCheck = new PillButton(Lang.T("btn.checkupd"), BtnKind.Primary);
            btnCheck.Bg = Theme.Card;
            btnCheck.SetBounds(Theme.S(20), Theme.S(cardH - 58), Theme.S(updateW - 40), Theme.S(42));

            var btnDl = new PillButton(Lang.T("btn.download"));
            btnDl.Bg = Theme.Card;
            btnDl.SetBounds(Theme.S(252), Theme.S(cardH - 58), Theme.S(updateW - 272), Theme.S(42));
            btnDl.Visible = false;

            var lblUpd = CardLabel(update, App.VersionTag + "  //  " + Lang.T("v20.about.standby"),
                20, 309, updateW - 40, 34, 7.7f, false, Theme.Faint);

            string dlUrl = null;
            btnDl.Click += (s, e) => { if (UpdateChecker.IsTrustedDownloadUrl(dlUrl)) try { using (Process.Start(dlUrl)) { } } catch { } };

            btnCheck.Click += (s, e) =>
            {
                btnCheck.Enabled = false;
                btnDl.Visible = false;
                btnCheck.SetBounds(Theme.S(20), Theme.S(cardH - 58), Theme.S(updateW - 40), Theme.S(42));
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
                                Logger.Log(Lang.T("log.panelformaboutpage.1") + r.Error);
                            }
                            else if (r.Newer)
                            {
                                dlUrl = r.Url;
                                btnCheck.SetBounds(Theme.S(20), Theme.S(cardH - 58), Theme.S(224), Theme.S(42));
                                btnDl.Visible = true;
                                Fx.SlideIn(btnDl);
                                lblUpd.ForeColor = Theme.Green;
                                lblUpd.Text = Lang.F("upd.newver", r.Latest, App.VersionTag)
                                    + " " + Lang.F("upd.route", r.Source);
                                Logger.Log(Lang.T("log.panelformaboutpage.2") + r.Latest + Lang.T("log.program.7") + App.VersionTag + " ");
                            }
                            else
                            {
                                lblUpd.ForeColor = Theme.Green;
                                lblUpd.Text = Lang.F("upd.latest", App.VersionTag)
                                    + " " + Lang.F("upd.route", r.Source);
                                Logger.Log(Lang.T("log.panelformaboutpage.3") + App.VersionTag + " ");
                            }
                        }));
                    }
                    catch { }
                });
            };

            update.Controls.AddRange(new Control[] { btnCheck, btnDl });
        }

        private void AddAboutMetric(Control parent, string title, string value, int x, int y, int width, bool good)
        {
            CardLabel(parent, title.ToUpperInvariant(), x, y, width, 18, 6.7f, true, Theme.Faint);
            Label metric = CardLabel(parent, value, x, y + 24, width, 27, 9.2f, true, good ? Theme.Green : Theme.Fg);
            metric.TextAlign = ContentAlignment.MiddleLeft;
            var rail = new AccentLine();
            rail.SetBounds(Theme.S(x), Theme.S(y + 60), Theme.S(width - 14), Theme.S(1));
            parent.Controls.Add(rail);
        }

        private Label AddAboutRow(Control parent, string title, string value, int y, int width, bool accent)
        {
            CardLabel(parent, title, 20, y, 112, 20, 7.2f, true, Theme.Faint);
            Label result = accent
                ? AccentLabel(parent, value, 136, y - 2, width - 156, 25, 8.8f, true)
                : CardLabel(parent, value, 136, y - 2, width - 156, 25, 8.8f, false, Theme.Fg);
            var separator = new Panel();
            separator.BackColor = Theme.Stroke;
            separator.SetBounds(Theme.S(20), Theme.S(y + 31), Theme.S(width - 40), 1);
            parent.Controls.Add(separator);
            return result;
        }

        private void AddAboutRoute(Control parent, string title, string index, int x, int y, int width, bool live)
        {
            var cell = MakeConsolePanel(parent, x, y, width, 60, false);
            var dot = new StatusDot();
            dot.SetBounds(Theme.S(10), Theme.S(10), Theme.S(20), Theme.S(20));
            dot.Bg = Theme.Card; dot.Color = live ? Theme.Green : Theme.Accent;
            cell.Controls.Add(dot);
            CardLabel(cell, title, 34, 8, width - 46, 20, 7.3f, true, Theme.Fg);
            CardLabel(cell, live ? Lang.T("v20.about.ready") : Lang.T("v20.about.standby"),
                12, 33, width - 48, 18, 6.6f, false, live ? Theme.Green : Theme.Faint);
            CardLabel(cell, index, width - 34, 34, 22, 16, 6.2f, false, Theme.Faint).TextAlign = ContentAlignment.MiddleRight;
        }

        // 换档位之后重画 亮暗切换走 RebuildUi 不用管 档位切换不重建页面 得自己刷
        //   OwnedImage 只在控件销毁时释放当前那张 换图时旧的在这里当场释放 不靠闭包攒着
        internal void RefreshAboutIcon()
        {
            if (aboutIcon == null || aboutIcon.IsDisposed) return;
            Image old = aboutIcon.Image;
            aboutIcon.Image = IconArt.Render(Theme.S(108), Theme.CurrentMode, true);
            if (old != null) try { old.Dispose(); } catch { }
        }

        private static void OwnedImage(PictureBox pb, Image img)
        {
            pb.Image = img;
            pb.Disposed += delegate { try { if (pb.Image != null) pb.Image.Dispose(); } catch { } };
        }
    }
}

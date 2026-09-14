﻿// @author bdth 2074055628@qq.com
// File purpose Original About layout with website navigation and automatic version status
using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Label lblAboutUpdate;

        private void BuildAboutPage()
        {
            int y = PageHeader(pageAbout, Lang.T("nav.about"), Lang.T("site.about.originalSub"), 1);

            const int heroH = 180;
            var hero = new AboutHeroPanel();
            hero.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(heroH));
            pageAbout.Controls.Add(hero);

            // This used to call the single-argument overload that hard-codes the Smart tier, so the About page icon was always the Smart tier color
            //   The other three sites (NavRail, ContactDialog, tray) all pass the current tier; only this one was missed
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

            string[] rowKeys = { "about.author", "about.douyin", "about.repo", "about.lic" };
            string[] rowVals = { App.Author + " " + App.AuthorEmail, App.Douyin,
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

            var website = new PillButton(Lang.T("site.entry")) { Name = "aboutWebsite", Bg = Theme.Card };
            website.SetBounds(Theme.S(20), Theme.S(cardH - 58), Theme.S(infoW - 40), Theme.S(42));
            website.Click += delegate { OpenExternal(App.WebsiteUrl); };
            card.Controls.Add(website);
            var update = MakeConsolePanel(pageAbout, ContentX + infoW + gap, cardsY, updateW, cardH, true);
            CardLabel(update, "RELEASE // " + Lang.T("v20.about.versionstatus").ToUpperInvariant(), 20, 15, updateW - 126, 20, 7.6f, true, Theme.Faint);
            var channelDot = new StatusDot();
            channelDot.SetBounds(Theme.S(updateW - 126), Theme.S(12), Theme.S(22), Theme.S(22));
            channelDot.Bg = Theme.Card; channelDot.Color = Theme.Green;
            update.Controls.Add(channelDot);
            CardLabel(update, Lang.T("v20.about.online"),
                updateW - 102, 12, 82, 24, 6.7f, true, Theme.Green)
                .TextAlign = ContentAlignment.MiddleRight;

            AccentLabel(update, App.VersionTag, 20, 45, 254, 43, 23f, true);
            CardLabel(update, Lang.T("v15.about.identity"), 22, 87, updateW - 44, 22, 8.2f, false, Theme.Dim);

            var divider = new Panel();
            divider.BackColor = Theme.StrokeHi;
            divider.SetBounds(Theme.S(20), Theme.S(119), Theme.S(updateW - 40), 1);
            update.Controls.Add(divider);
            CardLabel(update, Lang.T("site.about.source"), 20, 132, updateW - 40, 18, 7f, true, Theme.Faint);

            var source = MakeConsolePanel(update, 20, 156, updateW - 40, 60, false);
            CardLabel(source, "PAVISE // OFFICIAL WEBSITE", 14, 8, updateW - 68, 18, 7.3f, true, Theme.Faint);
            AccentLabel(source, "pavise.club", 14, 29, updateW - 68, 25, 10f, true);
            var privacy = MakeConsolePanel(update, 20, 232, updateW - 40, 70, false);
            var privacyDot = new StatusDot();
            privacyDot.SetBounds(Theme.S(14), Theme.S(22), Theme.S(22), Theme.S(22));
            privacyDot.Bg = Theme.Card; privacyDot.FollowAccent = true;
            privacy.Controls.Add(privacyDot);
            CardLabel(privacy, Lang.T("v20.about.privacy"), 44, 12, updateW - 116, 19, 7f, true, Theme.Faint);
            CardLabel(privacy, Lang.T("v20.about.privacy.value"), 44, 31, updateW - 116, 25, 9.4f, true, Theme.Fg);

            lblAboutUpdate = CardLabel(update, "", 20, cardH - 80, updateW - 40, 18, 7.7f, false, Theme.Faint);
            lblAboutUpdate.Name = "aboutUpdateStatus";
            var download = new PillButton(Lang.T("site.about.gotoDownload"), BtnKind.Primary)
                { Name = "aboutWebsiteDownload", Bg = Theme.Card };
            download.SetBounds(Theme.S(20), Theme.S(cardH - 58), Theme.S(updateW - 40), Theme.S(42));
            download.Click += delegate { OpenExternal(App.ChangelogUrl); };
            update.Controls.Add(download);
            RefreshAboutUpdate();
        }

        private void RefreshAboutUpdate()
        {
            if (lblAboutUpdate == null || lblAboutUpdate.IsDisposed) return;
            bool newer = latestUpdate != null && UpdateChecker.IsNewer(latestUpdate.Latest, App.Version);
            lblAboutUpdate.Text = newer ? Lang.F("site.about.found", latestUpdate.Latest)
                : latestUpdate != null ? Lang.T("site.about.latest") : Lang.T("site.about.wait");
            lblAboutUpdate.ForeColor = newer ? Theme.Green : Theme.Faint;
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

        // Repaint after a tier change; light/dark switching goes through RebuildUi and needs nothing here, but a tier switch does not rebuild the page so refresh here
        //   OwnedImage releases only the current image when the control is destroyed; on swap the old one is released right here, not hoarded in a closure
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

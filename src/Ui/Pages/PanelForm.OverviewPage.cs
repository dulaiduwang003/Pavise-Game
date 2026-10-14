// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private const int OverviewGap = 12, OverviewPadding = 16;
        private const int OverviewButtonWidth = 140, OverviewButtonGap = 10;
        private Toggle swGame;
        private PaviseCore paviseCore;
        private StatusDot statusDot;
        private Label lblStatus;
        private Label lblOverviewBoost, lblEvidenceLive;
        private Label lblHeroMode, lblHeroSource;
        private Label lblOverviewAttention, lblOverviewRuntime;
        private PillButton btnOverviewAction;
        private PillButton btnOverviewLogs;
        private PillButton btnOverviewAutomaticActions;
        private string overviewAction;
        private long overviewLogVersion = -1;
        private int overviewErrors;
        private LinkLabel btnUpdateHint;
        private DBPanel updateBadge;
        private UpdateResult latestUpdate;
        private RogLinkButton btnDonate;
        private volatile bool donateLoading;
        private DonateDialog donateDialog;
#if PAVISE_SELFTEST
        internal Action<Bitmap> DonateDisplayForTest;
#endif
        private void BuildOverviewPage()
        {
            lblOverviewBoost = null;
            int y = PageHeader(pageOverview, Lang.T("nav.overview"), Lang.T("v20.overview.sub"), 1, false);
            const int coreW = 370, coreH = 376;
            int rightX = ContentX + coreW + OverviewGap;
            int rightW = Math.Max(360, ContentW - coreW - OverviewGap);
            int innerW = rightW - OverviewPadding * 2;

            paviseCore = new PaviseCore();
            paviseCore.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(coreW), Theme.S(coreH));
            paviseCore.SetState(gameMode.ActivePreset, gameMode.Enabled, gameMode.IsActive);
            pageOverview.Controls.Add(paviseCore);

            var guard = MakeConsolePanel(pageOverview, rightX, y, rightW, coreH, true);
            CardLabel(guard, Lang.T("workflow.game.current"), OverviewPadding, 20, innerW - 72, 24, 10f, true, Theme.Faint);
            statusDot = new StatusDot(); statusDot.SetBounds(Theme.S(OverviewPadding - 2), Theme.S(76), Theme.S(22), Theme.S(22));
            statusDot.Bg = Theme.Card; statusDot.Color = Theme.Dim;

            lblStatus = CardLabel(guard, " ", OverviewPadding + 30, 68, innerW - 30, 42, 12.5f, true, Theme.Fg);
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            swGame = MakeSwitch(gameMode.Enabled, delegate
            {
                gameMode.Enabled = swGame.Checked;
                Settings.Save("GameModeOn", swGame.Checked);
                UpdateModePresentation(true);
            });
            swGame.Size = new Size(Theme.S(54), Theme.S(28));
            swGame.Bg = Theme.Card; swGame.Location = new Point(Theme.S(rightW - OverviewPadding - 54), Theme.S(20));
            lblOverviewRuntime = CardLabel(guard,"",OverviewPadding,116,innerW,38,9f,false,Theme.Dim);
            lblOverviewRuntime.AutoEllipsis = true;
            guard.Controls.AddRange(new Control[] { statusDot, swGame });

            AddOverviewDivider(guard, 168, rightW);
            CardLabel(guard, Lang.T("workflow.policy.current"), OverviewPadding, 194, innerW, 22, 9f, false, Theme.Faint);
            lblHeroMode = AccentLabel(guard, ModeButton.ModeName(gameMode.ActivePreset), OverviewPadding, 220, innerW, 36, 18f, true);
            lblHeroSource = CardLabel(guard, Lang.T("mode.source.global"), OverviewPadding, 258, innerW, 20, 8.4f, false, Theme.Dim);

            AddOverviewDivider(guard, 286, rightW);
            CardLabel(guard, Lang.T("workflow.attention.title"), OverviewPadding, 292, innerW, 18, 9f, false, Theme.Faint);
            lblOverviewAttention = CardLabel(guard,"",OverviewPadding,312,innerW,18,9f,false,Theme.Dim);
            lblOverviewAttention.Name = "overviewAttention";
            lblOverviewAttention.AutoEllipsis = true;
            btnOverviewAction = new PillButton("") { Name = "overviewNextAction" };
            btnOverviewAction.SetBounds(Theme.S(OverviewPadding),Theme.S(332),Theme.S(120),Theme.S(34));
            btnOverviewAction.Click += delegate {
                if (overviewAction == "admin") PaviseDialog.Info(this,App.DisplayName,Lang.T("irq.flow.hint.admin"));
                else if (overviewAction == "library") nav.Select((int)PageId.Library);
                else if (overviewAction == "logs") nav.Select((int)PageId.Log); };
            guard.Controls.Add(btnOverviewAction);
            int detailWidth = OverviewButtonWidth;
            int detailX = rightW - OverviewPadding - detailWidth * 2 - OverviewButtonGap;
            btnOverviewAutomaticActions = AddAutomationLink(guard, "自动执行详情", "Automatic actions", PageId.Policy, detailX, 332, detailWidth);
            btnOverviewAutomaticActions.Name = "overviewAutomaticActions";
            btnOverviewLogs = AddAutomationLink(guard, "查看日志", "View logs", PageId.Log, detailX + detailWidth + OverviewButtonGap, 332, detailWidth);
            btnOverviewLogs.Text = Lang.T("workflow.attention.action.logs");
            btnOverviewLogs.Name = "overviewLogs";
            btnOverviewLogs.Kind = BtnKind.Primary;

            int tileY = y + coreH + OverviewGap;
            int tileW = (ContentW - OverviewGap * 2) / 3;
            int tile2X = ContentX + tileW + OverviewGap;
            int tile3X = ContentX + (tileW + OverviewGap) * 2;
            int tile3W = ContentX + ContentW - tile3X;
            MakeDashboardTile(pageOverview, ContentX, tileY, tileW, Lang.T("v15.tile.game"), Lang.T("v20.tile.game.sub"), "game", 1);
            MakeDashboardTile(pageOverview, tile2X, tileY, tileW, Lang.T("v15.tile.background"), Lang.T("v20.tile.background.sub"), "settings", 2);
            MakeDashboardTile(pageOverview, tile3X, tileY, tile3W, Lang.T("v15.tile.environment"), Lang.T("v20.tile.environment.sub"), "shield", 3);

            int statusY = PageH - 70;
            var status = new DBPanel();
            status.SetBounds(0, Theme.S(statusY), Theme.S(PageW), Theme.S(70));
            status.BackColor = Theme.Nav;
            pageOverview.Controls.Add(status);
            var topEdge = new AccentLine();
            topEdge.SetBounds(0, 0, Theme.S(PageW), Math.Max(1, Theme.S(1)));
            status.Controls.Add(topEdge);
            var readyDot = new StatusDot();
            readyDot.SetBounds(Theme.S(30), Theme.S(25), Theme.S(20), Theme.S(20));
            readyDot.Bg = Theme.Nav; readyDot.FollowAccent = true;
            status.Controls.Add(readyDot);
            lblEvidenceLive = CardLabel(status, Lang.F("v20.ready", App.Version), 58, 22, 205, 26, 9f, true, Theme.Faint);
            lblEvidenceLive.TextAlign = ContentAlignment.MiddleLeft;

            lblEvidenceLive.Width = Math.Min(lblEvidenceLive.Width,
                lblEvidenceLive.GetPreferredSize(Size.Empty).Width);
            updateBadge = new DBPanel { Name = "overviewUpdateBadge", BackColor = Theme.Nav };
            updateBadge.SetBounds(lblEvidenceLive.Right + Theme.S(12), Theme.S(26), Theme.S(34), Theme.S(18));
            updateBadge.Paint += delegate(object sender, PaintEventArgs e) {
                var badge = (Control)sender;
                var bounds = new Rectangle(0, 0, badge.Width - 1, badge.Height - 1);
                using (var path = Theme.TechPath(bounds, Theme.S(3)))
                using (var fill = new SolidBrush(Col.Lerp(Theme.Nav, Theme.Accent, 0.12f)))
                using (var edge = new Pen(Col.Lerp(Theme.Nav, Theme.Accent, 0.45f)))
                {
                    e.Graphics.FillPath(fill, path);
                    e.Graphics.DrawPath(edge, path);
                }
                TextRenderer.DrawText(e.Graphics, "NEW", Theme.Mono(7f), bounds, Theme.Accent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            };
            status.Controls.Add(updateBadge);
            btnUpdateHint = new LinkLabel {
                Name = "overviewUpdateHint",
                Font = Theme.UI(8.5f, false), BackColor = Color.Transparent,
                LinkColor = Theme.Accent, ActiveLinkColor = Theme.Fg, VisitedLinkColor = Theme.Accent,
                LinkBehavior = LinkBehavior.HoverUnderline, TextAlign = ContentAlignment.MiddleLeft,
                UseCompatibleTextRendering = false, TabStop = true
            };
            int updateX = updateBadge.Right + Theme.S(8);
            btnUpdateHint.SetBounds(updateX, Theme.S(22), Theme.S(210), Theme.S(26));
            btnUpdateHint.LinkClicked += delegate { OpenExternal(App.ChangelogUrl); };
            status.Controls.Add(btnUpdateHint);
            accentLabels.Add(btnUpdateHint);
            int linkY = 12, linkH = 46, linkGap = 10, linkW = 186;
            int websiteX = PageW - 30 - linkW;
            int donateX = websiteX - linkGap - linkW;
            btnDonate = AddOverviewLink(status, donateX, linkY, linkW, linkH,
                Lang.T("donate.entry"), "DONATE // 01", "heart", null);
            btnDonate.Name = "overviewDonate";
            btnDonate.External = false;
            btnDonate.Tint = Color.FromArgb(255, 170, 60);
            btnDonate.Click += delegate { ShowDonate(); };
            var website = AddOverviewLink(status, websiteX, linkY, linkW, linkH,
                Lang.T("site.entry"), "WEBSITE // 02", "info", App.WebsiteUrl);
            website.Name = "overviewWebsite";
            RefreshDonateButton();
            RefreshUpdatePresentation();
            UpdateModePresentation(false);
            RefreshOverviewAttention();
        }

        public void NotifyLastSession(string brief)
        {
            try
            {
                if (!IsHandleCreated || IsDisposed || string.IsNullOrEmpty(brief)) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed) return;
                    RefreshOverviewAttention();
                });
            }
            catch { }
        }

        private RogLinkButton AddOverviewLink(Control parent, int x, int y, int w, int h,
            string text, string code, string glyph, string url)
        {
            var btn = new RogLinkButton(text, code, glyph);
            btn.Bg = Theme.Nav;
            btn.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
            if (url != null) btn.Click += delegate { OpenExternal(url); };
            parent.Controls.Add(btn);
            return btn;
        }

        public void NotifyUpdate(UpdateResult result)
        {
            try { PostUiStateResult(delegate { ApplyUpdateResult(result); }); } catch { }
        }

        private void ApplyUpdateResult(UpdateResult result)
        {
            if (IsDisposed || result == null || !result.Ok || string.IsNullOrEmpty(result.Latest)) return;
            // 延迟旧响应不能清除已发现的新版本。
            if (latestUpdate == null || !UpdateChecker.IsNewer(latestUpdate.Latest, result.Latest)) latestUpdate = result;
            RefreshUpdatePresentation();
        }

        private void RefreshUpdatePresentation()
        {
            bool newer = latestUpdate != null && UpdateChecker.IsNewer(latestUpdate.Latest, App.Version);
            if (btnUpdateHint != null && !btnUpdateHint.IsDisposed)
            {
                btnUpdateHint.Text = newer ? Lang.F("site.update.available", latestUpdate.Latest) : "";
                btnUpdateHint.AccessibleName = btnUpdateHint.Text;
                btnUpdateHint.Visible = newer;
            }
            if (updateBadge != null && !updateBadge.IsDisposed) updateBadge.Visible = newer;
            RefreshAboutUpdate();
        }

        public void NotifyDonate(DonateInfo d)
        {
            if (d != null) DonateCache.Latest = d;
        }

        private void RefreshDonateButton()
        {
            if (btnDonate == null || btnDonate.IsDisposed) return;
            btnDonate.Text = Lang.T(donateLoading ? "donate.loading" : "donate.entry");
            btnDonate.AccessibleName = btnDonate.Text;
            btnDonate.Enabled = !donateLoading;
            btnDonate.Cursor = donateLoading ? Cursors.WaitCursor : Cursors.Hand;
            btnDonate.Invalidate();
        }

        private void ShowDonate()
        {
            if (donateLoading || IsDisposed || btnDonate == null || btnDonate.IsDisposed) return;
            DonateInfo latest = DonateCache.Effective;
            bool has = DonateCache.HasImage;
            if (has && !DonateCache.NeedsRefresh(DonateCache.CachedId, latest.Id, true))
            {
                OpenDonate(DonateCache.LoadCached(), false);
                return;
            }
            donateLoading = true;
            RefreshDonateButton();
            try
            {
                FetchDonateImage(delegate(byte[] bytes, DonateInfo info)
                {
                    try { PostUiStateResult(delegate { CompleteDonate(bytes, info); }); }
                    catch { donateLoading = false; }
                });
            }
            catch { CompleteDonate(null, null); return; }
            OpenDonate(has ? DonateCache.LoadCached() : null, true);
        }

        private static void FetchDonateImage(Action<byte[], DonateInfo> done)
        {
            Action<DonateInfo> pull = delegate(DonateInfo i)
            {
                byte[] bytes = UpdateChecker.FetchBytes(i.Url, DonateCache.MaxImageBytes);
                done(bytes, i);
            };
            DonateInfo known = DonateCache.Latest;
            if (known != null) { ThreadPool.QueueUserWorkItem(delegate { pull(known); }); return; }
            UpdateChecker.CheckAsync(delegate(UpdateResult r)
            {
                DonateInfo i = r != null && r.Ok ? r.Donate : null;
                if (i != null) DonateCache.Latest = i;
                pull(i ?? DonateCache.Default);
            });
        }

        private void CompleteDonate(byte[] bytes, DonateInfo info)
        {
            donateLoading = false;
            if (IsDisposed) return;
            Bitmap fresh = null;
            if (bytes != null && info != null && DonateCache.Store(bytes, info.Id)) fresh = DonateCache.Decode(bytes);
            RefreshDonateButton();
            if (donateDialog != null && !donateDialog.IsDisposed)
            {
                if (fresh != null) donateDialog.SetImage(fresh); else donateDialog.MarkFailed();
            }
            else if (fresh != null) fresh.Dispose();
        }

        private void OpenDonate(Bitmap qr, bool loading)
        {
#if PAVISE_SELFTEST
            if (DonateDisplayForTest == null) throw new InvalidOperationException("Donate dialog was not mocked");
            DonateDisplayForTest(qr);
#else
            using (var dlg = new DonateDialog(qr, loading))
            {
                donateDialog = dlg;
                try { dlg.ShowDialog(this); }
                finally { donateDialog = null; }
            }
#endif
        }

        private void OpenExternal(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try { using (System.Diagnostics.Process.Start(url)) { } }
            catch { PaviseDialog.Warn(this, App.DisplayName, Lang.T("v211.link.failed")); }
        }

        private void AddOverviewDivider(Control parent, int y, int width)
        {
            var divider = new Panel();
            divider.BackColor = Theme.Stroke;
            divider.SetBounds(Theme.S(OverviewPadding), Theme.S(y), Theme.S(width - OverviewPadding * 2), Math.Max(1, Theme.S(1)));
            parent.Controls.Add(divider);
        }

        private void MakeDashboardTile(Control parent, int x, int y, int w, string title, string detail, string glyph, int channel)
        {
            var tile = new DashboardTile();
            tile.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(112));
            tile.Bg = Theme.Bg;
            tile.Title = title;
            tile.Detail = detail;
            tile.Glyph = glyph;
            tile.Channel = channel;
            parent.Controls.Add(tile);
        }

        private void RefreshBoostPresentation()
        {
            if (lblOverviewBoost == null) return;
            string text = gameMode.BoostStatusText;
            if (lblOverviewBoost.Text != text) lblOverviewBoost.Text = text;
            lblOverviewBoost.ForeColor = gameMode.BoostStateVerified ? Theme.Green
                : (gameMode.BoostHandleProtected ? Theme.Dim : Theme.Fg);
        }

    }
}

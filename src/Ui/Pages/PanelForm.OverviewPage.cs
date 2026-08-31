// @author bdth 2074055628@qq.com
// 文件用途 构建概览页 核心动画 守护状态与仪表盘图块
using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private Toggle swGame;
        private PaviseCore paviseCore;
        private StatusDot statusDot;
        private Label lblStatus;
        private Label lblOverviewBoost, lblEvidenceLive;
        private Label lblHeroMode, lblHeroSource;
        private Label lblLastSession;

        private void BuildOverviewPage()
        {
            lblOverviewBoost = null;
            int y = PageHeader(pageOverview, Lang.T("nav.overview"), Lang.T("v20.overview.sub"), 1);
            const int coreW = 370, coreH = 376, gap = 24;
            int rightX = ContentX + coreW + gap;
            int rightW = Math.Max(360, ContentW - coreW - gap);

            paviseCore = new PaviseCore();
            paviseCore.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(coreW), Theme.S(coreH));
            paviseCore.SetState(gameMode.ActivePreset, gameMode.Enabled, gameMode.IsActive);
            pageOverview.Controls.Add(paviseCore);

            var guard = MakeConsolePanel(pageOverview, rightX, y, rightW, coreH, true);
            CardLabel(guard, Lang.T("v15.guard.state"), 24, 20, rightW - 120, 24, 10f, true, Theme.Faint);
            statusDot = new StatusDot(); statusDot.SetBounds(Theme.S(22), Theme.S(76), Theme.S(22), Theme.S(22));
            statusDot.Bg = Theme.Card; statusDot.Color = Theme.Dim;

            lblStatus = CardLabel(guard, " ", 54, 68, rightW - 142, 42, 12.5f, true, Theme.Fg);
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            swGame = MakeSwitch(gameMode.Enabled, delegate
            {
                gameMode.Enabled = swGame.Checked;
                Settings.Save("GameModeOn", swGame.Checked);
                UpdateModePresentation(true);
            });
            swGame.Size = new Size(Theme.S(54), Theme.S(28));
            swGame.Bg = Theme.Card; swGame.Location = new Point(Theme.S(rightW - 76), Theme.S(20));
            CardLabel(guard, Lang.T("v20.guard.detail"), 24, 116, rightW - 48, 28, 9f, false, Theme.Dim);
            guard.Controls.AddRange(new Control[] { statusDot, swGame });

            AddOverviewDivider(guard, 168, rightW);
            CardLabel(guard, Lang.T("v20.current.mode"), 24, 194, rightW - 48, 22, 9f, false, Theme.Faint);
            // 高 42 会压到下面"全局默认"的 y=258 字顶被裁 18f 行高约 32 收到 36 正好
            lblHeroMode = AccentLabel(guard, ModeButton.ModeName(gameMode.ActivePreset), 24, 220, rightW - 48, 36, 18f, true);
            lblHeroSource = CardLabel(guard, Lang.T("mode.source.global"), 24, 258, rightW - 48, 20, 8.4f, false, Theme.Dim);

            AddOverviewDivider(guard, 286, rightW);
            CardLabel(guard, Lang.T("v20.last.session"), 24, 312, rightW - 48, 22, 9f, false, Theme.Faint);
            // 摘要落在注册表 重启后仍然显示上一局 而不是又退回"暂无数据"
            string brief = GameMode.LastSessionBrief;
            lblLastSession = CardLabel(guard,
                brief.Length > 0 ? brief : Lang.T("v20.no.data"),
                24, 342, rightW - 48, 24, 9.2f, false,
                brief.Length > 0 ? Theme.Fg : Theme.Dim);
            lblLastSession.AutoEllipsis = true;

            int tileY = y + coreH + 26;
            int tileW = (ContentW - 54) / 3;
            int tile2X = ContentX + tileW + 27;
            int tile3X = ContentX + (tileW + 27) * 2;
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

            // 高级区只保留侧栏入口 底栏右侧留给三个外链
            //   不等宽是有意的 教程那条标题最长 平分的话它会被省略号截掉
            //   按钮内部左侧图标占 43 右端外链角标占 32 剩下才是文字可用宽度
            int linkY = 12, linkH = 46, linkGap = 10;
            int gx = 276;
            int available = PageW - gx - 30 - linkGap * 2;
            int otherW = (available - 40) / 3, guideW = available - otherW * 2;
            AddOverviewLink(status, gx, linkY, guideW, linkH,
                Lang.T("v211.link.guide"), "GUIDE // 01", "info", App.GuideUrl);
            gx += guideW + linkGap;
            AddOverviewLink(status, gx, linkY, otherW, linkH,
                Lang.T("v211.link.survey"), "SURVEY // 02", "chart", App.SurveyUrl);
            gx += otherW + linkGap;
            AddOverviewLink(status, gx, linkY, otherW, linkH,
                Lang.T("v211.link.bug"), "REPORT // 03", "search", App.BugUrl);

            UpdateModePresentation(false);
        }

        // 由 GameMode 的会话摘要事件驱动 一局一次 对局中不做任何事
        public void NotifyLastSession(string brief)
        {
            try
            {
                if (!IsHandleCreated || IsDisposed || string.IsNullOrEmpty(brief)) return;
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed || lblLastSession == null) return;
                    lblLastSession.Text = brief;
                    lblLastSession.ForeColor = Theme.Fg;
                });
            }
            catch { }
        }

        private void AddOverviewLink(Control parent, int x, int y, int w, int h,
            string text, string code, string glyph, string url)
        {
            var btn = new RogLinkButton(text, code, glyph);
            btn.Bg = Theme.Nav;
            btn.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
            btn.Click += delegate { OpenExternal(url); };
            parent.Controls.Add(btn);
        }

        // 只放行写死在 App 里的 https 常量 不接受任何运行期拼出来的地址
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
            divider.SetBounds(Theme.S(20), Theme.S(y), Theme.S(width - 40), Math.Max(1, Theme.S(1)));
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

        private string CpuTopologySummary()
        {
            if (CpuTopology.MultiGroup) return Lang.T("v14.cpu.multigroup");
            if (CpuTopology.Hybrid) return Lang.T("v14.cpu.hybrid");
            if (CpuTopology.AsymCache) return Lang.T("v14.cpu.x3d");
            return Lang.F("v14.cpu.generic", Environment.ProcessorCount);
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

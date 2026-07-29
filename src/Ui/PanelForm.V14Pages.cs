// @author bdth 2074055628@qq.com
// 文件用途 构建新版概览 策略和游戏库页面

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AegisApp
{
    internal partial class PanelForm
    {
        private void BuildOverviewPageV14()
        {
            int y = PageHeader(pageGame, Lang.T("nav.overview"), Lang.T("v15.overview.sub"), 2);
            const int coreW = 360, coreH = 342, gap = 16;
            int rightX = ContentX + coreW + gap;
            int rightW = ContentW - coreW - gap;

            aegisCore = new AegisCore();
            aegisCore.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(coreW), Theme.S(coreH));
            aegisCore.SetState(gameMode.ActivePreset, gameMode.Enabled, gameMode.IsActive);
            pageGame.Controls.Add(aegisCore);

            var guard = MakeConsolePanel(pageGame, rightX, y, rightW, 112, true);
            CardLabel(guard, Lang.T("v15.guard.state"), 18, 12, rightW - 92, 18, 7.8f, true, Theme.Faint);
            statusDot = new StatusDot(); statusDot.SetBounds(Theme.S(15), Theme.S(39), Theme.S(22), Theme.S(22));
            statusDot.Bg = Theme.Card; statusDot.Color = Theme.Dim;
            // 原来是 11pt 单行、宽度还预留了 120px 给右上角开关，
            // 结果中文需 402px 只有 284px 可用，状态文字永远被截。缩字号并放开到两行。
            lblStatus = CardLabel(guard, "…", 47, 30, rightW - 114, 44, 9.2f, true, Theme.Fg);
            swGame = MakeSwitch(gameMode.Enabled, delegate
            {
                gameMode.Enabled = swGame.Checked;
                Settings.Save("GameModeOn", swGame.Checked);
                UpdateModePresentation(true);
            });
            swGame.Bg = Theme.Card; swGame.Location = new Point(Theme.S(rightW - 66), Theme.S(14));
            CardLabel(guard, Lang.T("v15.master.short"), 18, 72, rightW - 36, 34, 7.7f, false, Theme.Dim);
            guard.Controls.AddRange(new Control[] { statusDot, swGame });

            var mode = MakeConsolePanel(pageGame, rightX, y + 122, rightW, 96, false);
            CardLabel(mode, Lang.T("v15.effective.mode"), 18, 12, rightW - 36, 17, 7.6f, true, Theme.Faint);
            lblHeroMode = CardLabel(mode, ModeButton.ModeName(gameMode.ActivePreset), 18, 31, rightW - 36, 31, 14.5f, true, Theme.Accent);
            lblHeroSource = CardLabel(mode, Lang.T("mode.source.global"), 18, 66, rightW - 36, 18, 7.7f, false, Theme.Dim);

            var boost = MakeConsolePanel(pageGame, rightX, y + 228, rightW, 114, false);
            CardLabel(boost, Lang.T("v14.boost.status"), 18, 13, rightW - 36, 18, 7.7f, true, Theme.Faint);
            lblOverviewBoost = CardLabel(boost, "…", 18, 37, rightW - 36, 70, 10.2f, false, Theme.Fg);

            int tileY = y + coreH + 14;
            int tileW = (ContentW - 28) / 3;
            MakeDashboardTile(pageGame, ContentX, tileY, tileW, Lang.T("v15.tile.game"), Lang.T("v15.tile.game.sub"), "game", 1);
            MakeDashboardTile(pageGame, ContentX + tileW + 14, tileY, tileW, Lang.T("v15.tile.background"), Lang.T("v15.tile.background.sub"), "settings", 2);
            MakeDashboardTile(pageGame, ContentX + (tileW + 14) * 2, tileY, tileW, Lang.T("v15.tile.environment"), Lang.T("v15.tile.environment.sub"), "shield", 3);

            int topologyY = tileY + 84;
            var topology = MakeConsolePanel(pageGame, ContentX, topologyY, ContentW, 68, false);
            CardLabel(topology, Lang.T("v14.cpu.topology"), 18, 10, ContentW - 36, 17, 7.7f, true, Theme.Faint);
            lblEvidenceLive = CardLabel(topology, CpuTopologySummary(), 18, 30, ContentW - 36, 27, 9.5f, false, Theme.Fg);
            lblEvidenceLive.Text = CpuTopologySummary();
            UpdateModePresentation(false);
        }

        private RoundPanel MakeConsolePanel(Control parent, int x, int y, int width, int height, bool accent)
        {
            var panel = new RoundPanel();
            panel.SetBounds(Theme.S(x), Theme.S(y), Theme.S(width), Theme.S(height));
            panel.BackColor = Theme.Bg; panel.Fill = Theme.Card; panel.Border = Theme.Stroke;
            panel.Radius = Theme.S(14); panel.AccentEdge = accent;
            parent.Controls.Add(panel); return panel;
        }

        private Label CardLabel(Control parent, string text, int x, int y, int w, int h, float size, bool bold, Color color)
        {
            var label = new Label();
            label.Text = text; label.ForeColor = color; label.BackColor = Color.Transparent;
            label.Font = Theme.UI(size, bold); label.AutoEllipsis = true;
            label.UseCompatibleTextRendering = false;
            label.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
            parent.Controls.Add(label); return label;
        }

        private void MakeDashboardTile(Control parent, int x, int y, int w, string title, string detail, string glyph, int channel)
        {
            var tile = new DashboardTile();
            tile.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(70));
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

        private void BuildLibraryPageV14()
        {
            int y = PageHeader(pageWhite, Lang.T("nav.library"), Lang.T("v15.library.sub"), 2);
            int listH = PageH - y - 16;
            int listW = ContentW - 238;
            var listWrap = new EmptyStatePanel();
            gameListPanel = listWrap;
            listWrap.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(listW), Theme.S(listH));
            listWrap.BackColor = Theme.Bg; listWrap.Fill = Theme.Card; listWrap.Border = Theme.Stroke; listWrap.Radius = Theme.S(14);
            listWrap.EmptyTitle = "AEGIS LIBRARY";
            listWrap.EmptyDetail = Lang.T("v15.library.empty");
            listWrap.Padding = new Padding(Theme.S(8));
            lstGames = new ListBox(); lstGames.Dock = DockStyle.Fill; Theme.StyleList(lstGames);
            lstGames.ItemHeight = Math.Min(255, Theme.S(68));
            lstGames.DrawItem += DrawGameLibraryItem;
            lstGames.KeyDown += delegate(object s, KeyEventArgs e)
            {
                GameLibraryItem item = lstGames.SelectedItem as GameLibraryItem;
                if (e.KeyCode == Keys.Delete && item != null) { gameMode.RemoveProfile(item.Profile.Id); RefreshGames(); }
            };
            listWrap.AllowDrop = true; lstGames.AllowDrop = true;
            DragEventHandler enter = delegate(object s, DragEventArgs e)
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            };
            DragEventHandler drop = delegate(object s, DragEventArgs e)
            {
                AddDroppedGames(e.Data.GetData(DataFormats.FileDrop) as string[]);
            };
            listWrap.DragEnter += enter; listWrap.DragDrop += drop;
            lstGames.DragEnter += enter; lstGames.DragDrop += drop;
            listWrap.Controls.Add(lstGames);
            int bx = ContentX + listW + 16, bw = ContentW - listW - 16, bh = 40;
            var browse = new PillButton(Lang.T("v15.library.add"), BtnKind.Primary); browse.SetBounds(Theme.S(bx), Theme.S(y), Theme.S(bw), Theme.S(bh)); browse.Click += delegate { BrowseGameExecutable(); };
            var remove = new PillButton(Lang.T("btn.remove")); remove.SetBounds(Theme.S(bx), Theme.S(y + 50), Theme.S(bw), Theme.S(bh));
            remove.Click += delegate
            {
                GameLibraryItem item = lstGames.SelectedItem as GameLibraryItem;
                if (item != null) { gameMode.RemoveProfile(item.Profile.Id); RefreshGames(); }
            };
            Label hint = new Label(); hint.Text = Lang.T("v15.library.drop"); hint.ForeColor = Theme.Dim; hint.BackColor = Theme.Bg;
            hint.Font = Theme.UI(8.2f, false); hint.AutoEllipsis = true; hint.SetBounds(Theme.S(bx + 4), Theme.S(y + 108), Theme.S(bw - 8), Theme.S(48));
            pageWhite.Controls.AddRange(new Control[] { listWrap, browse, remove, hint });
            RefreshGames();
        }

        private void DrawGameLibraryItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            GameLibraryItem item = lstGames.Items[e.Index] as GameLibraryItem;
            if (item == null || item.Profile == null) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            int hover = lstGames.Tag is int ? (int)lstGames.Tag : -1;
            Rectangle row = Rectangle.Inflate(e.Bounds, -Theme.S(4), -Theme.S(3));
            using (var back = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(back, e.Bounds);
            Theme.FillRound(e.Graphics, row, Theme.S(10), selected ? Theme.Sel : (e.Index == hover ? Theme.CardHover : Theme.Card));
            int iconSize = Theme.S(38), ix = e.Bounds.X + Theme.S(14), iy = e.Bounds.Y + (e.Bounds.Height - iconSize) / 2;
            e.Graphics.DrawImage(GameIcon(item.Profile.ExecutablePath), new Rectangle(ix, iy, iconSize, iconSize));
            int tx = ix + iconSize + Theme.S(14), right = Theme.S(92);
            TextRenderer.DrawText(e.Graphics, item.Profile.Name, Theme.UI(10.2f, true),
                    new Rectangle(tx, e.Bounds.Y + Theme.S(11), e.Bounds.Width - tx - right, Theme.S(22)),
                    Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string path = item.Profile.ExecutablePath ?? item.Profile.Root ?? "";
            TextRenderer.DrawText(e.Graphics, path, Theme.UI(7.8f, false),
                    new Rectangle(tx, e.Bounds.Y + Theme.S(37), e.Bounds.Width - tx - Theme.S(18), Theme.S(18)),
                    Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, Lang.T(item.Running ? "v15.library.running" : "v15.library.ready"), Theme.UI(7.6f, true),
                    new Rectangle(e.Bounds.Right - right, e.Bounds.Y + Theme.S(12), right - Theme.S(16), Theme.S(20)),
                    item.Running ? Theme.Green : Theme.Faint, TextFormatFlags.Right | TextFormatFlags.NoPadding);
        }

        private void BuildPolicyPageV14()
        {
            policySync.Clear();
            int y = PageHeader(pageTame, Lang.T("nav.policy"), Lang.T("v15.policy.sub"), 2);
            var banner = new RoundPanel();
            banner.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(62));
            banner.BackColor = Theme.Bg; banner.Fill = Theme.Card; banner.Border = Theme.Stroke; banner.Radius = Theme.S(12);
            banner.AccentEdge = true;
            lblPolicyMode = CardLabel(banner, "", 18, 10, 300, 22, 9.5f, true, Theme.Accent);
            CardLabel(banner, Lang.T("v15.policy.mode.hint"), 18, 33, ContentW - 36, 18, 7.8f, false, Theme.Dim);
            pageTame.Controls.Add(banner); y += 74;

            var scroll = new DBPanel();
            scroll.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            scroll.BackColor = Theme.Bg; scroll.AutoScroll = true; Native.Dark(scroll); pageTame.Controls.Add(scroll);
            int sy = 2;
            Section(scroll, Lang.T("v15.policy.core"), 6, sy); sy += 24;
            swPolicyBackground = AddPolicyToggle(scroll, ref sy, Lang.T("v14.bg.master"), Lang.T("v14.bg.master.sub"),
                delegate { return gameMode.SuppressBackground; }, delegate(bool v) { gameMode.SuppressBackground = v; });
            var btnPolicyWhite = new PillButton(Lang.T("v14.manage.white"), BtnKind.Primary);
            // SettingCard 只负责定位 host，不会替按钮推导宽度；这里必须显式给宽，
            // 否则 Control 默认 0px，入口虽然存在于控件树却完全不可见。
            btnPolicyWhite.Size = new Size(Theme.S(164), Theme.S(34));
            btnPolicyWhite.Click += delegate
            {
                using (var dlg = new WhitelistDialog(gameMode)) ShowDim(dlg);
            };
            int whiteCardH;
            MakeAutoCard(scroll, 6, sy, ScrollContentW, 72, Lang.T("nav.white"), Lang.T("white.policy.sub"),
                btnPolicyWhite, out whiteCardH);
            sy += whiteCardH + 8;
            AddPolicyToggle(scroll, ref sy, Lang.T("gm.boost"), Lang.T("v15.boost.sub"),
                delegate { return gameMode.BoostGame; }, delegate(bool v) { gameMode.BoostGame = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("set.plan"), Lang.T("v15.plan.sub"),
                delegate { return gameMode.PowerPlanSwitch; }, delegate(bool v) { gameMode.PowerPlanSwitch = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("set.notif"), Lang.T("v15.notif.sub"),
                delegate { return gameMode.NotifQuiet; }, delegate(bool v) { gameMode.NotifQuiet = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("set.hz"), Lang.T("v15.hz.sub"),
                delegate { return gameMode.HzGuard; }, delegate(bool v) { gameMode.HzGuard = v; });

            sy += 10; Section(scroll, Lang.T("v15.policy.custom"), 6, sy); sy += 24;
            swPolicyStrict = AddPolicyToggle(scroll, ref sy, Lang.T("v14.cpu.adaptive"), Lang.T("v14.cpu.adaptive.sub2"),
                delegate { return gameMode.StrictCoreIsolation; }, delegate(bool v) { gameMode.StrictCoreIsolation = v; });
            cardPolicyStrict = (SettingCard)swPolicyStrict.Parent;
            swPolicyAggressive = AddPolicyToggle(scroll, ref sy, Lang.T("gm.aggressive"), Lang.T("gm.aggressive.sub"),
                delegate { return gameMode.AggressiveSuppression; }, delegate(bool v) { gameMode.AggressiveSuppression = v; });
            cardPolicyAggressive = (SettingCard)swPolicyAggressive.Parent;
            swPolicyNet = AddPolicyToggle(scroll, ref sy, Lang.T("gm.net"), Lang.T("v15.custom.override"), delegate { return gameMode.NetOptimize; }, delegate(bool v) { gameMode.NetOptimize = v; });
            cardPolicyNet = (SettingCard)swPolicyNet.Parent;
            swPolicyFg = AddPolicyToggle(scroll, ref sy, Lang.T("gm.fgboost"), Lang.T("v15.custom.override"), delegate { return gameMode.FgSchedBoost; }, delegate(bool v) { gameMode.FgSchedBoost = v; });
            cardPolicyFg = (SettingCard)swPolicyFg.Parent;
            swPolicyMmcss = AddPolicyToggle(scroll, ref sy, Lang.T("gm.mmcss"), Lang.T("v15.custom.override"), delegate { return gameMode.MmcssPriority; }, delegate(bool v) { gameMode.MmcssPriority = v; });
            cardPolicyMmcss = (SettingCard)swPolicyMmcss.Parent;
            swPolicyPauseDl = AddPolicyToggle(scroll, ref sy, Lang.T("gm.pausedl"), Lang.T("v15.custom.override"), delegate { return gameMode.PauseDownloads; }, delegate(bool v) { gameMode.PauseDownloads = v; });
            cardPolicyPauseDl = (SettingCard)swPolicyPauseDl.Parent;
            swPolicyPauseSvc = AddPolicyToggle(scroll, ref sy, Lang.T("gm.pausesvc"), Lang.T("v15.custom.override"), delegate { return gameMode.PauseSvcIndex; }, delegate(bool v) { gameMode.PauseSvcIndex = v; });
            cardPolicyPauseSvc = (SettingCard)swPolicyPauseSvc.Parent;
            swPolicyDvr = AddPolicyToggle(scroll, ref sy, Lang.T("set.dvr"), Lang.T("v15.custom.override"), delegate { return gameMode.KillGameDvr; }, delegate(bool v) { gameMode.KillGameDvr = v; });
            cardPolicyDvr = (SettingCard)swPolicyDvr.Parent;
            AddPolicyToggle(scroll, ref sy, Lang.T("gm.idledisable"), Lang.T("gm.idledisable.sub"),
                delegate { return gameMode.IdleStateDisable; }, delegate(bool v) { gameMode.IdleStateDisable = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("gm.visualfx"), Lang.T("gm.visualfx.sub"),
                delegate { return gameMode.VisualFxDowngrade; }, delegate(bool v) { gameMode.VisualFxDowngrade = v; });
            AddPolicyToggle(scroll, ref sy, Lang.T("set.trim"), Lang.T("v15.trim.sub"),
                delegate { return gameMode.TrimWorkingSet; }, delegate(bool v) { gameMode.TrimWorkingSet = v; });
            RefreshPolicyPresentation();
        }

        private Toggle AddPolicyToggle(Control parent, ref int y, string title, string desc, Func<bool> read, Action<bool> write)
        {
            Toggle sw = MakeSwitch(read(), null);
            sw.CheckedChanged += delegate { write(sw.Checked); };
            // 高度要能容下两行说明（描述区 = 高度 - S(40)），否则风险提示会被截掉
            int cardH;
            SettingCard card = MakeAutoCard(parent, 6, y, ScrollContentW, 78, title, desc, sw, out cardH);
            y += cardH + 8;
            policySync.Add(delegate { sw.SetSilently(read()); });
            return sw;
        }

        private void RefreshPolicyPresentation()
        {
            if (lblPolicyMode != null) lblPolicyMode.Text = Lang.F("mode.policy.active", ModeButton.ModeName(gameMode.ActivePreset));
            PerformancePreset mode = gameMode.ActivePreset;
            bool competitive = mode == PerformancePreset.Competitive;
            bool custom = mode == PerformancePreset.Custom;
            ApplyPresetPolicy(swPolicyStrict, cardPolicyStrict, Lang.T("v14.cpu.adaptive"), competitive, true);
            ApplyPresetPolicy(swPolicyAggressive, cardPolicyAggressive, Lang.T("gm.aggressive"), !custom, competitive);
            ApplyPresetPolicy(swPolicyNet, cardPolicyNet, Lang.T("gm.net"), !custom, competitive);
            ApplyPresetPolicy(swPolicyFg, cardPolicyFg, Lang.T("gm.fgboost"), !custom, true);
            ApplyPresetPolicy(swPolicyMmcss, cardPolicyMmcss, Lang.T("gm.mmcss"), !custom, competitive);
            ApplyPresetPolicy(swPolicyPauseDl, cardPolicyPauseDl, Lang.T("gm.pausedl"), !custom, competitive);
            ApplyPresetPolicy(swPolicyPauseSvc, cardPolicyPauseSvc, Lang.T("gm.pausesvc"), !custom, false);
            ApplyPresetPolicy(swPolicyDvr, cardPolicyDvr, Lang.T("set.dvr"), !custom, competitive);
        }

        private static void ApplyPresetPolicy(Toggle toggle, SettingCard card, string title, bool forced, bool effective)
        {
            if (toggle != null)
            {
                toggle.Enabled = !forced;
                if (forced) toggle.SetSilently(effective);
            }
            if (card != null) card.Title = title + (forced ? " · " + Lang.T("v14.preset.forced") : "");
        }

        private void BuildAntiCheatPageV14()
        {
            int y = PageHeader(pageAnti, Lang.T("v14.anticheat"), Lang.T("v15.anticheat.sub"), 2);
            Section(pageAnti, Lang.T("v14.anticheat.boundary"), 26, y + 8); y += 46;
            swTame = MakeSwitch(!tamer.Paused, delegate { tamer.Paused = !swTame.Checked; Settings.Save("TameOn", swTame.Checked); });
            MakeCard(pageAnti, ContentX, y, ContentW, 56, Lang.T("tame.toggle"), Lang.T("v14.anticheat.master.sub"), swTame); y += 66;
            tameList = new DBPanel();
            tameList.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
            tameList.BackColor = Theme.Bg; tameList.AutoScroll = true; Native.Dark(tameList); pageAnti.Controls.Add(tameList);
            RefreshTameList();
        }

        private void BuildReportsPageV14()
        {
            int y = PageHeader(pageReports, Lang.T("nav.reports"), Lang.T("v14.reports.sub"), 2);
            var wrap = new RoundPanel();
            wrap.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(PageH - y - 66));
            wrap.BackColor = Theme.Bg; wrap.Fill = Theme.Inset; wrap.Border = Theme.Stroke; wrap.Radius = Theme.S(14); wrap.Padding = new Padding(Theme.S(14));
            tbReports = new TextBox(); tbReports.Multiline = true; tbReports.ReadOnly = true; tbReports.ScrollBars = ScrollBars.Vertical;
            tbReports.BackColor = Theme.Inset; tbReports.ForeColor = Theme.Fg; tbReports.BorderStyle = BorderStyle.None; tbReports.Font = Theme.Mono(8.75f); tbReports.Dock = DockStyle.Fill;
            Native.Dark(tbReports); wrap.Controls.Add(tbReports);
            var openReport = new PillButton(Lang.T("v14.open.report")); openReport.SetBounds(Theme.S(ContentX), Theme.S(PageH - 48), Theme.S(190), Theme.S(36));
            openReport.Click += delegate { OpenTextFile(Path.Combine(Paths.Data, SessionReportStore.FileName)); };
            var openLog = new PillButton(Lang.T("btn.openlog")); openLog.SetBounds(Theme.S(ContentX + 202), Theme.S(PageH - 48), Theme.S(190), Theme.S(36));
            openLog.Click += delegate { OpenTextFile(Logger.LogPath); };
            pageReports.Controls.AddRange(new Control[] { wrap, openReport, openLog }); RefreshReportsV14();
        }

        private void OpenTextFile(string path)
        {
            try { if (!File.Exists(path)) File.WriteAllText(path, "", System.Text.Encoding.UTF8); using (Process.Start(System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe"), path)) { } }
            catch { }
        }

        private void RefreshBoostPresentation()
        {
            if (lblOverviewBoost == null) return;
            string text = gameMode.BoostStatusText;
            if (lblOverviewBoost.Text != text) lblOverviewBoost.Text = text;
            lblOverviewBoost.ForeColor = gameMode.BoostStateVerified ? Theme.Green
                : (gameMode.BoostStateFailed ? Theme.Danger : Theme.Fg);
        }

        private void RefreshReportsV14()
        {
            if (tbReports == null) return;
            string text = SessionReportStore.ReadTail(Paths.Data, 120);
            if (tbReports.Text != text) tbReports.Text = text;
        }

    }
}

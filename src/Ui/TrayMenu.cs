// @author bdth 2074055628@qq.com
// 文件用途 构建托盘菜单并同步运行状态

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AegisApp
{
    internal sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color MenuBorder { get { return Theme.Stroke; } }
        public override Color MenuItemBorder { get { return Theme.Sel; } }
        public override Color MenuItemSelected { get { return Theme.CardHover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.CardHover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.CardHover; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.Nav; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.Nav; } }
        public override Color SeparatorDark { get { return Theme.Stroke; } }
        public override Color SeparatorLight { get { return Theme.Stroke; } }
        public override Color CheckBackground { get { return Theme.Sel; } }
        public override Color CheckSelectedBackground { get { return Theme.Sel; } }
        public override Color CheckPressedBackground { get { return Theme.Sel; } }
    }

    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (var pen = new Pen(Theme.Stroke)) e.Graphics.DrawRectangle(pen, r);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var it = e.Item;
            bool open = (it is ToolStripMenuItem) && ((ToolStripMenuItem)it).DropDown.Visible;
            if (it.Selected || open)
            {
                var r = new Rectangle(Theme.S(4), Theme.S(2), it.Width - Theme.S(8), it.Height - Theme.S(4));
                Theme.FillRound(g, r, Theme.S(7), open ? Theme.Sel : Theme.CardHover);
            }

            var mi = it as ToolStripMenuItem;
            if (mi != null && mi.Checked)
            {
                int sz = Theme.S(13);
                int x = it.Width - Theme.S(14) - sz;
                DrawCheck(g, new Rectangle(x, (it.Height - sz) / 2, sz, sz));
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

        internal static void DrawCheck(Graphics g, Rectangle rect)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Theme.Accent, Math.Max(2f, Theme.S(2))))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                var p1 = new PointF(rect.X + rect.Width * 0.12f, rect.Y + rect.Height * 0.52f);
                var p2 = new PointF(rect.X + rect.Width * 0.40f, rect.Y + rect.Height * 0.78f);
                var p3 = new PointF(rect.X + rect.Width * 0.86f, rect.Y + rect.Height * 0.24f);
                g.DrawLines(pen, new[] { p1, p2, p3 });
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Fg : Theme.Faint;

            Rectangle r = e.TextRectangle;
            if (r.Height > 0 && r.Height < e.Item.Height)
            {
                int y = (e.Item.Height - r.Height) / 2;
                if (y != r.Y) e.TextRectangle = new Rectangle(r.X, y, r.Width, r.Height);
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var pen = new Pen(Col.Alpha(Theme.Stroke, 150)))
                e.Graphics.DrawLine(pen, Theme.S(12), y, e.Item.Width - Theme.S(12), y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Selected ? Theme.Fg : Theme.Dim;
            base.OnRenderArrow(e);
        }
    }

    internal sealed class TrayMenu
    {
        private readonly Tamer tamer;
        private readonly GameMode gameMode;
        private readonly Action openPanel;
        private readonly Action exitApp;
        private readonly Action afterChange;
        private readonly ContextMenuStrip strip;

        public ContextMenuStrip Strip { get { return strip; } }

        public TrayMenu(Tamer tamer, GameMode gameMode, Action openPanel, Action exitApp, Action afterChange)
        {
            this.tamer = tamer;
            this.gameMode = gameMode;
            this.openPanel = openPanel;
            this.exitApp = exitApp;
            this.afterChange = afterChange;

            ToolStripManager.Renderer = new DarkMenuRenderer();
            strip = new ContextMenuStrip();
            StyleDropDown(strip);
            strip.Opening += (s, e) => Rebuild();
            Rebuild();
        }

        private void Changed() { var a = afterChange; if (a != null) { try { a(); } catch { } } }

        private bool EffectiveDvr()
        {
            PerformancePreset mode = gameMode.ActivePreset;
            return mode == PerformancePreset.Custom
                ? gameMode.KillGameDvr
                : mode == PerformancePreset.Competitive;
        }

        private static void StyleDropDown(ToolStripDropDown dd)
        {
            dd.Font = Theme.UI(9.5f, false);
            var menu = dd as ToolStripDropDownMenu;
            if (menu != null)
            {
                menu.ShowImageMargin = true;
                menu.ShowCheckMargin = false;
                menu.ImageScalingSize = new Size(Theme.S(6), Theme.S(6));
            }
            dd.Opened += (s, e) => { try { Native.RoundCorners(dd.Handle); } catch { } };
        }

        private ToolStripMenuItem Item(string text, EventHandler onClick)
        {
            var mi = new ToolStripMenuItem(text);
            mi.ForeColor = Theme.Fg;
            mi.Padding = new Padding(0, Theme.S(7), 0, Theme.S(7));
            if (onClick != null) mi.Click += onClick;
            return mi;
        }

        private ToolStripMenuItem SubMenu(string text)
        {
            var mi = Item(text, null);
            StyleDropDown(mi.DropDown);
            return mi;
        }

        private ToolStripMenuItem Check(string text, bool on, EventHandler onClick)
        {
            var mi = Item(text + Reserve(), onClick);
            mi.Checked = on;
            return mi;
        }

        private static string reserve;
        private static string Reserve()
        {
            if (reserve == null)
            {
                var f = Theme.UI(9.5f, false);
                {
                    int w = TextRenderer.MeasureText(" ", f, Size.Empty, TextFormatFlags.NoPadding).Width;
                    int n = w > 0 ? (int)Math.Ceiling((double)Theme.S(30) / w) : 6;
                    reserve = new string(' ', Math.Max(7, n));
                }
            }
            return reserve;
        }

        private void Rebuild()
        {
            while (strip.Items.Count > 0) { var it = strip.Items[0]; strip.Items.RemoveAt(0); it.Dispose(); }

            strip.Items.Add(Item(Lang.T("tray.open"), (s, e) => openPanel()));
            strip.Items.Add(new ToolStripSeparator());

            strip.Items.Add(Check(Lang.T("v14.master"), gameMode.Enabled, (s, e) =>
            {
                gameMode.Enabled = !gameMode.Enabled;
                Settings.Save("GameModeOn", gameMode.Enabled);
                Changed();
            }));
            var currentMode = Item(Lang.F("mode.tray.current", ModeButton.ModeName(gameMode.ActivePreset)), (s, e) => openPanel());
            currentMode.ForeColor = Theme.ModeColor(gameMode.ActivePreset);
            strip.Items.Add(currentMode);
            strip.Items.Add(Check(Lang.T("nav.tame"), !tamer.Paused, (s, e) =>
            {
                tamer.Paused = !tamer.Paused;
                Settings.Save("TameOn", !tamer.Paused);
                Changed();
            }));

            var ac = SubMenu(Lang.T("tray.aclist"));
            foreach (AcGroup g in AntiCheatCatalog.Groups)
            {
                string key = g.Key;
                ac.DropDownItems.Add(Check(Lang.T("ac." + key + ".n"), tamer.IsGroupEnabled(key), (s, e) =>
                {
                    tamer.SetGroupEnabled(key, !tamer.IsGroupEnabled(key));
                    Changed();
                }));
            }
            strip.Items.Add(ac);

            var set = SubMenu(Lang.T("nav.set"));
            set.DropDownItems.Add(Check(Lang.T("tm.gpu"), gameMode.GpuHighPerf, (s, e) => { gameMode.GpuHighPerf = !gameMode.GpuHighPerf; Changed(); }));

            bool dvrForced = gameMode.ActivePreset != PerformancePreset.Custom;
            ToolStripMenuItem dvr = Check(Lang.T("tm.dvr") + (dvrForced ? " · " + Lang.T("v14.preset.forced") : ""), EffectiveDvr(),
                (s, e) => { gameMode.KillGameDvr = !gameMode.KillGameDvr; Changed(); });
            dvr.Enabled = !dvrForced;
            set.DropDownItems.Add(dvr);
            set.DropDownItems.Add(Check(Lang.T("tm.fso"), gameMode.DisableFso, (s, e) => { gameMode.DisableFso = !gameMode.DisableFso; Changed(); }));
            set.DropDownItems.Add(new ToolStripSeparator());
            set.DropDownItems.Add(Check(Lang.T("tm.notif"), gameMode.NotifQuiet, (s, e) => { gameMode.NotifQuiet = !gameMode.NotifQuiet; Changed(); }));
            set.DropDownItems.Add(Check(Lang.T("tm.trim"), gameMode.TrimWorkingSet, (s, e) => { gameMode.TrimWorkingSet = !gameMode.TrimWorkingSet; Changed(); }));
            set.DropDownItems.Add(Check(Lang.T("tm.plan"), gameMode.PowerPlanSwitch, (s, e) => { gameMode.PowerPlanSwitch = !gameMode.PowerPlanSwitch; Changed(); }));
            set.DropDownItems.Add(new ToolStripSeparator());
            set.DropDownItems.Add(Check(Lang.T("tm.autostart"), TaskHelper.TaskExistsCached(), (s, e) => { ToggleAutostart(); Changed(); }));

            // 语言子菜单随英文/日文译文一并移除；恢复多语言时加回来即可。
            strip.Items.Add(set);

            strip.Items.Add(new ToolStripSeparator());
            strip.Items.Add(Item(Lang.T("tray.reset"), (s, e) => ResetDefaults()));
            strip.Items.Add(new ToolStripSeparator());
            strip.Items.Add(Item(Lang.T("tray.exit"), (s, e) => exitApp()));
        }

        private void ToggleAutostart()
        {
            if (!TaskHelper.TaskExists())
                TaskHelper.CreateStartupTask();
            else
                TaskHelper.DeleteStartupTask();
        }

        private void ResetDefaults()
        {
            if (MessageBox.Show(Lang.T("tray.resetask"), "Aegis", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

            gameMode.SuppressBackground = true;
            gameMode.BoostGame = true;
            gameMode.PowerPlanSwitch = true;
            gameMode.NetOptimize = true;
            gameMode.MmcssPriority = true;
            gameMode.PauseDownloads = true;
            gameMode.FgSchedBoost = true;
            gameMode.PauseSvcIndex = false;
            gameMode.GpuHighPerf = true;
            gameMode.KillGameDvr = true;
            gameMode.DisableFso = false;
            gameMode.NotifQuiet = false;
            gameMode.TrimWorkingSet = false;
            gameMode.HzGuard = false;
            gameMode.StrictCoreIsolation = false;
            gameMode.AggressiveSuppression = false;
            gameMode.IdleStateDisable = true;
            gameMode.VisualFxDowngrade = false;
            gameMode.Enabled = true; Settings.Save("GameModeOn", true);
            gameMode.Preset = PerformancePreset.Standard;
            bool whitelistReset = gameMode.ResetWhitelist();

            foreach (AcGroup g in AntiCheatCatalog.Groups) tamer.SetGroupEnabled(g.Key, g.Default);
            tamer.Paused = false; Settings.Save("TameOn", true);

            Changed();
            if (!whitelistReset)
            {
                MessageBox.Show(
                    gameMode.WhitelistLastError, "Aegis",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Logger.Log("默认配置已部分恢复，但白名单写入失败");
            }
            else Logger.Log("已恢复默认配置");
        }
    }
}

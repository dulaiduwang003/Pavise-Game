// @author bdth 2074055628@qq.com
// 文件用途 面板各页通用的控件工厂 页眉 分节 开关与设置卡

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm
    {
        private int PageHeader(DBPanel page, string title, string sub, int subLines)
        {
            var rail = new AccentLine();
            rail.SetBounds(Theme.S(26), Theme.S(5), Theme.S(28), Math.Max(1, Theme.S(2)));
            page.Controls.Add(rail);

            var sys = new Label();
            sys.Text = "PAVISE  //  CONTROL";
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

        internal const int DescMaxLines = 3;
        internal const int CollapseChevronW = 20;

        private int AutoCardHeight(string desc, int cardW, Control host, int minHeight)
        {
            return AutoCardHeight(desc, cardW, host, minHeight, 0);
        }

        private int AutoCardHeight(string desc, int cardW, Control host, int minHeight, int valueReserve)
        {
            if (string.IsNullOrEmpty(desc)) return minHeight;
            int padL = Theme.S(18);
            int reserve = padL + (host != null ? host.Width + Theme.S(14) : 0);
            int chevW = Theme.S(CollapseChevronW);
            int textW = Theme.S(cardW) - padL - reserve - valueReserve - chevW;
            if (textW <= 0) return minHeight;
            Font font = Theme.UI(8.5f, false);
            int lineH = TextRenderer.MeasureText("Ag", font).Height;
            if (lineH <= 0) return minHeight;
            int need = TextRenderer.MeasureText(
                desc, font, new Size(textW, int.MaxValue), TextFormatFlags.WordBreak).Height;
            int lines = (need + lineH - 1) / lineH;
            if (lines < 1) lines = 1;
            if (lines > DescMaxLines) lines = DescMaxLines;
            int scale100 = Theme.S(100);
            if (scale100 <= 0) return minHeight;
            int px = lines * lineH + Theme.S(SettingCard.StatusLineH) + Theme.S(44);
            int logical = (px * 100 + scale100 - 1) / scale100;
            return logical > minHeight ? logical : minHeight;
        }

        private SettingCard MakeAutoCard(
            Control parent, int x, int y, int w, int minH, string title, string desc, Control host, out int used)
        {
            used = AutoCardHeight(desc, w, host, minH);
            return MakeCard(parent, x, y, w, used, title, desc, host);
        }

        private SettingCard MakeAutoCard(
            Control parent, int x, int y, int w, int minH, string title, string desc, Control host,
            int valueReserve, out int used)
        {
            used = AutoCardHeight(desc, w, host, minH, valueReserve);
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

        private readonly List<Label> accentLabels = new List<Label>();

        // 用当前主题色创建的 Label 自动登记 由 RefreshAccentLabels 统一刷新
        // WinForms Label 的 ForeColor 定死在赋值那刻 不会随主题色动画走 靠这个注册制兜底
        // 避免每加一个主题色文字就得去 OnFormFrame 手动点名 漏一个就定死一个
        private Label AccentLabel(Control parent, string text, int x, int y, int w, int h, float size, bool bold)
        {
            Label l = CardLabel(parent, text, x, y, w, h, size, bold, Theme.Accent);
            accentLabels.Add(l);
            return l;
        }

        private void RefreshAccentLabels()
        {
            for (int i = accentLabels.Count - 1; i >= 0; i--)
            {
                Label l = accentLabels[i];
                if (l == null || l.IsDisposed) { accentLabels.RemoveAt(i); continue; }
                l.ForeColor = Theme.Accent;
            }
        }

        private readonly List<Action> themeRefreshers = new List<Action>();

        // 颜色随业务状态变化的元素(不是恒定 accent 的 那类用 AccentLabel)在此注册
        // 主题色切换结束时统一重跑其状态刷新 让条件色也跟随主题
        // 用注册制取代 OnFormFrame 里逐页硬编码特判 以后加条件色元素挂上来即可 不会再漏
        private void RegisterThemeRefresh(Action refresh)
        {
            if (refresh != null) themeRefreshers.Add(refresh);
        }

        private void RunThemeRefreshers()
        {
            foreach (Action r in themeRefreshers)
            {
                try { r(); } catch { }
            }
        }

        private DialogResult ShowDim(Form dlg)
        {
            return dlg.ShowDialog(this);
        }

        private readonly Dictionary<Control, DBPanel[]> pageTabPanels
            = new Dictionary<Control, DBPanel[]>();

        private DBPanel[] MakeTabPanels(Control page, TechTabs tabs, int count, int y)
        {
            var panels = new DBPanel[count];
            for (int i = 0; i < panels.Length; i++)
            {
                var panel = new DBPanel();
                panel.SetBounds(Theme.S(20), Theme.S(y), Theme.S(PageW - 40), Theme.S(PageH - y - 8));
                panel.BackColor = Theme.Bg; panel.AutoScroll = true; Native.Dark(panel);
                panel.Visible = i == 0;
                page.Controls.Add(panel);
                panels[i] = panel;
            }
            pageTabPanels[page] = panels;
            tabs.IndexChanged = delegate(int index)
            {
                for (int i = 0; i < panels.Length; i++)
                {
                    if (i != index) { Fx.Settle(panels[i]); panels[i].Visible = false; }
                }
                panels[index].Visible = true;
                Fx.SlideIn(panels[index]);
            };
            return panels;
        }
    }
}

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
            var tag = new Label();
            tag.Text = PageModuleCode(page);
            tag.ForeColor = Theme.Faint; tag.BackColor = Color.Transparent;
            tag.Font = Theme.Mono(6.2f); tag.TextAlign = ContentAlignment.MiddleRight;
            tag.SetBounds(Theme.S(ContentX + ContentW - 230), Theme.S(25), Theme.S(214), Theme.S(18));
            page.Controls.Add(tag);
            var tagRail = new AccentLine();
            tagRail.SetBounds(Theme.S(ContentX + ContentW - 112), Theme.S(50), Theme.S(96), Math.Max(1, Theme.S(1)));
            page.Controls.Add(tagRail);

            var titleRail = new AccentLine();
            titleRail.SetBounds(Theme.S(ContentX - 2), Theme.S(31), Theme.S(3), Theme.S(24));
            page.Controls.Add(titleRail);
            var t = new Label();
            t.Text = title;
            t.ForeColor = Theme.Fg; t.BackColor = Color.Transparent;
            t.Font = Theme.UI(18f, true);
            t.UseCompatibleTextRendering = false;
            t.SetBounds(Theme.S(ContentX + 12), Theme.S(27), Theme.S(ContentW - 270), Theme.S(38));
            page.Controls.Add(t);
            int y = 72;
            if (!string.IsNullOrEmpty(sub))
            {
                var s2 = new Label();
                s2.Text = sub;
                s2.ForeColor = Theme.Dim; s2.BackColor = Color.Transparent;
                s2.Font = Theme.UI(9.5f, false);
                s2.UseCompatibleTextRendering = false;
                s2.AutoEllipsis = true;
                s2.SetBounds(Theme.S(ContentX), Theme.S(y), Theme.S(ContentW), Theme.S(18 * subLines + 2));
                page.Controls.Add(s2);
                y += 18 * subLines + 5;
            }
            return y + 8;
        }

        private string PageModuleCode(DBPanel page)
        {
            if (page == pagePolicy) return "POLICY // MODULE 01";
            if (page == pageAntiCheat) return "DEFENSE // MODULE 02";
            if (page == pageWhitelist) return "EXCLUSION // MODULE 03";
            if (page == pageGraphics) return "GRAPHICS // MODULE 04";
            if (page == pageEnvironment) return "SYSTEM // MODULE 05";
            if (page == pageIrq) return "INTERRUPT // MODULE 06";
            if (page == pageGameConfig) return "PROFILE // MODULE 07";
            if (page == pageAudit) return "REPORT // MODULE 08";
            if (page == pageLog) return "EVENT BUS // MODULE 09";
            return "PAVISE // CONTROL SURFACE";
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
            int padL = Theme.S(42);
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
            int channel = 1;
            foreach (Control child in parent.Controls) if (child is SettingCard) channel++;
            c.Channel = channel;
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

        // 文字放不下就逐档缩字号 缩到下限还放不下才交给省略号
        //   状态行里带着游戏名 长度不定 光把字号调小治不了根 得按实际内容自适应
        //   Theme.UI 有字体缓存 反复取同一档不会新建字体
        internal const float StatusFontMax = 12.5f;
        internal const float StatusFontMin = 8.5f;

        internal static float FitFontSize(string text, int widthPx, bool bold,
            float maxSize, float minSize)
        {
            if (string.IsNullOrEmpty(text) || widthPx <= 0) return maxSize;
            for (float size = maxSize; size > minSize; size -= 0.5f)
            {
                Size m = TextRenderer.MeasureText(text, Theme.UI(size, bold),
                    new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
                if (m.Width <= widthPx) return size;
            }
            return minSize;
        }

        private static void FitLabelFont(Label label, bool bold, float maxSize, float minSize)
        {
            if (label == null || label.IsDisposed || label.Width <= 0) return;
            float size = FitFontSize(label.Text, label.Width, bold, maxSize, minSize);
            Font want = Theme.UI(size, bold);
            if (!ReferenceEquals(label.Font, want)) label.Font = want;
        }

        private readonly List<Label> accentLabels = new List<Label>();

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
                var panel = new WorkspacePanel();
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

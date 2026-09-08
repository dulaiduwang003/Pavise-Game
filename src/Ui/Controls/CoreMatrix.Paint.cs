// @author bdth 2074055628@qq.com
// 文件用途 核心矩阵的绘制 热度条与图例
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed partial class CoreMatrix : Control
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (Backdrop.AppliesTo(this)) Backdrop.PaintOnCard(g, this, ClientRectangle);
            else using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            foreach (Band b in bands)
            {
                var titleRect = new Rectangle(0, b.TitleY, Width, Theme.S(BandHead));
                TextRenderer.DrawText(g, b.Title, Theme.UI(8.4f, true), titleRect, Theme.Fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, b.Meta, Theme.MonoFor(b.Meta, 7.4f), titleRect, Theme.Faint,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                int lineY = b.TitleY + Theme.S(BandHead) - Theme.S(4);
                using (var p = new Pen(Theme.Stroke))
                    g.DrawLine(p, 0, lineY, Width - 1, lineY);

                foreach (Group grp in b.Groups) DrawGroup(g, grp);
            }

            if (annotate) DrawLegend(g);
        }

        private void DrawGroup(Graphics g, Group grp)
        {
            int on = 0;
            foreach (Cell c in grp.Cells)
                if ((selected & (1UL << c.Cpu)) != 0) on++;
            bool anyOn = on > 0;
            bool exclusive = MarkExclusive && grp.Cells.Count > 1 && on == 1;

            float lit = 0f;
            foreach (Cell c in grp.Cells)
                if (cellOn[c.Cpu].Value > lit) lit = cellOn[c.Cpu].Value;

            // 整张物理核卡的峰值负载 决定卡片是否 烧红 发光
            float peak = 0f;
            if (annotate && HasLoads)
                foreach (Cell c in grp.Cells)
                    if (cellHeat[c.Cpu].Value > peak) peak = cellHeat[c.Cpu].Value;
            bool cardHot = annotate && HasLoads && peak >= WarmT;

            Rectangle r = grp.Rect;
            r.Width -= 1; r.Height -= 1;
            using (GraphicsPath path = Theme.TechPath(r, Theme.S(5)))
            {
                using (var b = new SolidBrush(Col.Lerp(Theme.Bg, Theme.Card, lit)))
                    g.FillPath(b, path);
                Color edge = exclusive ? Col.Alpha(Theme.Accent, 200)
                    : anyOn ? Theme.StrokeHi
                    : Theme.Stroke;
                float edgeW = exclusive ? Math.Max(1f, Theme.S(2) * 0.75f) : 1f;
                if (annotate)
                {
                    // 当前落核卡片=紫罗兰霓虹边(与高负载红发光明确区分) 繁忙卡片=按峰值负载烧红外发光
                    if ((grp.Mask & seenMask) != 0)
                    {
                        edge = Col.Alpha(SeenViolet, 235);
                        edgeW = Math.Max(1f, Theme.S(2) * 1.0f);
                        NeonEdge(g, path, SeenViolet, 0.85f);
                    }
                    else if (cardHot)
                    {
                        Color rc = LoadRog(peak);
                        float ck = GlowK(peak);
                        edge = Col.Lerp(Col.Alpha(rc, 224), Color.White, 0.10f * ck);
                        edgeW = Math.Max(1f, Theme.S(2) * 0.85f);
                        NeonEdge(g, path, rc, ck);
                    }
                }
                using (var p = new Pen(edge, edgeW))
                    g.DrawPath(p, path);
            }

            var head = new Rectangle(r.Left + Theme.S(GroupPad), r.Top + Theme.S(1),
                r.Width - Theme.S(GroupPad) * 2, Theme.S(GroupHead));
            bool cache = cacheHeavy != 0 && (grp.Mask & cacheHeavy) != 0;
            TextRenderer.DrawText(g, "C" + grp.Index.ToString("00"), Theme.Mono(6.9f), head,
                cache ? (anyOn ? Theme.Accent2 : Col.Alpha(Theme.Accent2, 170))
                    : anyOn ? Theme.Dim : Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            bool roomy = grp.Cells.Count > 1;
            if (annotate)
            {
                // 选核弹窗 头标改成核类型 指引 挪去哪里 ;当前落核卡片改标紫罗兰 当前 让落点自解释
                bool grpSeen = (grp.Mask & seenMask) != 0;
                CoreKind kind = KindOf(grp);
                string ktag = grpSeen ? Lang.T("core.tag.now")
                    : kind == CoreKind.Game ? Lang.T("core.tag.game")
                    : kind == CoreKind.Eff ? Lang.T("core.irqtype.e")
                    : kind == CoreKind.PerfIdle ? Lang.T("core.irqtype.pidle")
                    : Lang.T("core.irqtype.p");
                Color kcol = grpSeen ? Col.Alpha(SeenViolet, 245)
                    : kind == CoreKind.Game ? Col.Alpha(Theme.Danger, 225)
                    : kind == CoreKind.Eff ? Col.Alpha(Theme.Faint, 190)
                    : kind == CoreKind.PerfIdle ? RogCool
                    : Col.Alpha(Theme.Dim, 190);
                TextRenderer.DrawText(g, ktag, Theme.MonoFor(ktag, 6.4f), head, kcol,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            else
            {
                string tag = exclusive ? Lang.T("core.tag.exclusive")
                    : anyOn ? (roomy ? PrimaryTag : null)
                    : cache ? Lang.T("core.tag.cache")
                    : roomy ? "SMT" : null;
                if (tag != null)
                    TextRenderer.DrawText(g, tag, Theme.MonoFor(tag, 6.4f), head,
                        exclusive ? Theme.Accent
                            : anyOn ? Col.Alpha(Theme.Accent, 220)
                            : cache ? Col.Alpha(Theme.Accent2, 170)
                            : Col.Alpha(Theme.Faint, 150),
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                            | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }

            foreach (Cell c in grp.Cells)
            {
                if (annotate) DrawCellAnno(g, c); else DrawCell(g, c);
            }
        }

        // 非标注页(核心页 / 自定义核) 保持原样 一行不变
        private void DrawCell(Graphics g, Cell c)
        {
            float on = cellOn[c.Cpu].Value;
            float hot = cellHot[c.Cpu].Value;
            Rectangle r = c.Rect;
            r.Inflate(-Theme.S(2), -Theme.S(2));

            Color fill = Col.Lerp(Theme.Bg, Theme.Accent, on);
            if (hot > 0.01f)
                fill = Col.Lerp(fill, Col.Lerp(Theme.Accent, Color.White, on),
                    hot * (0.25f - on * 0.03f));
            using (GraphicsPath path = Theme.TechPath(r, Theme.S(4)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                Color border = Col.Lerp(Theme.Stroke, Col.Alpha(Theme.Accent, 235), on);
                using (var p = new Pen(border, 1f)) g.DrawPath(p, path);
            }

            TextRenderer.DrawText(g, c.Cpu.ToString(), Theme.Mono(7.6f), r,
                Col.Lerp(Col.Lerp(Theme.Dim, Theme.Fg, hot), Theme.OnAccent, on),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        // 选核弹窗 边框/负载条保留辉光 数字只画一次 保证高负载时依然清晰
        private void DrawCellAnno(Graphics g, Cell c)
        {
            float on = cellOn[c.Cpu].Value;
            float hot = cellHot[c.Cpu].Value;
            Rectangle r = c.Rect;
            r.Inflate(-Theme.S(2), -Theme.S(2));

            double value = 0;
            bool hasLoad = loads != null && loads.TryGetValue(c.Cpu, out value);
            float t = cellHeat[c.Cpu].Value;                 // 平滑后的负载 0..1
            Color load = LoadRog(t);
            bool warm = hasLoad && t >= WarmT;               // 60% 起就有发光张力
            float gk = GlowK(t);

            // 繁忙核先在卡底铺一圈大红辉光 外溢到卡片背景上 空闲核不铺 一眼分空/忙
            if (warm) GlowEllipse(g, r, load, gk, 150, 13);

            // 淡色负载底保留明暗主题的文字对比度 选中时使用强调色
            Color baseFill = Col.Lerp(Theme.Inset, Theme.Bg, 0.35f);
            if (hasLoad) baseFill = Col.Lerp(baseFill, load, 0.06f + 0.18f * t);
            Color fill = Col.Lerp(baseFill, Theme.Accent, on);
            if (hot > 0.01f)
                fill = Col.Lerp(fill, Col.Lerp(Theme.Accent, Color.White, on), hot * 0.22f);

            using (GraphicsPath path = Theme.TechPath(r, Theme.S(4)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                Color border;
                if (on >= 0.5f) border = Col.Alpha(Theme.Accent, 235);
                else if (warm) border = Col.Lerp(Col.Alpha(load, 220), Color.White, 0.12f * gk);
                else if (hasLoad) border = Col.Lerp(Col.Alpha(Theme.Stroke, 210), load, 0.30f + 0.55f * t);
                else border = Theme.Stroke;
                float bw = warm ? Math.Max(1f, Theme.S(2) * 0.8f) : 1f;
                // 未选中的繁忙核 多层红外发光描边
                if (warm && on < 0.5f) NeonEdge(g, path, load, gk);
                using (var p = new Pen(border, bw)) g.DrawPath(p, path);
            }

            // 发光负载条:填充比例=负载 低冷高红 高负载更亮更饱和 + 上方辉光
            if (hasLoad) DrawLoadBar(g, r, t, load, warm, gk);

            if ((seenMask & (1UL << c.Cpu)) != 0) DrawSeenMark(g, r);

            if (hasLoad)
            {
                // 角标小核号 便于识别与点选 主视觉让给百分比
                var idBox = new Rectangle(r.Left + Theme.S(3), r.Top + Theme.S(1),
                    r.Width - Theme.S(6), Theme.S(11));
                TextRenderer.DrawText(g, c.Cpu.ToString(), Theme.Mono(6.3f), idBox,
                    on >= 0.5f ? Col.Alpha(Theme.OnAccent, 205) : Col.Alpha(Theme.Faint, 205),
                    TextFormatFlags.Left | TextFormatFlags.Top
                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

                int pct = (int)Math.Round(value);
                if (pct < 0) pct = 0; else if (pct > 100) pct = 100;
                string s = pct.ToString() + "%";
                var numBox = new Rectangle(r.Left, r.Top + Theme.S(6),
                    r.Width, r.Height - Theme.S(12));
                Font nf = Theme.Mono(pct >= 100 ? 8.6f : 9.8f);
                Color numCol = on >= 0.5f ? Theme.OnAccent : Theme.Fg;
                TextRenderer.DrawText(g, s, nf, numBox, numCol,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            else
            {
                // 缺少该核心的观测不等于 0% 核号仍在角落 中间显示破折号
                var idBox = new Rectangle(r.Left + Theme.S(3), r.Top + Theme.S(1),
                    r.Width - Theme.S(6), Theme.S(11));
                TextRenderer.DrawText(g, c.Cpu.ToString(), Theme.Mono(6.3f), idBox,
                    on >= 0.5f ? Theme.OnAccent : Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "—", Theme.Mono(9.8f), r,
                    on >= 0.5f ? Theme.OnAccent : Theme.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
        }

        // 底部发光负载条 圆角轨道 + 负载填充 + 高负载更亮更饱和 + 同色外发光
        private void DrawLoadBar(Graphics g, Rectangle r, float t, Color load, bool warm, float gk)
        {
            int barH = Math.Max(Theme.S(5), 4);
            var track = new Rectangle(r.Left + Theme.S(4), r.Bottom - barH - Theme.S(3),
                r.Width - Theme.S(8), barH);
            if (track.Width < 3) return;
            int rad = Math.Max(1, barH / 2);
            int fw = (int)(track.Width * t);
            if (fw < barH && t > 0f) fw = barH;
            var fillRect = new Rectangle(track.X, track.Y, Math.Max(fw, 0), track.Height);
            // 繁忙条先在条身四周垫一圈辉光 让条子 亮起来
            if (warm && fw > 0)
            {
                var halo = new Rectangle(fillRect.X - Theme.S(2), fillRect.Y - Theme.S(3),
                    fillRect.Width + Theme.S(4), barH + Theme.S(6));
                GlowEllipse(g, halo, load, gk, 130, 3);
            }
            using (var tp = Theme.Rounded(track, rad))
            using (var tb = new SolidBrush(Col.Alpha(Theme.Stroke, 170)))
                g.FillPath(tb, tp);
            if (fw <= 0) return;
            Color barCol = warm ? Col.Lerp(load, Color.White, 0.14f) : load;
            using (var fp = Theme.Rounded(fillRect, rad))
            {
                using (var fb = new SolidBrush(barCol)) g.FillPath(fb, fp);
                // 亮顶边 让条子有霓虹感
                using (var hp = new Pen(Col.Lerp(load, Color.White, 0.45f), 1f))
                    g.DrawLine(hp, fillRect.X + rad, fillRect.Y + 0.5f,
                        fillRect.Right - rad, fillRect.Y + 0.5f);
            }
        }

        // 本局观测落核 右下角紫罗兰三角 与高负载红明确区分
        private void DrawSeenMark(Graphics g, Rectangle r)
        {
            int s = Theme.S(9);
            var pts = new[]
            {
                new Point(r.Right, r.Bottom),
                new Point(r.Right - s, r.Bottom),
                new Point(r.Right, r.Bottom - s),
            };
            var box = new Rectangle(r.Right - s, r.Bottom - s, s, s);
            GlowEllipse(g, box, SeenViolet, 1f, 110, 6);
            using (var b = new SolidBrush(Col.Alpha(SeenViolet, 248))) g.FillPolygon(b, pts);
        }

        // 同局游戏核范围与观测负载仅供选核参考 不宣称低平均负载能保证收益
        private enum CoreKind { Game, Eff, PerfIdle, Perf }

        private CoreKind KindOf(Group grp)
        {
            ulong game = ObservedGameMask;
            if (game != 0 && (grp.Mask & game) != 0) return CoreKind.Game;
            // E 能效核只在真混合架构上存在 且必须落在能效核簇 EffMask
            //   全大核机器(i7-9750H 等)ThrottleMask 只是后台预留 不是能效核 绝不标 E
            //   非混合架构 EffMask 恒为 0 这里自然命中不了 全按 P 和 P 空闲处理
            if (CpuTopology.Hybrid && (grp.Mask & CpuTopology.EffMask) != 0) return CoreKind.Eff;
            if (HasLoads)
            {
                float peak = 0f;
                foreach (Cell c in grp.Cells)
                {
                    double value;
                    if (!loads.TryGetValue(c.Cpu, out value)) return CoreKind.Perf;
                    peak = Math.Max(peak, (float)(value / 100.0));
                }
                return peak < 0.35f ? CoreKind.PerfIdle : CoreKind.Perf;
            }
            return CoreKind.Perf;
        }

        // 图例一项:负载渐变条 或 一枚圆点 + 文案 讲清面板里每个视觉元素的含义
        private sealed class LegendItem
        {
            public bool Bar;      // true=负载冷→热渐变条 false=分类圆点
            public Color Color;
            public string Text;
        }

        // 图例 同局平均负载 / 观测落核(紫) / 低载性能核(青) / 游戏核(红) / 能效核
        private List<LegendItem> BuildLegendItems()
        {
            var items = new List<LegendItem>();
            items.Add(new LegendItem { Bar = true, Text = Lang.T("core.legend.load") });
            items.Add(new LegendItem { Color = SeenViolet, Text = Lang.T("core.legend.seen") });
            items.Add(new LegendItem { Color = RogCool, Text = Lang.T("core.legend.pidle") });
            items.Add(new LegendItem { Color = Col.Alpha(Theme.Danger, 225), Text = Lang.T("core.legend.game") });
            // E 能效核图例只在真混合架构显示 全大核机器不出现
            if (CpuTopology.Hybrid && CpuTopology.EffMask != 0)
                items.Add(new LegendItem { Color = Theme.Faint, Text = Lang.T("core.legend.e") });
            return items;
        }

        private int LegendItemWidth(LegendItem it)
        {
            int lead = (it.Bar ? Theme.S(58) : Theme.S(8)) + Theme.S(4);
            int tw = TextRenderer.MeasureText(it.Text, Theme.MonoFor(it.Text, 7f)).Width;
            return lead + tw + Theme.S(14);
        }

        // 按可用宽度把图例折行 xs/rows 可空(只算行数时) 返回总行数
        private int LayoutLegend(int avail, List<LegendItem> items, int[] xs, int[] rows)
        {
            int rowCount = 1, x = 0;
            for (int i = 0; i < items.Count; i++)
            {
                int w = LegendItemWidth(items[i]);
                if (x > 0 && x + w > avail) { rowCount++; x = 0; }
                if (xs != null) xs[i] = x;
                if (rows != null) rows[i] = rowCount - 1;
                x += w;
            }
            return rowCount;
        }

        // 图例实际高度(折行后) LayoutFor 用它把弹窗撑够
        private int LegendHeight(int width)
        {
            var items = BuildLegendItems();
            if (items.Count == 0) return 0;
            int rows = LayoutLegend(width - Theme.S(2), items, null, null);
            return rows * Theme.S(LegendRowH);
        }

        private void DrawLegend(Graphics g)
        {
            if (legendY < 0) return;
            var items = BuildLegendItems();
            if (items.Count == 0) return;
            var xs = new int[items.Count];
            var rows = new int[items.Count];
            LayoutLegend(Width - Theme.S(2), items, xs, rows);
            int rowH = Theme.S(LegendRowH);
            for (int i = 0; i < items.Count; i++)
            {
                var box = new Rectangle(Theme.S(1) + xs[i], legendY + rows[i] * rowH,
                    LegendItemWidth(items[i]), rowH);
                DrawLegendItem(g, box, items[i]);
            }
        }

        private void DrawLegendItem(Graphics g, Rectangle row, LegendItem it)
        {
            int x = row.Left;
            int mid = row.Top + row.Height / 2;
            if (it.Bar)
            {
                // 负载渐变条 ROG 冷青→黄橙→红 说明格内大数字的冷→热配色
                int barW = Theme.S(58), barH = Theme.S(8);
                var bar = new Rectangle(x, mid - barH / 2, barW, barH);
                using (var lg = new LinearGradientBrush(bar, RogCool, Theme.Danger, 0f))
                {
                    var blend = new ColorBlend(3);
                    blend.Colors = new[] { RogCool, RogAmber, Theme.Danger };
                    blend.Positions = new[] { 0f, 0.5f, 1f };
                    lg.InterpolationColors = blend;
                    g.FillRectangle(lg, bar);
                }
                x += barW + Theme.S(4);
            }
            else
            {
                int d = Theme.S(8);
                using (var b = new SolidBrush(it.Color))
                    g.FillEllipse(b, new Rectangle(x, mid - d / 2, d, d));
                x += d + Theme.S(4);
            }
            TextRenderer.DrawText(g, it.Text, Theme.MonoFor(it.Text, 7f),
                new Rectangle(x, row.Top, row.Right - x, row.Height),
                it.Bar ? Theme.Faint : Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

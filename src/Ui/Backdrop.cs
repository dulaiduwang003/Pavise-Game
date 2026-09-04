// @author bdth 2074055628@qq.com
// 文件用途 主窗口背景封面 一张图按窗口尺寸裁切缓存 各控件按自身绝对坐标取同一块
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace PaviseApp
{
    // WinForms 的子控件没有真透明 所以不存在"底下铺一张图上面全透"这种做法
    // 这里的办法是 只留一张按窗口尺寸裁好的位图 谁要透谁就按自己在窗口里的绝对坐标
    // 从同一张位图上取自己那一块画下来 各控件画的是同一张图的不同部分 拼起来就是整幅
    internal static class Backdrop
    {
        private const string DimKey = "BackdropDim";
        private const string StoreName = "backdrop.img";

        // 遮罩三档 最淡一档也不给全透 否则随便一张浅色图就能让正文彻底读不出来
        private static readonly int[] DimDark = { 120, 170, 214 };
        // 亮色不能用白膜直接盖黑图 否则黑色只会变成一层灰雾
        //   三档改成高亮映射的底色占比 黑色落到主题浅底 彩色仍保留色相
        private static readonly float[] LightFloorMix = { 0.90f, 0.94f, 0.97f };

        // 卡片自己的底色也要留一点 纯靠遮罩压不住高频细节 文字会糊在纹理上
        private const int CardAlphaDark = 208;
        // 透明度不能只看本层 alpha 封面前面已经有一层 DimLight
        //   高亮映射已经把底图抬亮 因此本层只需薄薄一层玻璃白 保留接近暗色的图案对比度
        private const int CardAlphaLight = 100;
        private const int NavAlphaLight = 80;
        private const int LightSoftenDivisor = 2;

        private static Bitmap source;
        private static Bitmap fitted;
        private static Size fittedFor;
        private static int fittedDim = -1;
        private static bool fittedLight;
        private static int dim = 1;

        public static bool Active { get { return source != null; } }

        // 封面只属于主窗口内容 独立弹窗和明确禁用封面的浮层子树使用主题底色
        public static bool AppliesTo(Control control)
        {
            if (!Active) return false;
            for (Control current = control; current != null; current = current.Parent)
            {
                var panel = current as RoundPanel;
                if (panel != null && !panel.UseBackdrop) return false;
                if (current is Form) return current is PanelForm;
            }
            return false;
        }

        public static string StorePath { get { return Path.Combine(Paths.Data, StoreName); } }

        public static int Dim
        {
            get { return dim; }
            set
            {
                int v = value < 0 ? 0 : value > 2 ? 2 : value;
                if (v == dim) return;
                dim = v;
                Settings.SaveStr(DimKey, v.ToString(System.Globalization.CultureInfo.InvariantCulture));
                DropFitted();
            }
        }

        // 导航条上全是入口 一直得读得清 所以压得比卡片更狠
        public static Color NavFill(Control control, Color baseFill)
        {
            if (!AppliesTo(control)) return baseFill;
            return Col.Alpha(baseFill, Theme.LightMode ? NavAlphaLight : 198);
        }

        public static Color CardFill(Control control, Color baseFill)
        {
            if (!AppliesTo(control)) return baseFill;
            return Col.Alpha(baseFill, Theme.LightMode ? CardAlphaLight : CardAlphaDark);
        }

        public static void Init()
        {
            int v;
            if (int.TryParse(Settings.LoadStr(DimKey, "1"), out v)) dim = v < 0 ? 0 : v > 2 ? 2 : v;
            LoadFromStore();
        }

        // 图存进数据目录 不记用户挑图时的原路径 否则他挪一次文件封面就没了
        public static bool Choose(IWin32Window owner)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = Lang.T("backdrop.pick");
                dlg.Filter = Lang.T("backdrop.filter") + "|*.jpg;*.jpeg;*.png;*.bmp";
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
                try
                {
                    using (Bitmap probe = Read(dlg.FileName))
                    {
                        if (probe == null) return false;
                        Directory.CreateDirectory(Paths.Data);
                        File.Copy(dlg.FileName, StorePath, true);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogFailure(Lang.T("log.backdrop.1"), ex);
                    return false;
                }
            }
            LoadFromStore();
            return Active;
        }

        public static void Clear()
        {
            Drop();
            try { if (File.Exists(StorePath)) File.Delete(StorePath); }
            catch (Exception ex) { Logger.LogFailure(Lang.T("log.backdrop.2"), ex); }
        }

        // 控件自己的背景 g 的坐标系就是 c 的客户区
        public static void Paint(Graphics g, Control c, Rectangle client)
        {
            if (!AppliesTo(c) || client.Width <= 0 || client.Height <= 0) return;
            Bitmap bmp = Fit(c);
            if (bmp == null) return;

            int ox = 0, oy = 0;
            for (Control p = c; p != null && !(p is Form); p = p.Parent) { ox += p.Left; oy += p.Top; }

            var src = new Rectangle(client.X + ox, client.Y + oy, client.Width, client.Height);
            if (src.X >= bmp.Width || src.Y >= bmp.Height) return;
            if (src.X < 0) { client.X -= src.X; client.Width += src.X; src.Width += src.X; src.X = 0; }
            if (src.Y < 0) { client.Y -= src.Y; client.Height += src.Y; src.Height += src.Y; src.Y = 0; }
            if (src.Right > bmp.Width) { client.Width -= src.Right - bmp.Width; src.Width = bmp.Width - src.X; }
            if (src.Bottom > bmp.Height) { client.Height -= src.Bottom - bmp.Height; src.Height = bmp.Height - src.Y; }
            if (src.Width <= 0 || src.Height <= 0) return;

            InterpolationMode oldI = g.InterpolationMode;
            PixelOffsetMode oldP = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(bmp, client, src, GraphicsUnit.Pixel);
            g.InterpolationMode = oldI;
            g.PixelOffsetMode = oldP;
        }

        // 设置页的小窗直接展示整张封面的裁切结果 不借主窗口坐标取片
        public static void PaintPreview(Graphics g, Rectangle target)
        {
            if (!Active || g == null || target.Width <= 0 || target.Height <= 0) return;
            GraphicsState state = g.Save();
            try
            {
                // 交叉而不是替换 调用方多半已经按切角设过裁剪 换掉就画出边了
                g.SetClip(target, CombineMode.Intersect);
                InterpolationMode oldI = g.InterpolationMode;
                PixelOffsetMode oldP = g.PixelOffsetMode;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                DrawCover(g, target, Theme.LightMode);
                if (!Theme.LightMode)
                    using (var veil = new SolidBrush(Col.Alpha(Theme.Bg, DimDark[dim])))
                        g.FillRectangle(veil, target);
                g.InterpolationMode = oldI;
                g.PixelOffsetMode = oldP;
            }
            finally { g.Restore(state); }
        }

        private static void LoadFromStore()
        {
            Drop();
            try { if (File.Exists(StorePath)) source = Read(StorePath); }
            catch (Exception ex) { Logger.LogFailure(Lang.T("log.backdrop.3"), ex); }
        }

        // 走内存读 不能让 Image.FromFile 把数据目录里那张图一直锁住
        // 原图常驻内存 窗口最大也就两千多像素宽 超出的分辨率只占内存不出画质 读入时先缩
        private const int SourceMaxEdge = 2560;

        private static Bitmap Read(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            using (var ms = new MemoryStream(raw))
            using (var img = Image.FromStream(ms, false, true))
            {
                int edge = Math.Max(img.Width, img.Height);
                if (edge <= SourceMaxEdge) return new Bitmap(img);
                double scale = (double)SourceMaxEdge / edge;
                int tw = Math.Max(1, (int)Math.Round(img.Width * scale));
                int th = Math.Max(1, (int)Math.Round(img.Height * scale));
                var small = new Bitmap(tw, th, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(img, new Rectangle(0, 0, tw, th));
                }
                return small;
            }
        }

        // 缩放只在窗口尺寸 遮罩档位 主题这三样变了才做一次 之后每个控件都是等比例直取
        private static Bitmap Fit(Control c)
        {
            Form f = c.FindForm();
            Size want = f != null ? f.ClientSize : c.ClientSize;
            if (want.Width <= 0 || want.Height <= 0) return null;

            if (fitted != null && fittedFor == want && fittedDim == dim && fittedLight == Theme.LightMode)
                return fitted;

            DropFitted();
            Bitmap bmp = null;
            try
            {
                bmp = new Bitmap(want.Width, want.Height);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    // 铺满不变形 长边溢出裁掉 居中 亮色在这里直接做高亮映射
                    DrawCover(g, new Rectangle(0, 0, want.Width, want.Height), Theme.LightMode);
                }

                // 亮色主题把高频纹理先缩小再放大 相当于低成本磨砂柔化
                //   只在缓存重建时做一次 日常重绘仍然只是从 fitted 等比例取片
                if (Theme.LightMode)
                {
                    int sw = Math.Max(1, want.Width / LightSoftenDivisor);
                    int sh = Math.Max(1, want.Height / LightSoftenDivisor);
                    using (var soft = new Bitmap(sw, sh))
                    {
                        using (Graphics sg = Graphics.FromImage(soft))
                        {
                            sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            sg.DrawImage(bmp, new Rectangle(0, 0, sw, sh),
                                new Rectangle(0, 0, bmp.Width, bmp.Height), GraphicsUnit.Pixel);
                        }
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(soft, new Rectangle(0, 0, want.Width, want.Height),
                                new Rectangle(0, 0, sw, sh), GraphicsUnit.Pixel);
                        }
                    }
                }

                if (!Theme.LightMode)
                    using (Graphics g = Graphics.FromImage(bmp))
                {
                    using (var veil = new SolidBrush(Col.Alpha(Theme.Bg, DimDark[dim])))
                        g.FillRectangle(veil, 0, 0, want.Width, want.Height);
                }
                fitted = bmp;
                bmp = null;
                fittedFor = want;
                fittedDim = dim;
                fittedLight = Theme.LightMode;
            }
            catch (Exception ex)
            {
                Logger.LogFailure(Lang.T("log.backdrop.4"), ex);
                if (bmp != null) bmp.Dispose();
                DropFitted();
            }
            return fitted;
        }

        private static void DrawCover(Graphics g, Rectangle target, bool lightTone)
        {
            float k = Math.Max((float)target.Width / source.Width, (float)target.Height / source.Height);
            int dw = (int)Math.Ceiling(source.Width * k);
            int dh = (int)Math.Ceiling(source.Height * k);
            var dest = new Rectangle(target.X + (target.Width - dw) / 2,
                target.Y + (target.Height - dh) / 2, dw, dh);
            if (!lightTone)
            {
                g.DrawImage(source, dest);
                return;
            }

            Color floor = Col.Lerp(Color.Black, Theme.Bg, LightFloorMix[dim]);
            float fr = floor.R / 255f, fg = floor.G / 255f, fb = floor.B / 255f;
            using (var attrs = new ImageAttributes())
            {
                attrs.SetColorMatrix(new ColorMatrix(new[]
                {
                    new[] { 1f - fr, 0f, 0f, 0f, 0f },
                    new[] { 0f, 1f - fg, 0f, 0f, 0f },
                    new[] { 0f, 0f, 1f - fb, 0f, 0f },
                    new[] { 0f, 0f, 0f, 1f, 0f },
                    new[] { fr, fg, fb, 0f, 1f },
                }), ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                g.DrawImage(source, dest, 0, 0, source.Width, source.Height,
                    GraphicsUnit.Pixel, attrs);
            }
        }

        private static void DropFitted()
        {
            if (fitted != null) { fitted.Dispose(); fitted = null; }
            fittedFor = Size.Empty;
            fittedDim = -1;
        }

        private static void Drop()
        {
            DropFitted();
            if (source != null) { source.Dispose(); source = null; }
        }

        // 控件自己那块底 先补上身下的封面
        // 落在卡片上的还要再叠一层与卡面同浓度的底 否则那一小块比卡片透一截 像挖了个洞
        // 直接摆在页面上的不能叠 叠了等于把封面又盖回去
        public static void PaintOnCard(Graphics g, Control c, Rectangle rect)
        {
            if (!AppliesTo(c)) return;
            Paint(g, c, rect);
            var card = c.Parent as RoundPanel;
            if (card == null) return;
            using (var b = new SolidBrush(CardFill(c, card.Fill))) g.FillRectangle(b, rect);
        }
    }
}

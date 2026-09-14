// @author bdth 2074055628@qq.com
// File purpose Main window backdrop, one image cropped and cached to window size, each control takes the same block by its absolute coordinates
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace PaviseApp
{
    // WinForms child controls have no real transparency, so there is no such thing as one image underneath and everything transparent above
    // The approach here is one bitmap cropped to window size, whoever needs transparency uses its absolute coordinates in the window
    // to take and draw its own block from that bitmap, controls draw different parts of the same image, together they form the whole
    internal static class Backdrop
    {
        private const string DimKey = "BackdropDim";
        private const string StoreName = "backdrop.img";

        // Three dim tiers, even the lightest is not fully transparent, otherwise any pale image makes body text unreadable
        private static readonly int[] DimDark = { 120, 170, 214 };
        // Light mode cannot lay a white film straight over a dark image, black would just become a grey haze
        //   The three tiers become the base-color share of the highlight mapping, black lands on the theme's light base, colors keep their hue
        private static readonly float[] LightFloorMix = { 0.90f, 0.94f, 0.97f };

        // Cards need some base color of their own too, the dim alone cannot tame high-frequency detail and text smears on the texture
        private const int CardAlphaDark = 208;
        // Opacity is not just this layer's alpha, a DimLight layer already sits in front of the backdrop
        //   The highlight mapping has already lifted the image, so this layer only needs a thin glass white, keeping contrast for near-dark patterns
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

        // The backdrop belongs only to main window content, standalone popups and overlay subtrees that explicitly disable it use the theme base
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

        // The nav bar is all entry points and must always be readable, so it is dimmed harder than cards
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

        // The image is stored in the data dir, the original path the user picked is not recorded, otherwise one file move loses the backdrop
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

        // The control's own background, g's coordinate system is c's client area
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

        // The small preview on the Settings page shows the whole cropped backdrop directly, not a slice by main window coordinates
        public static void PaintPreview(Graphics g, Rectangle target)
        {
            if (!Active || g == null || target.Width <= 0 || target.Height <= 0) return;
            GraphicsState state = g.Save();
            try
            {
                // Intersect rather than replace, the caller has most likely already clipped to the chamfer, replacing would draw outside the edge
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

        // Read via memory, Image.FromFile must not keep the image in the data dir locked
        // The source stays resident, the window is at most a bit over 2000 px wide, extra resolution costs memory without adding quality, downscale on load
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

        // Scaling is done once only when window size, dim tier or theme changes, afterwards every control takes a 1:1 slice
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
                    // Fill without distortion, overflow on the long edge is cropped, centered, light mode does the highlight mapping right here
                    DrawCover(g, new Rectangle(0, 0, want.Width, want.Height), Theme.LightMode);
                }

                // Light theme shrinks high-frequency texture then scales it back up, a cheap frosted softening
                //   Done once only on cache rebuild, everyday repaints still just take a 1:1 slice from fitted
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

        // The control's own base, first fill in the backdrop beneath it
        // Those sitting on a card also get a layer at the same density as the card face, otherwise that patch is more transparent than the card, like a hole
        // Those placed directly on the page must not get it, that would cover the backdrop back up
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

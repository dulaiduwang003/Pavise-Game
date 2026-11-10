using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace PaviseApp
{
    internal static class AntiCheatBrandLogos
    {
        // ACE 和 EAC 的图形路径来自官网 SVG。
        // BattlEye 标志由官网的 logo-large.png 转为矢量。
        // 标志统一使用单色，启用状态由外围界面呈现。
        // 来源：https://intl.anticheatexpert.com/ ; https://www.easy.ac/ ; https://www.battleye.com/
        internal const string AceSvg = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 1500.03 590""><path fill=""currentColor"" fill-rule=""evenodd"" d=""M1436,384.66l61.61,103.84,2.45,1-.91,1.57.26.44h-.51l-55.77,96.29-1.61-.67v2h-420V589l-.46.17-33.57-52.58-29.37,50.73v1.85H525.73l-.74.53L356.48,308.94l-44.29,74.23,65,105.3h1.08L319.61,589.15l-249,.05v0H61l-1.4.53-.32-.53H58.15l.58-1L1.8,493.57,0,492,285.36.45,285.09,0H716.71L748.9,53.77,779.31,1l.82.35L779.34,0h420.78l67.64,109.37h-2.06l.23.09-48.88,84.83H1321l63.3,105h-1.38l-49.17,85.34Zm-1.64,189.51,47.86-82.64H1085l-48.86,82.64Zm-485.28,0,47.86-82.64H590.08l-49.69,82.64ZM646.18,384.66H895.27L754.82,151.52l-34,56.62-1.29-.53H541ZM360.29,488.47l-56.41-91.36-54.5,91.35ZM19,491.18l48.87,81.24L286.16,197.84l4-7,0,0,6.55-11.22.55.32.42-.73L529.15,564.91,576.58,486,294.78,16.17ZM482.67,109.37l49.49,83.33h181.1l50.09-83.33ZM757.39,68l24.79,41.41-2.3.47L764.15,136,913.84,384.66h37.07l61.66,104,2.18.91-.81,1.4.36.61h-.71l-16.92,29.21,27.12,42.8L1070.22,485,788.12,14.63Zm491.93,41.41H976.92l50,84.92h173.44Zm116.92,190H1089.75l50.69,85.34h176.63Z""/></svg>";

        internal const string EasyAntiCheatSvg = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 300 263""><path fill=""currentColor"" d=""M288.3 115.67L278.79 98.83L289.92 79.99C292.66 75.35 292.67 69.46 289.95 64.61L272.74 33.92C269.66 28.43 264.33 25.26 258.1 25.21L236.18 25.04L231.92 17.69C225.42 6.45 213.62 0 199.57 0H92.5601C78.4901 0 66.5801 6.63 59.8701 18.19L55.8901 25.05L34.0501 25.3C28.6601 25.36 23.5001 28.38 20.9001 32.99L3.04008 64.6C0.360078 69.35 0.370078 75.39 3.07008 79.97L13.7401 98.05L4.59008 113.99C-1.67992 124.91 -1.50992 138.17 5.02008 149.47L60.3101 245.06C66.5001 255.77 78.2201 262.42 90.8901 262.42L201.71 262.38C214.35 262.38 226.1 255.63 232.37 244.79L287.65 149.22C293.95 138.33 294.19 126.1 288.3 115.67ZM280.62 145.16L225.34 240.73C220.59 248.95 211.74 254.26 201.7 254.27L90.8901 254.31H90.8801C81.2101 254.31 72.1401 249.31 67.3301 241L12.0501 145.41C7.05008 136.77 6.75008 126.53 11.6301 118.04L22.3901 99.3C22.8501 98.49 22.8501 97.5 22.3701 96.7L10.0701 75.85C8.79008 73.69 8.87008 70.81 10.1201 68.6L27.9801 36.98C29.1901 34.84 31.7301 33.45 34.1501 33.42L59.1401 33.13C60.0501 33.12 60.8901 32.63 61.3501 31.84L66.9101 22.26C72.2601 13.04 81.6301 8.12 92.5801 8.12H199.59C210.31 8.12 219.71 12.76 224.93 21.77L230.77 31.85C231.23 32.64 232.07 33.13 232.99 33.14L258.07 33.33C261.48 33.36 264.08 35.01 265.69 37.89L282.9 68.58C284.16 70.83 284.29 73.62 282.96 75.86L270.2 97.46C269.73 98.26 269.72 99.24 270.17 100.05L281.25 119.67C285.81 127.75 285.35 137.03 280.64 145.16H280.62Z""/><path fill=""currentColor"" d=""M107.79 124.4C107.23 124.07 106.57 123.78 105.86 123.54C104.92 123.23 103.89 123.02 102.91 122.94C102.66 122.92 102.42 122.91 102.18 122.91C100.98 122.9 99.8999 123.1 99.1599 123.57C97.0099 124.96 95.6799 131.13 97.0599 133.38C97.8299 134.63 100.25 135.69 102.59 136.07C102.85 136.11 103.11 136.15 103.37 136.17C103.63 136.19 103.88 136.21 104.13 136.22C105.12 136.24 106.02 136.12 106.68 135.79C106.84 135.71 106.99 135.61 107.13 135.51C107.59 135.14 108 134.63 108.35 134.03C108.59 133.63 108.8 133.19 108.98 132.73C109.35 131.8 109.61 130.78 109.74 129.79C109.84 129.05 109.86 128.32 109.81 127.66C109.73 126.78 109.52 126.03 109.13 125.53C108.82 125.12 108.36 124.74 107.8 124.41L107.79 124.4Z""/><path fill=""currentColor"" d=""M192.7 123.91C190.48 122.48 184.87 123.69 183.3 125.75C181.85 127.65 182.88 133.32 185.18 135.18C187.18 136.79 193.38 135.28 194.68 133.17C195.99 131.05 194.73 125.22 192.7 123.91Z""/><path fill=""currentColor"" d=""M144.8 131.21C140.97 131.21 137.98 132.23 135.49 133.89C133.61 134.98 132.01 136.37 130.99 138.02C130.99 138.02 130.99 138.05 130.96 138.09C127.71 142.08 125.29 147.22 122.22 151.86C121.03 153.66 119.9 155.45 118.84 157.24C118.16 158.39 117.27 159.42 116.18 160.18C113.42 162.11 110.34 163.21 106.98 162.79L85.57 161.06C78.83 160.51 73.74 156.45 70.59 150.58L61.34 133.36C58.37 127.82 57.44 121.19 60.75 115.6L70.29 99.52C73.6 93.93 79.61 90.56 86.03 91.11L105.63 92.79C106.14 92.83 106.64 92.9 107.12 92.99C109.01 93.33 110.58 91.56 109.93 89.72L109.65 88.91C107.32 82.24 107.77 75.45 111.69 69.66L141.61 25.62C142.73 23.97 141.57 21.7 139.6 21.71L93.16 21.77C85.84 21.77 79.78 25.65 76.26 32.14L24.2 124.39C21.4 129.35 21.89 135.64 24.62 140.46L76.81 232.68C80.26 238.78 86.33 242.2 93.18 242.21L140.24 242.23C142.01 242.23 143.09 240.3 142.18 238.79L129.05 217.13C128.64 216.46 129.3 215.63 130.04 215.89C136.92 218.34 144.97 219 153.35 217.61C168.74 215.06 180.6 202.3 180.55 186.7C180.52 175.29 175.25 163.78 167.37 151.87C161.22 142.57 157.65 131.22 144.8 131.22V131.21ZM159.72 164.71C157.62 171.04 151.4 177.76 144.82 177.77C138.12 177.77 132.16 171.01 129.91 164.93C129.17 162.94 131.45 160.91 132.99 160.11C146.15 153.25 161.22 160.22 159.72 164.71Z""/></svg>";

        internal const string BattlEyeSvg = @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""20 28 162 144""><path fill=""currentColor"" fill-rule=""evenodd"" d=""M 47 31 C 50.7 33.09 53.75 35.44 56.94 38.25 C 71.24 50.33 88.92 54.95 107.42 53.79 C 114.72 52.85 121.29 50.67 128.1 48.02 C 131 47 131 47 134 47 C 134 46.34 134 45.68 134 45 C 135.94 43.48 135.94 43.48 138.56 41.75 C 142.98 38.75 147.05 35.57 151 32 C 159.9 34.43 164.8 43.11 169.14 50.61 C 179.76 69.65 182.36 91.88 176.39 112.89 C 172.98 123.56 167.55 133.71 160 142 C 159.28 142.81 158.57 143.61 157.83 144.45 C 143.6 159.92 124.2 167.91 103.4 169.29 C 81.99 169.84 60.95 161.97 45.25 147.34 C 29.12 130.59 21.28 110.35 21.53 87.15 C 22.14 68.81 28.84 51.83 41 38 C 41.58 37.31 42.17 36.62 42.77 35.91 C 44.17 34.26 45.57 32.62 47 31 Z M 48 47 C 36.4 60.48 31.74 77.66 32.83 95.14 C 34.56 114.2 43.49 130.57 58 143 C 75.12 155.79 93.62 158.98 114.47 156.61 C 132.45 152.78 147.09 141.64 157.32 126.68 C 163.43 116.92 167.25 106.56 168 95 C 168.05 94.29 168.11 93.58 168.16 92.85 C 168.82 75.52 162.81 60.86 153 47 C 149.34 48.73 145.94 50.81 142.5 52.94 C 141.93 53.28 141.36 53.63 140.78 53.99 C 137.74 55.86 134.82 57.8 132 60 C 132.23 60.29 132.23 60.29 133.39 61.77 C 141.77 72.96 144.59 84.23 143 98 C 140.93 108.95 135.1 118.3 126.12 124.94 C 115.76 131.59 104.49 134.7 92.23 132.5 C 80.46 129.59 70.24 123.53 63.69 113.12 C 62.76 111.43 61.86 109.73 61 108 C 60.66 107.34 60.32 106.67 59.96 105.99 C 56.15 96.93 56.52 84.14 59.62 74.94 C 61.51 70.53 64.03 66.69 66.93 62.88 C 67.28 62.26 67.63 61.64 68 61 C 67 58 67 58 64.71 56.57 C 63.75 56.09 62.8 55.62 61.81 55.12 C 58.4 53.35 55.33 51.66 52.44 49.12 C 50 47 50 47 48 47 Z M 116.92 74.51 C 115.29 77.15 115.44 79.01 116 82 C 117.83 85.05 119.09 86.62 122.38 88 C 125.97 88 128 86.93 131 85 C 132.99 82.49 133 81.22 132.94 78 C 131.74 74.17 130.46 73 127 71 C 122.36 70.13 119.93 71.06 116.92 74.51 Z""/></svg>";

        private static readonly Regex Tokens = new Regex(@"[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.CultureInvariant);
        private static readonly Logo Ace = new Logo(AceSvg);
        private static readonly Logo EasyAntiCheat = new Logo(EasyAntiCheatSvg);
        private static readonly Logo BattlEye = new Logo(BattlEyeSvg);

        internal static bool TryDraw(Graphics graphics, Rectangle bounds, string key, Color ink)
        {
            Logo logo;
            switch (key)
            {
                case "ace": logo = Ace; break;
                case "eac": logo = EasyAntiCheat; break;
                case "battleye": logo = BattlEye; break;
                default: return false;
            }
            GraphicsState saved = graphics.Save();
            try
            {
                graphics.SetClip(bounds, CombineMode.Intersect);
                float scale = Math.Min(bounds.Width / logo.ViewBox.Width, bounds.Height / logo.ViewBox.Height);
                graphics.TranslateTransform(bounds.Left + (bounds.Width - logo.ViewBox.Width * scale) / 2f,
                    bounds.Top + (bounds.Height - logo.ViewBox.Height * scale) / 2f);
                graphics.ScaleTransform(scale, scale);
                graphics.TranslateTransform(-logo.ViewBox.X, -logo.ViewBox.Y);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (var brush = new SolidBrush(ink))
                    foreach (GraphicsPath path in logo.Paths) graphics.FillPath(brush, path);
            }
            finally { graphics.Restore(saved); }
            return true;
        }

        private sealed class Logo
        {
            internal readonly RectangleF ViewBox;
            internal readonly GraphicsPath[] Paths;

            internal Logo(string svg)
            {
                var document = new XmlDocument { XmlResolver = null };
                document.LoadXml(svg);
                MatchCollection viewBox = Tokens.Matches(document.DocumentElement.GetAttribute("viewBox"));
                ViewBox = new RectangleF(Number(viewBox[0].Value), Number(viewBox[1].Value),
                    Number(viewBox[2].Value), Number(viewBox[3].Value));
                var paths = new List<GraphicsPath>();
                foreach (XmlElement element in document.GetElementsByTagName("path"))
                    paths.Add(ReadPath(element.GetAttribute("d"),
                        element.GetAttribute("fill-rule") == "evenodd" ? FillMode.Alternate : FillMode.Winding));
                Paths = paths.ToArray();
            }
        }

        // 这些内嵌 SVG 只需解析一次，重绘时直接使用保留的矢量路径。
        // 保留矢量路径，避免每次重绘都重新解析或转成位图。
        private static GraphicsPath ReadPath(string data, FillMode fill)
        {
            var path = new GraphicsPath(fill);
            MatchCollection tokens = Tokens.Matches(data);
            int index = 0;
            char command = '\0';
            PointF current = PointF.Empty, start = PointF.Empty;
            try
            {
                while (index < tokens.Count)
                {
                    if (char.IsLetter(tokens[index].Value[0])) command = tokens[index++].Value[0];
                    bool relative = char.IsLower(command);
                    PointF next;
                    switch (char.ToUpperInvariant(command))
                    {
                        case 'M':
                            current = Point(tokens, ref index, current, relative);
                            start = current;
                            path.StartFigure();
                            command = relative ? 'l' : 'L';
                            break;
                        case 'L':
                            next = Point(tokens, ref index, current, relative);
                            path.AddLine(current, next);
                            current = next;
                            break;
                        case 'H':
                            next = new PointF(ReadNumber(tokens, ref index) + (relative ? current.X : 0f), current.Y);
                            path.AddLine(current, next);
                            current = next;
                            break;
                        case 'V':
                            next = new PointF(current.X, ReadNumber(tokens, ref index) + (relative ? current.Y : 0f));
                            path.AddLine(current, next);
                            current = next;
                            break;
                        case 'C':
                            PointF first = Point(tokens, ref index, current, relative);
                            PointF second = Point(tokens, ref index, current, relative);
                            next = Point(tokens, ref index, current, relative);
                            path.AddBezier(current, first, second, next);
                            current = next;
                            break;
                        case 'Z':
                            path.CloseFigure();
                            current = start;
                            command = '\0';
                            break;
                        default: throw new FormatException("Unsupported anti-cheat SVG path command: " + command);
                    }
                }
                return path;
            }
            catch { path.Dispose(); throw; }
        }

        private static PointF Point(MatchCollection tokens, ref int index, PointF origin, bool relative)
        {
            float x = ReadNumber(tokens, ref index), y = ReadNumber(tokens, ref index);
            return new PointF(x + (relative ? origin.X : 0f), y + (relative ? origin.Y : 0f));
        }

        private static float ReadNumber(MatchCollection tokens, ref int index)
        {
            return Number(tokens[index++].Value);
        }

        private static float Number(string value)
        {
            return float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}

using System.Drawing;
using System.Globalization;

namespace PaviseApp
{
    internal static class Col
    {
        public static Color Lerp(Color a, Color b, float t)
        {
            if (t < 0f)
            {
                t = 0f;
            }
            if (t > 1f)
            {
                t = 1f;
            }
            return Color.FromArgb((int)((float)(int)a.A + (float)(b.A - a.A) * t), (int)((float)(int)a.R + (float)(b.R - a.R) * t), (int)((float)(int)a.G + (float)(b.G - a.G) * t), (int)((float)(int)a.B + (float)(b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int a)
        {
            return Color.FromArgb((a >= 0) ? ((a > 255) ? 255 : a) : 0, c.R, c.G, c.B);
        }

        public static string ToHex(Color c)
        {
            return c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        public static bool TryHex(string s, out Color c)
        {
            c = Color.Empty;
            if (string.IsNullOrEmpty(s) || s.Length != 6)
            {
                return false;
            }
            int result;
            if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result))
            {
                return false;
            }
            c = Color.FromArgb((result >> 16) & 0xFF, (result >> 8) & 0xFF, result & 0xFF);
            return true;
        }
    }
}

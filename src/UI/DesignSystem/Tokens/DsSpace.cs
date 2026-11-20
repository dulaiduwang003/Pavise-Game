using System;

namespace PaviseApp
{
    internal static class DsSpace
    {
        public const int Hair = 1;

        public const int XS = 2;

        public const int S = 4;

        public const int M = 8;

        public const int L = 12;

        public const int XL = 16;

        public const int XXL = 24;

        public static int Px(int logical)
        {
            return Theme.S(logical);
        }

        public static float Line(int logical)
        {
            return Math.Max(1f, Theme.S(logical));
        }
    }
}

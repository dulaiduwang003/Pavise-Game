// 作者：bdth 2074055628@qq.com
using System;

namespace PaviseApp
{
    // ROG 表面配置：导航面与控制面的网格、装甲带、电路总线与切角参数。
    internal struct DsSurfaceProfile
    {
        public bool Navigation;
        public int MeshAlpha, FaceAlpha, EdgeAlpha;
        public int MeshStep;
        public int BandThickness;
        public int SweepDivisor;
        public int BusInset, BusTop, BusBottom, TickStep;
        public int[] BusLevels, BusReach;
        public int CornerCut, CornerReach;
        public string Code;
        public int CodeRight, CodeWidth, CodeMinHeight;
    }

    internal static class DsSurfaces
    {
        public static DsSurfaceProfile Profile(bool navigation)
        {
            bool light = Theme.LightMode;
            var p = new DsSurfaceProfile();
            p.Navigation = navigation;
            p.MeshAlpha = navigation ? (light ? 13 : 8) : (light ? 8 : 5);
            p.FaceAlpha = navigation ? (light ? 14 : 9) : (light ? 8 : 5);
            p.EdgeAlpha = navigation ? (light ? 44 : 30) : (light ? 25 : 17);
            p.MeshStep = navigation ? 17 : 22;
            p.BandThickness = navigation ? 15 : 20;
            p.SweepDivisor = navigation ? 2 : 5;
            p.BusInset = navigation ? 17 : 24;
            p.BusTop = navigation ? 96 : 24;
            p.BusBottom = 34;
            p.TickStep = navigation ? 25 : 31;
            p.BusLevels = navigation ? new[] { 15, 35, 57, 79 } : new[] { 18, 48, 78 };
            p.BusReach = navigation ? new[] { 56, 82, 56, 82 } : new[] { 92, 146, 92 };
            p.CornerCut = navigation ? 28 : 42;
            p.CornerReach = navigation ? 66 : 104;
            p.Code = navigation ? "PVS // NAV SURFACE" : "PAVISE // CONTROL SURFACE";
            p.CodeRight = navigation ? 142 : 220;
            p.CodeWidth = navigation ? 116 : 192;
            p.CodeMinHeight = navigation ? 0 : 120;
            return p;
        }

        // 表面铭文的透明度，浅色主题略高以保持可见。
        public static int CodeAlpha { get { return Theme.LightMode ? 66 : 42; } }
    }
}

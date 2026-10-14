#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunNvSmoothMotionTests()
        {
            Eq(58108u, NvDrsTweaks.SmoothMotionMinDriverForName("NVIDIA GeForce RTX 4070 SUPER"));
            Eq(58108u, NvDrsTweaks.SmoothMotionMinDriverForName("NVIDIA GeForce RTX 4090 Laptop GPU"));
            Eq(57186u, NvDrsTweaks.SmoothMotionMinDriverForName("NVIDIA GeForce RTX 5090"));
            Eq(57186u, NvDrsTweaks.SmoothMotionMinDriverForName("nvidia geforce rtx5070 ti"));
            foreach (string name in new[] { null, "", "RTX 3080", "GTX 1650", "RTX A4000", "AMD Radeon 780M", "Intel Arc Graphics" })
                Eq(0u, NvDrsTweaks.SmoothMotionMinDriverForName(name));
            int queried = 0;
            Func<bool> supported = delegate { queried++; return true; };
            Eq(false, NvDrsTweaks.IsSmoothMotionSupported(0, true, 60000, supported));
            Eq(false, NvDrsTweaks.IsSmoothMotionSupported(58108, false, 60000, supported));
            Eq(false, NvDrsTweaks.IsSmoothMotionSupported(58108, true, 58107, supported));
            Eq(0, queried);
            Eq(true, NvDrsTweaks.IsSmoothMotionSupported(58108, true, 58108, supported));
            Eq(true, NvDrsTweaks.IsSmoothMotionSupported(57186, true, 57186, supported));
            Eq(2, queried);
            Eq(false, NvDrsTweaks.IsSmoothMotionSupported(58108, true, 60000, null));
            Eq(false, NvDrsTweaks.IsSmoothMotionSupported(58108, true, 60000, delegate { return false; }));
            Eq(false, NvDrsTweaks.IsSmoothMotionSupported(58108, true, 60000, delegate { throw new InvalidOperationException("driver unavailable"); }));
        }
    }
}
#endif

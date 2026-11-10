using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class DisplaySolo
    {
        internal const string SnapKey = "DisplaySoloSnap";

        internal const uint TopologyInternal = 1u;

        internal const uint TopologyClone = 2u;

        internal const uint TopologyExtend = 4u;

        internal const uint TopologyExternal = 8u;

        private static readonly object lk = new object();

        private static bool SettledAsSingleDisplay()
        {
            if (RemoteSession())
            {
                return false;
            }
            return ActivePathCount() == 1;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string displaySoloSnapValue = Settings.LoadStr("DisplaySoloSnap", "");
                if (displaySoloSnapValue.Length == 0)
                {
                    return true;
                }
                uint result;
                if (!uint.TryParse(displaySoloSnapValue, out result) || (result != 2 && result != 4 && result != 8))
                {
                    return false;
                }
                uint topology;
                if (!TrySetTopology(result))
                {
                    if (!SettledAsSingleDisplay())
                    {
                        return false;
                    }
                }
                else if ((!TryCurrentTopology(out topology) || topology != result) && !SettledAsSingleDisplay())
                {
                    return false;
                }
                if (!Settings.SaveStr("DisplaySoloSnap", "") || Settings.LoadStr("DisplaySoloSnap", "").Length != 0)
                {
                    return false;
                }
                Logger.Log(Lang.T("log.solo.2", 0));
                return true;
            }
        }

        public static bool HealFromCrash()
        {
            return Restore();
        }

        public static bool HasResidue()
        {
            return Settings.LoadStr("DisplaySoloSnap", "").Length != 0;
        }

        private static int ActivePathCount()
        {
            try
            {
                uint numPaths;
                uint numModes;
                if (GetDisplayConfigBufferSizes(2u, out numPaths, out numModes) != 0)
                {
                    return -1;
                }
                return (int)numPaths;
            }
            catch
            {
                return -1;
            }
        }

        private static bool TryCurrentTopology(out uint topology)
        {
            topology = 0u;
            IntPtr nativeBuffer = IntPtr.Zero;
            IntPtr currentNativeBuffer = IntPtr.Zero;
            try
            {
                uint numPaths;
                uint numModes;
                if (GetDisplayConfigBufferSizes(4u, out numPaths, out numModes) != 0 || numPaths == 0 || numPaths > 64 || numModes > 128)
                {
                    return false;
                }
                nativeBuffer = Marshal.AllocHGlobal((int)(numPaths * 72));
                currentNativeBuffer = Marshal.AllocHGlobal((int)(Math.Max(numModes, 1u) * 64));
                if (QueryDisplayConfig(4u, ref numPaths, nativeBuffer, ref numModes, currentNativeBuffer, out topology) != 0)
                {
                    return false;
                }
                return topology != 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (nativeBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(nativeBuffer);
                }
                if (currentNativeBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(currentNativeBuffer);
                }
            }
        }

        private static bool TrySetTopology(uint topology)
        {
            try
            {
                return SetDisplayConfig(0u, IntPtr.Zero, 0u, IntPtr.Zero, topology | 0x80u) == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool RemoteSession()
        {
            try
            {
                return GetSystemMetrics(4096) != 0;
            }
            catch
            {
                return true;
            }
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, IntPtr paths, ref uint numModes, IntPtr modes, out uint topologyId);

        [DllImport("user32.dll")]
        private static extern int SetDisplayConfig(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);
    }
}

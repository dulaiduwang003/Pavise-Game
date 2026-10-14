// 作者：bdth 2074055628@qq.com
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class DisplaySolo
    {
        internal const string SnapKey = "DisplaySoloSnap";

        internal const uint TopologyInternal = 0x1;
        internal const uint TopologyClone = 0x2;
        internal const uint TopologyExtend = 0x4;
        internal const uint TopologyExternal = 0x8;

        private static readonly object lk = new object();

        // 远程桌面的活动路径不能证明物理显示器已恢复。
        private static bool SettledAsSingleDisplay()
        {
            if (RemoteSession()) return false;
            return ActivePathCount() == 1;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string snapshot = Settings.LoadStr(SnapKey, "");
                if (snapshot.Length == 0) return true;
                uint topology;
                if (!uint.TryParse(snapshot, out topology)
                    || (topology != TopologyClone && topology != TopologyExtend
                        && topology != TopologyExternal))
                {
                    // 损坏的拓扑快照保留给重置处理。
                    return false;
                }
                if (!TrySetTopology(topology))
                {
                    if (!SettledAsSingleDisplay()) return false;
                }
                else
                {
                    uint after;
                    if (!TryCurrentTopology(out after) || after != topology)
                        if (!SettledAsSingleDisplay()) return false;
                }
                if (!Settings.SaveStr(SnapKey, "") || Settings.LoadStr(SnapKey, "").Length != 0)
                    return false;
                Logger.Log(Lang.T("log.solo.2"));
                return true;
            }
        }

        public static bool HealFromCrash()
        {
            return Restore();
        }

        public static bool HasResidue()
        {
            return Settings.LoadStr(SnapKey, "").Length != 0;
        }

        private static int ActivePathCount()
        {
#if PAVISE_SELFTEST
            if (PathCountForTest != null) return PathCountForTest();
            throw new InvalidOperationException("DisplaySolo path counting requires an injected test double.");
#else
            try
            {
                uint paths, modes;
                if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out paths, out modes) != 0)
                    return -1;
                return (int)paths;
            }
            catch { return -1; }
#endif
        }

        private static bool TryCurrentTopology(out uint topology)
        {
            topology = 0;
#if PAVISE_SELFTEST
            if (TopologyForTest != null) return TopologyForTest(out topology);
            throw new InvalidOperationException("DisplaySolo topology queries require an injected test double.");
#else
            IntPtr pathBuffer = IntPtr.Zero, modeBuffer = IntPtr.Zero;
            try
            {
                uint paths, modes;
                if (GetDisplayConfigBufferSizes(QdcDatabaseCurrent, out paths, out modes) != 0
                    || paths == 0 || paths > 64 || modes > 128) return false;
                pathBuffer = Marshal.AllocHGlobal((int)(paths * PathInfoSize));
                modeBuffer = Marshal.AllocHGlobal((int)(Math.Max(modes, 1) * ModeInfoSize));
                if (QueryDisplayConfig(QdcDatabaseCurrent, ref paths, pathBuffer,
                        ref modes, modeBuffer, out topology) != 0) return false;
                return topology != 0;
            }
            catch { return false; }
            finally
            {
                if (pathBuffer != IntPtr.Zero) Marshal.FreeHGlobal(pathBuffer);
                if (modeBuffer != IntPtr.Zero) Marshal.FreeHGlobal(modeBuffer);
            }
#endif
        }

        private static bool TrySetTopology(uint topology)
        {
#if PAVISE_SELFTEST
            if (SetForTest != null) return SetForTest(topology);
            throw new InvalidOperationException("DisplaySolo topology writes require an injected test double.");
#else
            try
            {
                return SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, topology | SdcApply) == 0;
            }
            catch { return false; }
#endif
        }

        private static bool RemoteSession()
        {
#if PAVISE_SELFTEST
            if (RemoteForTest != null) return RemoteForTest();
            throw new InvalidOperationException("DisplaySolo session probing requires an injected test double.");
#else
            try { return GetSystemMetrics(SmRemoteSession) != 0; }
            catch { return true; }
#endif
        }

#if PAVISE_SELFTEST
        internal delegate bool TopologyOverride(out uint topology);
        internal static Func<int> PathCountForTest;
        internal static TopologyOverride TopologyForTest;
        internal static Func<uint, bool> SetForTest;
        internal static Func<bool> RemoteForTest;

        internal static void ResetForTest()
        {
            lock (lk)
            {
                PathCountForTest = null;
                TopologyForTest = null;
                SetForTest = null;
                RemoteForTest = null;
            }
        }
#endif

#if !PAVISE_SELFTEST
        private const uint QdcOnlyActivePaths = 0x2;
        private const uint QdcDatabaseCurrent = 0x4;
        private const uint SdcApply = 0x80;
        private const int SmRemoteSession = 0x1000;
        private const uint PathInfoSize = 72;
        private const uint ModeInfoSize = 64;

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags,
            out uint numPaths, out uint numModes);
        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPaths, IntPtr paths,
            ref uint numModes, IntPtr modes, out uint topologyId);
        [DllImport("user32.dll")]
        private static extern int SetDisplayConfig(uint numPaths, IntPtr paths,
            uint numModes, IntPtr modes, uint flags);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);
#endif
    }
}

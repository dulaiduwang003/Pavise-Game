// @author bdth 2074055628@qq.com
// File purpose Match single-display is retired; keeps only topology restore for crash residue, snapshot cleanup and residue reporting
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // Match single-display shipped in 2.1.3.3 and was pulled right after; no activation entry remains
    //   A crash mid-match on old versions leaves a topology snapshot; this switches back per snapshot at startup and clears the ledger
    //   A corrupt snapshot is never applied blindly, the record is kept for the reset flow; a secondary already unplugged settles per the physical world
    internal static class DisplaySolo
    {
        internal const string SnapKey = "DisplaySoloSnap";

        internal const uint TopologyInternal = 0x1;
        internal const uint TopologyClone = 0x2;
        internal const uint TopologyExtend = 0x4;
        internal const uint TopologyExternal = 0x8;

        private static readonly object lk = new object();

        // Single-display settlement proof: exactly one active path counts; 0 is a transient during reconfiguration and doesn't count
        //   In a remote session the path count describes the remote desktop, not the physical machine; when a post-crash makeup runs over RDP
        //   settling on the remote "single display" would lose the physical machine's original topology record, leaving only manual Win+P
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
                    // Corrupt snapshot can't be restored safely; keep the record, leave it to the reset flow
                    return false;
                }
                // The secondary may already be unplugged; switching a single-display machine to a multi-display topology is either refused by the API outright
                //   or written but unverifiable; both failure shapes settle per the physical world, else the snapshot never clears
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

        // After an abnormal Pavise exit the topology is still internal-only; next startup switches back per snapshot
        public static bool HealFromCrash()
        {
            return Restore();
        }

        public static bool HasResidue()
        {
            return Settings.LoadStr(SnapKey, "").Length != 0;
        }

        // Three primitives: isolated tests must inject them, production uses native calls
        //   Active path count -1 means unreadable; the caller treats it as multi-display, better to over-check
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
            // Can't tell, assume remote; better to leave it unsettled
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

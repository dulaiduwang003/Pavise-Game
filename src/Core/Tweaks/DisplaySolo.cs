// @author bdth 2074055628@qq.com
// 文件用途 对局单屏 对局中切到仅主屏拓扑 防副屏抢焦点 省一路合成 退局按快照切回
using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 对局单屏 面向多屏机器上的误点副屏丢焦点和多一路 DWM 合成
    //   不序列化整套显示路径 走 Win+P 同款拓扑机制 记住当前拓扑 扩展/复制/仅外屏
    //   对局切"仅内屏" 退局切回原拓扑 各屏的分辨率位置由 Windows 拓扑数据库自己恢复
    //
    // 快照就一个枚举值 先落盘再动系统 回读核实 验不过立刻切回并报失败
    //   熔断由 Env 框架统一管 连续失败两次自动关开关
    // 边界 单屏机器和已经是仅内屏的机器都无事可做 不落快照
    //   退局时副屏已被拔掉 切回扩展验不出也算成功 物理世界优先
    internal static class DisplaySolo
    {
        internal const string SnapKey = "DisplaySoloSnap";

        internal const uint TopologyInternal = 0x1;
        internal const uint TopologyClone = 0x2;
        internal const uint TopologyExtend = 0x4;
        internal const uint TopologyExternal = 0x8;

        private static readonly object lk = new object();

        // 单屏结算证明 恰好一条活动路径才算数 0 是重配置瞬间的过渡态不作数
        //   远程会话里路径数说的是远程桌面不是物理机 崩溃后经 RDP 启动补撤时
        //   若按远程的"单屏"结账 物理机的原拓扑记录就丢了 只能手动 Win+P
        private static bool SettledAsSingleDisplay()
        {
            if (RemoteSession()) return false;
            return ActivePathCount() == 1;
        }

        public static bool Activate()
        {
            lock (lk)
            {
                // 远程会话里切拓扑切的是远程桌面 无意义 整局不做
                if (RemoteSession()) return true;
                // 单屏无事可做 多屏才有"收成单屏"可言
                int count = ActivePathCount();
                if (count >= 0 && count <= 1) return true;
                uint topology;
                if (!TryCurrentTopology(out topology)) return false;
                if (topology == TopologyInternal) return true;
                if (topology != TopologyClone && topology != TopologyExtend
                    && topology != TopologyExternal) return false;
                // 已有快照说明上次的原拓扑还没还清 不覆盖 那才是真正的原值
                string snapshot = Settings.LoadStr(SnapKey, "");
                if (snapshot.Length == 0)
                {
                    string record = topology.ToString();
                    if (!Settings.SaveStr(SnapKey, record)
                        || Settings.LoadStr(SnapKey, "") != record) return false;
                }
                if (!TrySetTopology(TopologyInternal)) { Undo(topology); return false; }
                // 返回码不作数 回读才作数
                uint after;
                if (!TryCurrentTopology(out after) || after != TopologyInternal)
                {
                    Undo(topology);
                    return false;
                }
                Logger.Log(Lang.T("log.solo.1"));
                return true;
            }
        }

        private static void Undo(uint topology)
        {
            try { TrySetTopology(topology); } catch { }
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
                    // 快照坏了没法安全恢复 保留记录不清 由重置流程处置
                    return false;
                }
                // 副屏可能已经被拔掉 单屏机器切多屏拓扑要么被 API 直接拒绝
                //   要么写成功但验不出 两种失败形态都按物理世界收尾 否则快照永远还不清
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

        // Pavise 异常退出时拓扑还停在仅内屏 下次启动按快照切回
        public static bool HealFromCrash()
        {
            return Restore();
        }

        public static bool HasResidue()
        {
            return Settings.LoadStr(SnapKey, "").Length != 0;
        }

        // 三个原语 隔离测试必须注入 生产走原生调用
        //   活动路径数 -1 表示读不出来 调用方按多屏处理 宁可多做检查
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
            // 判不了就当远程 宁可不动不结算
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

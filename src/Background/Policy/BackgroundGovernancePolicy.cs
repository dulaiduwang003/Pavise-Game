using System;
using System.Globalization;

namespace PaviseApp
{
    internal static class BackgroundGovernancePolicy
    {
        internal const string SettingKey = "BackgroundGovernanceMode";
        internal const int Normal = 0;
        internal const int Aggressive = 1;

        internal static int Normalize(int mode) { return mode == Aggressive ? Aggressive : Normal; }

        internal static int Selection(bool masterEnabled, int mode)
        { return masterEnabled ? Normalize(mode) + 1 : 0; }

        internal static int Load()
        { return Parse(Settings.LoadStr(SettingKey, "0")); }

        internal static int Parse(string value)
        {
            int mode;
            return int.TryParse(value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out mode) ? Normalize(mode) : Normal;
        }

        internal static bool Save(int mode)
        {
            if (mode != Normal && mode != Aggressive) return false;
            string value = mode.ToString(CultureInfo.InvariantCulture);
            return Settings.SaveStrDurable(SettingKey, value) && Settings.LoadStr(SettingKey, "") == value;
        }

        // 两档都禁止普通后台线程临时提优

        internal static bool WantsBackgroundBoostLimit(bool masterEnabled, int mode)
        { return masterEnabled; }

        // 两档都把普通后台放进一个带锚点的 Job 内核锁定 Idle 优先级 进程自己改不回
        // CPU 硬上限按核数换算成 Job 接口要的万分比 正常档每个进程最多 2 个核 核少的机器最多全机 25%
        // 激进档用接口允许的最低值 1 也就是 0.01% 对局中后台基本冻结
        internal const uint AggressiveCpuRate = 1;
        internal const uint NormalCpuRateCap = 2500;

        internal static uint ResourceCpuRate(int mode, int logicalProcessors)
        {
            if (Normalize(mode) == Aggressive) return AggressiveCpuRate;
            int logical = Math.Max(1, logicalProcessors);
            return (uint)Math.Max(1, Math.Min(NormalCpuRateCap, (int)Math.Round(2.0 * 10000 / logical)));
        }

        // 每卷按字节每秒申请带宽上限 系统拒绝时继续使用进程的极低 I/O 优先级
        internal const long NormalIoBandwidth = 50L * 1024 * 1024;
        internal const long AggressiveIoBandwidth = 1L * 1024 * 1024;

        internal static long ResourceIoBandwidth(int mode)
        { return Normalize(mode) == Aggressive ? AggressiveIoBandwidth : NormalIoBandwidth; }

        // 激进档另把普通后台收到分区给后台的那组核心上 把游戏常用的核心让出来 正常档不收核
        // 有可靠分区时直接用它 大小核是能效核 X3D 是不带 3D 缓存的那组 双 CCD 是另一个 CCD 其余八核以上机型是 0 号核加末尾核
        // 没有分区时退回末尾 1～2 个物理核 算不出来 多处理器组或结果覆盖全部核心时不收核
        internal static ulong AggressiveCoreTarget(bool masterEnabled, int mode, bool multiGroup, bool safePartition, ulong partitionBackground, ulong tailSqueeze, ulong allMask)
        {
            if (!masterEnabled || mode != Aggressive || multiGroup || allMask == 0) return 0;
            ulong target = (safePartition ? partitionBackground : tailSqueeze) & allMask;
            return target == allMask ? 0 : target;
        }
    }
}

using System;
using System.Globalization;

namespace PaviseApp
{
    internal static class BackgroundGovernancePolicy
    {
        internal const string SettingKey = "BackgroundGovernanceMode";
        internal const int Normal = 0;
        internal const int Aggressive = 1;
        // 界面上这一档叫疯狂 代码里叫 Freeze 保存的值是 2 和已有用户的设置一致 不用迁移
        internal const int Freeze = 2;

        internal static bool Valid(int mode) { return mode == Normal || mode == Aggressive || mode == Freeze; }

        internal static int Normalize(int mode) { return Valid(mode) ? mode : Normal; }

        // 界面选项依次是 关闭 正常 激进 疯狂
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

        internal static string Text(int mode)
        { return Normalize(mode).ToString(CultureInfo.InvariantCulture); }

        internal static bool Save(int mode)
        {
            if (!Valid(mode)) return false;
            string value = Text(mode);
            return Settings.SaveStrDurable(SettingKey, value) && Settings.LoadStr(SettingKey, "") == value;
        }

        // 三档都禁止普通后台线程临时提优

        internal static bool WantsBackgroundBoostLimit(bool masterEnabled, int mode)
        { return masterEnabled; }

        // 三档都把普通后台放进一个带锚点的 Job 由内核锁定 Idle 优先级 进程自己改不回 并申请磁盘限速
        // CPU 硬上限按每个进程能用多少核时间给 再换算成 Job 接口要的全机万分比 核多核少的机器得到同样的绝对预算
        // 正常档每个进程最多 2 个核 核少的机器最多全机 25% 后台在 Idle 优先级下也会占满空闲核 推高功耗挤掉游戏核心的睿频
        // 激进档每个进程 0.5 个核 实测够键鼠驱动 改键工具和耳机软件按时响应 想吃满 CPU 的后台只拿到正常档的四分之一
        // 疯狂档用接口允许的最低值 1 也就是全机 0.01% 对局中后台基本停住
        internal const uint FreezeCpuRate = 1;
        internal const uint NormalCpuRateCap = 2500;
        internal const double NormalCores = 2.0;
        internal const double AggressiveCores = 0.5;

        internal static uint ResourceCpuRate(int mode, int logicalProcessors)
        {
            int normalized = Normalize(mode);
            if (normalized == Freeze) return FreezeCpuRate;
            int logical = Math.Max(1, logicalProcessors);
            double cores = normalized == Aggressive ? AggressiveCores : NormalCores;
            return (uint)Math.Max(1, Math.Min(NormalCpuRateCap, (int)Math.Round(cores * 10000 / logical, MidpointRounding.AwayFromZero)));
        }

        // 每卷按字节每秒申请带宽上限 系统拒绝时继续使用进程的极低 I/O 优先级
        internal const long NormalIoBandwidth = 50L * 1024 * 1024;
        internal const long AggressiveIoBandwidth = 10L * 1024 * 1024;
        internal const long FreezeIoBandwidth = 1L * 1024 * 1024;

        internal static long ResourceIoBandwidth(int mode)
        {
            int normalized = Normalize(mode);
            return normalized == Freeze ? FreezeIoBandwidth : normalized == Aggressive ? AggressiveIoBandwidth : NormalIoBandwidth;
        }

        // 激进和疯狂档另把普通后台收到分区给后台的那组核心上 把游戏常用的核心让出来 正常档不收核
        // 有可靠分区时直接用它 大小核是能效核 X3D 是不带 3D 缓存的那组 双 CCD 是另一个 CCD 其余八核以上机型是 0 号核加末尾核
        // 没有分区时只有疯狂档退回末尾 1～2 个物理核 算不出来 多处理器组或结果覆盖全部核心时不收核
        // 激进档只收到整组核心式的分区上 也就是能效核 X3D 不带缓存的 CCD 或双 CCD 的另一组
        // 从同构 CPU 里抠出一两个核的分区和末尾核都不用 后台锁在 Idle 优先级 只剩一两个核时 游戏和系统线程一占住 键鼠驱动就排不上队
        // 模拟 6 核 12 线程笔记本实测 收到末尾一个物理核后 1000Hz 鼠标回调只跟上 9%～77% 不收核时全部跟上
        internal static ulong AggressiveCoreTarget(bool masterEnabled, int mode, bool multiGroup, bool safePartition, bool domainPartition, ulong partitionBackground, ulong tailSqueeze, ulong allMask)
        {
            if (!masterEnabled || multiGroup || allMask == 0) return 0;
            ulong target;
            if (mode == Freeze) target = safePartition ? partitionBackground : tailSqueeze;
            else if (mode == Aggressive && safePartition && domainPartition) target = partitionBackground;
            else return 0;
            target &= allMask;
            return target == allMask ? 0 : target;
        }

        // 分区方案的标签见 CpuPartitionPolicy.Decide 这三种把一整组核心分给后台 其余几种只分一到四个核
        internal static bool DomainPartition(string tag)
        {
            return tag == "hybrid-pe" || tag == "x3d-cache" || tag == "symmetric-ccd";
        }
    }
}

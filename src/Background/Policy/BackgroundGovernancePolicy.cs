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

        // 设置里的疯狂档开关 默认关 关着时界面只给关闭 正常和激进 存着的疯狂档按正常档运行
        // 存着的档位不改 打开开关后按它生效
        internal const string InsaneVisibleKey = "BackgroundGovernanceInsaneVisible";

        internal static bool InsaneVisible()
        { return Settings.LoadStr(InsaneVisibleKey, "0") == "1"; }

        internal static int Effective(int storedMode)
        {
            int mode = Normalize(storedMode);
            return mode == Freeze && !InsaneVisible() ? Normal : mode;
        }

        // 运行时实际生效的档位
        internal static int Load()
        { return Effective(LoadStored()); }

        // 用户保存的档位 不看疯狂档开关
        internal static int LoadStored()
        { return Parse(Settings.LoadStr(SettingKey, "0")); }

        // 升级到这一版时 不管以前选的是哪一档 都改回正常档 疯狂档开关也关上 只做一次
        internal const string ResetMarkerKey = "BackgroundGovernanceResetV1";

        internal static void ResetForThisVersionOnce()
        {
            if (Settings.LoadStr(ResetMarkerKey, "") == "1") return;
            bool levelSaved = Settings.LoadStr(SettingKey, "0") == Text(Normal) || Save(Normal);
            bool switchSaved = Settings.LoadStr(InsaneVisibleKey, "0") != "1" || Settings.SaveStrDurable(InsaneVisibleKey, "0");
            if (levelSaved && switchSaved) Settings.SaveStrDurable(ResetMarkerKey, "1");
        }

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

        // 激进和疯狂档禁止 Windows 临时提升普通后台线程的优先级 正常档不限制

        internal static bool WantsBackgroundBoostLimit(bool masterEnabled, int mode)
        { return masterEnabled && Normalize(mode) != Normal; }

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

        // 三档都不挪核 后台留在原来的核心范围里 只靠 CPU 上限 优先级和磁盘限速管住
    }
}

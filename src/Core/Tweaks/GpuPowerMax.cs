// @author bdth 2074055628@qq.com
// 文件用途 对局中把显卡功耗墙拉到厂商允许的上限 退局按快照还原
// NVIDIA 走私有功耗策略接口 数值经驱动校验 AMD 走 ADLX 手动功耗调节 范围由驱动强制

using System;
using System.Globalization;

namespace PaviseApp
{
    internal static class GpuPowerMax
    {
        private const string SnapKey = "GpuPowerSnap";
        private static readonly object lk = new object();

        public static bool Supported()
        {
            uint nvCur, nvDef, nvMax;
            if (NvApi.Available && NvApi.TryGetPowerLimit(out nvCur, out nvDef, out nvMax))
                return nvMax > nvCur;
            int amdCur, amdMax;
            if (AdlxTweaks.PowerLimitGetFirst(out amdCur, out amdMax))
                return amdMax > amdCur;
            return false;
        }

        public static bool Activate()
        {
            lock (lk)
            {
                if (Settings.LoadStr(SnapKey, "").Length > 0) return true;

                uint nvCur, nvDef, nvMax;
                if (NvApi.Available && NvApi.TryGetPowerLimit(out nvCur, out nvDef, out nvMax))
                {
                    if (nvCur >= nvMax) return true;
                    Settings.SaveStr(SnapKey, "nv:" + nvCur.ToString(CultureInfo.InvariantCulture));
                    if (Settings.LoadStr(SnapKey, "").Length == 0)
                    {
                        Logger.Log("显卡功耗墙 快照无法持久化 本轮未调整");
                        return false;
                    }
                    if (!NvApi.TrySetPowerLimit(nvMax))
                    {
                        Settings.SaveStr(SnapKey, "");
                        Logger.Log("显卡功耗墙 NVIDIA 写入失败或本机不允许上调");
                        return false;
                    }
                    Logger.Log("显卡功耗墙 已拉至上限 " + (nvMax / 1000) + "% 原 "
                        + (nvCur / 1000) + "% 退出对局还原");
                    return true;
                }

                int amdCur, amdMax;
                if (AdlxTweaks.PowerLimitGetFirst(out amdCur, out amdMax))
                {
                    if (amdCur >= amdMax) return true;
                    Settings.SaveStr(SnapKey, "amd:" + amdCur.ToString(CultureInfo.InvariantCulture));
                    if (Settings.LoadStr(SnapKey, "").Length == 0)
                    {
                        Logger.Log("显卡功耗墙 快照无法持久化 本轮未调整");
                        return false;
                    }
                    if (!AdlxTweaks.PowerLimitSetFirst(amdMax))
                    {
                        Settings.SaveStr(SnapKey, "");
                        Logger.Log("显卡功耗墙 AMD 写入失败");
                        return false;
                    }
                    Logger.Log("显卡功耗墙 已拉至上限 " + amdMax + " 原 " + amdCur + " 退出对局还原");
                    return true;
                }

                Logger.Log("显卡功耗墙 本机不支持调整 已跳过");
                return false;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string snap = Settings.LoadStr(SnapKey, "");
                if (snap.Length == 0) return true;
                bool ok = false;
                if (snap.StartsWith("nv:", StringComparison.Ordinal))
                {
                    uint prev;
                    ok = uint.TryParse(snap.Substring(3), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out prev) && NvApi.TrySetPowerLimit(prev);
                }
                else if (snap.StartsWith("amd:", StringComparison.Ordinal))
                {
                    int prev;
                    ok = int.TryParse(snap.Substring(4), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out prev) && AdlxTweaks.PowerLimitSetFirst(prev);
                }
                else ok = true;
                if (!ok)
                {
                    Logger.Log("显卡功耗墙还原失败 快照保留 下次启动继续尝试");
                    return false;
                }
                Settings.SaveStr(SnapKey, "");
                Logger.Log("显卡功耗墙已还原");
                return true;
            }
        }

        public static bool HasResidue() { return Settings.LoadStr(SnapKey, "").Length > 0; }

        public static void HealFromCrash()
        {
            if (HasResidue() && Restore())
                Logger.Log("检测到上次未还原的显卡功耗墙设置 已恢复");
        }
    }
}

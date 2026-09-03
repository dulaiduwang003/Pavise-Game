// @author bdth 2074055628@qq.com
// 文件用途 显卡锁频已下架 只保留旧版留下的锁频收据清收 启动与清除时按收据解锁
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    // 2.2.0.0 上架 随后下架 笔记本上钉死显卡等于先划走 CPU 的功耗份 实测掉帧
    //   台式机收益没有实测支撑 整项撤掉 旧版写过的收据照旧在启动和清除时解锁
    internal static class GpuClockLock
    {
        // 会话收据 由恢复完成判定共同引用 改名必须两边一起
        internal const string ReceiptKey = "GpuClockLockReceipt";
        private const int ClockGraphics = 0;
        private const int NvmlSuccess = 0;
        private const int NvmlNotSupported = 3;
        private const int NvmlNoPermission = 4;
        private static readonly object lk = new object();

        [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
        private static extern int NvmlInit();
        [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
        private static extern int NvmlShutdown();
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
        private static extern int NvmlGetCount(out uint count);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
        private static extern int NvmlGetHandle(uint index, out IntPtr device);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUUID", CharSet = CharSet.Ansi)]
        private static extern int NvmlGetUuid(IntPtr device, StringBuilder buffer, uint length);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMaxClockInfo")]
        private static extern int NvmlGetMaxClock(IntPtr device, int type, out uint mhz);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceSetGpuLockedClocks")]
        private static extern int NvmlSetLockedClocks(IntPtr device, uint minMhz, uint maxMhz);
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceResetGpuLockedClocks")]
        private static extern int NvmlResetLockedClocks(IntPtr device);

        // A 卡走 ADLX 的最低核心频率 快照存在 AdlxTweaks 的账本里 这里只记一个"本局用了 A 卡路径"的标记
        private const string AmdKey = "GpuClockLockAmd";

        public static bool HasResidue
        {
            get
            {
                return Settings.LoadStr(ReceiptKey, "").Length > 0
                    || Settings.Load(AmdKey, false) || AdlxTweaks.HasGfxMinResidue();
            }
        }

        // 电竞和极限档锁定开启 掌机和笔记本不由档位强制
        private sealed class Device
        {
            public IntPtr Handle;
            public string Uuid;
        }

        // 每次操作独立 Init 和 Shutdown NVML 自己按引用计数 不占着句柄跨局
        private static bool Open(List<Device> devices)
        {
            try
            {
                if (NvmlInit() != NvmlSuccess) return false;
                uint count;
                if (NvmlGetCount(out count) != NvmlSuccess) { NvmlShutdown(); return false; }
                for (uint i = 0; i < count; i++)
                {
                    IntPtr handle;
                    if (NvmlGetHandle(i, out handle) != NvmlSuccess) continue;
                    var uuid = new StringBuilder(96);
                    if (NvmlGetUuid(handle, uuid, (uint)uuid.Capacity) != NvmlSuccess) continue;
                    devices.Add(new Device { Handle = handle, Uuid = uuid.ToString() });
                }
                return true;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch { return false; }
        }

        private static void Close()
        {
            try { NvmlShutdown(); }
            catch { }
        }

        // 支持性按进程缓存 驱动更新才会变 那种场景本来就要重启程序

        internal static string EncodeReceipt(IList<KeyValuePair<string, uint>> locked)
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, uint> entry in locked)
                parts.Add(entry.Key + "|" + entry.Value.ToString(CultureInfo.InvariantCulture));
            return string.Join(";", parts.ToArray());
        }

        internal static List<KeyValuePair<string, uint>> DecodeReceipt(string raw)
        {
            var list = new List<KeyValuePair<string, uint>>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (string part in raw.Split(';'))
            {
                int bar = part.IndexOf('|');
                if (bar <= 0) continue;
                uint mhz;
                if (!uint.TryParse(part.Substring(bar + 1), NumberStyles.None, CultureInfo.InvariantCulture, out mhz))
                    continue;
                list.Add(new KeyValuePair<string, uint>(part.Substring(0, bar), mhz));
            }
            return list;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (Settings.Load(AmdKey, false) || AdlxTweaks.HasGfxMinResidue())
                {
                    if (!AdlxTweaks.RestoreGfxMin()) return false;
                    Settings.Save(AmdKey, false);
                }
                string raw = Settings.LoadStr(ReceiptKey, "");
                if (raw.Length == 0) return true;
                List<KeyValuePair<string, uint>> entries = DecodeReceipt(raw);
                var devices = new List<Device>();
                if (!Open(devices)) { Logger.Log(Lang.T("log.gpuclock.6")); return false; }
                try
                {
                    bool ok = true;
                    foreach (KeyValuePair<string, uint> entry in entries)
                    {
                        Device found = null;
                        foreach (Device d in devices)
                            if (string.Equals(d.Uuid, entry.Key, StringComparison.OrdinalIgnoreCase)) { found = d; break; }
                        // 收据里的卡已经不在机器上 锁随驱动会话一起没了 不算失败
                        if (found == null) continue;
                        int rc = NvmlResetLockedClocks(found.Handle);
                        if (rc != NvmlSuccess && rc != NvmlNotSupported) ok = false;
                    }
                    if (!ok) { Logger.Log(Lang.T("log.gpuclock.6")); return false; }
                    Settings.SaveStr(ReceiptKey, "");
                    Logger.Log(Lang.T("log.gpuclock.7"));
                    return true;
                }
                finally { Close(); }
            }
        }

        public static void HealFromCrash()
        {
            if (HasResidue && Restore()) Logger.Log(Lang.T("log.gpuclock.8"));
        }
    }
}

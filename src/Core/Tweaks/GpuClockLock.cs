// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    // 频率锁已撤回，仅保留旧版收据恢复。
    internal static class GpuClockLock
    {
        internal const string ReceiptKey = "GpuClockLockReceipt";
        private const int NvmlSuccess = 0;
        private const int NvmlNotSupported = 3;
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
        [DllImport("nvml.dll", EntryPoint = "nvmlDeviceResetGpuLockedClocks")]
        private static extern int NvmlResetLockedClocks(IntPtr device);

        private const string AmdKey = "GpuClockLockAmd";

        public static bool HasResidue
        {
            get
            {
                return Settings.LoadStr(ReceiptKey, "").Length > 0
                    || Settings.Load(AmdKey, false) || AdlxTweaks.HasGfxMinResidue();
            }
        }

        private sealed class Device
        {
            public IntPtr Handle;
            public string Uuid;
        }

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
                        // 收据对应显卡已移除时，驱动会话中的锁随之失效。
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

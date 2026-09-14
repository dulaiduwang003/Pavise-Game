// @author bdth 2074055628@qq.com
// File purpose GPU clock lock is withdrawn; only cleanup of receipts left by older versions remains, unlocking by receipt at startup and on wipe
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    // Shipped in 2.2.0.0, withdrawn soon after; pinning the GPU on a laptop takes the power share away from the CPU first, dropped frames in testing
    //   No test data backs a desktop gain; the whole item is pulled; receipts written by older versions are still unlocked at startup and on wipe
    internal static class GpuClockLock
    {
        // Session receipt shared with the restore-complete check; rename both sides together
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

        // AMD cards go through ADLX minimum core clock; the snapshot lives in the AdlxTweaks ledger, here we only record a 'this match used the AMD path' flag
        private const string AmdKey = "GpuClockLockAmd";

        public static bool HasResidue
        {
            get
            {
                return Settings.LoadStr(ReceiptKey, "").Length > 0
                    || Settings.Load(AmdKey, false) || AdlxTweaks.HasGfxMinResidue();
            }
        }

        // Esports tier forces the lock on; Handheld and laptop are not forced by tier
        private sealed class Device
        {
            public IntPtr Handle;
            public string Uuid;
        }

        // Each operation does its own Init and Shutdown; NVML refcounts internally; don't hold the handle across matches
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

        // Support is cached per process; only a driver update changes it, and that scenario needs an app restart anyway

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
                        // Card in the receipt is no longer in the machine; the lock died with the driver session, not a failure
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

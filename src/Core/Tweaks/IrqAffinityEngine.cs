// @author bdth 2074055628@qq.com
// 文件用途 中断亲和策略的通用引擎 供显卡和网卡等设备复用
using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace PaviseApp
{
    internal enum IrqRebootState { Unknown, AwaitingReboot, Rebooted }

    internal sealed class IrqAffinityEngine
    {
        private const int PolicyAllCloseProcessors = 1;
        private const int PolicySpecifiedProcessors = 4;

        private readonly string settingsKey;
        private readonly string slotPrefix;
        private readonly string logPrefix;

        public IrqAffinityEngine(string settingsKey, string slotPrefix, string logPrefix)
        {
            this.settingsKey = settingsKey;
            this.slotPrefix = slotPrefix;
            this.logPrefix = logPrefix;
        }

        public bool EnabledByPavise { get { return Settings.Load(settingsKey, false); } }

        public bool HasResidue
        {
            get { return EnabledByPavise || LoadTouched().Count > 0; }
        }

        public List<string> TouchedDevices() { return LoadTouched(); }

        private sealed class Target
        {
            public string DeviceId;
            public ReversibleReg Policy;
            public ReversibleReg Mask;
        }

        private static string StableSlot(string deviceId)
        {
            unchecked
            {
                int h = 17;
                foreach (char c in deviceId) h = h * 31 + c;
                return h.ToString("X8");
            }
        }

        internal static byte[] MaskToBytes(ulong mask)
        {
            var b = new byte[8];
            for (int i = 0; i < 8; i++) b[i] = (byte)((mask >> (i * 8)) & 0xFF);
            return b;
        }

        internal static ulong BytesToMask(byte[] b)
        {
            if (b == null || b.Length < 8) return 0;
            ulong m = 0;
            for (int i = 0; i < 8; i++) m |= ((ulong)b[i]) << (i * 8);
            return m;
        }

        private List<Target> BuildTargets(List<string> deviceIds)
        {
            var list = new List<Target>();
            foreach (string id in deviceIds)
            {
                string regPath = @"SYSTEM\CurrentControlSet\Enum\" + id + @"\Device Parameters\Interrupt Management\Affinity Policy";
                string slotBase = slotPrefix + StableSlot(id);
                list.Add(new Target
                {
                    DeviceId = id,
                    Policy = new ReversibleReg(Registry.LocalMachine, regPath, "DevicePolicy", RegistryValueKind.DWord, slotBase + "_Policy"),
                    Mask = new ReversibleReg(Registry.LocalMachine, regPath, "AssignmentSetOverride", RegistryValueKind.Binary, slotBase + "_Mask")
                });
            }
            return list;
        }

        internal static void ReportMsiState(List<string> deviceIds)
        {
            if (deviceIds == null) return;
            foreach (string id in deviceIds)
            {
                try
                {
                    string path = @"SYSTEM\CurrentControlSet\Enum\" + id
                        + @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
                    {
                        object v = k == null ? null : k.GetValue("MSISupported");
                        if (v == null)
                            Logger.Log(Lang.T("log.irqaffinityengine.1") + id + Lang.T("log.irqaffinityengine.2"));
                        else if (Convert.ToInt64(v) == 0)
                            Logger.Log(Lang.T("log.irqaffinityengine.1") + id + Lang.T("log.irqaffinityengine.3"));
                    }
                }
                catch { }
            }
        }

        public bool Enable(List<string> deviceIds)
        {
            return Enable(deviceIds, CpuTopology.BoostMask);
        }

        public bool Enable(List<string> deviceIds, ulong preferredMask)
        {
            ReportMsiState(deviceIds);
            if (deviceIds == null || deviceIds.Count == 0)
            {
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.4"));
                return false;
            }
            List<Target> targets = BuildTargets(deviceIds);
            bool useMask = !CpuTopology.MultiGroup && preferredMask != 0 && preferredMask != CpuTopology.AllMask;
            if (CpuTopology.MultiGroup)
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.5"));

            var touched = LoadTouched();

            var ledger = new List<string>(touched);
            foreach (Target t in targets) if (!ledger.Contains(t.DeviceId)) ledger.Add(t.DeviceId);
            if (!SaveTouched(ledger))
            {
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.8"));
                return false;
            }

            object policyValue = useMask ? PolicySpecifiedProcessors : PolicyAllCloseProcessors;
            byte[] maskBytes = useMask ? MaskToBytes(preferredMask) : null;

            bool anyOk = false;
            string stamp = BootStamp();
            var applied = new List<Target>();
            var dirty = new List<string>();
            foreach (Target t in targets)
            {
                bool atTarget = t.Policy.Matches(policyValue)
                    && (!useMask || t.Mask.Matches(maskBytes));
                bool alreadyConfigured = atTarget
                    && (touched.Contains(t.DeviceId)
                        || (!t.Policy.HasBackup && (!useMask || !t.Mask.HasBackup)));
                string bootKey = BootKeyFor(t.DeviceId);
                bool attempted;
                bool ok = ApplyWithBootStamp(alreadyConfigured, stamp,
                    delegate(string value) { return Settings.SaveStr(bootKey, value); },
                    delegate { return Settings.LoadStr(bootKey, ""); },
                    delegate
                    {
                        return useMask ? t.Policy.Apply(policyValue) & t.Mask.Apply(maskBytes)
                            : t.Policy.Apply(policyValue);
                    }, out attempted);
                if (!attempted)
                {
                    // No registry change took place, so do not restore an older
                    // backup merely because this write's marker could not be saved.
                    if (ok) anyOk = true;
                    else Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.bootstamp") + t.DeviceId);
                    continue;
                }
                if (ok) { anyOk = true; applied.Add(t); }
                else
                {
                    if (!(t.Policy.Restore() & t.Mask.Restore())) dirty.Add(t.DeviceId);
                    Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.6") + t.DeviceId + Lang.T("log.irqaffinityengine.7"));
                }
            }
            if (!anyOk)
            {
                foreach (string id in dirty) if (!touched.Contains(id)) touched.Add(id);
                SaveTouched(touched);
                return false;
            }

            foreach (Target t in applied) if (!touched.Contains(t.DeviceId)) touched.Add(t.DeviceId);
            foreach (string id in dirty) if (!touched.Contains(id)) touched.Add(id);
            SaveTouched(touched);

            Settings.Save(settingsKey, true);
            if (!Settings.Load(settingsKey, false))
            {
                var failed = new List<string>();
                foreach (Target t in applied)
                    if (!(t.Policy.Restore() & t.Mask.Restore())) failed.Add(t.DeviceId);
                var remaining = new List<string>();
                foreach (string id in touched)
                {
                    bool wasApplied = false;
                    foreach (Target t in applied)
                        if (string.Equals(t.DeviceId, id, StringComparison.OrdinalIgnoreCase)) { wasApplied = true; break; }
                    if (!wasApplied || failed.Contains(id)) remaining.Add(id);
                }
                SaveTouched(remaining);
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.9"));
                return false;
            }
            if (applied.Count > 0)
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.10") + applied.Count + Lang.T("log.irqaffinityengine.11")
                    + (useMask ? Lang.T("log.irqaffinityengine.12") + preferredMask.ToString("X") + " " : Lang.T("log.irqaffinityengine.13"))
                    + Lang.T("log.irqaffinityengine.14"));
            return true;
        }

        private string TouchedKey { get { return slotPrefix + "Touched"; } }

        private string BootKeyFor(string deviceId)
        {
            return slotPrefix + StableSlot(deviceId ?? "") + "_BootStamp";
        }

        public bool RebootedSinceWrite(string deviceId)
        {
            return GetRebootState(deviceId) == IrqRebootState.Rebooted;
        }

        public IrqRebootState GetRebootState(string deviceId)
        {
            return RebootStateFromStamps(Settings.LoadStr(BootKeyFor(deviceId), ""), BootStamp());
        }

        internal static bool RebootedSinceStamp(string saved, string now)
        {
            return RebootStateFromStamps(saved, now) == IrqRebootState.Rebooted;
        }

        internal static IrqRebootState RebootStateFromStamps(string saved, string now)
        {
            long before, current;
            if (!TryParseBootStamp(saved, out before) || !TryParseBootStamp(now, out current))
                return IrqRebootState.Unknown;
            long elapsed = current - before;
            if (elapsed >= -BootStampToleranceSeconds && elapsed <= BootStampToleranceSeconds)
                return IrqRebootState.AwaitingReboot;
            return elapsed > 0 ? IrqRebootState.Rebooted : IrqRebootState.Unknown;
        }

        private static bool TryParseBootStamp(string stamp, out long seconds)
        {
            return long.TryParse(stamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)
                && seconds > 0 && seconds <= DateTime.MaxValue.Ticks / TimeSpan.TicksPerSecond;
        }

        internal static bool ApplyWithBootStamp(bool alreadyConfigured, string stamp,
            Func<string, bool> saveStamp, Func<string> loadStamp, Func<bool> apply, out bool attempted)
        {
            attempted = false;
            // A no-op must neither alter a valid marker nor invent one for an external pin.
            if (alreadyConfigured) return true;
            long seconds;
            if (!TryParseBootStamp(stamp, out seconds)
                || saveStamp == null || loadStamp == null || apply == null) return false;
            try
            {
                // Persist before changing affinity. A failed save can leave an
                // older boot marker intact; it must never describe a new write.
                if (!saveStamp(stamp) || !string.Equals(loadStamp(), stamp, StringComparison.Ordinal))
                    return false;
            }
            catch { return false; }
            attempted = true;
            return apply();
        }

        internal void RememberBootStampForTest(string deviceId)
        {
            Settings.SaveStr(BootKeyFor(deviceId), BootStamp());
        }

        public void ForgetBootStamp(string deviceId)
        {
            Settings.Remove(BootKeyFor(deviceId));
        }

        internal static bool SameBoot(string saved, string now)
        {
            if (saved == null || saved.Length == 0) return false;
            long a, b;
            if (!long.TryParse(saved, NumberStyles.Integer, CultureInfo.InvariantCulture, out a)) return false;
            if (!long.TryParse(now, NumberStyles.Integer, CultureInfo.InvariantCulture, out b)) return false;
            return Math.Abs(a - b) <= BootStampToleranceSeconds;
        }

        internal const int BootStampToleranceSeconds = 5;

        internal static string BootStamp()
        {
            try
            {
                long upSeconds = (long)(Native.GetTickCount64() / 1000UL);
                return (DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond - upSeconds)
                    .ToString(CultureInfo.InvariantCulture);
            }
            catch { return ""; }
        }

        private List<string> LoadTouched()
        {
            var list = new List<string>();
            foreach (string s in Settings.LoadStr(TouchedKey, "").Split('\n'))
            {
                string id = s.Trim();
                if (id.Length > 0 && !list.Contains(id)) list.Add(id);
            }
            return list;
        }

        private bool SaveTouched(List<string> ids)
        {
            string joined = string.Join("\n", ids.ToArray());
            Settings.SaveStr(TouchedKey, joined);
            return Settings.LoadStr(TouchedKey, "") == joined;
        }

        public bool Disable(List<string> deviceIds)
        {
            var scope = LoadTouched();
            if (deviceIds != null)
                foreach (string id in deviceIds)
                    if (!scope.Contains(id)) scope.Add(id);

            List<Target> targets = BuildTargets(scope);
            bool allOk = true;
            int restored = 0;
            var stillDirty = new List<string>();
            foreach (Target t in targets)
            {
                bool hadBackup = t.Policy.HasBackup || t.Mask.HasBackup;
                bool ok = t.Policy.Restore() & t.Mask.Restore();
                if (hadBackup && ok) restored++;
                if (ok) ForgetBootStamp(t.DeviceId);
                if (!ok) { allOk = false; stillDirty.Add(t.DeviceId); }
            }

            if (!SaveTouched(stillDirty)) allOk = false;
            if (allOk)
            {
                Settings.Save(settingsKey, false);
                if (Settings.Load(settingsKey, true)) return false;
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.22") + restored + Lang.T("log.irqaffinityengine.23"));
            }
            else
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.24") + stillDirty.Count + Lang.T("log.irqaffinityengine.25"));
            return allOk;
        }
    }
}

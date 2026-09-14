// @author bdth 2074055628@qq.com
// File purpose Generic engine for interrupt affinity policy, reused by GPU, NIC and other devices
using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace PaviseApp
{
    internal enum IrqRebootState { Unknown, AwaitingReboot, Rebooted }

    internal sealed class IrqAffinityEngine
    {
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

        // Strict read: ledger unreadable and ledger truly empty must stay distinct; orphan detection must not treat a read failure as evidence
        public bool TryTouchedDevices(out List<string> ids)
        {
            ids = new List<string>();
            string raw;
            if (!Settings.TryLoadStr(TouchedKey, out raw)) return false;
            foreach (string s in (raw ?? "").Split('\n'))
            {
                string id = s.Trim();
                if (id.Length > 0 && !ids.Contains(id)) ids.Add(id);
            }
            return true;
        }

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

        internal IrqWriteResult LastResult = new IrqWriteResult();

        internal static bool SupportsExactMask(bool multiGroup, ulong mask, ulong all)
        { return !multiGroup && mask != 0 && all != 0 && (mask & ~all) == 0; }

        public bool Enable(List<string> deviceIds, ulong preferredMask)
        {
            LastResult = new IrqWriteResult();
            // AssignmentSetOverride is a group-local KAFFINITY Never substitute a
            // proximity policy for an explicit mask on an unsupported topology
            if (!SupportsExactMask(CpuTopology.MultiGroup, preferredMask, CpuTopology.AllMask))
            { LastResult.Failed = deviceIds == null ? 1 : deviceIds.Count; LastResult.Failure = Lang.T("irq.exact.unsupported"); return false; }
            ReportMsiState(deviceIds);
            if (deviceIds == null || deviceIds.Count == 0)
            {
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.4"));
                return false;
            }
            List<Target> targets = BuildTargets(deviceIds);

            List<string> touched;
            if (!TryTouchedDevices(out touched))
            { LastResult.Failed = deviceIds.Count; LastResult.Failure = Lang.T("irq.write.journalfailed"); return false; }

            var ledger = new List<string>(touched);
            foreach (Target t in targets) if (!ledger.Contains(t.DeviceId)) ledger.Add(t.DeviceId);
            if (!SaveTouched(ledger))
            {
                LastResult.Failed = deviceIds.Count;
                LastResult.Failure = Lang.T("irq.write.journalfailed");
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.8"));
                return false;
            }

            object policyValue = PolicySpecifiedProcessors;
            byte[] maskBytes = MaskToBytes(preferredMask);

            bool anyOk = false;
            string stamp = BootStamp();
            var applied = new List<Target>();
            var dirty = new List<string>();
            foreach (Target t in targets)
            {
                bool atTarget = t.Policy.Matches(policyValue)
                    && t.Mask.Matches(maskBytes);
                bool alreadyConfigured = atTarget
                    && (touched.Contains(t.DeviceId)
                        || (!t.Policy.HasBackup && !t.Mask.HasBackup));
                string bootKey = BootKeyFor(t.DeviceId);
                bool attempted;
                int rollbackFailures = LastResult.RollbackFailed;
                bool ok = ApplyTarget(LastResult, alreadyConfigured, stamp,
                    delegate(string value) { return Settings.SaveStr(bootKey,value); },
                    delegate { return Settings.LoadStr(bootKey,""); },
                    delegate { return t.Policy.Apply(policyValue) & t.Mask.Apply(maskBytes); },
                    delegate { return t.Policy.Restore() & t.Mask.Restore(); }, out attempted);
                if (ok) { anyOk = true; if (attempted) applied.Add(t); }
                else
                {
                    if (LastResult.RollbackFailed > rollbackFailures) dirty.Add(t.DeviceId);
                    Logger.Log(logPrefix + t.DeviceId + " " + LastResult.Describe());
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
            bool ledgerSaved = SaveTouched(touched);

            Settings.Save(settingsKey, true);
            if (!ledgerSaved || !Settings.Load(settingsKey, false))
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
                LastResult.Failed += applied.Count;
                LastResult.Succeeded -= applied.Count;
                LastResult.RollbackFailed += failed.Count;
                LastResult.RolledBack += applied.Count - failed.Count;
                LastResult.Failure = Lang.T("irq.write.journalfailed");
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.9"));
                return false;
            }
            if (applied.Count > 0)
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.10") + applied.Count + Lang.T("log.irqaffinityengine.11")
                    + (Lang.T("log.irqaffinityengine.12") + preferredMask.ToString("X") + " ")
                    + Lang.T("log.irqaffinityengine.14"));
            return LastResult.Failed == 0 && LastResult.RollbackFailed == 0;
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

        internal static bool ApplyTarget(IrqWriteResult result, bool configured, string stamp,
            Func<string,bool> save, Func<string> load, Func<bool> apply, Func<bool> restore, out bool attempted)
        {
            attempted = false; bool ok = false;
            try { ok = ApplyWithBootStamp(configured,stamp,save,load,apply,out attempted); } catch { }
            if (attempted) result.Attempted++;
            if (ok) { result.Succeeded++; if (!attempted) result.Unchanged++; return true; }
            result.Failed++;
            if (attempted)
            {
                bool rolled = false;
                try { rolled = restore(); } catch { }
                if (rolled) result.RolledBack++; else result.RollbackFailed++;
            }
            return false;
        }

        internal static bool ApplyWithBootStamp(bool alreadyConfigured, string stamp,
            Func<string, bool> saveStamp, Func<string> loadStamp, Func<bool> apply, out bool attempted)
        {
            attempted = false;
            // A no-op must neither touch the active stamp nor fabricate one for an external pin
            if (alreadyConfigured) return true;
            long seconds;
            if (!TryParseBootStamp(stamp, out seconds)
                || saveStamp == null || loadStamp == null || apply == null) return false;
            try
            {
                // Persist before touching affinity; a failed save may leave an older boot stamp in place
                // and that must never be taken as describing this new write
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
            string actual;
            return Settings.SaveStr(TouchedKey, joined)
                && Settings.TryLoadStr(TouchedKey,out actual) && actual == joined;
        }

        // Restore only the given devices without merging new IDs into the list; for plan-level partial rollback
        //   Unlike Disable, which tears everything down, this backs out just one item
        public bool RestoreOnly(List<string> deviceIds)
        {
            if (deviceIds == null || deviceIds.Count == 0) return true;
            List<string> touched;
            if (!TryTouchedDevices(out touched)) return false;
            return RestoreTouchedScope(touched,deviceIds,delegate(string id)
                {
                    Target target = BuildTargets(new List<string> { id })[0];
                    return target.Policy.Restore() & target.Mask.Restore();
                },SaveTouched,ForgetBootStamp,delegate
                {
                    return Settings.Save(settingsKey,false) && !Settings.Load(settingsKey,true);
                });
        }

        internal static bool RestoreTouchedScope(List<string> touched, List<string> selected,
            Func<string,bool> restore, Func<List<string>,bool> save,
            Action<string> forgetBoot, Func<bool> disable)
        {
            if (touched == null || selected == null) return false;
            var remaining = new List<string>(touched);
            bool allOk = true;
            foreach (string id in selected)
            {
                bool ok = false;
                try { ok = restore(id); } catch { }
                if (!ok) { allOk = false; continue; }
                try { if (forgetBoot != null) forgetBoot(id); } catch { allOk = false; continue; }
                for (int i = remaining.Count - 1; i >= 0; i--)
                    if (string.Equals(remaining[i],id,StringComparison.OrdinalIgnoreCase)) remaining.RemoveAt(i);
            }
            if (allOk && remaining.Count == 0)
            {
                // The last device remains discoverable until the global flag
                // has also settled If finalization fails its existing receipt
                // index lets a later single-device or full restore retry it
                try { if (disable == null || !disable()) return false; } catch { return false; }
            }
            try { if (save == null || !save(remaining)) allOk = false; } catch { allOk = false; }
            return allOk;
        }

        public bool Disable(List<string> deviceIds)
        {
            List<string> scope;
            if (!TryTouchedDevices(out scope)) return false;
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

using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class IrqAffinityEngine
    {
        private sealed class Target
        {
            public string DeviceId;

            public ReversibleReg Policy;

            public ReversibleReg Mask;
        }

        private readonly string settingsKey;

        private readonly string slotPrefix;

        private readonly string logPrefix;

        internal IrqWriteResult LastResult = new IrqWriteResult();

        public bool EnabledByPavise
        {
            get
            {
                return Settings.Load(settingsKey, false);
            }
        }

        public bool HasResidue
        {
            get
            {
                if (!EnabledByPavise)
                {
                    return LoadTouched().Count > 0;
                }
                return true;
            }
        }

        private string TouchedKey
        {
            get
            {
                return slotPrefix + "Touched";
            }
        }

        public IrqAffinityEngine(string settingsKey, string slotPrefix, string logPrefix)
        {
            this.settingsKey = settingsKey;
            this.slotPrefix = slotPrefix;
            this.logPrefix = logPrefix;
        }

        public List<string> TouchedDevices()
        {
            return LoadTouched();
        }

        public bool TryTouchedDevices(out List<string> ids)
        {
            ids = new List<string>();
            string text;
            if (!Settings.TryLoadStr(TouchedKey, out text))
            {
                return false;
            }
            foreach (string entryText in (text ?? "").Split('\n'))
            {
                string entryTextTrimmed = entryText.Trim();
                if (entryTextTrimmed.Length > 0 && !ids.Contains(entryTextTrimmed))
                {
                    ids.Add(entryTextTrimmed);
                }
            }
            return true;
        }

        private static string StableSlot(string deviceId)
        {
            int hashValue = 17;
            foreach (char c in deviceId)
            {
                hashValue = hashValue * 31 + c;
            }
            return hashValue.ToString("X8");
        }

        internal static byte[] MaskToBytes(ulong mask)
        {
            byte[] rawBytes = new byte[8];
            for (int i = 0; i < 8; i++)
            {
                rawBytes[i] = (byte)((mask >> i * 8) & 0xFF);
            }
            return rawBytes;
        }

        internal static ulong BytesToMask(byte[] b)
        {
            if (b == null || b.Length < 8)
            {
                return 0uL;
            }
            ulong bytesToMaskResult = 0uL;
            for (int i = 0; i < 8; i++)
            {
                bytesToMaskResult |= (ulong)b[i] << i * 8;
            }
            return bytesToMaskResult;
        }

        private List<Target> BuildTargets(List<string> deviceIds)
        {
            List<Target> targets = new List<Target>();
            foreach (string deviceId in deviceIds)
            {
                string subKey = "SYSTEM\\CurrentControlSet\\Enum\\" + deviceId + "\\Device Parameters\\Interrupt Management\\Affinity Policy";
                string slotPrefixOffset = slotPrefix + StableSlot(deviceId);
                targets.Add(new Target
                {
                    DeviceId = deviceId,
                    Policy = new ReversibleReg(Registry.LocalMachine, subKey, "DevicePolicy", RegistryValueKind.DWord, slotPrefixOffset + "_Policy"),
                    Mask = new ReversibleReg(Registry.LocalMachine, subKey, "AssignmentSetOverride", RegistryValueKind.Binary, slotPrefixOffset + "_Mask")
                });
            }
            return targets;
        }

        internal static void ReportMsiState(List<string> deviceIds)
        {
            if (deviceIds == null)
            {
                return;
            }
            foreach (string deviceId in deviceIds)
            {
                try
                {
                    string name = "SYSTEM\\CurrentControlSet\\Enum\\" + deviceId + "\\Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties";
                    using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey(name))
                    {
                        object instance = ((registryKey == null) ? null : registryKey.GetValue("MSISupported"));
                        if (instance == null)
                        {
                            Logger.Log(Lang.T("log.irqaffinityengine.1", 0) + deviceId + Lang.T("log.irqaffinityengine.2", 0));
                        }
                        else if (Convert.ToInt64(instance) == 0)
                        {
                            Logger.Log(Lang.T("log.irqaffinityengine.1", 0) + deviceId + Lang.T("log.irqaffinityengine.3", 0));
                        }
                    }
                }
                catch
                {
                }
            }
        }

        public bool Enable(List<string> deviceIds)
        {
            return Enable(deviceIds, CpuTopology.BoostMask);
        }

        internal static bool SupportsExactMask(bool multiGroup, ulong mask, ulong all)
        {
            if (!multiGroup && mask != 0 && all != 0)
            {
                return (mask & ~all) == 0;
            }
            return false;
        }

        public bool Enable(List<string> deviceIds, ulong preferredMask)
        {
            LastResult = new IrqWriteResult();
            if (!SupportsExactMask(CpuTopology.MultiGroup, preferredMask, CpuTopology.AllMask))
            {
                LastResult.Failed = ((deviceIds == null) ? 1 : deviceIds.Count);
                LastResult.Failure = Lang.T("irq.exact.unsupported");
                return false;
            }
            ReportMsiState(deviceIds);
            if (deviceIds == null || deviceIds.Count == 0)
            {
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.4", 0));
                return false;
            }
            List<Target> targets = BuildTargets(deviceIds);
            List<string> ids;
            if (!TryTouchedDevices(out ids))
            {
                LastResult.Failed = deviceIds.Count;
                LastResult.Failure = Lang.T("irq.write.journalfailed");
                return false;
            }
            List<string> idsSnapshot = new List<string>(ids);
            foreach (Target target in targets)
            {
                if (!idsSnapshot.Contains(target.DeviceId))
                {
                    idsSnapshot.Add(target.DeviceId);
                }
            }
            if (!SaveTouched(idsSnapshot))
            {
                LastResult.Failed = deviceIds.Count;
                LastResult.Failure = Lang.T("irq.write.journalfailed");
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.8", 0));
                return false;
            }
            object policyValue = 4;
            byte[] maskBytes = MaskToBytes(preferredMask);
            bool anyTargetApplied = false;
            string stamp = BootStamp();
            List<Target> ts = new List<Target>();
            List<string> currentDeviceIds = new List<string>();
            foreach (Target t in targets)
            {
                bool configured = t.Policy.Matches(policyValue) && t.Mask.Matches(maskBytes) && (ids.Contains(t.DeviceId) || (!t.Policy.HasBackup && !t.Mask.HasBackup));
                string bootKey = BootKeyFor(t.DeviceId);
                int rollbackFailed = LastResult.RollbackFailed;
                bool attempted;
                if (ApplyTarget(LastResult, configured, stamp, (string value) => Settings.SaveStr(bootKey, value), () => Settings.LoadStr(bootKey, ""), () => t.Policy.Apply(policyValue) & t.Mask.Apply(maskBytes), () => t.Policy.Restore() & t.Mask.Restore(), out attempted))
                {
                    anyTargetApplied = true;
                    if (attempted)
                    {
                        ts.Add(t);
                    }
                }
                else
                {
                    if (LastResult.RollbackFailed > rollbackFailed)
                    {
                        currentDeviceIds.Add(t.DeviceId);
                    }
                    Logger.Log(logPrefix + t.DeviceId + " " + LastResult.Describe());
                }
            }
            if (!anyTargetApplied)
            {
                foreach (string currentDeviceId in currentDeviceIds)
                {
                    if (!ids.Contains(currentDeviceId))
                    {
                        ids.Add(currentDeviceId);
                    }
                }
                SaveTouched(ids);
                return false;
            }
            foreach (Target currentT in ts)
            {
                if (!ids.Contains(currentT.DeviceId))
                {
                    ids.Add(currentT.DeviceId);
                }
            }
            foreach (string candidateDeviceId in currentDeviceIds)
            {
                if (!ids.Contains(candidateDeviceId))
                {
                    ids.Add(candidateDeviceId);
                }
            }
            bool saveTouchedSucceeded = SaveTouched(ids);
            Settings.Save(settingsKey, true);
            if (!saveTouchedSucceeded || !Settings.Load(settingsKey, false))
            {
                List<string> candidateDeviceIds = new List<string>();
                foreach (Target candidateT in ts)
                {
                    if (!(candidateT.Policy.Restore() & candidateT.Mask.Restore()))
                    {
                        candidateDeviceIds.Add(candidateT.DeviceId);
                    }
                }
                List<string> texts = new List<string>();
                foreach (string id in ids)
                {
                    bool targetApplied = false;
                    foreach (Target resolvedT in ts)
                    {
                        if (string.Equals(resolvedT.DeviceId, id, StringComparison.OrdinalIgnoreCase))
                        {
                            targetApplied = true;
                            break;
                        }
                    }
                    if (!targetApplied || candidateDeviceIds.Contains(id))
                    {
                        texts.Add(id);
                    }
                }
                SaveTouched(texts);
                LastResult.Failed += ts.Count;
                LastResult.Succeeded -= ts.Count;
                LastResult.RollbackFailed += candidateDeviceIds.Count;
                LastResult.RolledBack += ts.Count - candidateDeviceIds.Count;
                LastResult.Failure = Lang.T("irq.write.journalfailed");
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.9", 0));
                return false;
            }
            if (ts.Count > 0)
            {
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.10", 0) + ts.Count + Lang.T("log.irqaffinityengine.11", 0) + Lang.T("log.irqaffinityengine.12", 0) + preferredMask.ToString("X") + " " + Lang.T("log.irqaffinityengine.14", 0));
            }
            if (LastResult.Failed == 0)
            {
                return LastResult.RollbackFailed == 0;
            }
            return false;
        }

        private string BootKeyFor(string deviceId)
        {
            return slotPrefix + StableSlot(deviceId ?? "") + "_BootStamp";
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
            return BootSessionIdentity.Compare(saved, now);
        }

        internal static bool ApplyTarget(IrqWriteResult result, bool configured, string stamp, Func<string, bool> save, Func<string> load, Func<bool> apply, Func<bool> restore, out bool attempted)
        {
            attempted = false;
            bool applied = false;
            try
            {
                applied = ApplyWithBootStamp(configured, stamp, save, load, apply, out attempted);
            }
            catch
            {
            }
            if (attempted)
            {
                result.Attempted++;
            }
            if (applied)
            {
                result.Succeeded++;
                if (!attempted)
                {
                    result.Unchanged++;
                }
                return true;
            }
            result.Failed++;
            if (attempted)
            {
                bool restored = false;
                try
                {
                    restored = restore();
                }
                catch
                {
                }
                if (restored)
                {
                    result.RolledBack++;
                }
                else
                {
                    result.RollbackFailed++;
                }
            }
            return false;
        }

        internal static bool ApplyWithBootStamp(bool alreadyConfigured, string stamp, Func<string, bool> saveStamp, Func<string> loadStamp, Func<bool> apply, out bool attempted)
        {
            attempted = false;
            if (alreadyConfigured)
            {
                return true;
            }
            if (!BootSessionIdentity.Valid(stamp) || saveStamp == null || loadStamp == null || apply == null)
            {
                return false;
            }
            try
            {
                if (!saveStamp(stamp) || !string.Equals(loadStamp(), stamp, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }
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

        internal static string BootStamp()
        {
            return BootSessionIdentity.Read() ?? "";
        }

        private List<string> LoadTouched()
        {
            List<string> entryTextTrimmeds = new List<string>();
            foreach (string entryText in Settings.LoadStr(TouchedKey, "").Split('\n'))
            {
                string entryTextTrimmed = entryText.Trim();
                if (entryTextTrimmed.Length > 0 && !entryTextTrimmeds.Contains(entryTextTrimmed))
                {
                    entryTextTrimmeds.Add(entryTextTrimmed);
                }
            }
            return entryTextTrimmeds;
        }

        private bool SaveTouched(List<string> ids)
        {
            string joinResult = string.Join("\n", ids.ToArray());
            string value;
            if (Settings.SaveStr(TouchedKey, joinResult) && Settings.TryLoadStr(TouchedKey, out value))
            {
                return value == joinResult;
            }
            return false;
        }

        public bool RestoreOnly(List<string> deviceIds)
        {
            if (deviceIds == null || deviceIds.Count == 0)
            {
                return true;
            }
            List<string> ids;
            if (!TryTouchedDevices(out ids))
            {
                return false;
            }
            return RestoreTouchedScope(ids, deviceIds, delegate(string id)
            {
                Target target = BuildTargets(new List<string> { id })[0];
                return target.Policy.Restore() & target.Mask.Restore();
            }, SaveTouched, ForgetBootStamp, () => Settings.Save(settingsKey, false) && !Settings.Load(settingsKey, true));
        }

        internal static bool RestoreTouchedScope(List<string> touched, List<string> selected, Func<string, bool> restore, Func<List<string>, bool> save, Action<string> forgetBoot, Func<bool> disable)
        {
            if (touched == null || selected == null)
            {
                return false;
            }
            List<string> touchedSnapshot = new List<string>(touched);
            bool restoreTouchedScopeSucceeded = true;
            foreach (string currentSelected in selected)
            {
                bool restored = false;
                try
                {
                    restored = restore(currentSelected);
                }
                catch
                {
                }
                if (!restored)
                {
                    restoreTouchedScopeSucceeded = false;
                    continue;
                }
                try
                {
                    if (forgetBoot != null)
                    {
                        forgetBoot(currentSelected);
                    }
                }
                catch
                {
                    restoreTouchedScopeSucceeded = false;
                    continue;
                }
                for (int listIndex = touchedSnapshot.Count - 1; listIndex >= 0; listIndex--)
                {
                    if (string.Equals(touchedSnapshot[listIndex], currentSelected, StringComparison.OrdinalIgnoreCase))
                    {
                        touchedSnapshot.RemoveAt(listIndex);
                    }
                }
            }
            if (restoreTouchedScopeSucceeded && touchedSnapshot.Count == 0)
            {
                try
                {
                    if (disable == null || !disable())
                    {
                        return false;
                    }
                }
                catch
                {
                    return false;
                }
            }
            try
            {
                if (save == null || !save(touchedSnapshot))
                {
                    restoreTouchedScopeSucceeded = false;
                }
            }
            catch
            {
                restoreTouchedScopeSucceeded = false;
            }
            return restoreTouchedScopeSucceeded;
        }

        public bool Disable(List<string> deviceIds)
        {
            List<string> ids;
            if (!TryTouchedDevices(out ids))
            {
                return false;
            }
            if (deviceIds != null)
            {
                foreach (string deviceId in deviceIds)
                {
                    if (!ids.Contains(deviceId))
                    {
                        ids.Add(deviceId);
                    }
                }
            }
            List<Target> targets = BuildTargets(ids);
            bool disableSucceeded = true;
            int processedCount = 0;
            List<string> currentDeviceIds = new List<string>();
            foreach (Target target in targets)
            {
                bool hasOwnedPolicy = target.Policy.HasBackup || target.Mask.HasBackup;
                bool policyRestored = target.Policy.Restore() & target.Mask.Restore();
                if (hasOwnedPolicy && policyRestored)
                {
                    processedCount++;
                }
                if (policyRestored)
                {
                    ForgetBootStamp(target.DeviceId);
                }
                if (!policyRestored)
                {
                    disableSucceeded = false;
                    currentDeviceIds.Add(target.DeviceId);
                }
            }
            if (!SaveTouched(currentDeviceIds))
            {
                disableSucceeded = false;
            }
            if (disableSucceeded)
            {
                Settings.Save(settingsKey, false);
                if (Settings.Load(settingsKey, true))
                {
                    return false;
                }
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.22", 0) + processedCount + Lang.T("log.irqaffinityengine.23", 0));
            }
            else
            {
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.24", 0) + currentDeviceIds.Count + Lang.T("log.irqaffinityengine.25", 0));
            }
            return disableSucceeded;
        }
    }
}

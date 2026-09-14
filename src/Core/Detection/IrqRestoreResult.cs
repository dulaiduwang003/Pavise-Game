using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal delegate bool IrqReceiptReader(out List<string> devices);

    internal sealed class IrqDeviceRestoreResult
    {
        internal string DeviceId;
        internal bool AffinityRequested, PriorityRequested, Affinity, Priority;
        internal bool Success { get { return (!AffinityRequested || Affinity) && (!PriorityRequested || Priority); } }
        internal string AffinityResult { get { return !AffinityRequested ? "skipped" : Affinity ? "ok" : "failed"; } }
        internal string PriorityResult { get { return !PriorityRequested ? "skipped" : Priority ? "ok" : "failed"; } }
    }

    // Snapshot both receipt scopes before restoring anything Each outcome belongs
    // only to the device whose receipts were actually included in this operation
    internal sealed class IrqRestoreResult
    {
        internal bool ScopeReadSucceeded;
        internal bool AffinityFinalized = true;
        internal readonly List<IrqDeviceRestoreResult> Devices = new List<IrqDeviceRestoreResult>();

        internal bool Success
        {
            get
            {
                if (!ScopeReadSucceeded || !AffinityFinalized) return false;
                foreach (var device in Devices) if (!device.Success) return false;
                return true;
            }
        }

        internal IrqDeviceRestoreResult ForDevice(string id)
        {
            foreach (var device in Devices)
                if (string.Equals(device.DeviceId,id,StringComparison.OrdinalIgnoreCase)) return device;
            return null;
        }

        internal string Describe()
        {
            if (!ScopeReadSucceeded) return Lang.T("irq.restore.readfailed");
            bool affinityRequested = !AffinityFinalized, priorityRequested = false, affinity = AffinityFinalized, priority = true;
            foreach (var device in Devices)
            {
                affinityRequested |= device.AffinityRequested;
                priorityRequested |= device.PriorityRequested;
                if (device.AffinityRequested && !device.Affinity) affinity = false;
                if (device.PriorityRequested && !device.Priority) priority = false;
            }
            return Lang.F("irq.restore.result",
                Lang.T(!affinityRequested ? "irq.step.skipped" : affinity ? "irq.step.ok" : "irq.step.failed"),
                Lang.T(!priorityRequested ? "irq.step.skipped" : priority ? "irq.step.ok" : "irq.step.failed"));
        }

        internal static IrqRestoreResult Run(string selected, IrqReceiptReader readAffinity,
            IrqReceiptReader readPriority, Func<List<string>> otherOwners,
            Func<string,bool> restoreAffinity, Func<string,bool> restorePriority)
        {
            var result = new IrqRestoreResult();
            List<string> affinity, priority;
            try
            {
                if (readAffinity == null || readPriority == null
                    || !readAffinity(out affinity) || affinity == null
                    || !readPriority(out priority) || priority == null) return result;
                AddScope(result,affinity,selected,true);
                AddScope(result,priority,selected,false);
                // Full restore retains the existing full-receipt scope A single
                // device operation must still respect the other tweak owners
                if (selected != null)
                {
                    var owners = otherOwners == null ? null : otherOwners();
                    if (owners == null) { result.Devices.Clear(); return result; }
                    foreach (string owner in owners)
                        if (string.Equals(owner,selected,StringComparison.OrdinalIgnoreCase))
                        { result.ScopeReadSucceeded = true; return result; }
                }
            }
            catch { result.Devices.Clear(); return result; }
            result.ScopeReadSucceeded = true;
            foreach (var device in result.Devices)
            {
                if (device.AffinityRequested)
                    try { device.Affinity = restoreAffinity != null && restoreAffinity(device.DeviceId); } catch { }
                if (device.PriorityRequested)
                    try { device.Priority = restorePriority != null && restorePriority(device.DeviceId); } catch { }
            }
            return result;
        }

        private static void AddScope(IrqRestoreResult result, List<string> scope, string selected, bool affinity)
        {
            foreach (string id in scope)
            {
                if (string.IsNullOrEmpty(id)) throw new ArgumentException("Invalid IRQ receipt device");
                if (selected != null && !string.Equals(id,selected,StringComparison.OrdinalIgnoreCase)) continue;
                var device = result.ForDevice(id);
                if (device == null)
                { device = new IrqDeviceRestoreResult { DeviceId = id }; result.Devices.Add(device); }
                if (affinity) device.AffinityRequested = true; else device.PriorityRequested = true;
            }
        }
    }
}

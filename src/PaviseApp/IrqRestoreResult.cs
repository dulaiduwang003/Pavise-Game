using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class IrqRestoreResult
    {
        internal bool ScopeReadSucceeded;

        internal bool AffinityFinalized = true;

        internal readonly List<IrqDeviceRestoreResult> Devices = new List<IrqDeviceRestoreResult>();

        internal bool Success
        {
            get
            {
                if (!ScopeReadSucceeded || !AffinityFinalized)
                {
                    return false;
                }
                foreach (IrqDeviceRestoreResult device in Devices)
                {
                    if (!device.Success)
                    {
                        return false;
                    }
                }
                return true;
            }
        }

        internal IrqDeviceRestoreResult ForDevice(string id)
        {
            foreach (IrqDeviceRestoreResult device in Devices)
            {
                if (string.Equals(device.DeviceId, id, StringComparison.OrdinalIgnoreCase))
                {
                    return device;
                }
            }
            return null;
        }

        internal string Describe()
        {
            if (!ScopeReadSucceeded)
            {
                return Lang.T("irq.restore.readfailed");
            }
            bool affinityPending = !AffinityFinalized;
            bool priorityRequested = false;
            bool affinityFinalizedSnapshot = AffinityFinalized;
            bool priorityRestored = true;
            foreach (IrqDeviceRestoreResult device in Devices)
            {
                affinityPending |= device.AffinityRequested;
                priorityRequested |= device.PriorityRequested;
                if (device.AffinityRequested && !device.Affinity)
                {
                    affinityFinalizedSnapshot = false;
                }
                if (device.PriorityRequested && !device.Priority)
                {
                    priorityRestored = false;
                }
            }
            return Lang.F("irq.restore.result", Lang.T((!affinityPending) ? "irq.step.skipped" : (affinityFinalizedSnapshot ? "irq.step.ok" : "irq.step.failed")), Lang.T((!priorityRequested) ? "irq.step.skipped" : (priorityRestored ? "irq.step.ok" : "irq.step.failed")));
        }

        internal static IrqRestoreResult Run(string selected, IrqReceiptReader readAffinity, IrqReceiptReader readPriority, Func<List<string>> otherOwners, Func<string, bool> restoreAffinity, Func<string, bool> restorePriority)
        {
            IrqRestoreResult irqRestoreResult = new IrqRestoreResult();
            try
            {
                List<string> devices;
                List<string> devices2;
                if (readAffinity == null || readPriority == null || !readAffinity(out devices) || devices == null || !readPriority(out devices2) || devices2 == null)
                {
                    return irqRestoreResult;
                }
                AddScope(irqRestoreResult, devices, selected, true);
                AddScope(irqRestoreResult, devices2, selected, false);
                if (selected != null)
                {
                    List<string> list = ((otherOwners == null) ? null : otherOwners());
                    if (list == null)
                    {
                        irqRestoreResult.Devices.Clear();
                        return irqRestoreResult;
                    }
                    foreach (string item in list)
                    {
                        if (string.Equals(item, selected, StringComparison.OrdinalIgnoreCase))
                        {
                            irqRestoreResult.ScopeReadSucceeded = true;
                            return irqRestoreResult;
                        }
                    }
                }
            }
            catch
            {
                irqRestoreResult.Devices.Clear();
                return irqRestoreResult;
            }
            irqRestoreResult.ScopeReadSucceeded = true;
            foreach (IrqDeviceRestoreResult device in irqRestoreResult.Devices)
            {
                if (device.AffinityRequested)
                {
                    try
                    {
                        device.Affinity = restoreAffinity != null && restoreAffinity(device.DeviceId);
                    }
                    catch
                    {
                    }
                }
                if (device.PriorityRequested)
                {
                    try
                    {
                        device.Priority = restorePriority != null && restorePriority(device.DeviceId);
                    }
                    catch
                    {
                    }
                }
            }
            return irqRestoreResult;
        }

        private static void AddScope(IrqRestoreResult result, List<string> scope, string selected, bool affinity)
        {
            foreach (string item in scope)
            {
                if (string.IsNullOrEmpty(item))
                {
                    throw new ArgumentException("Invalid IRQ receipt device");
                }
                if (selected == null || string.Equals(item, selected, StringComparison.OrdinalIgnoreCase))
                {
                    IrqDeviceRestoreResult irqDeviceRestoreResult = result.ForDevice(item);
                    if (irqDeviceRestoreResult == null)
                    {
                        IrqDeviceRestoreResult irqDeviceRestoreResult2 = new IrqDeviceRestoreResult();
                        irqDeviceRestoreResult2.DeviceId = item;
                        irqDeviceRestoreResult = irqDeviceRestoreResult2;
                        result.Devices.Add(irqDeviceRestoreResult);
                    }
                    if (affinity)
                    {
                        irqDeviceRestoreResult.AffinityRequested = true;
                    }
                    else
                    {
                        irqDeviceRestoreResult.PriorityRequested = true;
                    }
                }
            }
        }
    }
}

// @author bdth 2074055628@qq.com
// File purpose Isolate the system boundary of interrupt observation so lifecycle regressions need neither ETW nor touching the game
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal interface IIrqSessionCapture
    {
        bool Busy { get; }
        string FailDetail { get; }
        bool Start();
        InterruptAttributionResult Stop();
        List<InterruptAttribution.DpcTimelineEntry> DpcTimeline { get; }
        bool DpcTimelineTruncated { get; }
    }

    internal interface IIrqSessionPlatform
    {
        bool Enabled { get; }
        bool IsElevated { get; }
        long UtcTicks { get; }
        string BootStamp { get; }
        string TopologyStamp { get; }
        IIrqSessionCapture CreateCapture(bool captureTimeline);
        ICoreLoadSource OpenCoreLoadSource();
        bool Append(IrqSessionRecord record);
        Func<string, string> DriverVersionReader();
        void Log(string message);
        string LoadLastResult();
        void SaveLastResult(string result);
    }

    internal interface IIrqDeviceSnapshotPlatform
    { Dictionary<string,string> DeviceConfigurations(); }

    internal sealed class WindowsIrqSessionPlatform : IIrqSessionPlatform, IIrqDeviceSnapshotPlatform
    {
        public Dictionary<string,string> DeviceConfigurations()
        {
            var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in IrqDeviceInventory.Enumerate())
                if (d.ConfigurationKnown) result[d.InstanceId] = IrqAdjustmentLedger.DeviceConfiguration(d);
            return result;
        }
        private const string LastResultKey = "IrqLastCaptureResultV1";
        public bool Enabled { get { return IrqSessionProbe.EnabledSetting; } }
        public bool IsElevated { get { return Native.IsElevated(); } }
        public long UtcTicks { get { return DateTime.UtcNow.Ticks; } }
        public string BootStamp { get { return IrqAffinityEngine.BootStamp(); } }
        public string TopologyStamp { get { return CpuTopology.TopologyStamp(); } }
        public IIrqSessionCapture CreateCapture(bool captureTimeline) { return new Capture(captureTimeline); }
        public ICoreLoadSource OpenCoreLoadSource() { return CoreLoadProbe.OpenSource(); }
        public bool Append(IrqSessionRecord record) { return IrqSessionLedger.Append(record); }
        public Func<string, string> DriverVersionReader() { return IrqSessionProbe.CreateDriverVersionReader(); }
        public void Log(string message) { Logger.Log(message); }
        public string LoadLastResult() { return Settings.LoadStr(LastResultKey, ""); }
        public void SaveLastResult(string result) { Settings.SaveStr(LastResultKey, result); }

        private sealed class Capture : IIrqSessionCapture
        {
            private readonly InterruptAttribution probe = new InterruptAttribution();
            public Capture(bool captureTimeline)
            {
                // Match observation uses DPC evidence only and the ledger does not store ISR; subscribing to it wastes half the event volume
                probe.EnableDpcOnly();
                if (captureTimeline) probe.EnableDpcTimeline();
            }
            public bool Busy { get { return probe.Busy; } }
            public string FailDetail { get { return probe.FailDetail; } }
            public bool Start() { return probe.Start(); }
            public InterruptAttributionResult Stop() { return probe.Stop(); }
            public List<InterruptAttribution.DpcTimelineEntry> DpcTimeline
            { get { return probe.DpcTimeline; } }
            public bool DpcTimelineTruncated { get { return probe.DpcTimelineTruncated; } }
        }
    }
}

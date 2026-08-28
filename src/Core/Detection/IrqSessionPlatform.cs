// @author bdth 2074055628@qq.com
// 文件用途 隔离中断观测的系统边界，让生命周期回归不需要启动 ETW 或改动游戏。
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
        string DriverVersion(string name);
        void Log(string message);
        string LoadLastResult();
        void SaveLastResult(string result);
    }

    internal sealed class WindowsIrqSessionPlatform : IIrqSessionPlatform
    {
        private const string LastResultKey = "IrqLastCaptureResultV1";
        public bool Enabled { get { return IrqSessionProbe.EnabledSetting; } }
        public bool IsElevated { get { return Native.IsElevated(); } }
        public long UtcTicks { get { return DateTime.UtcNow.Ticks; } }
        public string BootStamp { get { return IrqAffinityEngine.BootStamp(); } }
        public string TopologyStamp { get { return CpuTopology.TopologyStamp(); } }
        public IIrqSessionCapture CreateCapture(bool captureTimeline) { return new Capture(captureTimeline); }
        public ICoreLoadSource OpenCoreLoadSource() { return CoreLoadProbe.OpenSource(); }
        public bool Append(IrqSessionRecord record) { return IrqSessionLedger.Append(record); }
        public string DriverVersion(string name) { return IrqSessionProbe.DriverVersionOf(name); }
        public void Log(string message) { Logger.Log(message); }
        public string LoadLastResult() { return Settings.LoadStr(LastResultKey, ""); }
        public void SaveLastResult(string result) { Settings.SaveStr(LastResultKey, result); }

        private sealed class Capture : IIrqSessionCapture
        {
            private readonly InterruptAttribution probe = new InterruptAttribution();
            public Capture(bool captureTimeline)
            {
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

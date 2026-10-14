using System;

namespace PaviseApp
{
    internal sealed class MonitoringGpuRouteBackend
    {
        internal Func<MonitoringGpuAdapter[]> Enumerate;
        internal Func<int, bool> Open;
        internal Func<int, RenderAdapter> Sample;
        internal Action Close;
    }

    internal sealed class MonitoringGpuRouting : IDisposable
    {
        internal const int StableIntervalMs = 5000, FailureIntervalMs = 30000, InventoryIntervalMs = 30000;
        private readonly MonitoringGpuRouteBackend backend;
        private MonitoringGpuAdapter[] adapters;
        private int pid;
        private long creation, nextSample, nextInventory, lastTime;
        private bool open, hasClock;
        internal string SensorName { get; private set; }
        internal string AdapterName { get; private set; }
        internal string State { get; private set; }
        internal long? AdapterLuid { get; private set; }
        internal long Version { get; private set; }

        internal MonitoringGpuRouting() : this(NativeBackend()) { }
        internal MonitoringGpuRouting(MonitoringGpuRouteBackend backend)
        { this.backend = backend; Set(null, "waiting"); }
        private static MonitoringGpuRouteBackend NativeBackend()
        {
            var sampler = new GpuEvidence.GpuEngineSampler();
            return new MonitoringGpuRouteBackend { Enumerate = MonitoringGpuAdapters.Enumerate,
                Open = sampler.Open, Sample = delegate(int target) { return sampler.Resolve(target, true); }, Close = sampler.Close };
        }
        internal void Update(int targetPid, long targetCreation, long? suppliedLuid, long now)
        {
            if (targetPid != pid || targetCreation != creation || hasClock && now < lastTime)
            { Reset(); pid = targetPid; creation = targetCreation; }
            hasClock = true; lastTime = now;
            if (pid <= 4 || creation <= 0) { Reset(); return; }
            if (adapters == null || now >= nextInventory)
            {
                try { adapters = backend.Enumerate() ?? new MonitoringGpuAdapter[0]; }
                catch { adapters = new MonitoringGpuAdapter[0]; }
                nextInventory = now + InventoryIntervalMs;
                if (AdapterLuid.HasValue) Select(AdapterLuid.Value);
            }
            if (suppliedLuid.HasValue) { CloseQuery(); Select(suppliedLuid.Value); return; }
            if (adapters.Length == 1) { CloseQuery(); Set(adapters[0], "ready"); return; }
            if (adapters.Length == 0) { CloseQuery(); Set(null, "inventory-unavailable"); return; }
            if (now < nextSample) return;
            if (!open)
            {
                try { open = backend.Open(pid); } catch { open = false; }
                nextSample = now + (open ? 1000 : FailureIntervalMs);
                Set(null, open ? "detecting" : "counter-unavailable");
                return;
            }
            RenderAdapter render;
            try { render = backend.Sample(pid); } catch { render = null; }
            nextSample = now + StableIntervalMs;
            if (render == null) { Set(null, "detecting"); return; }
            if (render.Ambiguous) { Set(null, "ambiguous"); return; }
            Select(((long)render.LuidHigh << 32) | render.LuidLow);
        }
        private void Select(long luid)
        {
            MonitoringGpuAdapter found = null;
            foreach (var adapter in adapters)
                if (adapter != null && adapter.Luid == luid)
                { if (found != null) { Set(null, "ambiguous"); return; } found = adapter; }
            if (found == null) { Set(null, "adapter-unavailable"); return; }
            foreach (var adapter in adapters)
                if (adapter != null && adapter.Luid != luid && String.Equals(adapter.Name, found.Name, StringComparison.OrdinalIgnoreCase))
                { Set(found, "name-ambiguous"); return; }
            Set(found, "ready");
        }
        private void Set(MonitoringGpuAdapter adapter, string state)
        {
            string name = adapter == null ? "" : adapter.Name;
            long? luid = adapter == null ? (long?)null : adapter.Luid;
            if (AdapterLuid != luid || State != state || AdapterName != name) Version++;
            AdapterLuid = luid; AdapterName = name; State = state;
            // 空名称表示未确认，不取第一张显卡兜底。
            SensorName = state == "ready" ? name : "";
        }
        private void CloseQuery() { if (open) { try { backend.Close(); } catch { } open = false; } }
        internal void Reset()
        {
            CloseQuery(); adapters = null; pid = 0; creation = 0;
            nextSample = nextInventory = lastTime = 0; hasClock = false; Set(null, "waiting");
        }
        public void Dispose() { Reset(); }
    }
}

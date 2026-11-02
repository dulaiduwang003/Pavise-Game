using System;
using System.Threading;

namespace PaviseApp
{
    internal sealed class ConfigurationPresetRuntime
    {
        internal const string IdentityKey = "ConfigurationPresetIdentityV1";
        private static int globalBusy;
        private readonly ConfigurationPresetCoordinator coordinator;
        private readonly Func<int> readGeneration;
        private readonly Func<Func<bool>, bool> run;
        private int epoch, refreshing, observedGeneration = -1, busy;
        private ConfigurationPresetKind? current;
        private readonly object stateGate = new object();

        internal ConfigurationPresetRuntime(GameMode gameMode)
            : this(new ConfigurationPresetBackend(gameMode), () => Settings.MutationGeneration,
                work => gameMode != null && gameMode.RunLaboratoryManualWork(work)) { }
        internal ConfigurationPresetRuntime(IConfigurationPresetBackend backend, Func<int> generation, Func<Func<bool>, bool> runWork)
        { coordinator = new ConfigurationPresetCoordinator(backend); readGeneration = generation ?? (() => 0); run = runWork ?? (work => work()); }
        internal bool Busy { get { return Volatile.Read(ref globalBusy) != 0; } }
        internal ConfigurationPresetKind? CurrentPreset
        {
            get
            {
                RefreshIdentity();
                lock (stateGate) return current;
            }
        }
        private void RefreshIdentity()
        {
            int generation = readGeneration();
            if (Busy || generation == Volatile.Read(ref observedGeneration) || Interlocked.CompareExchange(ref refreshing, 1, 0) != 0) return;
            try
            {
                if (!ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        ConfigurationPresetKind? value = coordinator.CurrentPreset();
                        if (generation == readGeneration() && !Busy)
                        { lock (stateGate) current = value; Volatile.Write(ref observedGeneration, generation); }
                    }
                    catch { lock (stateGate) current = null; }
                    finally { Volatile.Write(ref refreshing, 0); }
                })) Volatile.Write(ref refreshing, 0);
            }
            catch { Volatile.Write(ref refreshing, 0); }
        }
        internal void Cancel() { Interlocked.Increment(ref epoch); }
        internal bool WaitForCompletion(int timeoutMilliseconds)
        {
            int start = Environment.TickCount;
            while (Volatile.Read(ref busy) != 0)
            { if (unchecked(Environment.TickCount - start) >= timeoutMilliseconds) return false; Thread.Sleep(10); }
            return true;
        }
        internal bool TryApply(ConfigurationPresetKind preset, Func<bool> cancelled, Action<ConfigurationPresetResult> completed)
        {
            if (preset != ConfigurationPresetKind.Default && preset != ConfigurationPresetKind.Extreme) return false;
            if (Interlocked.CompareExchange(ref globalBusy, 1, 0) != 0) return false;
            Volatile.Write(ref busy, 1); int captured = Volatile.Read(ref epoch);
            Func<bool> revoked = delegate { return captured != Volatile.Read(ref epoch) || (cancelled != null && cancelled()); };
            try
            {
                if (revoked()) { Volatile.Write(ref busy, 0); Volatile.Write(ref globalBusy, 0); return false; }
                if (ThreadPool.QueueUserWorkItem(delegate
                {
                    ConfigurationPresetResult result = null;
                    try
                    {
                        bool admitted = run(delegate
                        {
                            if (revoked()) return false;
                            result = coordinator.Apply(preset, revoked);
                            return true;
                        });
                        if (!admitted || result == null)
                        {
                            result = new ConfigurationPresetResult { Preset = preset, Cancelled = revoked(), Failed = 1 };
                            result.Items.Add(new ConfigurationPresetItemResult { Title = ConfigurationPresetCatalog.Name(preset), Status = ConfigurationPresetItemStatus.Failed,
                                Detail = PerformancePolicy.Text("对局、退出或其他配置工作正在进行，未应用预设", "A match, shutdown or configuration operation is active; preset not applied") });
                        }
                        if (revoked()) result.Cancelled = true;
                        lock (stateGate) current = result.Failed == 0 && !result.Cancelled && result.Identity != null ? (ConfigurationPresetKind?)preset : null;
                    }
                    catch (Exception ex)
                    {
                        result = new ConfigurationPresetResult { Preset = preset, Failed = 1, Cancelled = revoked() };
                        result.Items.Add(new ConfigurationPresetItemResult { Title = ConfigurationPresetCatalog.Name(preset), Status = ConfigurationPresetItemStatus.Failed, Detail = ex.Message });
                        lock (stateGate) current = null;
                    }
                    finally
                    {
                        Volatile.Write(ref observedGeneration, -1);
                        // Existing subtransactions finish before busy is released; cancellation never abandons an in-flight system write.
                        Volatile.Write(ref busy, 0); Volatile.Write(ref globalBusy, 0);
                    }
                    if (completed != null) try { completed(result); } catch { }
                })) return true;
            }
            catch { }
            Volatile.Write(ref busy, 0); Volatile.Write(ref globalBusy, 0); return false;
        }
        // AI export reads saved settings only and never probes GPU / hardware capabilities.
        internal static ConfigurationPresetKind? CurrentFromSettings()
        { try { return new ConfigurationPresetCoordinator(new ConfigurationPresetBackend(null)).CurrentPreset(); } catch { return null; } }
    }
}

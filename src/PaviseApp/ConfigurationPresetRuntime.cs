using System;
using System.Collections.Generic;
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
        private readonly Func<long> readSession;
        private readonly Func<WaitCallback, bool> queueIdentity;
        private readonly Func<string> readPhaseSnapshot;
        private int epoch, identityEpoch, refreshing, observedGeneration = -1, busy;
        private ConfigurationPresetKind? current;
        private string currentConfiguration;
        private readonly object stateGate = new object();
        private sealed class IdentityRefreshPhase
        {
            internal long Session;
            internal string Configuration;
            internal int Failures;
        }
        private IdentityRefreshPhase identityPhase = new IdentityRefreshPhase { Session = -1 };
        private readonly Dictionary<string, IdentityRefreshPhase> identityPhases = new Dictionary<string, IdentityRefreshPhase>(StringComparer.Ordinal);
        private long identitySession = -1;
        private int phaseGeneration = -1, phaseReadFailures, generationReadFailures;
        private string savedConfiguration;

        internal ConfigurationPresetRuntime(GameMode gameMode)
            : this(new ConfigurationPresetBackend(gameMode), () => Settings.MutationGeneration,
                work => gameMode != null && gameMode.RunLaboratoryManualWork(work), null, null, ConfigurationPresetBackend.SavedIdentityPhase) { }
        internal ConfigurationPresetRuntime(IConfigurationPresetBackend backend, Func<int> generation, Func<Func<bool>, bool> runWork,
            Func<long> session = null, Func<WaitCallback, bool> queueRefresh = null, Func<string> phaseSnapshot = null)
        {
            coordinator = new ConfigurationPresetCoordinator(backend); readGeneration = generation ?? (() => 0); run = runWork ?? (work => work());
            readSession = session ?? (() => PerformanceTuning.RecoveryCycleSerial);
            queueIdentity = queueRefresh ?? (callback => ThreadPool.QueueUserWorkItem(callback));
            readPhaseSnapshot = phaseSnapshot ?? ConfigurationPresetBackend.SavedIdentityPhase;
        }
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
            if (Busy || Interlocked.CompareExchange(ref refreshing, 1, 0) != 0) return;
            int observationEpoch = Volatile.Read(ref identityEpoch);
            if (Busy) { Volatile.Write(ref refreshing, 0); return; }
            IdentityRefreshPhase phase;
            long session = readSession();
            lock (stateGate)
            {
                if (session < identitySession) { Volatile.Write(ref refreshing, 0); return; }
                if (session > identitySession)
                {
                    identitySession = session; identityPhases.Clear();
                    phaseGeneration = -1; savedConfiguration = null;
                    phaseReadFailures = generationReadFailures = 0;
                    Volatile.Write(ref observedGeneration, -1);
                }
                if (phaseReadFailures >= 3 || generationReadFailures >= 3) { Volatile.Write(ref refreshing, 0); return; }
            }
            int generation;
            try { generation = readGeneration(); generationReadFailures = 0; }
            catch { generationReadFailures++; Volatile.Write(ref refreshing, 0); return; }
            if (savedConfiguration == null || generation != phaseGeneration)
            {
                try
                {
                    // MutationGeneration invalidates this cheap cache, but is never a phase identity.
                    string configuration = readPhaseSnapshot() ?? "?";
                    lock (stateGate)
                    {
                        if (!IdentityObservationCurrent(session, generation, observationEpoch)) { Volatile.Write(ref refreshing, 0); return; }
                        savedConfiguration = configuration;
                        phaseGeneration = generation; phaseReadFailures = 0;
                    }
                }
                catch
                {
                    phaseReadFailures++;
                    lock (stateGate) if (IdentityObservationCurrent(session, generation, observationEpoch)) current = null;
                    Logger.Log("Configuration preset phase snapshot: session=" + session + " attempt=" + phaseReadFailures + "/3; read failed");
                    Volatile.Write(ref refreshing, 0); return;
                }
            }
            lock (stateGate)
            {
                if (!IdentityObservationCurrent(session, generation, observationEpoch)) { Volatile.Write(ref refreshing, 0); return; }
                // Cached identity must name the verified material configuration, including after phase exhaustion.
                if (currentConfiguration != savedConfiguration) { current = null; currentConfiguration = null; }
                if (!identityPhases.TryGetValue(savedConfiguration, out phase))
                    identityPhases[savedConfiguration] = phase = new IdentityRefreshPhase { Session = session, Configuration = savedConfiguration };
                identityPhase = phase;
                if (phase.Failures >= 3) { Volatile.Write(ref refreshing, 0); return; }
            }
            try
            {
                if (generation == Volatile.Read(ref observedGeneration)) { Volatile.Write(ref refreshing, 0); return; }
                if (!queueIdentity(delegate
                {
                    try
                    {
                        ConfigurationPresetKind? value = coordinator.CurrentPreset();
                        lock (stateGate)
                        {
                            if (object.ReferenceEquals(phase, identityPhase) && IdentityObservationCurrent(session, generation, observationEpoch))
                            {
                                current = value;
                                currentConfiguration = phase.Configuration;
                                phase.Failures = 0; // A complete negative observation is healthy too.
                                Volatile.Write(ref observedGeneration, generation);
                            }
                        }
                    }
                    catch { IdentityRefreshFailed(phase, generation, observationEpoch); }
                    finally { Volatile.Write(ref refreshing, 0); }
                })) { IdentityRefreshFailed(phase, generation, observationEpoch); Volatile.Write(ref refreshing, 0); }
            }
            catch { IdentityRefreshFailed(phase, generation, observationEpoch); Volatile.Write(ref refreshing, 0); }
        }
        private bool IdentityObservationCurrent(long session, int generation, int observationEpoch)
        {
            try { return !Busy && observationEpoch == Volatile.Read(ref identityEpoch) && session == readSession() && generation == readGeneration(); }
            catch { return false; }
        }
        private void IdentityRefreshFailed(IdentityRefreshPhase phase, int generation, int observationEpoch)
        {
            lock (stateGate)
            {
                phase.Failures = Math.Min(3, phase.Failures + 1);
                if (object.ReferenceEquals(phase, identityPhase) && IdentityObservationCurrent(phase.Session, generation, observationEpoch)) current = null;
                Logger.Log("Configuration preset identity: session=" + phase.Session + " attempt=" + phase.Failures + "/3; refresh failed");
            }
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
            Interlocked.Increment(ref identityEpoch);
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
                        bool verified = result.Failed == 0 && !result.Cancelled && result.Identity != null;
                        string configuration = verified ? (readPhaseSnapshot() ?? "?") : null;
                        lock (stateGate)
                        {
                            current = verified ? (ConfigurationPresetKind?)preset : null;
                            currentConfiguration = configuration;
                            // Invalidate the cheap snapshot, not any failure budget.
                            savedConfiguration = null; phaseGeneration = -1;
                        }
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

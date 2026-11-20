using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    // 核心隔离引擎在本进程运行 每两秒核验一次 核验失败立即恢复
    // 恢复未结清时引擎和句柄留在客户端 下次 Stop 在同一份有限预算内继续
    internal sealed class CoreIsolationClient
    {
        private readonly object gate = new object();

        private CoreIsolationEngine engine;
        private string applyScope;
        private static readonly object phaseGate = new object();
        private static readonly HashSet<string> failedScopes = new HashSet<string>(StringComparer.Ordinal);
        private static long automaticSession;
        internal static void SyncAutomaticSession(long session)
        {
            lock (phaseGate)
            {
                if (session <= automaticSession) return;
                automaticSession = session; failedScopes.Clear();
            }
            CoreIsolationRecovery.SyncAutomaticSession(session);
        }

        private Timer heartbeat;

        private bool active;

        private bool failed;

        private ICoreIsolationStore journal = new CoreIsolationSettingsStore();

        internal static volatile string State = "schedule.isolation.idle";

        internal static ulong ActiveMask;

        internal string LastError { get; private set; }

        internal bool Ensure(ulong isolation, int pid, long creation, ulong game, bool manual = false)
        {
            lock (gate)
            {
                lock (phaseGate)
                {
                    applyScope = automaticSession + "|" + pid + "|" + creation + "|" + isolation + "|" + game + "|" + manual;
                    if (failed || failedScopes.Contains(applyScope)) return false;
                }
                if (engine != null)
                {
                    return (engine.Allow(pid, creation, game) && engine.Audit()) || Break(engine.Error);
                }
                try
                {
                    // 先结清此前留下的回执 再以新的所有权开始隔离
                    if (!CoreIsolationRecovery.Recover(journal)) return Break("recovery-pending");
                    if (!Native.EnsureBoostPrivilege()) return Break("privilege");
                    engine = CoreIsolationRecovery.Lease(journal);
                    if (!engine.Begin(isolation, pid, creation, game, manual)) return Break(engine.Error);
                    active = true;
                    ActiveMask = isolation;
                    State = "schedule.isolation.active";
                    heartbeat = new Timer(delegate { Check(); }, null, 2000, 2000);
                    return true;
                }
                catch (Exception ex)
                {
                    return Break(ex.GetType().Name);
                }
            }
        }

        private void Check()
        {
            if (!Monitor.TryEnter(gate))
            {
                return;
            }
            try
            {
                if (active && !engine.Audit())
                {
                    Break(engine.Error);
                }
            }
            finally
            {
                Monitor.Exit(gate);
            }
        }

        internal bool Drop(int pid, long creation)
        {
            lock (gate)
            {
                if (!active)
                {
                    return !failed;
                }
                return engine.Drop(pid, creation) || Break(engine.Error);
            }
        }

        private bool Break(string reason)
        {
            LastError = reason;
            failed = true;
            lock (phaseGate) { if (applyScope != null) failedScopes.Add(applyScope); }
            active = false;
            bool closed = Close();
            if (closed) ActiveMask = 0uL;
            State = (closed ? "schedule.isolation.failed" : "schedule.isolation.pending");
            Logger.Warn(Lang.T(State) + " (" + reason + ")");
            return false;
        }

        private bool Close()
        {
            if (heartbeat != null) { heartbeat.Dispose(); heartbeat = null; }
            if (engine == null) return true;
            try { engine.Restore(); }
            catch (Exception ex) { LastError = ex.GetType().Name; }
            if (engine.Pending) return false;
            CoreIsolationRecovery.Return(engine);
            engine = null;
            return true;
        }

        internal bool Stop()
        {
            lock (gate)
            {
                active = false;
                bool closed = Close();
                if (closed)
                {
                    failed = false;
                    ActiveMask = 0uL;
                    State = "schedule.isolation.idle";
                }
                else
                {
                    failed = true;
                    State = "schedule.isolation.pending";
                }
                return closed;
            }
        }
    }
}

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // 核心隔离引擎在 Pavise 进程内运行 崩溃后隔离保留 下次启动按回执结清
    // 客户端持有隔离期间回执归它的引擎 其他入口的恢复只能推迟
    // 同一份回执只由一个引擎按有限预算恢复 反复调用沿用同一个引擎 不重新计数
    internal static class CoreIsolationRecovery
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<object, CoreIsolationEngine> owners = new Dictionary<object, CoreIsolationEngine>();
        private static readonly HashSet<CoreIsolationEngine> leased = new HashSet<CoreIsolationEngine>();
        private static long automaticSession;

        internal static void SyncAutomaticSession(long session)
        {
            lock (gate)
            {
                if (session <= automaticSession) return;
                automaticSession = session;
                foreach (CoreIsolationEngine owner in owners.Values) owner.SyncAutomaticSession(session);
                foreach (CoreIsolationEngine engine in leased) engine.SyncAutomaticSession(session);
            }
        }

        private static CoreIsolationEngine CreateEngine(ICoreIsolationStore journal)
        { return new CoreIsolationEngine(new CoreIsolationNative(), journal); }

        // 客户端开始隔离前取用 恢复结清后交回
        internal static CoreIsolationEngine Lease(ICoreIsolationStore journal)
        {
            lock (gate)
            {
                CoreIsolationEngine engine = CreateEngine(journal);
                engine.SyncAutomaticSession(automaticSession);
                leased.Add(engine);
                return engine;
            }
        }

        internal static void Return(CoreIsolationEngine engine)
        {
            lock (gate)
            {
                engine.Dispose();
                leased.Remove(engine);
            }
        }

        internal static bool Recover(ICoreIsolationStore journal)
        {
            lock (gate)
            {
                if (leased.Count > 0) { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1)); return false; }
                object key = journal is CoreIsolationSettingsStore ? (object)typeof(CoreIsolationSettingsStore) : journal;
                CoreIsolationEngine engine;
                if (!owners.TryGetValue(key, out engine))
                {
                    engine = CreateEngine(journal);
                    engine.SyncAutomaticSession(automaticSession);
                    owners[key] = engine;
                }
                if (!engine.RecoveryDue) return false;
                try
                {
                    if (!engine.Recover()) return false;
                    engine.Dispose();
                    owners.Remove(key);
                    return true;
                }
                catch { return false; }
            }
        }
    }
}

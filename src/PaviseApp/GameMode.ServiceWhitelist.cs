using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private sealed class ServiceWhitelistRuntime
        {
            internal readonly ServiceWhitelistObservation Observation;
            internal readonly long Epoch;
            internal ServiceWhitelistRuntime(ServiceWhitelistObservation observation, long epoch) { Observation = observation; Epoch = epoch; }
        }
        private readonly object serviceWhitelistGate = new object();
        private readonly ServiceWhitelistObserver serviceWhitelistObserver = new ServiceWhitelistObserver();
        private ServiceWhitelistRuntime serviceWhitelistRuntime;
        private long serviceWhitelistEpoch, serviceWhitelistCycle;
        private long whiteEvalServiceVersion = -1;
        private int serviceWhitelistRuleCount;

        public bool AddWhitelistService(string serviceName) { return AddWhitelistRule(WhitelistRuleKind.Service, serviceName); }

        private void RequestServiceWhitelistApply()
        {
            // 服务规则只唤醒普通后台轮询；共享的 firstSweep 会提前微软后台扫描，不能在此改动。
            Interlocked.Exchange(ref urgentProcessScan, 1);
            Interlocked.Exchange(ref processSetDirty, 1);
            try { kick.Set(); } catch (ObjectDisposedException) { }
        }

        // 此入口也用于初始加载；只撤销普通后台资格，不触及微软后台、ACE 或其预算。
        private void InvalidateServiceWhitelistRules()
        {
            int count = 0;
            foreach (WhitelistRule rule in whiteRules) if (rule.Kind == WhitelistRuleKind.Service) count++;
            Volatile.Write(ref serviceWhitelistRuleCount, count);
            Interlocked.Increment(ref serviceWhitelistEpoch);
            Volatile.Write(ref serviceWhitelistRuntime, null);
        }

        private ServiceWhitelistObservation CurrentServiceWhitelistObservation()
        {
            ServiceWhitelistRuntime current = Volatile.Read(ref serviceWhitelistRuntime);
            return current != null && current.Epoch == Interlocked.Read(ref serviceWhitelistEpoch)
                && current.Observation.Revision == Volatile.Read(ref whiteRevision)
                && current.Observation.Session == Interlocked.Read(ref gameSessionStarts) ? current.Observation : null;
        }

        private bool ServiceWhitelistForwardAllowed()
        {
            if (Volatile.Read(ref serviceWhitelistRuleCount) == 0) return true;
            ServiceWhitelistObservation current = CurrentServiceWhitelistObservation();
            return current != null && current.Known;
        }

        private long ServiceWhitelistBindingVersion()
        {
            if (Volatile.Read(ref serviceWhitelistRuleCount) == 0) return 0;
            ServiceWhitelistObservation current = CurrentServiceWhitelistObservation();
            return current == null ? -1 : current.Version;
        }

        private void BeginServiceWhitelistCycle()
        {
            if (Volatile.Read(ref serviceWhitelistRuleCount) == 0) return;
            if (!EffSuppress || SmartSceneSwitchedOut || !enabled || stopping || panicReq || ProfileStoreSaveFailed) return;
            var names = new List<string>();
            int revision;
            lock (sync)
            {
                revision = whiteRevision;
                foreach (WhitelistRule rule in whiteRules) if (rule.Kind == WhitelistRuleKind.Service) names.Add(rule.Value);
            }
            long serial = Interlocked.Read(ref gameSessionStarts), epoch;
            lock (serviceWhitelistGate)
            {
                epoch = Interlocked.Increment(ref serviceWhitelistEpoch);
                Volatile.Write(ref serviceWhitelistRuntime, null);
            }
            Func<bool> canceled = () => stopping || panicReq || revision != Volatile.Read(ref whiteRevision)
                || !enabled || ProfileStoreSaveFailed || !EffSuppress || SmartSceneSwitchedOut
                || serial != Interlocked.Read(ref gameSessionStarts) || epoch != Interlocked.Read(ref serviceWhitelistEpoch);
            ServiceWhitelistObservation result = serviceWhitelistObserver.Observe(serial, Interlocked.Increment(ref serviceWhitelistCycle),
                revision, names, ServiceCatalog.ReadHosts, canceled, () => AutoCores.NowTicks);
            if (canceled()) return;
            lock (whiteEvalSync)
            {
                lock (serviceWhitelistGate)
                {
                    if (canceled()) return;
                    Volatile.Write(ref serviceWhitelistRuntime, new ServiceWhitelistRuntime(result, epoch));
                    foreach (string failure in result.Failures) Logger.Warn("服务白名单读取未完成：" + failure);
                    if (!result.Known) RestoreOrdinaryServiceWhitelistUnknown();
                    else foreach (KeyValuePair<int, long> host in result.Hosts) ReleaseOrdinaryServiceWhitelistHost(host.Key, host.Value);
                }
            }
        }

        private void ReleaseOrdinaryServiceWhitelistHost(int pid, long birth)
        {
            if (birth <= 0 || core.CreationOf(pid) != birth) return;
            string name = core.NameOf(pid);
            if (string.IsNullOrEmpty(name)) return;
            BackgroundReleaseState state = core.ReleaseBackgroundForRenderer(pid, birth, name);
            if (RuntimeBackgroundExempt(state)) ReportUntrack(pid);
        }

        private void RestoreOrdinaryServiceWhitelistUnknown()
        {
            // 服务位置未知时只撤回已经持有的普通后台身份；恢复失败仍由原账本、句柄及次数上限收尾。
            foreach (int pid in core.PidsWith(SuppressReason.Background))
            {
                long birth = core.CreationOf(pid);
                if (birth > 0) ReleaseOrdinaryServiceWhitelistHost(pid, birth);
            }
        }

        private bool ServiceWhitelistBlocksOrdinary(int pid)
        {
            ServiceWhitelistObservation current = CurrentServiceWhitelistObservation();
            long birth;
            if (current == null || !current.Known || !current.TryHost(pid, out birth)) return false;
            // PID 只用于拒绝新的普通限制；解除既有字段时还必须与本方持有的创建时间一致。
            ReleaseOrdinaryServiceWhitelistHost(pid, birth);
            return true;
        }

        private void EvaluateServiceWhitelistRule(WhitelistEvaluation evaluation, WhitelistRule rule)
        {
            var matched = new HashSet<int>();
            ServiceWhitelistObservation current = CurrentServiceWhitelistObservation();
            ServiceWhitelistHost host = current == null ? null : current.Find(rule.Value);
            if (host != null && host.HasHost) matched.Add(host.ProcessId);
            evaluation.ByRule[rule.Key] = matched;
            evaluation.Protected.UnionWith(matched);
        }
    }
}

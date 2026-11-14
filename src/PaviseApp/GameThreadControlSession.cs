using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class GameThreadControlSession : IDisposable
    {
        private readonly object sync = new object();
        private readonly IGameThreadControlNative native;
        private readonly IGameThreadControlStore store;
        private readonly IGameThreadControlAuthority authority;
        private readonly Func<long> clock;
        private readonly GameThreadDiagnosticLog diagnosticLog;
        private readonly string authorizedRule;
        private GameThreadControlJournal journal;
        private long nextTick;
        private int generation, stopping, ticking;
        private bool storageFault, disposed, inNative;
        private bool handleFault;
        private string detail = "等待角色准入";

        internal GameThreadControlSession(IGameThreadControlNative native, IGameThreadControlStore store,
            IGameThreadControlAuthority authority, Func<long> clock, Action<string> log, CriticalThreadRule rule = null)
        {
            if (native == null || store == null || authority == null || clock == null) throw new ArgumentNullException("统一线程控制边界");
            this.native = native; this.store = store; this.authority = authority; this.clock = clock;
            journal = store.Load(); stopping = journal.Stopping ? 1 : 0;
            diagnosticLog = new GameThreadDiagnosticLog(log == null ? null : (Action<string>)(text => log("统一线程控制：进程编号=" + journal.Target.ProcessId + "；" + text)));
            if (rule != null && !rule.IsTimeCriticalRender) throw new ArgumentException("时间关键授权必须来自对应的原始规则");
            authorizedRule = rule == null ? "" : rule.Encode();
            if (journal.Revision == 0 && journal.Rows.Count == 0 && string.IsNullOrEmpty(journal.Rule)) journal.Rule = authorizedRule;
        }

        internal static long NowMilliseconds()
        { long ticks = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency; return ticks / frequency * 1000 + ticks % frequency * 1000 / frequency; }

        internal bool HasDebt { get { lock (sync) return storageFault || handleFault || GameThreadControlPolicy.Pending(journal); } }
        internal bool Stopped { get { return Volatile.Read(ref stopping) != 0; } }
        internal string Detail { get { lock (sync) return CapabilityDetail(); } }
        private string CapabilityDetail()
        {
            string result = detail;
            if (journal.ProcessObservedKnown && journal.ObservedProcessClass == 0x100)
                result += "；当前 Realtime 类别不新增线程提优，仅按归属撤回本方增量；理想核和亲和性独立核验";
            foreach (GameThreadControlRow row in journal.Rows)
                if (!row.Priority.Managed && !string.IsNullOrEmpty(row.Priority.Detail)
                    && result.IndexOf(row.Priority.Detail, StringComparison.Ordinal) < 0)
                    result += "；" + row.Priority.Detail;
            return result;
        }
        internal GameThreadControlJournal Snapshot { get { lock (sync) return journal.Clone(); } }
        internal int ManagedFieldCount
        {
            get { lock (sync) { int count = 0; foreach (GameThreadControlRow row in journal.Rows) foreach (GameThreadControlField field in new[] { row.Priority, row.Ideal }) if (field.Managed && !field.Terminal) count++; return count; } }
        }
        internal bool AllVerified { get { lock (sync) return CompleteVerification(clock()); } }
        private bool CompleteVerification(long now)
        {
            if (Stopped || storageFault || !journal.ProcessObservedKnown || now - journal.ProcessObservedMilliseconds < 0 || now - journal.ProcessObservedMilliseconds > 4000) return false;
            int managed = 0;
            foreach (GameThreadControlRow row in journal.Rows)
                foreach (GameThreadControlField field in new[] { row.Priority, row.Ideal })
                    if (field.Managed && !field.Terminal)
                    {
                        managed++;
                        if (!field.InitialAdmitted || !field.AppliedKnown || !field.PolicyVerified || !field.ObservedKnown || field.Observed != field.Desired
                            || object.ReferenceEquals(field, row.Priority) && field.DesiredProcessClass != journal.ObservedProcessClass
                            || now - field.LastObservedMilliseconds < 0 || now - field.LastObservedMilliseconds > 4000) return false;
                    }
            return managed != 0;
        }

        private void InvalidateObservations()
        {
            journal.ProcessObservedKnown = false;
            foreach (GameThreadControlRow row in journal.Rows)
                foreach (GameThreadControlField field in new[] { row.Priority, row.Ideal }) field.ObservedKnown = false;
        }
        internal int VerifiedCount
        {
            get
            {
                lock (sync)
                {
                    if (!journal.ProcessObservedKnown || Stopped) return 0;
                    int count = 0;
                    foreach (GameThreadControlRow row in journal.Rows)
                        foreach (GameThreadControlField field in new[] { row.Priority, row.Ideal })
                            if (field.Managed && !field.Terminal && field.AppliedKnown && field.PolicyVerified && field.ObservedKnown && field.Observed == field.Desired && field.InitialAdmitted
                                && (!object.ReferenceEquals(field, row.Priority) || journal.ProcessObservedKnown && field.DesiredProcessClass == journal.ObservedProcessClass)
                                && clock() - field.LastObservedMilliseconds >= 0 && clock() - field.LastObservedMilliseconds <= 4000) count++;
                    return count;
                }
            }
        }

        private void Save()
        {
            var next = journal.Clone(); next.Revision = checked(journal.Revision + 1);
            try { store.Save(journal.Revision, next); journal.Revision = next.Revision; }
            catch
            {
                storageFault = true;
                foreach (GameThreadControlRow row in journal.Rows) if (StopFailedTimeCriticalControl(row)) break;
                throw;
            }
        }

        private void Trace(string key, string text, long now, string category = "控制状态")
        {
            detail = text;
            diagnosticLog.Record(category, key, text, now);
        }

        private GameThreadControlObserved Read(HardThreadIdentity identity, bool priorityOnly)
        {
            IGameThreadPriorityReader reader = priorityOnly ? native as IGameThreadPriorityReader : null;
            return reader == null ? native.Read(identity) : reader.ReadPriority(identity);
        }

        private GameThreadControlProcess ReadProcessForMaintenance()
        {
            try { return native.ReadProcess(); }
            catch (Exception error)
            {
                if (StopForClosureFailure(error)) throw;
                Trace("process-read-error", "进程身份或类别读取异常；" + error.Message, clock(), "进程读取未确认");
                return null;
            }
        }

        private bool StopForClosureFailure(Exception error)
        {
            if (!HardThreadClosureFailure.Contains(error)) return false;
            if (handleFault) return true;
            handleFault = true;
            RequestStop();
            Trace("handle-close", "线程观察句柄关闭未确认，停止新增写入并保留恢复归属；" + error.Message, clock(), "句柄收尾未确认");
            return true;
        }

        private bool TimeCriticalAuthorized
        {
            get { return journal.Target.AllowedMask != 0 && journal.Rule == authorizedRule && GameThreadControlPolicy.TimeCriticalRule(authorizedRule); }
        }

        private bool TimeCriticalPlacement(HardThreadIdentity identity)
        {
            IGameThreadTimeCriticalAuthority placement = authority as IGameThreadTimeCriticalAuthority;
            return TimeCriticalAuthorized && placement != null && placement.IsPlacementVerified(identity);
        }

        private bool TimeCriticalRoles(GameThreadControlRole[] roles)
        {
            bool requested = Array.Exists(roles, role => role != null && role.PriorityLimit == 15);
            if (!requested) return !GameThreadControlPolicy.TimeCriticalRule(journal.Rule);
            if (!TimeCriticalAuthorized || roles.Length != 1 || roles[0] == null || !roles[0].Priority || roles[0].Ideal.HasValue
                || journal.Rows.Count > 1 || journal.Rows.Count == 1 && journal.Rows[0].Thread.Key != roles[0].Thread.Key) return false;
            return TimeCriticalPlacement(roles[0].Thread);
        }

        internal bool Admit(GameThreadControlRole[] roles)
        {
            using (authority.EnterUse()) lock (sync)
            {
                bool result;
                try { result = AdmitCore(roles); }
                finally { try { ReleaseTransientReads(); } finally { diagnosticLog.Flush(clock(), Stopped); } }
                return result && !handleFault;
            }
        }

        private bool AdmitCore(GameThreadControlRole[] roles)
        {
            if (disposed || Stopped || storageFault || roles == null || roles.Length == 0 || roles.Length > 512) return false;
            if (journal.Target.AllowedMask == 0 && Array.Exists(roles, role => role != null && role.Ideal.HasValue)) return false;
            if (!TimeCriticalRoles(roles)) return false;
            authority.RequireForward();
            GameThreadControlProcess process = native.ReadProcess();
            if (process == null || process.Read != HardThreadRead.Known) return false;
            if (journal.Rows.Count == 0) journal.OriginalProcessClass = process.PriorityClass;
            journal.ObservedProcessClass = process.PriorityClass; journal.ProcessObservedMilliseconds = clock(); journal.ProcessObservedKnown = true;
            var incoming = new HashSet<string>(StringComparer.Ordinal);
            var additions = new List<GameThreadControlRow>();
            var preserved = new List<string>();
            bool idealRequested = false;
            foreach (GameThreadControlRole role in roles)
            {
                if (role == null || !incoming.Add(role.Thread.Key) || role.Thread.Creation < journal.Target.ProcessCreation
                    || journal.Target.AllowedMask == 0 && role.Ideal.HasValue) return false;
                idealRequested |= role.Ideal.HasValue;
                GameThreadControlRow existing = journal.Rows.Find(candidate => candidate.Thread.Key == role.Thread.Key);
                if (existing != null)
                {
                    if (existing.PriorityLimit != role.PriorityLimit) return false;
                    continue;
                }
                GameThreadControlObserved observed = Read(role.Thread, !role.Ideal.HasValue);
                if (observed == null || observed.Read != HardThreadRead.Known
                    || role.Priority && !observed.PriorityKnown && !role.Ideal.HasValue) return false;
                var row = new GameThreadControlRow { Thread = role.Thread, Name = role.Name, PriorityLimit = role.PriorityLimit };
                int target;
                if (role.Priority && observed.PriorityKnown && GameThreadControlPolicy.InitiallyManaged(process.PriorityClass, observed.Priority, row.PriorityLimit)
                    && GameThreadControlPolicy.PriorityTarget(process.PriorityClass, observed.Priority, row.PriorityLimit, out target))
                {
                    Capture(row.Priority, observed.Priority, target, clock());
                    row.Priority.DesiredProcessClass = process.PriorityClass;
                }
                else
                {
                    row.Priority.Detail = !role.Priority ? "不适用：当前角色未请求线程提优"
                        : !observed.PriorityKnown ? "线程优先级原值未知，未接管优先级；理想核独立核验"
                        : process.PriorityClass == 0x100 ? "按策略保留游戏原有 Realtime 类别，不新增线程提优；理想核和亲和性独立核验"
                        : observed.Priority < -2 || observed.Priority > 2 ? "按策略保留游戏原有特殊线程优先级，不新增线程提优；理想核和亲和性独立核验"
                        : process.PriorityClass == 0x80 && row.PriorityLimit == 15 ? "保留 High 类别，不新增时间关键15；普通线程提优按各自角色准入"
                        : "当前进程类别未准入线程提优，未接管";
                    if (role.Priority && !preserved.Contains(row.Priority.Detail)) preserved.Add(row.Priority.Detail);
                }
                if (role.Ideal.HasValue)
                {
                    if (observed.IdealKnown && IdealAllowed(role.Ideal.Value, observed, process))
                        Capture(row.Ideal, GameThreadControlPolicy.Encode(observed.Ideal), GameThreadControlPolicy.Encode(role.Ideal.Value), clock());
                    else
                    {
                        row.Ideal.Detail = "理想核原值或当前范围未确认，未接管";
                        Trace(role.Thread.Key + "-ideal-admission", "线程=" + role.Thread.Tid + "；理想核原值或当前范围未确认，仅保留已核验字段，未接管理想核", clock(), "首次字段准入未确认");
                    }
                }
                else row.Ideal.Detail = "不适用：当前角色未请求理想核，固定亲和性另行核验";
                additions.Add(row);
            }
            if (journal.Rows.Count + additions.Count > 512) return false;
            journal.Rows.AddRange(additions);
            if (additions.Count != 0) Save();
            int managed = ManagedFieldCount;
            detail = managed == 0 ? "线程优先级与理想核均未接管；亲和性另行核验"
                : "已接管 " + managed + " 项线程优先级/理想核，等待正常周期施加与读回；亲和性另行核验";
            if (preserved.Count != 0) detail += "；" + string.Join("；", preserved.ToArray());
            if (!idealRequested) detail += "；理想核不适用：当前角色未请求";
            return true;
        }

        private void ReleaseTransientReads()
        {
            var lifetime = native as IHardThreadTransientReads;
            if (lifetime == null || handleFault || inNative) return;
            var retained = new List<string>();
            // 尚未接管的观察不需要常驻设置句柄；有效字段和未结清恢复债必须继续固定原对象。
            foreach (GameThreadControlRow row in journal.Rows)
                if (NeedsReadOwnership(row.Priority) || NeedsReadOwnership(row.Ideal))
                    retained.Add(row.Thread.Key);
            try { handleFault = !lifetime.ReleaseUnowned(retained.ToArray()); }
            catch { handleFault = true; }
            if (handleFault)
            {
                RequestStop();
                detail = "线程观察句柄关闭未确认，停止新增写入并保留恢复归属";
                Trace("handle-close", detail, clock(), "句柄收尾未确认");
            }
        }

        private bool NeedsReadOwnership(GameThreadControlField field)
        {
            return field.Managed && !field.Terminal
                && (storageFault || field.InitialAdmitted || field.AdmissionFailures < 3 || field.HasWritten);
        }

        private static void Capture(GameThreadControlField field, int original, int desired, long now)
        {
            field.Managed = true; field.Original = original; field.Desired = desired; field.Observed = original;
            field.ObservedKnown = true; field.Applied = original; field.AppliedKnown = original == desired;
            field.InitialAdmitted = original == desired; field.Phase = GameThreadControlPhase.Observing;
            field.PolicyVerified = original == desired;
            field.LastCheckedMilliseconds = field.LastObservedMilliseconds = now;
            if (field.InitialAdmitted) field.LastVerifiedMilliseconds = now;
        }

        private static bool IdealAllowed(GameThreadControlProcessor target, GameThreadControlObserved observed, GameThreadControlProcess process)
        {
            return !process.MultipleGroups && observed.PlacementKnown && target.Group == observed.Group
                && target.Number < 64 && (observed.Allowed & (1UL << target.Number)) != 0;
        }

        internal bool Tick()
        {
            try
            {
                bool verified = TickCore();
                // 清理发生在核验之后；关闭故障也必须反映在本次返回值，不能短暂报成已生效。
                lock (sync) return verified && !handleFault;
            }
            finally { lock (sync) diagnosticLog.Flush(clock(), Stopped); }
        }

        private bool TickCore()
        {
            if (Stopped || Interlocked.CompareExchange(ref ticking, 1, 0) != 0) return false;
            try
            {
                using (authority.EnterUse()) lock (sync)
                {
                    long now = clock();
                    if (disposed || Stopped) return false;
                    if (storageFault)
                    {
                        foreach (GameThreadControlRow row in journal.Rows) if (StopFailedTimeCriticalControl(row)) break;
                        return false;
                    }
                    if (now < nextTick) return CompleteVerification(now);
                    nextTick = now + 2000;
                    // 所有字段都在退避或首次预算已耗尽时，不再为没有到期消费者的周期读取进程。
                    if (!HasDueField(now)) return CompleteVerification(now);
                    int epoch = Volatile.Read(ref generation);
                    authority.RequireForward();
                    GameThreadControlProcess process = ReadProcessForMaintenance();
                    if (process == null || process.Read == HardThreadRead.Unknown)
                    { FailProcessObservation(now, null, "进程身份或类别读取未知，暂停写入并保留规则"); return false; }
                    if (process.Read == HardThreadRead.Gone)
                    { RequestStop(); MarkGone(); return false; }
                    journal.ObservedProcessClass = process.PriorityClass; journal.ProcessObservedMilliseconds = now; journal.ProcessObservedKnown = true;
                    foreach (GameThreadControlRow row in journal.Rows)
                    {
                        if (!Forward(epoch)) break;
                        Step(row, row.Priority, true, process, now, epoch);
                        // 未核验的15不能因首次预算耗尽而长期遗留；停写后沿原账本进入有界恢复。
                        if (StopFailedTimeCriticalControl(row)) break;
                        if (!journal.ProcessObservedKnown || journal.ObservedProcessClass != process.PriorityClass) break;
                        if (!Forward(epoch) || storageFault) break;
                        Step(row, row.Ideal, false, process, now, epoch);
                    }
                    return CompleteVerification(clock());
                }
            }
            catch (Exception error)
            {
                lock (sync)
                {
                    StopForClosureFailure(error);
                    InvalidateObservations(); detail = "运行核验未完成；保留规则和收据；" + error.Message;
                    foreach (GameThreadControlRow row in journal.Rows) if (StopFailedTimeCriticalControl(row)) break;
                }
                return false;
            }
            finally
            {
                try { lock (sync) ReleaseTransientReads(); }
                finally { Volatile.Write(ref ticking, 0); }
            }
        }

        private bool Forward(int epoch) { return !Stopped && !disposed && Volatile.Read(ref generation) == epoch; }

        private static bool FieldDue(GameThreadControlField field, long now)
        { return field.Managed && !field.Terminal && now >= field.NextAttempt && (field.InitialAdmitted || field.AdmissionFailures < 3); }

        private bool HasDueField(long now)
        {
            foreach (GameThreadControlRow row in journal.Rows)
                if (FieldDue(row.Priority, now) || FieldDue(row.Ideal, now)) return true;
            return false;
        }

        private static bool FailObservation(GameThreadControlField field, long now, bool chargeAdmission, string reason)
        {
            field.ObservedKnown = false; field.PolicyVerified = false;
            field.Failures = Math.Min(5, field.Failures + 1);
            field.NextAttempt = now + GameThreadControlPolicy.Backoff(field.Failures);
            field.Detail = reason;
            // 首次尚未接管的读取失败与写入失败共享原三次预算；已接管字段只退避，继续保留纠偏及恢复归属。
            if (!chargeAdmission || field.InitialAdmitted || field.AdmissionFailures >= 3)
            {
                if (!field.InitialAdmitted && field.AdmissionFailures >= 3) field.Detail += "；首次接管预算耗尽，未生效，停止该字段的首次尝试";
                return false;
            }
            field.AdmissionFailures++;
            if (field.AdmissionFailures >= 3) field.Detail += "；首次接管预算耗尽，未生效，停止该字段的首次尝试";
            return true;
        }

        private void FailProcessObservation(long now, GameThreadControlField chargedField, string reason)
        {
            InvalidateObservations();
            bool charged = false, exhausted = false;
            foreach (GameThreadControlRow row in journal.Rows)
                foreach (GameThreadControlField field in new[] { row.Priority, row.Ideal })
                    if (FieldDue(field, now) || object.ReferenceEquals(field, chargedField))
                    {
                        charged |= FailObservation(field, now, !object.ReferenceEquals(field, chargedField), reason);
                        exhausted |= !field.InitialAdmitted && field.AdmissionFailures >= 3;
                    }
            if (charged) Save();
            Trace("process-unknown", reason + (exhausted ? "；首次字段接管预算耗尽，未生效；已接管字段仍按退避维护" : ""), now, "进程读取未确认");
            foreach (GameThreadControlRow row in journal.Rows) if (StopFailedTimeCriticalControl(row)) break;
        }

        private void FailFieldObservation(GameThreadControlRow row, GameThreadControlField field, bool priority, long now, string key, string reason,
            string category = "属性读取未确认")
        {
            if (FailObservation(field, now, true, reason)) Save();
            Trace(row.Thread.Key + key, "线程=" + row.Thread.Tid + "；属性=" + (priority ? "优先级" : "理想核") + "；" + field.Detail
                + (!field.InitialAdmitted && field.AdmissionFailures >= 3 ? "" : "；下次间隔毫秒=" + GameThreadControlPolicy.Backoff(field.Failures)), now, category);
        }

        private bool StopFailedTimeCriticalControl(GameThreadControlRow row)
        {
            if (row.PriorityLimit != 15 || !storageFault && (row.Priority.InitialAdmitted || row.Priority.AdmissionFailures < 3)) return false;
            RequestStop(); detail = storageFault ? "时间关键账本状态未确认，停止新增写入并等待原账本恢复"
                : "时间关键首次接管预算已耗尽，停止新增写入并等待原账本恢复";
            return true;
        }

        private void Step(GameThreadControlRow row, GameThreadControlField field, bool priority, GameThreadControlProcess process, long now, int epoch)
        {
            if (!FieldDue(field, now)) return;
            field.LastCheckedMilliseconds = now;
            GameThreadControlObserved current;
            try { current = Read(row.Thread, priority); }
            catch (Exception error)
            {
                if (StopForClosureFailure(error)) throw;
                FailFieldObservation(row, field, priority, now, priority ? "-priority-read" : "-ideal-read", "原生读取异常，暂停该属性写入；" + error.Message);
                return;
            }
            if (current != null && current.Read == HardThreadRead.Gone)
            { field.Terminal = true; field.Phase = GameThreadControlPhase.Gone; Save(); return; }
            if (current == null || current.Read == HardThreadRead.Unknown || priority && !current.PriorityKnown || !priority && !current.IdealKnown)
            {
                FailFieldObservation(row, field, priority, now, priority ? "-priority-read" : "-ideal-read", "原生读回未知，暂停该属性写入");
                return;
            }
            int previousDesired = field.Desired;
            uint previousDesiredClass = field.DesiredProcessClass;
            bool timeCriticalReady = !priority || row.PriorityLimit != 15 || TimeCriticalPlacement(row.Thread);
            if (priority)
            {
                int desired;
                if (!GameThreadControlPolicy.PriorityTarget(process.PriorityClass, field.Original, row.PriorityLimit, out desired))
                { FailFieldObservation(row, field, priority, now, "-priority-scope", "当前进程类别或优先级目标无法核验，暂停该属性写入", "范围未确认"); return; }
                field.Desired = row.PriorityLimit == 15 && !timeCriticalReady ? field.Original : desired;
                field.DesiredProcessClass = process.PriorityClass;
                if (row.PriorityLimit == 15 && field.Desired != 15) timeCriticalReady = false;
            }
            else if (!IdealAllowed(GameThreadControlPolicy.Decode(field.Desired), current, process))
            { FailFieldObservation(row, field, priority, now, "-ideal-scope", "理想核不属于当前线程、进程及既有范围，暂停该属性写入", "范围未确认"); return; }
            field.Observed = priority ? current.Priority : GameThreadControlPolicy.Encode(current.Ideal);
            field.ObservedKnown = true; field.LastObservedMilliseconds = clock();
            bool preservingPriority = priority && (process.PriorityClass == 0x100 || row.PriorityLimit == 15 && !timeCriticalReady);
            if (field.Observed == field.Desired)
            {
                bool durableChanged = previousDesired != field.Desired || previousDesiredClass != field.DesiredProcessClass
                    || !field.AppliedKnown || field.Applied != field.Desired;
                field.Applied = field.Desired; field.AppliedKnown = true;
                field.PolicyVerified = timeCriticalReady;
                if (priority)
                {
                    GameThreadControlProcess afterRead = ReadProcessForMaintenance();
                    journal.ProcessObservedKnown = afterRead != null && afterRead.Read == HardThreadRead.Known;
                    if (journal.ProcessObservedKnown) { journal.ObservedProcessClass = afterRead.PriorityClass; journal.ProcessObservedMilliseconds = clock(); }
                    field.PolicyVerified = timeCriticalReady && journal.ProcessObservedKnown && afterRead.PriorityClass == field.DesiredProcessClass;
                    if (!journal.ProcessObservedKnown)
                    {
                        if (afterRead != null && afterRead.Read == HardThreadRead.Gone) { RequestStop(); MarkGone(); return; }
                        FailProcessObservation(now, null, "线程读回后进程身份或类别未知，暂停写入并保留规则");
                        if (durableChanged) Save();
                        return;
                    }
                }
                field.Failures = 0; field.NextAttempt = clock() + 2000;
                if (!field.InitialAdmitted && field.AdmissionFailures == 0 && field.PolicyVerified)
                { field.InitialAdmitted = true; field.Detail = "首次读取已符合冻结规则，明确只读接管，尚未产生本方写入"; durableChanged = true; }
                if (field.InitialAdmitted && field.PolicyVerified) field.LastVerifiedMilliseconds = field.LastObservedMilliseconds;
                if (durableChanged) Save();
                return;
            }
            // 实时类别或时间关键资格变化时只撤回本方增量，不把外部新值当成需要持续纠偏的目标。
            if (preservingPriority
                && (!field.HasWritten || field.Observed != field.Written))
            { field.PolicyVerified = false; Trace(row.Thread.Key + "-priority-preserved", "线程=" + row.Thread.Tid + "；当前类别或时间关键资格不准入新增提优，外部优先级保持原样；保留恢复记录", now, "外部优先级保留"); return; }
            if (!Forward(epoch)) return;
            authority.RequireForward();
            GameThreadControlField beforeIntent = field.Clone();
            if (!field.InitialAdmitted) field.AdmissionFailures++;
            bool runtimeCorrection = field.InitialAdmitted;
            if (runtimeCorrection) field.RuntimeAttempts++;
            // 撤回增量时先保留旧写值；API 前崩溃或明确失败，守护仍须认领先前的本方效果。
            bool withdrawingPriority = preservingPriority && beforeIntent.HasWritten;
            field.HasWritten = true; field.Written = withdrawingPriority ? beforeIntent.Written : field.Desired;
            field.AppliedKnown = false; field.PolicyVerified = false;
            field.Phase = GameThreadControlPhase.Intent;
            field.Detail = priority ? "优先级写前原值为已核验快照；API 不提供原子前值" : "理想核写前意图已记录，等待 API 返回实际前值";
            try { Save(); }
            catch { CancelUnwrittenIntent(field, beforeIntent); throw; }
            if (!Forward(epoch)) { CancelUnwrittenIntent(field, beforeIntent); Save(); return; }
            GameThreadControlProcess check;
            GameThreadControlObserved before;
            bool admitted;
            try
            {
                authority.RequireForward();
                check = ReadProcessForMaintenance(); before = check != null && check.Read == HardThreadRead.Known ? Read(row.Thread, priority) : null;
                admitted = !(!Forward(epoch) || check == null || check.Read != HardThreadRead.Known || check.PriorityClass != process.PriorityClass
                    || before == null || before.Read != HardThreadRead.Known
                    || priority && !before.PriorityKnown || !priority && (!before.IdealKnown || !IdealAllowed(GameThreadControlPolicy.Decode(field.Desired), before, check))
                    || priority && row.PriorityLimit == 15 && field.Desired == 15 && !TimeCriticalPlacement(row.Thread)
                    || preservingPriority && before.Priority != beforeIntent.Written);
                if (admitted) authority.RequireForward();
            }
            catch (Exception error)
            {
                CancelUnwrittenIntent(field, beforeIntent);
                if (StopForClosureFailure(error)) { Save(); throw; }
                if (Forward(epoch))
                {
                    FailObservation(field, now, false, "写前核验异常，撤回未执行的意图并退避；" + error.Message);
                    Trace(row.Thread.Key + (priority ? "-priority-read" : "-ideal-read"), field.Detail, now, "属性读取未确认");
                }
                Save(); return;
            }
            if (!admitted)
            {
                CancelUnwrittenIntent(field, beforeIntent);
                if (Forward(epoch))
                {
                    if (check == null || check.Read == HardThreadRead.Unknown)
                        FailProcessObservation(now, field, "写前进程身份或类别未知，撤回未执行的意图并保留规则");
                    else FailObservation(field, now, false, "写前身份、权限或范围未确认，撤回未执行的意图并退避");
                }
                Save(); return;
            }
            bool changed;
            field.ObservedKnown = false;
            inNative = true;
            try
            {
                if (priority) changed = native.SetPriority(row.Thread, field.Desired);
                else
                {
                    GameThreadControlProcessor previous;
                    changed = native.SetIdeal(row.Thread, GameThreadControlPolicy.Decode(field.Desired), out previous);
                    if (changed && !field.PreviousKnown)
                    { field.Previous = GameThreadControlPolicy.Encode(previous); field.PreviousKnown = true; }
                }
            }
            finally { inNative = false; }
            if (changed)
            {
                field.Phase = GameThreadControlPhase.Written; field.Applied = field.Desired;
                if (withdrawingPriority) field.Written = field.Desired;
            }
            Save();
            GameThreadControlObserved readback;
            try { readback = Read(row.Thread, priority); }
            catch (Exception error)
            {
                if (StopForClosureFailure(error)) throw;
                readback = null;
                Trace(row.Thread.Key + (priority ? "-priority-read" : "-ideal-read"), "写后原生读回异常，保留本方写入及恢复归属；" + error.Message, now, "属性读取未确认");
            }
            field.ObservedKnown = readback != null && readback.Read == HardThreadRead.Known && (priority ? readback.PriorityKnown : readback.IdealKnown);
            if (field.ObservedKnown) { field.Observed = priority ? readback.Priority : GameThreadControlPolicy.Encode(readback.Ideal); field.LastObservedMilliseconds = clock(); }
            bool ownedEffectConfirmed = changed && field.ObservedKnown && field.Observed == field.Desired;
            if (ownedEffectConfirmed)
            {
                field.Applied = field.Desired; field.AppliedKnown = true;
                if (row.PriorityLimit != 15 || timeCriticalReady) field.InitialAdmitted = true;
            }
            if (priority)
            {
                GameThreadControlProcess afterProcess = ReadProcessForMaintenance();
                journal.ProcessObservedKnown = afterProcess != null && afterProcess.Read == HardThreadRead.Known;
                if (journal.ProcessObservedKnown)
                { journal.ObservedProcessClass = afterProcess.PriorityClass; journal.ProcessObservedMilliseconds = clock(); }
                if (!journal.ProcessObservedKnown || afterProcess.PriorityClass != process.PriorityClass)
                {
                    field.PolicyVerified = false; field.NextAttempt = clock() + 2000;
                    field.Detail = ownedEffectConfirmed ? "优先级相对值与本方效果已读回，写后进程类别变化；本轮策略基准未确认，下个周期重算"
                        : "优先级写后身份、类别或相对值未确认，保留首次有限准入及恢复归属";
                    if (!journal.ProcessObservedKnown)
                    {
                        if (afterProcess != null && afterProcess.Read == HardThreadRead.Gone) { RequestStop(); MarkGone(); return; }
                        // 当前字段已扣首次次数；其他因进程身份受阻的到期字段各扣一次，避免重复计数。
                        field.NextAttempt = now;
                        FailProcessObservation(now, field, "优先级写后进程身份或类别未知，保留本方效果与恢复归属");
                    }
                    Save(); return;
                }
            }
            if (ownedEffectConfirmed)
            {
                field.Applied = field.Desired; field.AppliedKnown = true;
                if (row.PriorityLimit != 15 || timeCriticalReady) field.InitialAdmitted = true;
                field.PolicyVerified = priority ? timeCriticalReady : IdealAllowed(GameThreadControlPolicy.Decode(field.Desired), readback, process);
                field.Phase = field.PolicyVerified ? GameThreadControlPhase.Verified : GameThreadControlPhase.Written;
                field.Failures = 0; field.NextAttempt = clock() + 2000;
                if (field.PolicyVerified) field.LastVerifiedMilliseconds = field.LastObservedMilliseconds;
                if (runtimeCorrection && field.PolicyVerified) field.Corrections++;
                field.Detail = field.PolicyVerified ? "写入及实际读回已确认；字段继续受管，不补充启动或恢复预算"
                    : "原生效果已读回，当前范围策略未确认，保留归属并等待正常核验";
                Save();
                Trace(row.Thread.Key + (priority ? "-priority-write" : "-ideal-write"), "线程=" + row.Thread.Tid + "；属性=" + (priority ? "优先级" : "理想核")
                    + (field.PolicyVerified ? "；已按规则纠正并读回；目标=" : "；原生效果已确认，策略范围未确认；目标=") + field.Desired, now,
                    field.PolicyVerified ? "字段纠偏已核验" : "字段效果待核验");
            }
            else
            {
                field.AppliedKnown = false; field.PolicyVerified = false; field.Failures = Math.Min(5, field.Failures + 1);
                field.NextAttempt = clock() + GameThreadControlPolicy.Backoff(field.Failures);
                field.Detail = "原生写入或读回未确认；运行纠正按退避继续，首次准入仍有上限";
                Save();
                Trace(row.Thread.Key + (priority ? "-priority-fail" : "-ideal-fail"), "线程=" + row.Thread.Tid + "；属性=" + (priority ? "优先级" : "理想核") + "；未核验，保留规则；下次间隔毫秒=" + GameThreadControlPolicy.Backoff(field.Failures), now, "字段写入未确认");
            }
        }

        private static void CancelUnwrittenIntent(GameThreadControlField field, GameThreadControlField previous)
        {
            // 确认尚未原生写入时撤回意图，不退还首次准入次数。
            field.HasWritten = previous.HasWritten; field.Written = previous.Written;
            field.Applied = previous.Applied; field.AppliedKnown = previous.AppliedKnown;
            field.Previous = previous.Previous; field.PreviousKnown = previous.PreviousKnown;
            field.Phase = previous.Phase; field.PolicyVerified = false;
            field.Detail = "写前代次、权限或范围变化，已撤回未执行的原生写入意图";
        }

        internal void RequestStop()
        { Interlocked.Exchange(ref stopping, 1); Interlocked.Increment(ref generation); }

        internal bool StopRecovery()
        {
            RequestStop();
            using (authority.EnterUse()) lock (sync)
            {
                bool result;
                try { result = StopRecoveryCore(); }
                finally { try { if (!inNative) ReleaseTransientReads(); } finally { diagnosticLog.Flush(clock(), true); } }
                return result && !handleFault;
            }
        }

        private bool StopRecoveryCore()
        {
            if (disposed) return !storageFault && !GameThreadControlPolicy.Pending(journal);
            if (inNative) return false;
            authority.RequireRecovery();
            try
            {
                bool targetExited = native.TargetExited;
                if (targetExited ? journal.ExitRecoveryPasses >= 3 : journal.RecoveryPasses >= 3)
                    return !storageFault && !GameThreadControlPolicy.Pending(journal);
                if (targetExited) journal.ExitRecoveryPasses++; else journal.RecoveryPasses++;
                if (storageFault)
                {
                    // 保留本进程尚未落盘的 API 真实前值，只从已提交账本取当前代次。
                    GameThreadControlJournal committed = store.Load();
                    journal.Revision = committed.Revision; storageFault = false;
                }
                journal.Stopping = true; Save();
                if (targetExited) { MarkGone(); return true; }
                GameThreadControlProcess process = native.ReadProcess();
                if (process != null && process.Read == HardThreadRead.Gone) { MarkGone(); return true; }
                foreach (GameThreadControlRow row in journal.Rows)
                {
                    Recover(row, row.Priority, true);
                    Recover(row, row.Ideal, false);
                }
                detail = GameThreadControlPolicy.Pending(journal) ? "线程属性恢复待确认，保留原账本与归属" : "线程属性恢复已确认";
                return !GameThreadControlPolicy.Pending(journal);
            }
            catch (Exception error) { StopForClosureFailure(error); detail = "线程属性恢复未完成；" + error.Message; return false; }
        }

        private void Recover(GameThreadControlRow row, GameThreadControlField field, bool priority)
        {
            if (!GameThreadControlPolicy.Pending(field))
            { if (field.Managed && !field.HasWritten) field.Terminal = true; return; }
            if (field.RestoreAttempts >= 3) return;
            field.RestoreAttempts++; Save();
            GameThreadControlObserved observed = Read(row.Thread, priority);
            if (observed != null && observed.Read == HardThreadRead.Gone)
            { field.Terminal = true; field.Phase = GameThreadControlPhase.Gone; Save(); return; }
            if (observed == null || observed.Read != HardThreadRead.Known || priority && !observed.PriorityKnown || !priority && !observed.IdealKnown) return;
            int current = priority ? observed.Priority : GameThreadControlPolicy.Encode(observed.Ideal);
            // 恢复读数同步到账本与当前状态，随既有保存流程落盘。
            field.Observed = current; field.ObservedKnown = true; field.LastObservedMilliseconds = clock();
            int original = priority ? field.Original : field.PreviousKnown ? field.Previous : field.Original;
            if (field.RestoreValueKnown) original = field.RestoreValue;
            if (current == original)
            { field.Terminal = true; field.Phase = GameThreadControlPhase.Restored; Save(); return; }
            if (!priority && !field.PreviousKnown) { field.Detail = "理想核 API 实际前值未落盘，保留恢复债务"; Save(); return; }
            int owned = field.Written;
            if (current != owned) { field.Detail = "关闭后属性不再匹配本方写值，保留记录并避免覆盖外部后写值"; Save(); return; }
            field.Phase = GameThreadControlPhase.Restoring; field.RestoreValue = original; Save();
            authority.RequireRecovery();
            GameThreadControlObserved before = Read(row.Thread, priority);
            if (before == null || before.Read != HardThreadRead.Known || priority && (!before.PriorityKnown || before.Priority != current)
                || !priority && (!before.IdealKnown || GameThreadControlPolicy.Encode(before.Ideal) != current)) return;
            bool success;
            inNative = true;
            try
            {
                if (priority) success = native.SetPriority(row.Thread, original);
                else
                {
                    GameThreadControlProcessor previous;
                    success = native.SetIdeal(row.Thread, GameThreadControlPolicy.Decode(original), out previous);
                    if (success && GameThreadControlPolicy.Encode(previous) != current)
                    {
                        // 恢复 API 才发现更晚的外值时，将其记为下一次归还目标，不能丢弃真实前值。
                        field.RestoreValue = GameThreadControlPolicy.Encode(previous); field.RestoreValueKnown = true;
                        field.Written = original; Save(); return;
                    }
                }
            }
            finally { inNative = false; }
            if (!success) return;
            field.Written = original; Save();
            GameThreadControlObserved after = Read(row.Thread, priority);
            if (after != null && after.Read == HardThreadRead.Known && (priority ? after.PriorityKnown && after.Priority == original
                : after.IdealKnown && GameThreadControlPolicy.Encode(after.Ideal) == original))
            { field.Observed = original; field.LastObservedMilliseconds = clock(); field.Terminal = true; field.Phase = GameThreadControlPhase.Restored; Save(); }
        }

        private void MarkGone()
        {
            journal.Stopping = true;
            foreach (GameThreadControlRow row in journal.Rows)
                foreach (GameThreadControlField field in new[] { row.Priority, row.Ideal })
                { field.Terminal = true; field.Phase = GameThreadControlPhase.Gone; }
            Save(); detail = "原进程或线程已退出，原属性不再存在";
        }

        public void Dispose()
        {
            RequestStop();
            using (authority.EnterUse()) lock (sync)
            {
                diagnosticLog.Flush(clock(), true);
                if (disposed) return;
                if (GameThreadControlPolicy.Pending(journal) || storageFault || handleFault) throw new InvalidOperationException("统一线程恢复未结清，保留资源归属");
                native.Dispose(); store.Dispose(); disposed = true;
            }
        }
    }
}

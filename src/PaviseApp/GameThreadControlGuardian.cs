using System;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal sealed partial class GameThreadControlSession
    {
        internal static GameThreadControlSession CreateNative(CriticalThreadHardAffinitySession original, CriticalThreadRule rule = null)
        {
            if (original == null) throw new ArgumentNullException("原线程恢复承载");
            return original.OpenGameThreadControl(rule);
        }

        internal string Describe()
        {
            lock (sync)
            {
                var text = new StringBuilder("进程编号=").Append(journal.Target.ProcessId).Append("；进程创建=").Append(journal.Target.ProcessCreation)
                    .Append("；进程类别原值=0x").Append(journal.OriginalProcessClass.ToString("X"))
                    .Append("；进程类别读回=").Append(journal.ProcessObservedKnown ? "0x" + journal.ObservedProcessClass.ToString("X") : "未知")
                    .Append("；类别核验毫秒=").Append(journal.ProcessObservedMilliseconds)
                    .Append("；字段范围=线程优先级与理想核，亲和性另计")
                    .Append("；角色数=").Append(journal.Rows.Count).Append("；受管属性=").Append(ManagedFieldCount)
                    .Append("；已核验属性=").Append(VerifiedCount).Append("；完整核验=").Append(AllVerified)
                    .Append("；运行写入=").Append(Stopped ? "已停止" : ManagedFieldCount == 0
                        ? "无受管线程属性，未新增属性写入" : "每两秒串行核验，漂移持续纠正")
                    .Append("；恢复债务=").Append(HasDebt).Append("；详情=").Append(CapabilityDetail());
                foreach (GameThreadControlRow row in journal.Rows)
                {
                    text.Append(" | 线程=").Append(row.Thread.Tid).Append('/').Append(row.Thread.Creation).Append("；角色=").Append(row.Name);
                    DescribeField(text, "优先级", row.Priority);
                    DescribeField(text, "理想核", row.Ideal);
                }
                return text.ToString();
            }
        }

        private static void DescribeField(StringBuilder text, string name, GameThreadControlField field)
        {
            if (!field.Managed)
            {
                // 未接管字段中的零值只是账本默认值，不能显示成已读取的原值或目标。
                text.Append('；').Append(name).Append("={受管=False 说明=")
                    .Append(string.IsNullOrEmpty(field.Detail) ? "未接管，未声明本字段原值与目标" : field.Detail).Append('}');
                return;
            }
            text.Append("；").Append(name).Append("={受管=").Append(field.Managed).Append(" 原值=").Append(field.Original)
                .Append(" 目标=").Append(field.Desired).Append(" 已施加=").Append(field.AppliedKnown ? field.Applied.ToString() : "未确认")
                .Append(" 目标进程类=0x").Append(field.DesiredProcessClass.ToString("X")).Append(" 实际末次写值=").Append(field.Written)
                .Append(" 当前策略核验=").Append(field.PolicyVerified)
                .Append(" 当前=").Append(field.ObservedKnown ? field.Observed.ToString() : "未知")
                .Append(" 阶段=").Append(field.Phase).Append(" 首次准入尝试=").Append(field.AdmissionFailures)
                .Append(" 运行尝试=").Append(field.RuntimeAttempts).Append(" 核验纠正=").Append(field.Corrections)
                .Append(" 恢复尝试=").Append(field.RestoreAttempts).Append(" 下次尝试毫秒=").Append(field.NextAttempt)
                .Append(" 最近读取毫秒=").Append(field.LastObservedMilliseconds).Append(" 最近核验毫秒=").Append(field.LastVerifiedMilliseconds).Append('}');
        }
    }

    internal sealed partial class CriticalThreadHardAffinitySession
    {
        private GameThreadControlSession gameThreadControl;
        private bool gameThreadControlForwardClosed;
        private volatile bool timeCriticalWorkerCoverageVerified;
        internal void SetTimeCriticalWorkerCoverage(bool verified) { timeCriticalWorkerCoverageVerified = verified; }
        internal bool HasDeclaredGameThreadControl { get { return journal.ControlDeclared; } }

        private sealed class ControlAuthority : IGameThreadControlAuthority, IGameThreadTimeCriticalAuthority
        {
            private readonly CriticalThreadHardAffinitySession owner;
            internal ControlAuthority(CriticalThreadHardAffinitySession owner) { this.owner = owner; }
            public IDisposable EnterUse() { return owner.gate.EnterUse(); }
            public void RequireForward()
            {
                if (owner.gameThreadControlForwardClosed) throw new InvalidOperationException("统一线程运行写入代次已结束");
                owner.RequireForwardAuthority();
            }
            public void RequireRecovery() { owner.gate.RequireRecovery(); }
            public bool IsPlacementVerified(HardThreadIdentity thread) { return owner.TimeCriticalPlacementVerified(thread); }
        }

        private sealed class PinnedControlStore : IGameThreadControlStore
        {
            private readonly GameThreadControlStore store;
            private readonly CriticalThreadGuardian.GameThreadControlRootPin pin;
            internal PinnedControlStore(GameThreadControlStore store, CriticalThreadGuardian.GameThreadControlRootPin pin)
            { this.store = store; this.pin = pin; }
            public GameThreadControlJournal Load() { return store.Load(); }
            public void Save(long revision, GameThreadControlJournal journal) { store.Save(revision, journal); }
            public void Dispose() { store.Dispose(); pin.Dispose(); }
        }

        // 只建立独立守护与原目标权限，不创建硬绑定计划，也不写线程或进程掩码。
        internal static CriticalThreadHardAffinitySession OpenThreadControlNative(GameMode.CriticalThreadTargetLease original)
        { return OpenNative(original); }

        private string ControlRoot()
        {
            if (guardian == null || guardian.Actor == null) throw new InvalidOperationException("统一线程控制缺少已认证独立守护");
            string sessionDirectory = Path.GetDirectoryName(guardian.Actor.Path);
            string guards = Path.GetDirectoryName(sessionDirectory);
            string root = Path.GetDirectoryName(guards);
            if (!string.Equals(Path.GetFileName(guards), "guards", StringComparison.Ordinal)
                || Path.GetFileName(sessionDirectory) != gate.Target.SessionId.ToString("N"))
                throw new InvalidDataException("统一线程守护路径不属于原会话");
            return root;
        }

        internal bool TimeCriticalPlacementVerified(HardThreadIdentity thread)
        {
            try
            {
                using (gate.EnterUse())
                {
                    RequireForwardAuthority();
                    if (disposed || gameThreadControlForwardClosed || thread == null || plan == null || plan.FloatingCandidates
                        || !plan.Target.Same(gate.Target) || gate.Target.AllowedMask == 0
                        || plan.RequireWorkerCoverage && !timeCriticalWorkerCoverageVerified) return false;
                    HardThreadChoice[] choices = plan.Choices;
                    if (choices.Length != 1 || choices[0].Thread.Key != thread.Key || choices[0].Mask == 0
                        || (choices[0].Mask & (choices[0].Mask - 1)) != 0) return false;
                    if (!GameThreadControlNative.ControllerCanRunOutside(choices[0].Mask)) return false;
                    HardThreadReceipt row = FindPlacement(thread);
                    if (!RuntimeReceiptValid(row, Find(thread) != null) || row.Applied != choices[0].Mask) return false;
                    HardThreadOwnership ownership;
                    lock (factsGate)
                        if (!verified.TryGetValue(thread.Key, out ownership) || ownership.Group != plan.Group || ownership.Mask != choices[0].Mask) return false;
                    if (!RuntimeScopeKnown(ReadWriteScope())) return false;
                    IHardThreadPlacementReads reads = native as IHardThreadPlacementReads;
                    HardThreadObserved observed = reads == null ? native.ReadThread(thread) : reads.ReadPlacement(thread);
                    RequireForwardAuthority();
                    return observed != null && observed.Read == HardThreadRead.Known && observed.CpuSetsEmpty
                        && observed.Group == plan.Group && observed.Mask == choices[0].Mask;
                }
            }
            catch { return false; }
        }

        internal GameThreadControlSession OpenGameThreadControl(CriticalThreadRule rule = null)
        {
            using (gate.EnterUse())
            {
                RequireForwardAuthority();
                bool timeCritical = rule != null && rule.IsTimeCriticalRender;
                NativeGate source = gate as NativeGate;
                if (rule != null && (!timeCritical || source == null || source.OriginalRule != rule.Encode()))
                    throw new InvalidOperationException("时间关键规则与原游戏授权不一致");
                if (gameThreadControl != null) return gameThreadControl;
                if (gameThreadControlForwardClosed || journal.ControlDeclared) throw new InvalidOperationException("原统一线程会话已声明或进入恢复，不能重新创建补充预算");
                // 空硬绑定账本也须保留原scope索引，供历史目标退出核验与独立恢复使用。
                if (journal.Revision == 0) Save();
                string root = ControlRoot();
                var pin = CriticalThreadGuardian.PinGameThreadControlRoot(root);
                GameThreadControlStore controlStore = null;
                GameThreadControlNative controlNative = null;
                try
                {
                    controlStore = new GameThreadControlStore(root, gate.Target, journal.SourceNonce, journal.SourceScopeHash, pin.Require);
                    GameThreadControlJournal initial = controlStore.Load();
                    if (initial.Revision == 0) { initial.Rule = timeCritical ? rule.Encode() : ""; initial.Revision = 1; controlStore.Save(0, initial); }
                    controlNative = new GameThreadControlNative(gate.Target, false, timeCritical);
                    gameThreadControl = new GameThreadControlSession(controlNative, new PinnedControlStore(controlStore, pin),
                        new ControlAuthority(this), GameThreadControlSession.NowMilliseconds, Logger.Info, rule);
                    journal.ControlDeclared = true; Save();
                    return gameThreadControl;
                }
                catch
                {
                    if (gameThreadControl != null)
                    {
                        gameThreadControl.RequestStop();
                        if (!gameThreadControl.StopRecovery()) throw;
                        gameThreadControl.Dispose();
                        throw;
                    }
                    if (controlStore != null)
                    {
                        // 尚未声明且没有线程写入时，仍把空源结清，留给原守护在正式关闭后精确清理。
                        try
                        {
                            GameThreadControlJournal empty = controlStore.Load();
                            if (empty.Rows.Count == 0) { long revision = empty.Revision; empty.Stopping = true; empty.Revision++; controlStore.Save(revision, empty); }
                        }
                        catch { }
                    }
                    try { if (controlNative != null) controlNative.Dispose(); }
                    finally { try { if (controlStore != null) controlStore.Dispose(); } finally { pin.Dispose(); } }
                    throw;
                }
            }
        }

        private bool RestoreGameThreadControlBeforeAffinity()
        {
            gameThreadControlForwardClosed = true;
            if (gameThreadControl == null) return true;
            gameThreadControl.RequestStop();
            if (!gameThreadControl.StopRecovery()) return false;
            gameThreadControl.Dispose();
            // 已结束的会话对象继续保留，不能借新对象重置首次准入或恢复预算。
            return true;
        }
    }

    internal static partial class CriticalThreadGuardian
    {
        internal sealed class GameThreadControlRootPin : IDisposable
        {
            private readonly RootPin pin;
            private readonly string root, sid;
            internal GameThreadControlRootPin(string root)
            {
                this.root = Canonical(root); sid = UserSid();
                RequireAcl(this.root, sid); pin = new RootPin(this.root, sid);
            }
            internal void Require()
            {
                RequireAcl(root, sid);
                if (!pin.Valid) throw new IOException("统一线程原目录身份已变化");
            }
            public void Dispose() { pin.Dispose(); }
        }

        internal static GameThreadControlRootPin PinGameThreadControlRoot(string root)
        { return new GameThreadControlRootPin(root); }

        private sealed class StoppedControlAuthority : IGameThreadControlAuthority
        {
            private readonly StopProof proof;
            internal StoppedControlAuthority(StopProof proof) { this.proof = proof; }
            public IDisposable EnterUse() { return proof.EnterUse(); }
            public void RequireForward() { throw new InvalidOperationException("守护恢复不授予运行写入权限"); }
            public void RequireRecovery() { proof.RequireStopped(); }
        }

        private static bool RestoreGameThreadControl(StopProof proof, bool declared)
        {
            proof.RequireStopped();
            string root = proof.CanonicalStore;
            if (!GameThreadControlStore.HasDebt(root, proof.Target, proof.SourceNonce, proof.SourceScopeHash, declared)) return true;
            if (proof.TargetExited())
            {
                GameThreadControlStore.SettleGone(root, proof.Target, proof.SourceNonce, proof.SourceScopeHash,
                    delegate { proof.RequireStopped(); if (!proof.TargetExited()) throw new InvalidOperationException("原目标退出尚未核验"); });
                return true;
            }
            if (declared) GameThreadControlStore.RequireDeclaredSource(root, proof.Target, proof.SourceNonce, proof.SourceScopeHash);
            using (var pin = PinGameThreadControlRoot(root))
            using (var store = new GameThreadControlStore(root, proof.Target, proof.SourceNonce, proof.SourceScopeHash, pin.Require))
            {
                if (proof.TargetExited()) { GameThreadControlStore.MarkGone(store); return true; }
                using (var native = new GameThreadControlNative(proof.Target))
                {
                // session 未结清前不能释放，原守护须继续持有账本、目标及权限。
                var session = new GameThreadControlSession(native, store, new StoppedControlAuthority(proof),
                    GameThreadControlSession.NowMilliseconds, Logger.Info);
                return session.StopRecovery();
                }
            }
        }

        private static void MarkGameThreadControlGone(HistoricalRegistration original)
        {
            original.RequireOriginal(false);
            if (!GameThreadControlStore.HasDebt(original.Scope.Root, original.Scope.Target, original.Scope.Nonce, original.Scope.Digest, original.Journal.ControlDeclared)) return;
            GameThreadControlStore.SettleGone(original.Scope.Root, original.Scope.Target, original.Scope.Nonce, original.Scope.Digest,
                delegate { original.RequireOriginal(false); });
        }

        private static bool CleanupReleasedGameThreadControl(Scope scope, HardThreadSettledSource settled)
        {
            if (scope == null) return true;
            string path = Path.Combine(scope.Root, "game-thread-control-" + scope.Target.SessionId.ToString("N") + ".xml");
            if (!File.Exists(path) && !File.Exists(Path.ChangeExtension(path, ".lock"))) return true;
            using (var pin = PinGameThreadControlRoot(scope.Root))
            {
                return GameThreadControlStore.CleanupSettled(scope.Root, scope.Target, settled == null ? null : settled.ControlHash,
                    delegate
                    {
                        pin.Require();
                        string id = scope.Target.SessionId.ToString("N");
                        if (File.Exists(Path.Combine(scope.Root, "hard-thread-" + id + ".xml"))
                            || File.Exists(Path.Combine(scope.Root, "hard-thread-" + id + ".lock"))
                            || Directory.Exists(Path.Combine(scope.Root, "guards", id)))
                            throw new IOException("原亲和性与守护源尚未关闭，继续保留统一线程结清证明");
                    }, entry => DeleteSettledEntry(entry, false));
            }
        }

        private static bool CleanupHistoricalGameThreadControl(HistoricalRegistration original, NativeGoneProof proof)
        {
            if (!original.Journal.ControlDeclared && !GameThreadControlStore.HasRecord(original.Scope.Root, original.Scope.Target)) return true;
            original.RequireOriginal();
            if (proof == null || proof.Reason == HardThreadGoneReason.None || !proof.Target.Same(original.Scope.Target))
                throw new InvalidOperationException("统一线程历史清理缺少原目标退出证明");
            var parentTarget = new HardThreadTarget(original.Scope.Target.SessionId, original.Scope.Parent.Pid,
                original.Scope.Parent.Creation, original.Scope.Parent.Path, 1);
            using (var first = new HistoricalProcessPin(parentTarget.ProcessId))
            using (var second = new HistoricalProcessPin(parentTarget.ProcessId))
                if (HardThreadHistoricalPolicy.Gone(parentTarget, first.Fact, second.Fact) == HardThreadGoneReason.None) return true;
            if (GameThreadControlStore.HasDebt(original.Scope.Root, original.Scope.Target, original.Scope.Nonce, original.Scope.Digest, true))
                GameThreadControlStore.SettleGone(original.Scope.Root, original.Scope.Target, original.Scope.Nonce, original.Scope.Digest,
                    delegate { original.RequireOriginal(false); });
            string hash = GameThreadControlStore.SettledHash(original.Scope.Root, original.Scope.Target,
                original.Scope.Nonce, original.Scope.Digest, true);
            if (!GameThreadControlStore.CleanupSettled(original.Scope.Root, original.Scope.Target, hash,
                delegate { original.RequireOriginal(false); }, path => DeleteSettledEntry(path, false))) return false;
            HardThreadJournal current = original.Store.ReadHistorical(original.Scope.Target);
            if (!current.ControlDeclared) return true;
            long revision = current.Revision; current.ControlDeclared = false; current.Revision++;
            original.Store.Save(revision, current);
            return true;
        }

        private static bool SweepReleasedGameThreadControl(string root)
        {
            if (!Directory.Exists(root)) return true;
            using (var pin = PinGameThreadControlRoot(root))
                return GameThreadControlStore.SweepReleased(root, pin.Require, path => DeleteSettledEntry(path, false));
        }
    }
}

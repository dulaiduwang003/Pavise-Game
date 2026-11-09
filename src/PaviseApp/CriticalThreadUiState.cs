using System;

namespace PaviseApp
{
    internal sealed class CriticalThreadUiState
    {
        internal readonly string ProfileId;

        internal readonly string State;

        internal readonly string Detail;

        internal readonly bool Enabled;

        internal readonly bool Applied;

        internal readonly bool Pending;

        internal readonly bool SessionActive;

        internal readonly bool RequestedWorkerAvoid;

        internal readonly CriticalThreadCoverageState Coverage;

        internal readonly int Candidates;

        internal readonly int Workers;

        internal readonly long SnapshotTicks;

        internal readonly Guid SessionToken;

        internal readonly long GameSerial;

        internal readonly long IntentEpoch;

        internal readonly long TargetBirth;

        internal readonly int TargetPid;

        internal readonly string RuleSignature;

        internal readonly ulong WorkerMask;

        internal readonly CriticalThreadBoostCoupling Coupling;

        internal readonly int CouplingTid;

        private readonly ulong[] masks;

        private readonly string[] labels;

        internal string CouplingText
        {
            get
            {
                return CriticalThreadBoostCouplingPolicy.Text(Coupling, CouplingTid);
            }
        }

        internal ulong[] CandidateMasks
        {
            get
            {
                return (ulong[])masks.Clone();
            }
        }

        internal string[] CandidateLabels
        {
            get
            {
                return (string[])labels.Clone();
            }
        }

        internal string CoverageText
        {
            get
            {
                switch (Coverage)
                {
                case CriticalThreadCoverageState.CandidatesOnly:
                    return PerformancePolicy.Text("候选已回读；其他线程未限制", "Candidates read back; other threads are unrestricted");
                case CriticalThreadCoverageState.WorkersPartial:
                    return PerformancePolicy.Text("正在逐步处理该游戏的其他线程", "Gradually applying avoidance to this game's other threads");
                case CriticalThreadCoverageState.WorkersSnapshotComplete:
                    return PerformancePolicy.Text("当前线程快照已回读；新线程持续核验", "Current thread snapshot read back; new threads remain under review");
                default:
                    return "";
                }
            }
        }

        internal string StateText
        {
            get
            {
                switch (State)
                {
                case "waiting-game":
                    return PerformancePolicy.Text("等待游戏", "Waiting for game");
                case "observing":
                    return PerformancePolicy.Text("正在观察热点候选", "Observing hotspot candidates");
                case "active":
                    return PerformancePolicy.Text("硬亲和性已回读", "Hard affinity read back");
                case "coverage-pending":
                    return PerformancePolicy.Text("候选已绑定 · 覆盖待核验", "Candidates bound; coverage pending");
                case "recovery-pending":
                    return PerformancePolicy.Text("原设置恢复待核验", "Restoration pending verification");
                case "conflict":
                    return PerformancePolicy.Text("设置冲突 · 已停止新增应用", "Conflicting settings; further writes stopped");
                case "unsupported":
                    return PerformancePolicy.Text("当前条件不支持", "Unavailable under current conditions");
                default:
                    return PerformancePolicy.Text("已关闭", "Off");
                }
            }
        }

        internal CriticalThreadUiState(string profileId, string state, string detail, bool enabled = false, bool applied = false, bool pending = false, int workers = 0, long snapshotTicks = 0L, ulong workerMask = 0uL, ulong[] candidateMasks = null, string[] candidateLabels = null, bool sessionActive = false, bool requestedWorkerAvoid = false, CriticalThreadCoverageState coverage = CriticalThreadCoverageState.None, Guid? sessionToken = null, long gameSerial = 0L, long intentEpoch = 0L, int targetPid = 0, long targetBirth = 0L, string ruleSignature = "", CriticalThreadBoostCoupling coupling = CriticalThreadBoostCoupling.Unknown, int couplingTid = 0)
        {
            ProfileId = profileId ?? "";
            State = state ?? "";
            Detail = detail ?? "";
            Coupling = coupling;
            CouplingTid = couplingTid;
            Enabled = enabled;
            Applied = applied;
            Pending = pending;
            Workers = workers;
            SessionActive = sessionActive;
            RequestedWorkerAvoid = requestedWorkerAvoid;
            Coverage = coverage;
            SessionToken = sessionToken ?? Guid.Empty;
            GameSerial = gameSerial;
            IntentEpoch = intentEpoch;
            TargetPid = targetPid;
            TargetBirth = targetBirth;
            RuleSignature = ruleSignature ?? "";
            SnapshotTicks = snapshotTicks;
            WorkerMask = workerMask;
            masks = ((candidateMasks == null) ? new ulong[0] : ((ulong[])candidateMasks.Clone()));
            labels = ((candidateLabels == null) ? new string[0] : ((string[])candidateLabels.Clone()));
            Candidates = masks.Length;
        }
    }
}

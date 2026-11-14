using System;
using System.IO;

namespace PaviseApp
{
    // 路由只在握手和任何游戏写入之前选择；每次准入最多创建一个原生子进程和一个代理子进程。
    internal interface IHardThreadGuardianLaunchAttempt
    {
        bool StartNative(out int error);
        bool CallerInJob();
        bool ChildInJob();
        bool CallerJobTrusted();
        void StopNativeAndConfirm();
        void CloseNative();
        void ResumeNative();
        void StartBroker();
    }

    internal static class HardThreadGuardianLaunchRoute
    {
        internal static void Start(IHardThreadGuardianLaunchAttempt attempt)
        {
            int error;
            if (!attempt.StartNative(out error))
            {
                if (error == 5 && attempt.CallerInJob()) { attempt.StartBroker(); return; }
                throw new IOException("独立守护原生创建失败，win32=" + error);
            }
            bool suspended = true, stopAttempted = false;
            try
            {
                if (attempt.ChildInJob() && !attempt.CallerJobTrusted())
                {
                    stopAttempted = true;
                    attempt.StopNativeAndConfirm();
                    suspended = false;
                    attempt.CloseNative();
                    attempt.StartBroker();
                    return;
                }
                attempt.ResumeNative();
                suspended = false;
            }
            catch (Exception failure)
            {
                // 挂起子进程不会自行超时退出；查询或恢复主线程失败时必须收尾，不能只关闭句柄。
                if (suspended && !stopAttempted)
                {
                    try { attempt.StopNativeAndConfirm(); }
                    catch (Exception cleanup) { throw new AggregateException("独立守护启动失败，挂起子进程退出尚未确认", failure, cleanup); }
                }
                throw;
            }
        }
    }

    internal static class HardThreadGuardianLaunchPolicy
    {
        internal const string JobMarker = "|job|";
        internal const uint BreakawayOk = 0x800, SilentBreakawayOk = 0x1000;
        private const string StillInJob = "守护进程仍处于作业中，独立恢复条件尚未核验通过";

        internal static Exception Failed(int code, bool membershipKnown, bool inJob)
        {
            string codeOffset = "hidden independent guardian could not start: " + code;
            if (code != 5 || !membershipKnown || !inJob)
            {
                return new IOException(codeOffset);
            }
            return new HardThreadGuardianLaunchBlockedException(codeOffset + " (Pavise is running inside a job)");
        }

        internal static void RequireMembershipKnown(bool membershipKnown)
        {
            if (!membershipKnown)
            {
                throw new IOException("independent guardian job membership could not be verified");
            }
        }

        // 两条兼容路径共用限制与成员完整性检查，再分别核验会话锚点或本地 WMI 跨会话创建证据。
        // 不推断作业类型或未知祖先；限制或成员不可读时拒绝。
        internal static bool UnrestrictedSessionJob(HardThreadGuardianJobFacts facts, out string reason)
        {
            if (facts == null || !facts.Known) { reason = "作业归属读不到"; return false; }
            if (!facts.InJob) { reason = "不在作业里"; return false; }
            if (!string.IsNullOrEmpty(facts.Failure)) { reason = "作业信息没有读全"; return false; }
            if (!facts.AoAc) { reason = "未满足现代待机兼容路径条件"; return false; }
            if ((facts.LimitFlags & ~(BreakawayOk | SilentBreakawayOk)) != 0) { reason = "作业带有限制或关闭即终止标志"; return false; }
            if ((facts.LimitFlags & SilentBreakawayOk) == 0) { reason = "作业不允许子进程自行脱离，未满足兼容路径条件"; return false; }
            if (facts.UiRestrictions != 0) { reason = "作业带有界面限制"; return false; }
            if ((facts.CpuRateFlags & 1) != 0) { reason = "作业启用了处理器限额"; return false; }
            if (facts.Members <= 0 || facts.EvidenceVersion < 2) { reason = "作业成员或版本证据不足"; return false; }
            reason = "";
            return true;
        }

        internal static bool SystemSessionJob(HardThreadGuardianJobFacts facts, out string reason)
        {
            if (!UnrestrictedSessionJob(facts, out reason)) return false;
            if (facts.AnchorState != "verified"
                || (facts.AnchorKind != HardThreadGuardianJobAnchors.SessionHost && facts.AnchorKind != HardThreadGuardianJobAnchors.DesktopShell)
                || facts.AnchorPid <= 0 || facts.AnchorCreation <= 0 || facts.AnchorSession <= 0)
            { reason = "缺少可信会话外壳或宿主的身份与成员交集，不能排除私有容器"; return false; }
            reason = "";
            return true;
        }

        // 无作业时应答仅含摘要；有作业时附带限制与成员信息，核验失败则拒绝握手。
        internal static HardThreadGuardianJobFacts RequireIndependent(bool inJobSeenByParent, string reply, string digest,
            Func<HardThreadGuardianJobFacts, string> verifyAnchor = null, Func<string> verifyBroker = null)
        {
            string ready = "ready:" + digest;
            if (reply == ready)
            {
                if (inJobSeenByParent) throw new HardThreadGuardianLaunchBlockedException(StillInJob + " (guardian reported no job facts)");
                return null;
            }
            if (reply == null || !reply.StartsWith(ready + JobMarker, StringComparison.Ordinal))
            {
                throw new IOException("guardian did not retain the original scope");
            }
            HardThreadGuardianJobFacts facts = HardThreadGuardianJobFacts.Parse(reply.Substring(ready.Length + JobMarker.Length));
            if (facts == null) throw new HardThreadGuardianLaunchBlockedException(StillInJob + " (job facts unreadable)");
            string reason;
            if (!UnrestrictedSessionJob(facts, out reason))
            {
                throw new HardThreadGuardianLaunchBlockedException(StillInJob + " (" + facts.Describe() + " 原因=" + reason + ")");
            }
            if (SystemSessionJob(facts, out reason))
                reason = verifyAnchor == null ? "父进程未独立复核会话锚点" : verifyAnchor(facts);
            if (!string.IsNullOrEmpty(reason))
            {
                // 回调只由实际本地 WMI 创建路径提供；守护应答不能自行声明拥有此证明。
                string brokerReason = verifyBroker == null ? null : verifyBroker();
                if (verifyBroker == null || !string.IsNullOrEmpty(brokerReason))
                    throw new HardThreadGuardianLaunchBlockedException(StillInJob + " (" + facts.Describe() + " 原因=" + reason
                        + (verifyBroker == null ? "" : " WMI证明=" + brokerReason) + ")");
            }
            return facts;
        }

        internal static string ReadyReply(string digest, HardThreadGuardianJobFacts facts)
        {
            string ready = "ready:" + digest;
            if (facts != null && facts.Known && !facts.InJob) return ready;
            return ready + JobMarker + (facts ?? new HardThreadGuardianJobFacts()).Encode();
        }
    }
}

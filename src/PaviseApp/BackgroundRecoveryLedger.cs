using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    // 只记录恢复事实，不发起写入或重试；每个会话段保留自己的回执归属。
    internal sealed class BackgroundRecoveryLedger
    {
        internal enum State { Governed, Pending, Restored, Exited, OtherGovernance, NotRequired, RestoreVerifiedChangeUnknown }

        internal sealed class Ticket
        {
            internal readonly BackgroundRecoveryLedger Owner;
            internal readonly int Pid;
            internal readonly long Birth, Admission;
            internal State Outcome;
            internal bool ForwardEffects;
            internal bool ForwardEffectsUnknown;
            internal Ticket(BackgroundRecoveryLedger owner, int pid, long birth, long admission)
            { Owner = owner; Pid = pid; Birth = birth; Admission = admission; Outcome = State.Governed; }
        }

        private struct Identity : IEquatable<Identity>
        {
            internal int Pid;
            internal long Birth;
            public bool Equals(Identity other) { return Pid == other.Pid && Birth == other.Birth; }
            public override bool Equals(object other) { return other is Identity && Equals((Identity)other); }
            public override int GetHashCode() { return Pid ^ Birth.GetHashCode(); }
        }

        internal sealed class Snapshot
        {
            internal int Restored, Exited, Pending, Governed, OtherGovernance, NotRequired, RestoredUnknownChange;
            internal long Revision;
            internal string Fields
            {
                get
                {
                    return "restoredProcessCount=" + Restored.ToString(CultureInfo.InvariantCulture)
                        + " exitedProcessCount=" + Exited.ToString(CultureInfo.InvariantCulture)
                        + " pendingProcessCount=" + Pending.ToString(CultureInfo.InvariantCulture)
                        + " governedProcessCount=" + Governed.ToString(CultureInfo.InvariantCulture)
                        + " otherGovernanceProcessCount=" + OtherGovernance.ToString(CultureInfo.InvariantCulture)
                        + " unchangedProcessCount=" + NotRequired.ToString(CultureInfo.InvariantCulture)
                        + " restoredUnknownChangeProcessCount=" + RestoredUnknownChange.ToString(CultureInfo.InvariantCulture)
                        + " accountingRevision=" + Revision.ToString(CultureInfo.InvariantCulture)
                        + " countScope=session includes=mid-session,grace-period targetScope=ordinary-background countUnit=pid-and-creation outcome=latest-admission";
                }
            }
        }

        private static long nextSegment;
        private readonly object sync = new object();
        private readonly Dictionary<Identity, Ticket> latest = new Dictionary<Identity, Ticket>();
        private long nextAdmission;
        private long revision;
        private bool published;
        internal readonly long Session, Segment;

        internal BackgroundRecoveryLedger(long session)
        { Session = session; Segment = Interlocked.Increment(ref nextSegment); }

        internal Ticket Admit(int pid, long birth)
        {
            if (pid <= 0 || birth <= 0) return null;
            lock (sync)
            {
                Ticket ticket = new Ticket(this, pid, birth, ++nextAdmission);
                latest[new Identity { Pid = pid, Birth = birth }] = ticket;
                revision++;
                return ticket;
            }
        }

        internal void Record(Ticket ticket, State state)
        { Record(ticket, state, false); }

        internal void RecordVerifiedRestore(Ticket ticket)
        { Record(ticket, State.Restored, true); }

        internal void ObserveForwardEffects(Ticket ticket)
        {
            lock (sync)
            {
                if (ticket != null && object.ReferenceEquals(ticket.Owner, this)) ticket.ForwardEffects = true;
            }
        }

        internal void ObserveUnknownForwardEffects(Ticket ticket)
        {
            lock (sync)
            {
                if (ticket != null && object.ReferenceEquals(ticket.Owner, this)) ticket.ForwardEffectsUnknown = true;
            }
        }

        private void Record(Ticket ticket, State state, bool verifiedRestore)
        {
            string update = null;
            lock (sync)
            {
                if (verifiedRestore && ticket != null && !ticket.ForwardEffects)
                    state = ticket.ForwardEffectsUnknown ? State.RestoreVerifiedChangeUnknown : State.NotRequired;
                Ticket current;
                if (ticket == null || !object.ReferenceEquals(ticket.Owner, this)
                    || !latest.TryGetValue(new Identity { Pid = ticket.Pid, Birth = ticket.Birth }, out current)
                    || !object.ReferenceEquals(current, ticket) || current.Outcome == state) return;
                // 同一次接管的结论只结清一次；再次接管必须取得新票据。
                if (current.Outcome == State.Restored || current.Outcome == State.Exited || current.Outcome == State.NotRequired
                    || current.Outcome == State.RestoreVerifiedChangeUnknown) return;
                current.Outcome = state;
                revision++;
                // 日志在锁外写入，物理顺序可能交错；段号和快照修订号共同确定新旧。
                if (published) update = "后台恢复统计更新 " + IdentityFields + " " + CaptureLocked().Fields;
            }
            // 不回调 GameMode，避免恢复线程反向获取游戏会话锁。
            if (update != null) Logger.Info(update);
        }

        internal Snapshot Capture() { lock (sync) return CaptureLocked(); }

        private Snapshot CaptureLocked()
        {
            Snapshot result = new Snapshot { Revision = revision };
            foreach (Ticket ticket in latest.Values)
            {
                switch (ticket.Outcome)
                {
                case State.Restored: result.Restored++; break;
                case State.Exited: result.Exited++; break;
                case State.Pending: result.Pending++; break;
                case State.Governed: result.Governed++; break;
                case State.OtherGovernance: result.OtherGovernance++; break;
                case State.NotRequired: result.NotRequired++; break;
                // 恢复已核验但写入历史未知，不计入已恢复数量或未结清债务。
                case State.RestoreVerifiedChangeUnknown: result.RestoredUnknownChange++; break;
                }
            }
            return result;
        }

        internal string PublishFields()
        {
            lock (sync)
            {
                published = true;
                return CaptureLocked().Fields + " " + IdentityFields;
            }
        }

        internal bool TryPublishFields(out string fields)
        {
            lock (sync)
            {
                fields = null;
                if (published) return false;
                published = true;
                fields = CaptureLocked().Fields + " " + IdentityFields;
                return true;
            }
        }

        private string IdentityFields
        {
            get { return "accountingSession=" + Session.ToString(CultureInfo.InvariantCulture) + " accountingSegment=" + Segment.ToString(CultureInfo.InvariantCulture); }
        }
    }
}

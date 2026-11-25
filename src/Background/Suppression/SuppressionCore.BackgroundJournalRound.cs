using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        private const int BackgroundResourceCommitPhases = 2;
        private const int BackgroundJournalWriteLimit = BackgroundResourceCommitPhases + 2;

        private sealed class BackgroundJournalCommit
        {
            internal Action Apply, Reject;
        }

        private sealed class BackgroundJournalRound
        {
            internal SuppressionCore Owner;
            internal bool CleanupPending;
            internal int Writes;
            internal List<BackgroundJournalCommit> Jobs = new List<BackgroundJournalCommit>();
        }

        [ThreadStatic]
        private static BackgroundJournalRound backgroundJournalRound;
        private bool backgroundJournalCleanupPending;

        private BackgroundJournalRound ActiveBackgroundJournalRound
        {
            get
            {
                BackgroundJournalRound round = backgroundJournalRound;
                return round != null && round.Owner == this ? round : null;
            }
        }

        internal void BeginBackgroundJournalRound()
        {
            if (backgroundJournalRound != null)
                throw new InvalidOperationException("A background journal round is already active.");
            lock (sync)
                backgroundJournalRound = new BackgroundJournalRound { Owner = this, CleanupPending = backgroundJournalCleanupPending };
        }

        private bool WriteBackgroundCheckpoint(BackgroundJournalRound round)
        {
            if (round.Writes >= BackgroundJournalWriteLimit) return false;
            round.Writes++;
            if (!SaveJournalLocked(true, true)) return false;
            round.CleanupPending = false;
            return true;
        }

        private bool SaveAcquisitionJournal()
        {
            BackgroundJournalRound round = ActiveBackgroundJournalRound;
            return round == null ? SaveJournalLocked() : WriteBackgroundCheckpoint(round);
        }

        private void SavePendingOrdinaryOwnershipJournal(Entry entry)
        {
            if (entry.OrdinaryOwnershipJournalPending && !SaveBackgroundResourceCleanup())
                backgroundJournalCleanupPending = true;
        }

        private void CommitBackgroundResource(Action apply, Action reject)
        {
            BackgroundJournalRound round = ActiveBackgroundJournalRound;
            if (round == null)
            {
                if (SaveJournalLocked(true, true)) apply();
                else reject();
                return;
            }
            round.Jobs.Add(new BackgroundJournalCommit { Apply = apply, Reject = reject });
        }

        private bool SaveBackgroundResourceCleanup()
        {
            BackgroundJournalRound round = ActiveBackgroundJournalRound;
            if (round == null) return SaveJournalLocked(true, true);
            round.CleanupPending = true;
            return true;
        }

        internal int EndBackgroundJournalRound(bool applyJobs)
        {
            BackgroundJournalRound round = ActiveBackgroundJournalRound;
            if (round == null) return 0;
            try
            {
                lock (sync)
                {
                    // 先保存原值 归属和 Job 回执一起提交 合并失败再提交逐进程回执 最后结清释放
                    for (int phase = 0; phase < BackgroundResourceCommitPhases && round.Jobs.Count != 0; phase++)
                    {
                        List<BackgroundJournalCommit> jobs = round.Jobs;
                        round.Jobs = new List<BackgroundJournalCommit>();
                        bool committed = applyJobs && WriteBackgroundCheckpoint(round);
                        foreach (BackgroundJournalCommit job in jobs)
                        {
                            if (committed) job.Apply();
                            else job.Reject();
                        }
                    }
                    foreach (BackgroundJournalCommit job in round.Jobs) job.Reject();
                    if (round.CleanupPending) WriteBackgroundCheckpoint(round);
                    backgroundJournalCleanupPending = round.CleanupPending;
                    return round.Writes;
                }
            }
            finally { backgroundJournalRound = null; }
        }
    }
}

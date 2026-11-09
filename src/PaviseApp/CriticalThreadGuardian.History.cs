using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal static partial class CriticalThreadGuardian
    {

        private sealed class HistoricalRegistration : IDisposable
        {
            internal Scope Scope;

            internal RootPin Root;

            internal DirectoryPin Guards;

            internal DirectoryPin Directory;

            internal FileStream Source;

            internal FileStream Image;

            internal HardThreadFileStore Store;

            internal HardThreadJournal Journal;

            internal string JournalHash;

            internal HardThreadHistoricalBudget Budget;

            private bool imageVerified;

            internal void RequireOriginal(bool verifyImage = true)
            {
                if (Root == null || !Root.Valid || Guards == null || !Guards.Valid || Directory == null || !Directory.Valid || Source == null || Image == null || Store == null || !Store.HoldsRootWriter || Scope == null || Journal == null || Scope.RootIdentity != Root.Identity || !Scope.Target.Same(Journal.Target) || Scope.Nonce != Journal.SourceNonce || Scope.Digest != Journal.SourceScopeHash || Hash(Source) != Scope.Digest)
                {
                    throw new InvalidOperationException("original protected historical source is unavailable");
                }
                RequireAcl(Scope.Root, Scope.Sid);
                if (verifyImage && !imageVerified)
                {
                    if (Budget == null || !Budget.ReadImage(Image.Length) || Hash(Image) != Scope.Child.ImageHash)
                    {
                        throw new InvalidDataException("original image differs or the historical read budget is exhausted");
                    }
                    imageVerified = true;
                }
            }

            public void Dispose()
            {
                try
                {
                    if (Source != null)
                    {
                        Source.Dispose();
                    }
                }
                finally
                {
                    try
                    {
                        if (Image != null)
                        {
                            Image.Dispose();
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (Directory != null)
                            {
                                Directory.Dispose();
                            }
                        }
                        finally
                        {
                            if (Guards != null)
                            {
                                Guards.Dispose();
                            }
                        }
                    }
                }
            }
        }

        private sealed class HistoricalProcessPin : IDisposable
        {
            internal readonly HardThreadProcessFact Fact;

            private IntPtr handle;

            internal HistoricalProcessPin(int pid)
            {
                handle = OpenProcess(1052672u, false, pid);
                if (handle == IntPtr.Zero)
                {
                    Fact = new HardThreadProcessFact(false, pid, 0L, false, uint.MaxValue, Marshal.GetLastWin32Error());
                    return;
                }
                long born;
                long exit;
                long kernel;
                long user;
                bool processTimes = GetProcessTimes(handle, out born, out exit, out kernel, out user);
                Fact = new HardThreadProcessFact(true, (int)GetProcessId(handle), born, processTimes, WaitForSingleObject(handle, 0u), 0);
            }

            public void Dispose()
            {
                if (handle != IntPtr.Zero)
                {
                    IntPtr intPtr = handle;
                    handle = IntPtr.Zero;
                    if (!CloseHandle(intPtr))
                    {
                        throw new IOException("historical query handle closure is unconfirmed");
                    }
                }
            }
        }

        internal sealed class NativeGoneProof : IDisposable
        {
            private readonly HistoricalRegistration original;

            private HistoricalProcessPin first;

            private HistoricalProcessPin second;

            internal readonly HardThreadGoneReason Reason;

            private bool disposed;

            internal HardThreadTarget Target
            {
                get
                {
                    if (disposed)
                    {
                        throw new ObjectDisposedException("native gone proof");
                    }
                    return original.Scope.Target;
                }
            }

            private NativeGoneProof(HistoricalRegistration original, HistoricalProcessPin first, HistoricalProcessPin second, HardThreadGoneReason reason)
            {
                this.original = original;
                this.first = first;
                this.second = second;
                Reason = reason;
            }

            internal static NativeGoneProof Issue(object original)
            {
                HistoricalRegistration historicalRegistration = original as HistoricalRegistration;
                if (historicalRegistration == null || !object.ReferenceEquals(issuingHistorical, historicalRegistration))
                {
                    throw new InvalidOperationException("only the original protected historical probe can issue a gone fact");
                }
                historicalRegistration.RequireOriginal(false);
                HistoricalProcessPin historicalProcessPin = null;
                HistoricalProcessPin historicalProcessPin2 = null;
                try
                {
                    historicalProcessPin = new HistoricalProcessPin(historicalRegistration.Scope.Target.ProcessId);
                    if (!historicalProcessPin.Fact.Opened && historicalProcessPin.Fact.Error == 87)
                    {
                        historicalProcessPin2 = new HistoricalProcessPin(historicalRegistration.Scope.Target.ProcessId);
                    }
                    HardThreadGoneReason hardThreadGoneReason = HardThreadHistoricalPolicy.Gone(historicalRegistration.Scope.Target, historicalProcessPin.Fact, (historicalProcessPin2 == null) ? null : historicalProcessPin2.Fact);
                    if (hardThreadGoneReason == HardThreadGoneReason.None)
                    {
                        historicalProcessPin.Dispose();
                        if (historicalProcessPin2 != null)
                        {
                            historicalProcessPin2.Dispose();
                        }
                        return null;
                    }
                    historicalRegistration.RequireOriginal();
                    return new NativeGoneProof(historicalRegistration, historicalProcessPin, historicalProcessPin2, hardThreadGoneReason);
                }
                catch
                {
                    try
                    {
                        if (historicalProcessPin != null)
                        {
                            historicalProcessPin.Dispose();
                        }
                    }
                    finally
                    {
                        if (historicalProcessPin2 != null)
                        {
                            historicalProcessPin2.Dispose();
                        }
                    }
                    throw;
                }
            }

            internal void RequireCommit(HardThreadFileStore store, string root, HardThreadJournal current, string hash)
            {
                if (disposed || !object.ReferenceEquals(store, original.Store) || !string.Equals(Canonical(root), original.Scope.Root, StringComparison.OrdinalIgnoreCase) || current == null || !current.Target.Same(original.Journal.Target) || current.Revision != original.Journal.Revision || current.SourceNonce != original.Scope.Nonce || current.SourceScopeHash != original.Scope.Digest || hash != original.JournalHash || Reason == HardThreadGoneReason.None)
                {
                    throw new InvalidOperationException("gone metadata source changed");
                }
                original.RequireOriginal();
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                try
                {
                    if (first != null)
                    {
                        first.Dispose();
                        first = null;
                    }
                }
                finally
                {
                    if (second != null)
                    {
                        second.Dispose();
                        second = null;
                    }
                }
            }
        }
        private sealed class HistoricalTargetObservation { internal bool Exited; internal int FailedReads; internal long SessionSerial; }

        private static string HistoricalTargetPhase(string key, HardThreadTarget target)
        {
            lock (historicalGate)
            {
                HistoricalTargetObservation observation;
                if (!historicalTargets.TryGetValue(key, out observation)) historicalTargets[key] = observation = new HistoricalTargetObservation();
                long serial = PerformanceTuning.RecoveryCycleSerial;
                if (serial > observation.SessionSerial) { observation.SessionSerial = serial; observation.FailedReads = 0; }
                if (!observation.Exited && observation.FailedReads < 3)
                {
                    try
                    {
                        using (HistoricalProcessPin first = new HistoricalProcessPin(target.ProcessId))
                        using (HistoricalProcessPin second = !first.Fact.Opened && first.Fact.Error == 87 ? new HistoricalProcessPin(target.ProcessId) : null)
                        {
                            observation.Exited = HardThreadHistoricalPolicy.Gone(target, first.Fact, second == null ? null : second.Fact) != HardThreadGoneReason.None;
                            if (observation.Exited || (first.Fact.Opened && first.Fact.TimesKnown && first.Fact.Pid == target.ProcessId && first.Fact.Birth == target.ProcessCreation && first.Fact.Wait == 258)) observation.FailedReads = 0;
                            else observation.FailedReads++;
                        }
                    }
                    catch { observation.FailedReads++; }
                }
                return key + (observation.Exited ? "/target-exit" : "/live-or-unknown");
            }
        }

        private static bool HistoricalAttemptAllowed(string key)
        {
            lock (historicalGate) { int count; return !historicalFailures.TryGetValue(key, out count) || count < 3; }
        }

        private static bool BeginHistoricalAttempt(string key)
        {
            lock (historicalGate) { return HistoricalAttemptAllowed(key) && historicalInFlight.Add(key); }
        }

        private static bool HistoricalTargetReadable(string key)
        {
            lock (historicalGate)
            {
                HistoricalTargetObservation observation;
                return !historicalTargets.TryGetValue(key, out observation) || observation.Exited || observation.FailedReads < 3;
            }
        }

        private static void HistoricalAttemptEnded(string key, bool completed)
        {
            lock (historicalGate)
            {
                historicalInFlight.Remove(key);
                if (completed) { historicalFailures.Remove(key); return; }
                int count; historicalFailures.TryGetValue(key, out count);
                historicalFailures[key] = Math.Min(3, count + 1);
                Logger.Warn("关键线程历史恢复：范围=" + key + " 尝试=" + historicalFailures[key] + "/3；保留原始文件");
            }
        }

        internal static bool SettledLeftoversPending
        {
            get
            {
                return settledLeftovers;
            }
        }

        internal static bool RetainRecoveryUntilSettled(Func<bool> restore, Action<int> wait)
        {
            if (restore == null || wait == null)
            {
                throw new ArgumentNullException("original recovery operation");
            }
            int retainRecoveryUntilSettledValue = 250;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                bool restored = false;
                try
                {
                    restored = restore();
                }
                catch
                {
                }
                if (restored)
                {
                    return true;
                }
                Logger.Warn("关键线程守护恢复：尝试=" + attempt + "/3；保留原恢复权限");
                if (attempt < 3)
                {
                    try { wait(retainRecoveryUntilSettledValue); } catch { Thread.Sleep(retainRecoveryUntilSettledValue); }
                    retainRecoveryUntilSettledValue *= 2;
                }
            }
            return false;
        }

        private static void NoteSettledLeftovers()
        {
            settledLeftovers = true;
        }

        internal static bool SweepSettledGuards(string originalStartupData)
        {
            string enumerationKey = "cleanup-enumeration:" + originalStartupData + "/" + PerformanceTuning.RecoveryCycleSerial;
            if (!HistoricalAttemptAllowed(enumerationKey)) return false;
            settledLeftovers = false;
            bool sweepSettledGuardsSucceeded = false;
            try
            {
                if (string.IsNullOrEmpty(originalStartupData) || !Path.IsPathRooted(originalStartupData))
                {
                    return false;
                }
                string canonicalResult = Canonical(Path.Combine(originalStartupData, "CriticalThreadHardAffinityV1"));
                string guardsPath = Path.Combine(canonicalResult, "guards");
                if (HasControllerDebt(canonicalResult))
                {
                    return false;
                }
                if (!Directory.Exists(guardsPath))
                {
                    return sweepSettledGuardsSucceeded = true;
                }
                string sid = UserSid();
                RequireAcl(canonicalResult, sid);
                RequireAcl(guardsPath, sid, false);
                NoReparse(guardsPath);
                sweepSettledGuardsSucceeded = true;
                string[] directories = Directory.GetDirectories(guardsPath);
                HistoricalAttemptEnded(enumerationKey, true);
                foreach (string entryText in directories)
                {
                    Guid result;
                    if (Guid.TryParseExact(Path.GetFileName(entryText), "N", out result))
                    {
                        string resultText = result.ToString("N");
                        if (!File.Exists(Path.Combine(canonicalResult, "hard-thread-" + resultText + ".xml")) && !File.Exists(Path.Combine(canonicalResult, "hard-thread-" + resultText + ".lock")) && !SweepSettledGuard(entryText))
                        {
                            sweepSettledGuardsSucceeded = false;
                        }
                    }
                }
                return sweepSettledGuardsSucceeded;
            }
            catch
            {
                HistoricalAttemptEnded(enumerationKey, false);
                sweepSettledGuardsSucceeded = false;
                return false;
            }
            finally
            {
                if (!sweepSettledGuardsSucceeded)
                {
                    settledLeftovers = true;
                }
            }
        }

        private static bool SweepSettledGuard(string directory)
        {
            string key = "cleanup:" + directory + "/" + PerformanceTuning.RecoveryCycleSerial;
            if (!BeginHistoricalAttempt(key)) return false;
            bool completed = false;
            try { completed = SweepSettledGuardCore(directory); return completed; }
            finally { HistoricalAttemptEnded(key, completed); }
        }

        private static bool SweepSettledGuardCore(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            string[] fileSystemEntries = Directory.GetFileSystemEntries(directory);
            foreach (string path in fileSystemEntries)
            {
                if (Array.IndexOf(SettledLeftoverNames, Path.GetFileName(path)) < 0)
                {
                    return false;
                }
            }
            string[] settledLeftoverNames = SettledLeftoverNames;
            foreach (string path2 in settledLeftoverNames)
            {
                string path3 = Path.Combine(directory, path2);
                if (File.Exists(path3) && !DeleteSettledEntry(path3, false))
                {
                    return false;
                }
            }
            return DeleteSettledEntry(directory, true);
        }

        private static bool DeleteSettledEntry(string path, bool directory)
        {
            IntPtr intPtr = CreateFile(path, 65664u, 7u, IntPtr.Zero, 3u, directory ? 35651584u : 2097152u, IntPtr.Zero);
            if (intPtr == IntPtr.Zero || intPtr == new IntPtr(-1))
            {
                return false;
            }
            try
            {
                FileIdentity info;
                if (!GetFileInformationByHandle(intPtr, out info) || (info.Attributes & 0x400u) != 0 || (info.Attributes & 0x10) != 0 != directory)
                {
                    return false;
                }
                DeleteDisposition deleteDisposition = default(DeleteDisposition);
                deleteDisposition.Delete = 1;
                DeleteDisposition information = deleteDisposition;
                return SetFileInformationByHandle(intPtr, 4, ref information, 1u);
            }
            finally
            {
                CloseHandle(intPtr);
            }
        }

        private static byte[] ScopeBytes(FileStream source)
        {
            if (source.Length < 32 || source.Length > 65536)
            {
                throw new InvalidDataException("bounded original historical scope required");
            }
            byte[] rawBytes = new byte[(int)source.Length];
            int scopeBytesValue;
            for (int i = 0; i < rawBytes.Length; i += scopeBytesValue)
            {
                scopeBytesValue = source.Read(rawBytes, i, rawBytes.Length - i);
                if (scopeBytesValue <= 0)
                {
                    throw new EndOfStreamException();
                }
            }
            if (source.ReadByte() != -1)
            {
                throw new InvalidDataException("historical source grew during capture");
            }
            return rawBytes;
        }

        internal static bool TrySettleHistoricalGone(string originalStartupData)
        {
            string enumerationKey = "journal-enumeration:" + originalStartupData + "/" + PerformanceTuning.RecoveryCycleSerial;
            if (!HistoricalAttemptAllowed(enumerationKey)) return false;
            try
            {
                if (string.IsNullOrEmpty(originalStartupData) || !Path.IsPathRooted(originalStartupData))
                {
                    return false;
                }
                string canonicalResult = Canonical(Path.Combine(originalStartupData, "CriticalThreadHardAffinityV1"));
                string userSidResult = UserSid();
                if (HasControllerDebt(canonicalResult))
                {
                    return false;
                }
                if (!Directory.Exists(canonicalResult))
                {
                    return !HardThreadFileStore.CanonicalRootHasRecoveryDebt(canonicalResult);
                }
                RequireAcl(canonicalResult, userSidResult);
                using (RootPin root = new RootPin(canonicalResult, userSidResult))
                {
                    HardThreadHistoricalBudget hardThreadHistoricalBudget = new HardThreadHistoricalBudget();
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    HardThreadJournal[] historicalJournalsResult;
                    using (HardThreadFileStore hardThreadFileStore = new HardThreadFileStore(canonicalResult))
                    {
                        historicalJournalsResult = hardThreadFileStore.HistoricalJournals();
                    }
                    HistoricalAttemptEnded(enumerationKey, true);
                    bool recoveryPending = false;
                    HardThreadJournal[] hardThreadJournalItems = historicalJournalsResult;
                    foreach (HardThreadJournal hardThreadJournal in hardThreadJournalItems)
                    {
                        bool hasPendingReceipt = false;
                        foreach (HardThreadReceipt row in hardThreadJournal.Rows)
                        {
                            if (!row.Terminal)
                            {
                                hasPendingReceipt = true;
                            }
                        }
                        if (!hasPendingReceipt)
                        {
                            continue;
                        }
                        string observationKey = "journal:" + canonicalResult + "/" + hardThreadJournal.Target.SessionId + "/" + hardThreadJournal.Target.ProcessCreation;
                        string retryKey = HistoricalTargetPhase(observationKey, hardThreadJournal.Target);
                        if (!HistoricalTargetReadable(observationKey)) { recoveryPending = true; continue; }
                        if (!HistoricalAttemptAllowed(retryKey)) { recoveryPending = true; continue; }
                        if (!hardThreadHistoricalBudget.BeginSession(stopwatch.ElapsedMilliseconds))
                        {
                            recoveryPending = true;
                            break;
                        }
                        bool completed = false;
                        if (!BeginHistoricalAttempt(retryKey)) { recoveryPending = true; continue; }
                        try
                        {
                            using (HardThreadFileStore hardThreadFileStore2 = new HardThreadFileStore(canonicalResult))
                            {
                                using (HistoricalRegistration historicalRegistration = new HistoricalRegistration())
                                {
                                    historicalRegistration.Store = hardThreadFileStore2;
                                    historicalRegistration.Root = root;
                                    historicalRegistration.Budget = hardThreadHistoricalBudget;
                                    historicalRegistration.Journal = hardThreadFileStore2.ReadHistorical(hardThreadJournal.Target);
                                    if (string.IsNullOrEmpty(historicalRegistration.Journal.SourceNonce) || string.IsNullOrEmpty(historicalRegistration.Journal.SourceScopeHash))
                                    {
                                        throw new InvalidDataException("legacy historical source has no original host binding");
                                    }
                                    string guardsPath = Path.Combine(canonicalResult, "guards", hardThreadJournal.Target.SessionId.ToString("N"));
                                    historicalRegistration.Guards = new DirectoryPin(Path.GetDirectoryName(guardsPath), userSidResult);
                                    historicalRegistration.Directory = new DirectoryPin(guardsPath, userSidResult);
                                    string path = Path.Combine(guardsPath, "scope.xml");
                                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                                    {
                                        throw new InvalidDataException("original scope reparse source refused");
                                    }
                                    historicalRegistration.Source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                                    byte[] bytes = ScopeBytes(historicalRegistration.Source);
                                    historicalRegistration.Scope = Scope.Parse(bytes, canonicalResult, hardThreadJournal.Target.SessionId, historicalRegistration.Journal.SourceNonce, userSidResult, new NativeOrigin(), false);
                                    if ((File.GetAttributes(historicalRegistration.Scope.Child.Path) & FileAttributes.ReparsePoint) != 0)
                                    {
                                        throw new InvalidDataException("original guardian image reparse source refused");
                                    }
                                    historicalRegistration.Image = new FileStream(historicalRegistration.Scope.Child.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                                    historicalRegistration.JournalHash = hardThreadFileStore2.HistoricalHash(hardThreadJournal.Target);
                                    historicalRegistration.RequireOriginal(false);
                                    NativeGoneProof nativeGoneProof;
                                    lock (historicalGate)
                                    {
                                        issuingHistorical = historicalRegistration;
                                        try
                                        {
                                            nativeGoneProof = NativeGoneProof.Issue(historicalRegistration);
                                        }
                                        finally
                                        {
                                            issuingHistorical = null;
                                        }
                                    }
                                    if (nativeGoneProof == null)
                                    {
                                        completed = true; // 有效且存活的目标属于成功观测，不是执行失败。
                                        recoveryPending = true;
                                        continue;
                                    }
                                    using (nativeGoneProof)
                                    {
                                        hardThreadFileStore2.CommitHistoricalGone(nativeGoneProof);
                                        completed = true;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            completed = false;
                            recoveryPending = true;
                        }
                        finally { HistoricalAttemptEnded(retryKey, completed); }
                    }
                    return !recoveryPending && !HardThreadFileStore.CanonicalRootHasRecoveryDebt(canonicalResult);
                }
            }
            catch
            {
                HistoricalAttemptEnded(enumerationKey, false);
                return false;
            }
        }

    }
}

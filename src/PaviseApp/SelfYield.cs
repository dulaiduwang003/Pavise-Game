using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class SelfYield
    {
        private sealed class EmptyHelperScope : IDisposable
        {
            public void Dispose()
            {
            }
        }

        private sealed class HelperScope : IDisposable
        {
            internal IYieldChild Child;

            internal uint[] Original;

            internal uint[] Desired;

            internal bool EndRequested;

            internal bool Closed;

            internal int RestoreFailures;

            internal long RestoreAfter;
            internal bool Exited;

            internal bool Restore()
            {
                if (Closed)
                {
                    return true;
                }
                if (Exited) { Child.Dispose(); Closed = true; return true; }
                if (RestoreFailures >= 3) { RecoveryAttemptTrace.Exhausted(ObserveExit); return false; }
                if (DateTime.UtcNow.Ticks < RestoreAfter) { RecoveryAttemptTrace.DeferredUntil(RestoreAfter); return false; }
                uint[] ids;
                int readResult = Child.Read(out ids);
                if (readResult < 0)
                {
                    return Failed();
                }
                if (readResult == 0 && NativeYieldChild.Same(ids, Desired) && !Child.Write(Desired, Original))
                {
                    return Failed();
                }
                Child.Dispose();
                Closed = true;
                return true;
            }

            private bool Failed()
            {
                RestoreFailures++;
                RestoreAfter = DateTime.UtcNow.AddSeconds(30).Ticks;
                if (RestoreFailures >= 3) RecoveryAttemptTrace.Exhausted(ObserveExit);
                else RecoveryAttemptTrace.DeferredUntil(RestoreAfter);
                Logger.Warn("自身让路辅助恢复失败，尝试=" + RestoreFailures + "/3；保留所有权");
                return false;
            }
            private bool ObserveExit()
            {
                lock (sync)
                {
                    if (Closed) return true;
                    uint[] ignored;
                    if (!Exited && Child.Read(out ignored) > 0) Exited = true;
                    return Exited;
                }
            }

            public void Dispose()
            {
                lock (sync)
                {
                    EndRequested = true;
                    if (Restore())
                    {
                        helpers.Remove(this);
                    }
                }
            }
        }

        private static readonly object sync = new object();

        private static bool engaged;

        private static long session;
        private static int applyFailures;
        private static int restoreFailures;
        private static long applyAfter;
        private static long restoreAfter;

        internal static void BeginSession(long value)
        {
            lock (sync)
            {
                if (value <= session) return;
                session = value;
                applyFailures = restoreFailures = 0;
                applyAfter = restoreAfter = 0;
                foreach (HelperScope helper in helpers) { helper.RestoreFailures = 0; helper.RestoreAfter = 0; }
            }
        }

        private static uint origPriority;

        private static int origIo = -1;

        private static uint[] helperCpuSets;

        private static int helperRendererPid;

        private static long helperRendererBirth;

        private static readonly List<HelperScope> helpers = new List<HelperScope>();

        private static readonly IDisposable emptyHelper = new EmptyHelperScope();

        private static IntPtr Self
        {
            get
            {
                return (IntPtr)(-1);
            }
        }

        public static bool Engaged
        {
            get
            {
                lock (sync)
                {
                    return engaged;
                }
            }
        }

        internal static bool HasResidue
        {
            get
            {
                lock (sync)
                {
                    if (engaged) return true;
                    foreach (HelperScope helper in helpers) if (!helper.Closed) return true;
                    return false;
                }
            }
        }

        public static void Engage()
        {
            lock (sync)
            {
                if (engaged)
                {
                    return;
                }
                if (applyFailures >= 3 || DateTime.UtcNow.Ticks < applyAfter) return;
                IntPtr self = Self;
                uint priorityClass = Native.GetPriorityClass(self);
                if (priorityClass != 0)
                {
                    origPriority = priorityClass;
                    origIo = Native.QueryIoPriority(self);
                    helperCpuSets = null;
                    bool priorityLowered = priorityClass == 16384 || Native.SetPriorityClass(self, 16384u);
                    bool ioLowered = origIo >= 0 && origIo != 1 && Native.TrySetIoPriority(self, 1);
                    engaged = priorityLowered || ioLowered;
                    if (engaged)
                    {
                        applyFailures = 0;
                        Logger.Log(Lang.T("log.selfyield.1", 0));
                    }
                }
                if (!engaged)
                {
                    applyFailures++;
                    applyAfter = DateTime.UtcNow.AddSeconds(30).Ticks;
                    Logger.Warn("自身让路应用失败，尝试=" + applyFailures + "/3");
                }
            }
        }

        public static void Release()
        {
            lock (sync)
            {
                helperCpuSets = null;
                helperRendererPid = 0;
                helperRendererBirth = 0L;
                ReleaseHelpersLocked();
                if (engaged)
                {
                    if (restoreFailures >= 3) { RecoveryAttemptTrace.Exhausted(); return; }
                    if (DateTime.UtcNow.Ticks < restoreAfter) { RecoveryAttemptTrace.DeferredUntil(restoreAfter); return; }
                    bool restored = true;
                    IntPtr self = Self;
                    if (origPriority != 0)
                    {
                        uint current = Native.GetPriorityClass(self);
                        restored = current != 0 && (current != 16384 || current == origPriority || (Native.SetPriorityClass(self, origPriority) && Native.GetPriorityClass(self) == origPriority));
                    }
                    if (origIo >= 0)
                    {
                        int current = Native.QueryIoPriority(self);
                        restored &= current >= 0 && (current != 1 || current == origIo || (Native.TrySetIoPriority(self, origIo) && Native.QueryIoPriority(self) == origIo));
                    }
                    if (!restored)
                    {
                        restoreFailures++;
                        restoreAfter = DateTime.UtcNow.AddSeconds(30).Ticks;
                        RecoveryAttemptTrace.FailureSchedule(restoreFailures, 3, restoreAfter);
                        Logger.Warn("自身让路恢复失败，尝试=" + restoreFailures + "/3；保留所有权");
                        return;
                    }
                    engaged = false;
                    restoreFailures = 0;
                    restoreAfter = 0;
                    Logger.Log(Lang.T("log.selfyield.2", 0));
                }
            }
        }

        internal static bool HelperMasksSeparate(ulong game, ulong background, ulong all)
        {
            if (game != 0 && background != 0 && all != 0 && (game & ~all) == 0 && (background & ~all) == 0)
            {
                return (game & background) == 0;
            }
            return false;
        }

        private static bool HelperCoresSeparateLocked()
        {
            ulong background;
            return HelperCoresSeparateLocked(out background);
        }

        private static bool HelperCoresSeparateLocked(out ulong background)
        {
            background = 0uL;
            if (helperRendererPid <= 0 || helperRendererBirth <= 0 || helperCpuSets == null || CpuTopology.MultiGroup || !CpuTopology.TopologySourcesAgree || CpuTopology.AllMaskReconciled)
            {
                return false;
            }
            HashSet<uint> seenCpuSetIds = new HashSet<uint>();
            foreach (ulong physicalCoreMask in CpuTopology.PhysicalCoreMasks())
            {
                uint[] cpuSetIdsForResult = CpuTopology.CpuSetIdsFor(physicalCoreMask);
                if (cpuSetIdsForResult == null)
                {
                    return false;
                }
                foreach (uint cpuSetId in cpuSetIdsForResult)
                {
                    foreach (uint helperCpuSetId in helperCpuSets)
                    {
                        if (cpuSetId == helperCpuSetId)
                        {
                            background |= physicalCoreMask;
                            seenCpuSetIds.Add(cpuSetId);
                        }
                    }
                }
            }
            if (seenCpuSetIds.Count != helperCpuSets.Length)
            {
                return false;
            }
            IntPtr processHandle = Native.OpenProcess(1052672, false, helperRendererPid);
            if (processHandle == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                long creation;
                long cpu;
                ulong io;
                ulong mask;
                return Native.WaitForSingleObject(processHandle, 0u) == 258 && Native.QueryProcessSample(processHandle, out creation, out cpu, out io) && creation == helperRendererBirth && Native.TryQueryAffinity(processHandle, out mask) && HelperMasksSeparate(mask, background, CpuTopology.AllMask);
            }
            finally
            {
                Native.CloseHandle(processHandle);
            }
        }

        internal static void TrackHelperRenderer(int pid, long birth)
        {
            if (!OvernightPerformancePolicy.Enabled)
            {
                return;
            }
            lock (sync)
            {
                bool rendererChanged = pid != helperRendererPid || birth != helperRendererBirth;
                helperRendererPid = pid;
                helperRendererBirth = birth;
                if (rendererChanged || (helpers.Count > 0 && !HelperCoresSeparateLocked()))
                {
                    ReleaseHelpersLocked();
                }
            }
        }

        private static void DrainFinishedHelpersLocked()
        {
            for (int helpersIndex = helpers.Count - 1; helpersIndex >= 0; helpersIndex--)
            {
                if (helpers[helpersIndex].EndRequested && helpers[helpersIndex].Restore())
                {
                    helpers.RemoveAt(helpersIndex);
                }
            }
        }

        private static void ReleaseHelpersLocked()
        {
            foreach (HelperScope helper in helpers)
            {
                helper.EndRequested = true;
            }
            DrainFinishedHelpersLocked();
        }

        private static IDisposable BeginHelperLocked(IYieldChild child, uint[] desired)
        {
            DrainFinishedHelpersLocked();
            if (child == null)
            {
                return emptyHelper;
            }
            uint[] ids;
            if (!child.Owned || desired == null || desired.Length == 0 || !NativeYieldChild.Same(desired, desired) || helpers.Count >= 32 || child.Read(out ids) != 0 || ids == null || ids.Length != 0)
            {
                child.Dispose();
                return emptyHelper;
            }
            HelperScope currentHelperScope = new HelperScope();
            currentHelperScope.Child = child;
            currentHelperScope.Original = ids;
            currentHelperScope.Desired = (uint[])desired.Clone();
            helpers.Add(currentHelperScope);
            if (!child.Write(ids, currentHelperScope.Desired))
            {
                currentHelperScope.EndRequested = true;
                if (currentHelperScope.Restore())
                {
                    helpers.Remove(currentHelperScope);
                }
            }
            return currentHelperScope;
        }

        internal static IDisposable OwnHelperScope(IntPtr launchedChild)
        {
            if (!OvernightPerformancePolicy.Enabled)
            {
                return emptyHelper;
            }
            lock (sync)
            {
                ulong background;
                ulong mask;
                if (!engaged || helperCpuSets == null || !Native.CpuSetsMatch(Self, helperCpuSets) || !HelperCoresSeparateLocked(out background) || !Native.TryQueryAffinity(launchedChild, out mask) || (mask & background) == 0)
                {
                    return emptyHelper;
                }
                return BeginHelperLocked(NativeYieldChild.Open(launchedChild), helperCpuSets);
            }
        }
    }
}

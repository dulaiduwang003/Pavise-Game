using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal interface IYieldChild : IDisposable
    {
        bool Owned { get; }
        int Read(out uint[] ids);
        bool Write(uint[] expected, uint[] desired);
    }

    internal sealed class NativeYieldChild : IYieldChild
    {
        private IntPtr handle;
        private NativeYieldChild(IntPtr owned) { handle = owned; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source,
            IntPtr targetProcess, out IntPtr duplicate, uint access, bool inherit, uint options);
        internal static NativeYieldChild Open(IntPtr launchedChild)
        {
            if (launchedChild == IntPtr.Zero || Native.ParentProcessId(launchedChild) != Process.GetCurrentProcess().Id) return null;
            IntPtr duplicate, self = Native.GetCurrentProcess();
            uint access = Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION | Native.SYNCHRONIZE;
            return DuplicateHandle(self, launchedChild, self, out duplicate, access, false, 0)
                ? new NativeYieldChild(duplicate) : null;
        }
        public bool Owned { get { return handle != IntPtr.Zero; } }
        public int Read(out uint[] ids)
        {
            ids = null; if (handle == IntPtr.Zero) return 1;
            uint wait = Native.WaitForSingleObject(handle, 0);
            if (wait == 0) return 1; if (wait != 258) return -1;
            ids = Native.QueryCpuSets(handle); return ids == null ? -1 : 0;
        }
        public bool Write(uint[] expected, uint[] desired)
        {
            uint[] current;
            if (Read(out current) != 0 || !Same(current, expected) || desired == null) return false;
            return Native.RestoreCpuSetsVerified(handle, desired);
        }
        internal static bool Same(uint[] first, uint[] second)
        {
            if (first == null || second == null || first.Length != second.Length) return false;
            var ids = new HashSet<uint>(first); if (ids.Count != first.Length) return false;
            foreach (uint id in second) if (!ids.Remove(id)) return false;
            return ids.Count == 0;
        }
        public void Dispose() { if (handle != IntPtr.Zero) { Native.CloseHandle(handle); handle = IntPtr.Zero; } }
    }

    internal static partial class SelfYield
    {
        private static uint[] helperCpuSets;
        private static int helperRendererPid;
        private static long helperRendererBirth;
        private static readonly List<HelperScope> helpers = new List<HelperScope>();
        private sealed class EmptyHelperScope : IDisposable { public void Dispose() { } }
        private static readonly IDisposable emptyHelper = new EmptyHelperScope();
        internal static bool HelperMasksSeparate(ulong game, ulong background, ulong all)
        { return game != 0 && background != 0 && all != 0 && (game & ~all) == 0 && (background & ~all) == 0 && (game & background) == 0; }
        private static bool HelperCoresSeparateLocked()
        { ulong background; return HelperCoresSeparateLocked(out background); }
        private static bool HelperCoresSeparateLocked(out ulong background)
        {
            background = 0;
            if (helperRendererPid <= 0 || helperRendererBirth <= 0 || helperCpuSets == null || CpuTopology.MultiGroup
                || !CpuTopology.TopologySourcesAgree || CpuTopology.AllMaskReconciled) return false;
            var found = new HashSet<uint>();
            foreach (ulong core in CpuTopology.PhysicalCoreMasks())
            {
                uint[] ids = CpuTopology.CpuSetIdsFor(core); if (ids == null) return false;
                foreach (uint id in ids) foreach (uint selected in helperCpuSets)
                    if (id == selected) { background |= core; found.Add(id); }
            }
            if (found.Count != helperCpuSets.Length) return false;
            IntPtr game = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.SYNCHRONIZE, false, helperRendererPid);
            if (game == IntPtr.Zero) return false;
            try
            {
                long birth, cpu; ulong io, affinity;
                return Native.WaitForSingleObject(game, 0) == 258 && Native.QueryProcessSample(game, out birth, out cpu, out io)
                    && birth == helperRendererBirth && Native.TryQueryAffinity(game, out affinity)
                    && HelperMasksSeparate(affinity, background, CpuTopology.AllMask);
            }
            finally { Native.CloseHandle(game); }
        }
        internal static void TrackHelperRenderer(int pid, long birth)
        {
            if (!OvernightPerformancePolicy.Enabled) return;
            lock (sync)
            {
                bool changed = pid != helperRendererPid || birth != helperRendererBirth;
                helperRendererPid = pid; helperRendererBirth = birth;
                if (changed || (helpers.Count > 0 && !HelperCoresSeparateLocked())) ReleaseHelpersLocked();
            }
        }
        private sealed class HelperScope : IDisposable
        {
            internal IYieldChild Child;
            internal uint[] Original, Desired;
            internal bool EndRequested, Closed;
            internal bool Restore()
            {
                if (Closed) return true;
                uint[] current; int state = Child.Read(out current);
                if (state < 0) return false;
                if (state == 0 && NativeYieldChild.Same(current, Desired) && !Child.Write(Desired, Original)) return false;
                Child.Dispose(); Closed = true; return true;
            }
            public void Dispose()
            {
                lock (sync) { EndRequested = true; if (Restore()) helpers.Remove(this); }
            }
        }
        private static void DrainFinishedHelpersLocked()
        {
            for (int i = helpers.Count - 1; i >= 0; --i)
                if (helpers[i].EndRequested && helpers[i].Restore()) helpers.RemoveAt(i);
        }
        private static void ReleaseHelpersLocked()
        {
            foreach (var helper in helpers) helper.EndRequested = true;
            DrainFinishedHelpersLocked();
        }
        private static IDisposable BeginHelperLocked(IYieldChild child, uint[] desired)
        {
            DrainFinishedHelpersLocked();
            uint[] original;
            if (child == null) return emptyHelper;
            if (!child.Owned || desired == null || desired.Length == 0 || !NativeYieldChild.Same(desired, desired)
                || helpers.Count >= 32 || child.Read(out original) != 0 || original == null || original.Length != 0)
            { child.Dispose(); return emptyHelper; }
            var scope = new HelperScope { Child = child, Original = original, Desired = (uint[])desired.Clone() };
            helpers.Add(scope);
            if (!child.Write(original, scope.Desired)) { scope.EndRequested = true; if (scope.Restore()) helpers.Remove(scope); }
            return scope;
        }
        internal static IDisposable OwnHelperScope(IntPtr launchedChild)
        {
            if (!OvernightPerformancePolicy.Enabled) return emptyHelper;
            lock (sync)
            {
                ulong background, allowed;
                if (!engaged || helperCpuSets == null || !Native.CpuSetsMatch(Self, helperCpuSets)
                    || !HelperCoresSeparateLocked(out background) || !Native.TryQueryAffinity(launchedChild, out allowed)
                    || (allowed & background) == 0) return emptyHelper;
                return BeginHelperLocked(NativeYieldChild.Open(launchedChild), helperCpuSets);
            }
        }
#if PAVISE_SELFTEST
        internal static IDisposable HelperScopeForTest(IYieldChild child, uint[] desired)
        { lock (sync) return BeginHelperLocked(child, desired); }
        internal static int PendingHelpersForTest { get { lock (sync) return helpers.Count; } }
#endif
    }
}

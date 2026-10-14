using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PaviseApp
{
    // 最后一个状态句柄关闭时自动恢复挂起。
    internal static class FreezeNative
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("ntdll.dll")]
        private static extern int NtCreateProcessStateChange(out IntPtr state, uint access,
            IntPtr attributes, IntPtr process, uint reserved);
        [DllImport("ntdll.dll")]
        private static extern int NtChangeProcessState(IntPtr state, IntPtr process,
            int change, IntPtr info, UIntPtr size, uint reserved);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessCritical(IntPtr process, out bool critical);

        internal static readonly bool Supported = Detect();
        internal static bool IsAlive(IntPtr process)
        { return process != IntPtr.Zero && Native.WaitForSingleObject(process, 0) == 258; }
        private static bool Detect()
        {
            try
            {
                IntPtr module = GetModuleHandle("ntdll.dll");
                return GetProcAddress(module, "NtCreateProcessStateChange") != IntPtr.Zero
                    && GetProcAddress(module, "NtChangeProcessState") != IntPtr.Zero;
            }
            catch { return false; }
        }

        private sealed class StateLease : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal StateLease(IntPtr value) : base(true) { SetHandle(value); }
            protected override bool ReleaseHandle() { return Native.CloseHandle(handle); }
        }

        internal static IDisposable Acquire(FreezeTarget target, int ownerSession, int ownerPid)
        { string detail; return Acquire(target, ownerSession, ownerPid, out detail); }

        internal static IDisposable Acquire(FreezeTarget target, int ownerSession, int ownerPid, out string detail)
        {
            detail = "unsupported-api";
            if (!Supported) return null;
            detail = "invalid-target";
            if (target == null || target.Pid <= 4 || target.Pid == ownerPid) return null;
            IntPtr process = Native.OpenProcess(0x100000 | 0x0200 | 0x0800 | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, target.Pid);
            if (process == IntPtr.Zero) { detail = "OpenProcess win32=" + Marshal.GetLastWin32Error(); return null; }
            StateLease lease = null;
            try
            {
                long creation, cpu; ulong io;
                int session; bool critical;
                detail = "creation-query";
                if (!Native.QueryProcessSample(process, out creation, out cpu, out io)) return null;
                detail = "creation-mismatch"; if (creation != target.Creation) return null;
                detail = "path-mismatch-or-unavailable";
                if (!string.Equals(Native.ImagePath(process), target.Path, StringComparison.OrdinalIgnoreCase)) return null;
                string name = System.IO.Path.GetFileNameWithoutExtension(target.Path);
                detail = "protected-runtime";
                if (FamilyBoundary.CatalogProtected(target.Pid, creation, name, target.Path)) return null;
                detail = "unapproved-background-role";
#if PAVISE_SELFTEST
                bool ownedTestImage = WhitelistRule.PathEquals(target.Path, typeof(FreezeNative).Assembly.Location);
#else
                bool ownedTestImage = false;
#endif
                if (!ownedTestImage && !StrongBackgroundPolicy.ApprovedImage(target.Path)) return null;
                detail = "session-query";
                if (!Native.TryGetLiveProcessSessionId(process, target.Pid, out session)) return null;
                detail = "session-mismatch"; if (session != ownerSession) return null;
                if (!IsProcessCritical(process, out critical))
                { detail = "IsProcessCritical win32=" + Marshal.GetLastWin32Error(); return null; }
                detail = "critical-process"; if (critical) return null;
                detail = "process-exited"; if (!IsAlive(process)) return null;
                IntPtr state;
                int status = NtCreateProcessStateChange(out state, 1, IntPtr.Zero, process, 0);
                detail = "NtCreateProcessStateChange ntstatus=0x" + status.ToString("X8");
                if (state != IntPtr.Zero) lease = new StateLease(state);
                if (status < 0 || lease == null) return null;
                status = NtChangeProcessState(state, process, 0, IntPtr.Zero, UIntPtr.Zero, 0);
                detail = "NtChangeProcessState ntstatus=0x" + status.ToString("X8");
                if (status < 0) return null;
                detail = "state-handle-owned ntstatus=0x" + status.ToString("X8");
                StateLease result = lease; lease = null;
                return result;
            }
            catch (Exception error) { detail += " exception=" + error.GetType().Name; return null; }
            finally
            {
                if (lease != null) lease.Dispose();
                Native.CloseHandle(process);
            }
        }
    }
}

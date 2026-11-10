using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class NativeYieldChild : IYieldChild, IDisposable
    {
        private IntPtr handle;

        public bool Owned
        {
            get
            {
                return handle != IntPtr.Zero;
            }
        }

        private NativeYieldChild(IntPtr owned)
        {
            handle = owned;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr duplicate, uint access, bool inherit, uint options);

        internal static NativeYieldChild Open(IntPtr launchedChild)
        {
            if (launchedChild == IntPtr.Zero || Native.ParentProcessId(launchedChild) != Process.GetCurrentProcess().Id)
            {
                return null;
            }
            IntPtr currentProcess = Native.GetCurrentProcess();
            uint access = 1060864u;
            IntPtr duplicate;
            if (!DuplicateHandle(currentProcess, launchedChild, currentProcess, out duplicate, access, false, 0u))
            {
                return null;
            }
            return new NativeYieldChild(duplicate);
        }

        public int Read(out uint[] ids)
        {
            ids = null;
            if (handle == IntPtr.Zero)
            {
                return 1;
            }
            switch (Native.WaitForSingleObject(handle, 0u))
            {
            case 0u:
                return 1;
            default:
                return -1;
            case 258u:
                ids = Native.QueryCpuSets(handle);
                if (ids != null)
                {
                    return 0;
                }
                return -1;
            }
        }

        public bool Write(uint[] expected, uint[] desired)
        {
            uint[] ids;
            if (Read(out ids) != 0 || !Same(ids, expected) || desired == null)
            {
                return false;
            }
            return Native.RestoreCpuSetsVerified(handle, desired);
        }

        internal static bool Same(uint[] first, uint[] second)
        {
            if (first == null || second == null || first.Length != second.Length)
            {
                return false;
            }
            HashSet<uint> firstSnapshot = new HashSet<uint>(first);
            if (firstSnapshot.Count != first.Length)
            {
                return false;
            }
            foreach (uint currentSecond in second)
            {
                if (!firstSnapshot.Remove(currentSecond))
                {
                    return false;
                }
            }
            return firstSnapshot.Count == 0;
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                Native.CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    // 保留启动句柄，只能停止本程序启动的 RTSS。
    internal sealed class RtssOwnedProcess : IRtssOwnedProcess
    {
        private readonly Process process;
        private readonly IntPtr handle;
        private readonly string path;
        private readonly int pid;
        private readonly long birth;
        private int stopWorker;

        private RtssOwnedProcess(Process process, string path)
        {
            this.process = process; this.path = path; pid = process.Id; handle = process.Handle;
            long exit, kernel, user;
            if (!GetProcessTimes(handle, out birth, out exit, out kernel, out user) || birth <= 0)
                throw new InvalidOperationException("RTSS process identity could not be retained.");
        }

        internal static IRtssOwnedProcess Start(ProcessStartInfo info)
        {
            Process process = Process.Start(info);
            if (process == null) throw new InvalidOperationException("RTSS did not create a process.");
            try { return new RtssOwnedProcess(process, info.FileName); }
            catch { process.Dispose(); throw; }
        }

        public bool HasExited
        {
            get { return Volatile.Read(ref stopWorker) == 0 && process.HasExited; }
        }

        public void RequestStop()
        {
            if (Interlocked.CompareExchange(ref stopWorker, 1, 0) != 0) return;
            try { RtssComponent.RequestOwnedRtssExit(pid, birth, path); } catch { }
            try
            {
                if (ThreadPool.QueueUserWorkItem(delegate {
                    try
                    {
                        for (int attempt = 0; attempt < 50 && !process.WaitForExit(100); attempt++)
                            RtssComponent.RequestOwnedRtssExit(pid, birth, path);
                        // 正常退出无响应五秒后，仅终止持有句柄的自有服务器。
                        if (!process.HasExited)
                        {
                            long actual, exit, kernel, user;
                            if (GetProcessTimes(handle, out actual, out exit, out kernel, out user)
                                && actual == birth && exit == 0)
                                TerminateProcess(handle, 0);
                        }
                    }
                    catch { }
                    finally { Volatile.Write(ref stopWorker, 0); }
                })) return;
            }
            catch { }
            Volatile.Write(ref stopWorker, 0);
        }

        public void Dispose() { process.Dispose(); }
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr process, out long birth, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    }
}

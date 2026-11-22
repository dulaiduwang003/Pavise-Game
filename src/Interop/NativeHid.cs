using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace PaviseApp
{
    internal enum HidProbeResult
    {
        Absent = 0,
        Holding = 1,
        NoInterface = 2,
        ProcessUnreadable = 3,
        Timeout = 4,
        InterfaceUnreadable = 5,
        WorkerUnavailable = 6
    }

    internal interface IHidProbeWorker
    {
        HidProbeResult Prepare();
        HidProbeResult Probe(int pid, long creation);
    }

    internal static class NativeHid
    {
        private const string WorkerArgument = "--hid-probe-worker";
        private const string Greeting = "PAVISE-HID/1";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetAbi();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr OpenScanner(out HidProbeResult result);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate HidProbeResult ProbeProcess(IntPtr scanner, uint pid, long creation);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CloseScanner(IntPtr scanner);

        private sealed class ScannerApi
        {
            internal OpenScanner Open;
            internal ProbeProcess Probe;
            internal CloseScanner Close;
        }

        internal static bool TryRunWorker(string[] args, out int exitCode)
        {
            exitCode = 1;
            if (args.Length == 0 || args[0] != WorkerArgument) return false;
            if (args.Length != 1) return true;
            try
            {
                if (!Console.IsInputRedirected || !Console.IsOutputRedirected || !Environment.Is64BitProcess) return true;
                ScannerApi api = Load();
                if (api == null) return true;
                RunWorker(api, Console.In, Console.Out);
                exitCode = 0;
            }
            catch (Exception) { }
            return true;
        }

        private static void RunWorker(ScannerApi api, TextReader input, TextWriter output)
        {
            IntPtr scanner = IntPtr.Zero;
            try
            {
                output.WriteLine(Greeting);
                output.Flush();
                string request;
                while ((request = input.ReadLine()) != null)
                {
                    HidProbeResult result;
                    if (request == "scan")
                    {
                        if (scanner != IntPtr.Zero) api.Close(scanner);
                        scanner = api.Open(out result);
                    }
                    else
                    {
                        string[] identity = request.Split(' ');
                        uint pid;
                        long creation;
                        if (identity.Length != 2 || !uint.TryParse(identity[0], NumberStyles.None, CultureInfo.InvariantCulture, out pid)
                            || !long.TryParse(identity[1], NumberStyles.None, CultureInfo.InvariantCulture, out creation)
                            || pid <= 4 || creation <= 0) return;
                        result = scanner == IntPtr.Zero
                            ? HidProbeResult.InterfaceUnreadable : api.Probe(scanner, pid, creation);
                    }
                    output.WriteLine(((int)result).ToString(CultureInfo.InvariantCulture));
                    output.Flush();
                }
            }
            finally
            {
                if (scanner != IntPtr.Zero) api.Close(scanner);
            }
        }

        private static ScannerApi Bind(IntPtr module)
        {
            if (module == IntPtr.Zero) return null;
            IntPtr abi = GetProcAddress(module, "PaviseHidAbi");
            IntPtr open = GetProcAddress(module, "PaviseHidOpen");
            IntPtr probe = GetProcAddress(module, "PaviseHidProbe");
            IntPtr close = GetProcAddress(module, "PaviseHidClose");
            if (abi == IntPtr.Zero || open == IntPtr.Zero || probe == IntPtr.Zero || close == IntPtr.Zero
                || ((GetAbi)Marshal.GetDelegateForFunctionPointer(abi, typeof(GetAbi)))() != 1) return null;
            return new ScannerApi
            {
                Open = (OpenScanner)Marshal.GetDelegateForFunctionPointer(open, typeof(OpenScanner)),
                Probe = (ProbeProcess)Marshal.GetDelegateForFunctionPointer(probe, typeof(ProbeProcess)),
                Close = (CloseScanner)Marshal.GetDelegateForFunctionPointer(close, typeof(CloseScanner))
            };
        }

#if PAVISE_EMBEDDED_NATIVE
        private static ScannerApi Load()
        {
            return Bind(GetModuleHandleW(null));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);
#else
        private static ScannerApi Load()
        {
            string path, reason;
            using (FileStream held = NativeImageFile.Prepare(NativeHidImage.Base64, NativeHidImage.Length, NativeHidImage.Sha256, "PaviseHid.dll", out path, out reason))
            {
                if (held == null) return null;
                IntPtr module = LoadLibraryExW(path, IntPtr.Zero, 0x00000800u);
                ScannerApi api = Bind(module);
                if (api == null && module != IntPtr.Zero) FreeLibrary(module);
                return api;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);
#endif

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        internal sealed class Worker : IHidProbeWorker, IDisposable
        {
            private const int ExtendedLimitInformation = 9;
            private const uint KillOnJobClose = 0x2000;
            private readonly string executable;
            private readonly string arguments;
            private readonly int startupTimeoutMs;
            private readonly int probeTimeoutMs;
            private Process child;
            private StreamWriter requests;
            private StreamReader replies;
            private IntPtr isolation;
            private bool assigned;
            private bool prepared;

            internal Worker() : this(typeof(NativeHid).Assembly.Location, WorkerArgument, 1500, 200) { }

            internal Worker(string executable, string arguments, int startupTimeoutMs, int probeTimeoutMs)
            {
                this.executable = executable;
                this.arguments = arguments;
                this.startupTimeoutMs = startupTimeoutMs;
                this.probeTimeoutMs = probeTimeoutMs;
            }

            public HidProbeResult Prepare()
            {
                prepared = false;
                if (child == null && !Start()) return HidProbeResult.WorkerUnavailable;
                HidProbeResult result = Request("scan", startupTimeoutMs);
                prepared = result == HidProbeResult.Absent;
                return result == HidProbeResult.Timeout ? HidProbeResult.InterfaceUnreadable : result;
            }

            public HidProbeResult Probe(int pid, long creation)
            {
                if (!prepared)
                {
                    HidProbeResult result = Prepare();
                    if (result != HidProbeResult.Absent) return result;
                }
                string request = pid.ToString(CultureInfo.InvariantCulture) + " " + creation.ToString(CultureInfo.InvariantCulture);
                return Request(request, probeTimeoutMs);
            }

            private bool Start()
            {
                try
                {
                    isolation = CreateJobObjectW(IntPtr.Zero, null);
                    if (isolation == IntPtr.Zero) return false;
                    var limits = new ExtendedLimits();
                    limits.Basic.Flags = KillOnJobClose;
                    if (!SetInformationJobObject(isolation, ExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimits))))
                    {
                        Stop();
                        return false;
                    }
                    child = Process.Start(new ProcessStartInfo(executable, arguments)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true
                    });
                    assigned = child != null && AssignProcessToJobObject(isolation, child.Handle);
                    if (!assigned)
                    {
                        Stop();
                        return false;
                    }
                    requests = child.StandardInput;
                    replies = child.StandardOutput;
                    Task<string> ready = replies.ReadLineAsync();
                    if (ready.Wait(startupTimeoutMs) && ready.Result == Greeting) return true;
                }
                catch (Exception) { }
                Stop();
                return false;
            }

            private HidProbeResult Request(string request, int timeoutMs)
            {
                try
                {
                    requests.WriteLine(request);
                    requests.Flush();
                    Task<string> response = replies.ReadLineAsync();
                    if (!response.Wait(timeoutMs))
                    {
                        Stop();
                        return HidProbeResult.Timeout;
                    }
                    int code;
                    if (int.TryParse(response.Result, NumberStyles.None, CultureInfo.InvariantCulture, out code)
                        && (code == 0 || code == 1 || code == 2 || code == 3 || code == 5))
                        return (HidProbeResult)code;
                }
                catch (Exception) { }
                Stop();
                return HidProbeResult.WorkerUnavailable;
            }

            private void Stop()
            {
                prepared = false;
                if (child != null && !assigned)
                {
                    // 加入隔离 Job 失败时只处理本次启动的辅助进程
                    try { if (!child.HasExited) child.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
                if (isolation != IntPtr.Zero)
                {
                    // 阻塞在驱动里的句柄查询随辅助进程回收 后台扫描线程不等驱动返回
                    CloseHandle(isolation);
                    isolation = IntPtr.Zero;
                }
                assigned = false;
                if (requests != null) { try { requests.Dispose(); } catch (IOException) { } requests = null; }
                if (replies != null) { replies.Dispose(); replies = null; }
                if (child == null) return;
                child.Dispose();
                child = null;
            }

            public void Dispose()
            {
                Stop();
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct BasicLimits
            {
                internal long ProcessTime, JobTime;
                internal uint Flags;
                internal UIntPtr MinimumWorkingSet, MaximumWorkingSet;
                internal uint ActiveProcesses;
                internal UIntPtr Affinity;
                internal uint Priority, Scheduling;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct ExtendedLimits
            {
                internal BasicLimits Basic;
                internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
                internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateJobObjectW(IntPtr security, string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetInformationJobObject(IntPtr job, int information, ref ExtendedLimits value, uint size);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [DllImport("kernel32.dll")]
            private static extern bool CloseHandle(IntPtr handle);
        }
    }
}

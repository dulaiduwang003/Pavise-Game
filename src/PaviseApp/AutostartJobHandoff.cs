using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    // Task Scheduler jobs can prevent independent guardians from surviving the main process.
    // A single WMI handoff verifies an elevated successor outside the job; failures retain this instance.
    internal static class AutostartJobHandoff
    {
        internal const string Argument = "--job-handoff";
        private const int CreateWaitMs = 10000;
        private const int SuccessorWaitMs = 15000;
        private const int PredecessorExitWaitMs = 20000;
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint BreakawayOk = 0x800;
        private const uint SilentBreakawayOk = 0x1000;

        private static string outcome;
        private static bool outcomeIsWarning;

        internal sealed class Incoming
        {
            internal string Token;
            internal int PredecessorPid;
        }

        internal static string[] Strip(string[] args, out Incoming incoming)
        {
            incoming = null;
            if (args == null) return args;
            int index = Array.FindIndex(args, a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return args;
            Guid token;
            int pid;
            if (index + 2 < args.Length && Guid.TryParseExact(args[index + 1], "N", out token)
                && int.TryParse(args[index + 2], out pid) && pid > 0)
            {
                incoming = new Incoming { Token = token.ToString("N"), PredecessorPid = pid };
            }
            int removed = Math.Min(3, args.Length - index);
            string[] rest = new string[args.Length - removed];
            Array.Copy(args, 0, rest, 0, index);
            Array.Copy(args, index + removed, rest, index, args.Length - index - removed);
            return rest;
        }

        // Predecessor side. True means the successor took over and this process must exit.
        internal static bool TryHandOff(string[] args)
        {
            if (!Program.IsAutostart(args)) return false;
            uint flags;
            if (!InBlockingJob(out flags)) return false;
            string token = Guid.NewGuid().ToString("N");
            try
            {
                using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(token)))
                {
                    string failure;
                    int successor;
                    string command = CommandLine(args, token, Process.GetCurrentProcess().Id);
                    if (!CreateOutsideJob(command, out failure, out successor))
                    {
                        Record(true, "result=kept reason=" + failure + " jobFlags=0x" + flags.ToString("X"));
                        return false;
                    }
                    if (ready.WaitOne(SuccessorWaitMs)) return true;
                    Record(true, "result=kept reason=successor-timeout successor=" + successor + " jobFlags=0x" + flags.ToString("X"));
                    return false;
                }
            }
            catch (Exception error)
            {
                Record(true, "result=kept reason=" + error.GetType().Name + " jobFlags=0x" + flags.ToString("X"));
                return false;
            }
        }

        // Successor side. False means the predecessor kept running and this process must exit quietly.
        internal static bool Adopt(Incoming incoming)
        {
            Process predecessor = OpenPredecessor(incoming.PredecessorPid);
            try
            {
                try
                {
                    using (EventWaitHandle ready = EventWaitHandle.OpenExisting(EventName(incoming.Token)))
                    {
                        ready.Set();
                    }
                }
                catch
                {
                }
                // A live predecessor retains the session; proceed only after it has exited.
                if (predecessor != null && !predecessor.WaitForExit(PredecessorExitWaitMs)) return false;
                bool inJob;
                bool known = IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out inJob);
                Record(known && inJob, "result=adopted predecessor=" + incoming.PredecessorPid
                    + " inJob=" + (known ? inJob.ToString() : "unknown"));
                return true;
            }
            finally
            {
                if (predecessor != null) predecessor.Dispose();
            }
        }

        internal static void LogOutcome()
        {
            string text = Interlocked.Exchange(ref outcome, null);
            if (text == null) return;
            if (outcomeIsWarning) Logger.Warn("STARTUP event=job-handoff " + text);
            else Logger.Info("STARTUP event=job-handoff " + text);
        }

        private static void Record(bool warning, string text)
        {
            outcomeIsWarning = warning;
            outcome = text;
        }

        private static Process OpenPredecessor(int pid)
        {
            Process candidate = null;
            try
            {
                candidate = Process.GetProcessById(pid);
                using (Process self = Process.GetCurrentProcess())
                {
                    if (string.Equals(candidate.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase)
                        && candidate.StartTime <= self.StartTime)
                    {
                        Process accepted = candidate;
                        candidate = null;
                        return accepted;
                    }
                }
            }
            catch
            {
            }
            finally
            {
                if (candidate != null) candidate.Dispose();
            }
            return null;
        }

        private static bool InBlockingJob(out uint flags)
        {
            flags = 0;
            bool inJob;
            if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out inJob) || !inJob) return false;
            ExtendedLimitInformation info;
            if (!QueryInformationJobObject(IntPtr.Zero, JobObjectExtendedLimitInformation, out info,
                Marshal.SizeOf(typeof(ExtendedLimitInformation)), IntPtr.Zero)) return false;
            flags = info.Basic.LimitFlags;
            return (flags & (BreakawayOk | SilentBreakawayOk)) == 0;
        }

        private static string CommandLine(string[] args, string token, int pid)
        {
            var command = new StringBuilder(TaskHelper.QuoteCommandArgument(Application.ExecutablePath));
            foreach (string argument in args) command.Append(' ').Append(TaskHelper.QuoteCommandArgument(argument));
            command.Append(' ').Append(Argument).Append(' ').Append(token).Append(' ').Append(pid);
            return command.ToString();
        }

        private static bool CreateOutsideJob(string command, out string failure, out int pid)
        {
            string workerFailure = "wmi-unknown";
            int workerPid = 0;
            bool created = false;
            string directory = Path.GetDirectoryName(Application.ExecutablePath);
            var worker = new Thread(() =>
            {
                try
                {
                    using (var processClass = new ManagementClass(@"root\cimv2", "Win32_Process", null))
                    using (ManagementBaseObject input = processClass.GetMethodParameters("Create"))
                    {
                        input["CommandLine"] = command;
                        input["CurrentDirectory"] = directory;
                        using (ManagementBaseObject output = processClass.InvokeMethod("Create", input, null))
                        {
                            uint code = Convert.ToUInt32(output["ReturnValue"]);
                            if (code == 0)
                            {
                                workerPid = Convert.ToInt32(output["ProcessId"]);
                                created = workerPid > 0;
                                workerFailure = created ? null : "wmi-no-pid";
                            }
                            else workerFailure = "wmi-create-rc-" + code;
                        }
                    }
                }
                catch (Exception error)
                {
                    workerFailure = "wmi-" + error.GetType().Name;
                }
            });
            worker.IsBackground = true;
            worker.Start();
            if (!worker.Join(CreateWaitMs))
            {
                failure = "wmi-timeout";
                pid = 0;
                return false;
            }
            failure = workerFailure;
            pid = workerPid;
            return created;
        }

        private static string EventName(string token)
        {
            return "Local\\Pavise_JobHandoff_" + token;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out ExtendedLimitInformation info, int length, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
    }
}

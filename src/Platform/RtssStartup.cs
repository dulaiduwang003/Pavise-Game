using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal interface IRtssOwnedProcess : IDisposable
    {
        bool HasExited { get; }
        void RequestStop();
    }

    // 连接重置不清除启动重试冷却。
    internal sealed class RtssStartup
    {
        private readonly Func<long> clock;
        private readonly Func<string, int> running;
        private readonly Func<ProcessStartInfo, IRtssOwnedProcess> launch;
        private IRtssOwnedProcess owned;
        private string ownedPath = "", probePath = "";
        private long nextAbsentProbe;
        private bool stopping;
        private bool attempted;
        private long lastAttempt;
        private bool live;
        private long lastLiveCheck;
        private string livePath = "";
        private string lastStatus = "";
        internal static string StartingStatus { get { return AutomaticPolicy.Text("RTSS 正在启动…", "RTSS is starting…"); } }

        internal RtssStartup() : this(delegate { return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency); },
            RunningState, new Func<ProcessStartInfo, IRtssOwnedProcess>(RtssOwnedProcess.Start)) { }

        internal RtssStartup(Func<long> clock, Func<string, int> running, Action<ProcessStartInfo> launch)
            : this(clock, running, delegate(ProcessStartInfo info) { launch(info); return null; }) { }

        internal RtssStartup(Func<long> clock, Func<string, int> running, Func<ProcessStartInfo, IRtssOwnedProcess> launch)
        { this.clock = clock; this.running = running; this.launch = launch; }

        internal bool EnsureRunning(string executable, out string reason)
        {
            reason = "";
            long now = clock();
            ReleaseExitedProcess();
            if (stopping)
            {
                StopOwned();
                reason = AutomaticPolicy.Text("RTSS 正在退出，完成后自动启动。", "RTSS is exiting; monitoring will start when it finishes.");
                return false;
            }
            if (owned != null && String.Equals(ownedPath, executable, StringComparison.OrdinalIgnoreCase))
            { RememberLive(executable, now); return true; }
            if (live && String.Equals(livePath, executable, StringComparison.OrdinalIgnoreCase)
                && now - lastLiveCheck < 5000) return true;
            live = false;
            if (String.Equals(probePath, executable, StringComparison.OrdinalIgnoreCase)
                && now < nextAbsentProbe && (!attempted || now - lastAttempt < 30000))
            { reason = lastStatus; return false; }
            probePath = executable;
            int state;
            try { state = running(executable); }
            catch { state = -1; }
            if (state > 0) { RememberLive(executable, now); return true; }
            nextAbsentProbe = now + 1000;
            if (state < 0)
            {
                reason = AutomaticPolicy.Text("无法确认 RTSS 运行状态，稍后自动重试。", "RTSS status could not be verified; retrying automatically.");
                lastStatus = reason;
                return false;
            }
            if (attempted && now - lastAttempt < 30000)
            { reason = lastStatus; return false; }
            attempted = true; lastAttempt = now;
            try
            {
                ProcessStartInfo info = PrepareStartInfo(executable, out reason);
                if (info != null)
                {
                    // 准备期间可能已有外部 RTSS 启动，避免重复实例。
                    state = running(executable);
                    if (state > 0) { RememberLive(executable, now); lastStatus = ""; return true; }
                    if (state < 0) throw new InvalidOperationException("RTSS process identity could not be verified.");
                    owned = launch(info);
                    ownedPath = executable; nextAbsentProbe = 0;
                    reason = StartingStatus;
                }
            }
            catch
            {
                reason = AutomaticPolicy.Text("RTSS 启动失败，30 秒后重试。", "RTSS could not start; retrying in 30 seconds.");
            }
            lastStatus = reason;
            // 进程创建成功不代表共享映射已经就绪。
            return false;
        }

        private void RememberLive(string executable, long now)
        { live = true; livePath = executable; lastLiveCheck = now; }
        internal void ForgetLiveServer() { live = false; }

        private void ReleaseExitedProcess()
        {
            if (owned == null || !owned.HasExited) return;
            owned.Dispose(); owned = null; ownedPath = ""; live = false; nextAbsentProbe = 0;
            // 正常退局不触发启动失败冷却。
            if (stopping) { attempted = false; lastStatus = ""; stopping = false; }
        }

        internal void StopOwned()
        {
            live = false;
            ReleaseExitedProcess();
            if (owned == null) return;
            stopping = true;
            owned.RequestStop();
            ReleaseExitedProcess();
        }

        internal static ProcessStartInfo PrepareStartInfo(string executable, out string reason)
        {
            if (!PrepareBackgroundConfiguration(executable, out reason)) return null;
            // RTSS 的 /s 受自身开机启动设置影响，按普通方式启动。
            return new ProcessStartInfo(executable, "") {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable)) };
        }

        internal static bool PrepareBackgroundConfiguration(string executable, out string reason)
        {
            string preparationReason = "";
            bool prepared = RtssComponent.TryRunMaintenance(delegate {
                return PrepareBackgroundConfigurationCore(executable, out preparationReason);
            });
            reason = prepared ? "" : preparationReason.Length > 0 ? preparationReason
                : AutomaticPolicy.Text("RTSS 组件操作正在进行，请稍后重新检测。", "An RTSS component operation is in progress. Detect it again shortly.");
            return prepared;
        }

        private static bool PrepareBackgroundConfigurationCore(string executable, out string reason)
        {
            reason = AutomaticPolicy.Text("未能保存 RTSS 设置，请重新检测组件。", "RTSS settings could not be saved. Detect the component again.");
            try
            {
                if (String.IsNullOrWhiteSpace(executable) || !Path.IsPathRooted(executable)
                    || !String.Equals(Path.GetFileName(executable), "RTSS.exe", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(executable)) return false;
                string directory = Path.GetDirectoryName(Path.GetFullPath(executable));
                string profiles = Path.Combine(directory, "Profiles"), config = Path.Combine(profiles, "Config");
                if (!File.Exists(config))
                {
                    string template = Path.Combine(directory, "ProfileTemplates", "Config");
                    if (!ValidConfigSize(template)) return false;
                    Directory.CreateDirectory(profiles);
                    try { File.Copy(template, config, false); }
                    catch (IOException) { if (!File.Exists(config)) throw; }
                }
                if (!ValidConfigSize(config)) return false;
                // 用系统 INI 接口仅修改目标项，保留编码和其他设置。
                if (GetPrivateProfileIntW("Settings", "StartMinimized", 0, config) != 1
                    && !WritePrivateProfileStringW("Settings", "StartMinimized", "1", config)) return false;
                if (GetPrivateProfileIntW("Settings", "StartMinimized", 0, config) != 1) return false;
                reason = "";
                return true;
            }
            catch { return false; }
        }

        private static bool ValidConfigSize(string path)
        { var file = new FileInfo(path); return file.Exists && file.Length <= 1024 * 1024; }

        private static int RunningState(string executable)
        {
            Process[] processes = Process.GetProcessesByName("RTSS");
            bool unknown = false;
            try
            {
                foreach (Process process in processes)
                {
                    IntPtr handle = OpenProcess(0x1000, false, process.Id);
                    if (handle == IntPtr.Zero)
                    { if (Marshal.GetLastWin32Error() != 87) unknown = true; continue; }
                    try
                    {
                        long birth, exit, kernel, user;
                        if (!GetProcessTimes(handle, out birth, out exit, out kernel, out user)) { unknown = true; continue; }
                        if (birth <= 0 || exit != 0) continue;
                        var path = new StringBuilder(32768); int length = path.Capacity;
                        if (!QueryFullProcessImageNameW(handle, 0, path, ref length)) { unknown = true; continue; }
                        if (String.Equals(Path.GetFullPath(path.ToString()), executable, StringComparison.OrdinalIgnoreCase)) return 1;
                    }
                    finally { CloseHandle(handle); }
                }
                return unknown ? -1 : 0;
            }
            finally { foreach (Process process in processes) process.Dispose(); }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint GetPrivateProfileIntW(string section, string key, int defaultValue, string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool WritePrivateProfileStringW(string section, string key, string value, string path);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr handle, out long birth, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern bool QueryFullProcessImageNameW(IntPtr handle, uint flags, StringBuilder path, ref int length);
    }
}

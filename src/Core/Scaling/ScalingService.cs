using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal sealed class ScalingSnapshot
    {
        internal string State = "waiting", Detail = "";
    }
    internal static class ScalingService
    {
        private static readonly object gate = new object();
        private static readonly AutoResetEvent wake = new AutoResetEvent(false);
        private static Thread worker;
        private static bool quitting;
        private static string profileId = "", state = "waiting", detail = "";
        private static int rendererPid, generation, hostPid;
        private static long rendererCreation, hostCreation;
        private static long hostEpoch;
        private static Process host;
        private static EventWaitHandle stop;
#if PAVISE_SELFTEST
        internal static bool HiddenHostForTest;
        internal static int HostPidForTest { get { lock (gate) return hostPid; } }
#endif

        static ScalingService() { Lang.Merge(ScalingLang.Table); }

        internal static void Start()
        {
            lock (gate)
            {
                if (worker != null && worker.IsAlive) return;
                quitting = false;
                worker = new Thread(Run) { IsBackground = true, Name = "Pavise window scaling" };
                worker.Start();
            }
        }
        internal static void NotifySession(GameProfile profile, int pid, long creation, bool active)
        {
            string id = active && profile != null ? profile.Id : "";
            lock (gate)
            {
                if (profileId == id && rendererPid == pid && rendererCreation == creation) return;
                profileId = id; rendererPid = active ? pid : 0; rendererCreation = active ? creation : 0;
                generation++; state = "waiting"; detail = "";
            }
            wake.Set();
        }
        internal static void ConfigurationChanged(string id)
        {
            lock (gate) { if (profileId != id) return; generation++; state = "waiting"; detail = ""; }
            wake.Set();
        }
        internal static ScalingSnapshot Snapshot(string id)
        {
            lock (gate) return new ScalingSnapshot { State = id == profileId ? state : "waiting", Detail = id == profileId ? detail : "" };
        }
        internal static bool IsHost(int pid, long creation)
        {
            lock (gate) return pid > 0 && pid == hostPid && creation > 0 && creation == hostCreation;
        }
        private static void Publish(string id, int gen, string value, string message)
        {
            lock (gate) if (id == profileId && gen == generation) { state = value; detail = message ?? ""; }
        }
        private static void PublishHost(string id, int gen, long epoch, string value, string message)
        {
            lock (gate) if (epoch == hostEpoch && id == profileId && gen == generation)
            { state = value; detail = message ?? ""; }
        }
        internal static string ParseState(string line)
        {
            if (line == null || line.Length > 512) return null;
            if (line == "STARTING") return "starting";
            if (line.StartsWith("READY ", StringComparison.Ordinal))
            {
                string[] fields = line.Split(' ');
                if (fields.Length != 5) return null;
                for (int i = 1; i < fields.Length; i++)
                {
                    int dimension;
                    if (!int.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out dimension)
                        || dimension < 1 || dimension > 16384) return null;
                }
                return "running";
            }
            if (line == "PAUSED") return "paused";
            if (line == "WAITING windowed") return "windowed";
            if (line.StartsWith("STOPPED ", StringComparison.Ordinal)) return "stopped";
            if (line.StartsWith("ERROR ", StringComparison.Ordinal)) return "error";
            return null;
        }
        private static void Run()
        {
            int currentGeneration = -1, blockedGeneration = -1;
            IntPtr currentWindow = IntPtr.Zero;
            try
            {
                while (true)
                {
                    string id; int pid, gen; long creation;
                    lock (gate) { if (quitting) break; id = profileId; pid = rendererPid; creation = rendererCreation; gen = generation; }
                    bool enabled = id.Length > 0 && ScalingSettings.Enabled(id);
                    if (gen != currentGeneration || !enabled)
                    { StopHost(); currentGeneration = gen; currentWindow = IntPtr.Zero; }
                    if (host != null && host.HasExited)
                    {
                        int exit = host.ExitCode;
                        host.WaitForExit(); // Drain the completed child's final diagnostic.
                        bool reportedError = Snapshot(id).State == "error";
                        StopHost(); blockedGeneration = gen;
                        if (exit != 0 && !reportedError) Publish(id, gen, "error", "Scaling host exited (" + exit.ToString(CultureInfo.InvariantCulture) + ")");
                        else if (exit == 0) Publish(id, gen, "stopped", "");
                    }
                    if (enabled && pid > 0 && creation > 0 && host == null && blockedGeneration != gen)
                    {
                        if (!ScalingPayload.Available) { Publish(id, gen, "unavailable", ""); blockedGeneration = gen; }
                        else
                        {
                            IntPtr window = FindSourceWindow(pid, creation);
                            if (window != IntPtr.Zero)
                            {
                                try { Launch(id, gen, window, pid, creation); currentWindow = window; }
                                catch (Exception ex) { StopHost(); blockedGeneration = gen; Publish(id, gen, "error", ex.Message); Logger.Log("窗口缩放启动失败 " + ex.Message); }
                            }
                        }
                    }
                    // A replacement HWND in the same renderer process is a new target.
                    if (enabled && blockedGeneration == gen && currentWindow != IntPtr.Zero && !IsWindow(currentWindow))
                    { blockedGeneration = -1; currentWindow = IntPtr.Zero; }
                    wake.WaitOne(enabled ? 350 : Timeout.Infinite);
                }
            }
            catch (Exception ex) { Logger.Log("窗口缩放服务停止 " + ex.Message); lock (gate) { state = "error"; detail = ex.Message; } }
            finally { StopHost(); }
        }
        private static void Launch(string id, int gen, IntPtr window, int pid, long creation)
        {
            Publish(id, gen, "starting", "");
            string eventName = "Local\\Pavise.Scale." + Guid.NewGuid().ToString("N");
            stop = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
            long epoch;
            lock (gate) epoch = ++hostEpoch;
            using (var payload = new ScalingPayload())
            using (Process own = Process.GetCurrentProcess())
            {
                string args = string.Format(CultureInfo.InvariantCulture, "--window {0} {1} {2} {3} {4} {5} {6} {7}",
                    window.ToInt64(), pid, creation, own.Id, own.StartTime.ToUniversalTime().ToFileTimeUtc(), eventName,
                    ScalingSettings.MappedMouse(id) ? 1 : 0, ScalingSettings.Sharpen(id) ? 35 : 0);
#if PAVISE_SELFTEST
                if (HiddenHostForTest) args = args.Replace("--window ", "--window-hidden-test ");
#endif
                var process = new Process();
                process.StartInfo = new ProcessStartInfo(payload.Path, args) {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = System.IO.Path.GetDirectoryName(payload.Path)
                };
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    string parsed = ParseState(e.Data);
                    if (parsed != null) PublishHost(id, gen, epoch, parsed, e.Data);
                };
                process.ErrorDataReceived += delegate { };
                try
                {
                    if (!process.Start()) throw new InvalidOperationException("scaling-launch");
                    host = process;
                    lock (gate) { hostPid = process.Id; hostCreation = process.StartTime.ToUniversalTime().ToFileTimeUtc(); }
                    process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    lock (gate) if (quitting || gen != generation) stop.Set();
                }
                catch { if (host != process) process.Dispose(); throw; }
            }
        }
        private static void StopHost()
        {
            lock (gate) hostEpoch++;
            try { if (stop != null) stop.Set(); } catch { }
            if (host != null)
            {
                try { if (!host.WaitForExit(1400)) { host.Kill(); host.WaitForExit(1000); } } catch { }
                host.Dispose(); host = null;
            }
            lock (gate) { hostPid = 0; hostCreation = 0; }
            if (stop != null) { stop.Dispose(); stop = null; }
        }
        internal static void Shutdown(int waitMs)
        {
            Thread thread;
            lock (gate) { quitting = true; thread = worker; }
            wake.Set();
            if (thread != null && thread != Thread.CurrentThread) thread.Join(Math.Max(waitMs, 2600));
        }
        private delegate bool EnumWindow(IntPtr hwnd, IntPtr arg);
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr arg);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        private static IntPtr FindSourceWindow(int pid, long creation)
        {
            IntPtr query = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (query == IntPtr.Zero) return IntPtr.Zero;
            try { long observed, cpu; ulong io; if (!Native.QueryProcessSample(query, out observed, out cpu, out io) || observed != creation) return IntPtr.Zero; }
            finally { Native.CloseHandle(query); }
            IntPtr selected = IntPtr.Zero, foreground = GetForegroundWindow(); long area = 0;
            EnumWindows(delegate(IntPtr hwnd, IntPtr unused) {
                uint owner; GetWindowThreadProcessId(hwnd, out owner); Rect r;
                if (owner != pid || !IsWindowVisible(hwnd) || IsIconic(hwnd) || !GetClientRect(hwnd, out r)) return true;
                long size = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (r.Right - r.Left < 160 || r.Bottom - r.Top < 120) return true;
                if (hwnd == foreground) { selected = hwnd; return false; }
                if (size > area) { area = size; selected = hwnd; } return true;
            }, IntPtr.Zero);
            return selected;
        }
    }
}

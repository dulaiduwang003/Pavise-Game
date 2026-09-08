// Separate opt-in runner: transient settings, hidden output, existing task
// window only. The ordinary regression runner does not execute this test.
#if PAVISE_SELFTEST && PAVISE_SCALING_INTEGRATION
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
namespace PaviseApp
{
    internal static class ScalingIntegrationRunner
    {
        [STAThread] private static int Main(string[] args)
        {
            Settings.UseTransientStoreForCurrentProcess(); Lang.Init();
            ScalingService.HiddenHostForTest = true;
            bool orphan = args.Length == 1 && args[0] == "--orphan";
            try
            {
                if (!ScalingPayload.Available) throw new Exception("Embedded scaler missing");
                if (!orphan) SelfTests.RunScalingRegressionTests();
                Process source = Process.GetProcessesByName("ChatGPT").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
                if (source == null) source = Process.GetProcessesByName("Codex").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
                if (source == null) throw new Exception("No existing task window; no application was launched");
                string id = Guid.NewGuid().ToString("N");
                var profile = new GameProfile { Id = id, Name = "Hidden scaler integration" };
                ScalingSettings.Set(id, "Enabled", true);
                ScalingService.NotifySession(profile, source.Id, source.StartTime.ToUniversalTime().ToFileTimeUtc(), true);
                ScalingService.Start(); Wait(delegate { return ScalingService.Snapshot(id).State == "running"; }, id);
                int child = ScalingService.HostPidForTest;
                if (child <= 0) throw new Exception("Missing helper identity");
                using (var process = Process.GetProcessById(child))
                {
                    if (!ScalingService.IsHost(child, process.StartTime.ToUniversalTime().ToFileTimeUtc())) throw new Exception("Host exemption missing");
                    if (ScalingService.IsHost(child, 1)) throw new Exception("PID reuse guard missing");
                    bool shown = false;
                    EnumWindows(delegate(IntPtr hwnd, IntPtr unused) { uint pid; GetWindowThreadProcessId(hwnd, out pid); if (pid == child && IsWindowVisible(hwnd)) shown = true; return true; }, IntPtr.Zero);
                    if (shown) throw new Exception("Test output became visible");
                    if (orphan)
                    {
                        Console.WriteLine("ORPHAN_CHILD " + child);Console.Out.Flush();
                        Environment.Exit(0); // Intentionally skips Shutdown; the native parent handle must stop it.
                    }
                    ScalingSettings.Set(id, "Enabled", false);
                    Wait(delegate { return ScalingService.HostPidForTest == 0; }, id);
                    if (!process.WaitForExit(3000)) throw new Exception("Toggle-off did not close helper");
                }
                ScalingSettings.Set(id, "Enabled", true);
                Wait(delegate { return ScalingService.Snapshot(id).State == "running"; }, id);
                ScalingService.NotifySession(null, 0, 0, false);
                Wait(delegate { return ScalingService.HostPidForTest == 0; }, id);
                Console.WriteLine("PASS embedded-payload launch ready hidden-output identity-guard toggle-off restart session-end cleanup");
                source.Dispose(); return 0;
            }
            catch (Exception e) { Console.Error.WriteLine("FAIL " + e); return 1; }
            finally { ScalingService.Shutdown(4000); }
        }
        private static void Wait(Func<bool> condition, string id)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                var snapshot = ScalingService.Snapshot(id);
                if (snapshot.State == "error" || watch.ElapsedMilliseconds > 12000)
                    throw new Exception("State=" + snapshot.State + ": " + snapshot.Detail);
                Thread.Sleep(40);
            }
        }
        private delegate bool Callback(IntPtr hwnd, IntPtr unused);
        [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr unused);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    }
}
#endif

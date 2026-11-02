#if PAVISE_SELFTEST && PAVISE_NV_SESSION_CHECKS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    // 开局写 NVIDIA 游戏配置在后台线程进行：主循环不再等驱动会话；写入与退局还原用同一把锁串行；
    // 退局后才拿到锁的写入放弃；写入期间计划被重置或换了渲染进程，就不标记已完成。驱动是假的，不碰本机。
    internal static partial class SelfTests
    {
        private static int checks;
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const int Pid = 4242;
        private const long Creation = 777;

        private static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException("FAIL " + reason); }
        public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("The NVIDIA session checks cannot start the product runtime"); }

        private sealed class GatedDriver : INvDrsDriver
        {
            internal readonly ManualResetEvent Gate = new ManualResetEvent(true);
            internal readonly ManualResetEvent Entered = new ManualResetEvent(false);
            private Dictionary<uint, uint> values = new Dictionary<uint, uint>(), working;
            internal volatile bool DenyOpen;
            internal int Opens, Writes;
            public bool Available { get { return true; } }
            public bool TryOpenSession(out IntPtr session)
            {
                Interlocked.Increment(ref Opens); Entered.Set();
                if (!Gate.WaitOne(10000)) throw new TimeoutException("driver gate");
                working = new Dictionary<uint, uint>(values); session = new IntPtr(1); return !DenyOpen;
            }
            public void CloseSession(IntPtr session) { }
            public bool SaveSession(IntPtr session) { values = new Dictionary<uint, uint>(working); return true; }
            public bool FindOrCreateAppProfile(IntPtr session, string exe, out IntPtr profile) { profile = new IntPtr(2); return true; }
            public int TryGetDword(IntPtr session, IntPtr profile, uint id, out uint value) { return working.TryGetValue(id, out value) ? 1 : 0; }
            public bool SetDword(IntPtr session, IntPtr profile, uint id, uint value, out int status)
            { status = 0; Interlocked.Increment(ref Writes); working[id] = value; return true; }
            public bool DeleteSetting(IntPtr session, IntPtr profile, uint id) { working.Remove(id); return true; }
        }

        private static object Get(object o, string f) { return typeof(GameMode).GetField(f, Any).GetValue(o); }
        private static void Set(object o, string f, object v) { typeof(GameMode).GetField(f, Any).SetValue(o, v); }
        private static object Call(object o, string m, params object[] a)
        {
            try { return typeof(GameMode).GetMethod(m, Any).Invoke(o, a); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }

        private static GameMode NewSession()
        {
            Settings.UseTransientStoreForCurrentProcess(); ActionFailureStore.ReloadForTest(); NvDrsTweaks.RestoreAllGames();
            var mode = new GameMode(Path.Combine(Path.GetTempPath(), "PaviseNvSession-" + Guid.NewGuid().ToString("N")), new SuppressionCore());
            Set(mode, "enabled", true); Set(mode, "active", true);
            Set(mode, "activeDetection", new GameDetection { RendererPid = Pid, RendererCreation = Creation, RendererName = "BenchGame", RendererCandidateSelected = true });
            return mode;
        }

        private static object Pass()
        {
            object pass = Activator.CreateInstance(typeof(GameMode).GetNestedType("BoostPass", Any), true);
            Type t = pass.GetType();
            t.GetField("NvShader").SetValue(pass, true);   // a nonempty plan on battery too
            t.GetField("NvLowLat").SetValue(pass, "off");
            t.GetField("RendererPid").SetValue(pass, Pid);
            t.GetField("RendererCreation").SetValue(pass, Creation);
            return pass;
        }

        private static IntPtr Self { get { return Process.GetCurrentProcess().Handle; } }
        private static void Tweak(GameMode mode) { Call(mode, "ApplyGameTweaks", Self, Pid, Pass(), true); }
        private static bool InFlight(GameMode mode) { return (int)Get(mode, "nvTweakInFlight") != 0; }
        private static bool Applied(GameMode mode) { lock (Get(mode, "sync")) return ((HashSet<int>)Get(mode, "tweakApplied")).Contains(Pid); }
        private static void Drain(GameMode mode) { Check(SpinWait.SpinUntil(delegate { return !InFlight(mode); }, 10000), "the NVIDIA worker did not finish"); }

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--negative-control") { Console.WriteLine("FAIL: intentional NVIDIA session negative control"); return 3; }
            INvDrsDriver original = NvDrsTweaks.Driver;
            try
            {
                Logger.WritesEnabled = false; Lang.Init();
                var driver = new GatedDriver(); NvDrsTweaks.Driver = driver;

                // 驱动会话卡住时主循环照样返回，同一时刻只排一个写入；放行后写入完成并记为已应用。
                GameMode mode = NewSession();
                driver.Gate.Reset(); driver.Entered.Reset();
                var sw = Stopwatch.StartNew();
                Tweak(mode);
                long returnedMs = sw.ElapsedMilliseconds;
                Check(returnedMs < 500, "the scan loop waited " + returnedMs + " ms for the driver session");
                Check(driver.Entered.WaitOne(5000), "the worker never opened the driver session");
                Tweak(mode); Tweak(mode);
                Thread.Sleep(200);  // a wrongly queued second worker would have started by now
                driver.Gate.Set(); Drain(mode);
                Check(driver.Opens == 1 && mode.NvSessionAppliesForTest == 1,
                    "an apply already in flight was queued again (" + mode.NvSessionAppliesForTest + " workers, " + driver.Opens + " sessions)");
                Check(driver.Writes > 0 && Applied(mode), "the worker did not apply and record the NVIDIA plan");

                // 写入进行中时，退局还原（同一把锁）必须等它结束。
                mode = NewSession(); driver.Opens = 0;
                driver.Gate.Reset(); driver.Entered.Reset();
                Tweak(mode);
                Check(driver.Entered.WaitOne(5000), "the worker never opened the driver session");
                object stageGate = Get(mode, "driverStageGate");
                bool restoreGotIn = true;
                var restore = new Thread(delegate() { restoreGotIn = Monitor.TryEnter(stageGate, 300); if (restoreGotIn) Monitor.Exit(stageGate); });
                restore.Start(); restore.Join();
                Check(!restoreGotIn, "the session-end restore could run while the NVIDIA profile was being written");
                driver.Gate.Set(); Drain(mode);
                Check(Monitor.TryEnter(stageGate, 1000), "the stage gate was not released after the apply"); Monitor.Exit(stageGate);

                // 退局（active 先置假）之后才拿到锁的写入放弃，不留驱动配置。
                mode = NewSession(); driver.Opens = 0;
                stageGate = Get(mode, "driverStageGate");
                Monitor.Enter(stageGate);
                try
                {
                    Tweak(mode);
                    Thread.Sleep(100);
                    lock (Get(mode, "sync")) Set(mode, "active", false);
                }
                finally { Monitor.Exit(stageGate); }
                Drain(mode);
                Check(driver.Opens == 0 && !NvDrsTweaks.HasGameResidue, "an apply that got the gate after the session ended still wrote the profile");
                Check(!Applied(mode), "an abandoned apply was recorded as applied");

                // 写入期间计划被重置（设置变化、重试代数），或换了渲染进程：写入照常结束，但不标记已完成。
                foreach (string change in new[] { "reset", "renderer" })
                {
                    mode = NewSession();
                    driver.Gate.Reset(); driver.Entered.Reset();
                    Tweak(mode);
                    Check(driver.Entered.WaitOne(5000), "the worker never opened the driver session (" + change + ")");
                    lock (Get(mode, "sync"))
                    {
                        if (change == "reset") Call(mode, "ForgetNvTweaksLocked");
                        else Set(mode, "activeDetection", new GameDetection { RendererPid = Pid + 1, RendererCreation = Creation, RendererCandidateSelected = true });
                    }
                    driver.Gate.Set(); Drain(mode);
                    Check(!Applied(mode), "an apply that finished after a " + change + " was recorded as applied");
                }

                // 驱动会话打不开：进入 30 秒重试窗口，窗口内不再排队。
                mode = NewSession(); driver.Opens = 0; driver.DenyOpen = true;
                Tweak(mode); Drain(mode);
                Check(!Applied(mode), "a failed apply was recorded as applied");
                long retryAt = (long)Get(mode, "nvTweakRetryAtTicks");
                Check(retryAt > DateTime.UtcNow.AddSeconds(20).Ticks, "a failed apply did not start the retry window");
                int opens = driver.Opens;
                Tweak(mode); Drain(mode);
                Check(driver.Opens == opens, "an apply was queued inside the retry window");
                driver.DenyOpen = false;

                NvDrsTweaks.RestoreAllGames();
                Console.WriteLine("PASS NVIDIA session apply checks=" + checks);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { NvDrsTweaks.Driver = original; }
        }
    }
}
#endif

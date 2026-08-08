// @author bdth 2074055628@qq.com
// 文件用途 实证挂起低级键盘钩子宿主后按键延迟的真实行为 决定冻结的过滤策略

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private const int WhKeyboardLl = 13;

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int type, HookProc proc, IntPtr module, uint thread);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string name);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint Type;
            public uint Pad0;
            public KEYBDINPUT Ki;
            public long Pad1;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort Vk;
            public ushort Scan;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        internal static void RunHookChild(string counterFile)
        {
            long count = 0;
            HookProc proc = delegate(int code, IntPtr wParam, IntPtr lParam)
            {
                count++;
                return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            };
            IntPtr hook = SetWindowsHookEx(WhKeyboardLl, proc,
                GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) { Environment.ExitCode = 1; return; }
            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 250;
            timer.Tick += delegate
            {
                try { File.WriteAllText(counterFile, count.ToString(), Encoding.ASCII); }
                catch { }
            };
            timer.Start();
            Application.Run();
            GC.KeepAlive(proc);
        }

        private static double[] MeasureKeyLatency(Form focus, int presses)
        {
            var results = new double[presses];
            var sw = new Stopwatch();
            bool received = false;
            KeyEventHandler handler = delegate(object s, KeyEventArgs e) { received = true; };
            focus.KeyDown += handler;
            try
            {
                for (int i = 0; i < presses; i++)
                {
                    if (GetForegroundWindow() != focus.Handle)
                    {
                        SetForegroundWindow(focus.Handle);
                        Application.DoEvents();
                        if (GetForegroundWindow() != focus.Handle)
                        {
                            for (int k = i; k < presses; k++) results[k] = -1;
                            return results;
                        }
                    }
                    received = false;
                    var inputs = new INPUT[2];
                    inputs[0].Type = 1;
                    inputs[0].Ki.Vk = 0x41;
                    inputs[1].Type = 1;
                    inputs[1].Ki.Vk = 0x41;
                    inputs[1].Ki.Flags = 2;
                    sw.Restart();
                    SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
                    while (!received && sw.Elapsed.TotalMilliseconds < 3000)
                        Application.DoEvents();
                    sw.Stop();
                    results[i] = received ? sw.Elapsed.TotalMilliseconds : -1;
                    Thread.Sleep(40);
                }
            }
            finally { focus.KeyDown -= handler; }
            return results;
        }

        private static string LatencyRow(double[] samples)
        {
            var ok = new System.Collections.Generic.List<double>();
            int lost = 0;
            foreach (double v in samples)
                if (v >= 0) ok.Add(v); else lost++;
            if (ok.Count == 0) return "全部超时";
            ok.Sort();
            return "首键 " + samples[0].ToString("F0") + "ms  中位 "
                + ok[ok.Count / 2].ToString("F0") + "ms  最大 "
                + ok[ok.Count - 1].ToString("F0") + "ms"
                + (lost > 0 ? "  丢失 " + lost : "");
        }

        private static long ReadHookCount(string counterFile)
        {
            try
            {
                long v;
                return long.TryParse(File.ReadAllText(counterFile, Encoding.ASCII), out v) ? v : -1;
            }
            catch { return -1; }
        }

        private static int SendProbeKeys(int count, int gapMs)
        {
            int sent = 0;
            for (int i = 0; i < count; i++)
            {
                var inputs = new INPUT[2];
                inputs[0].Type = 1;
                inputs[0].Ki.Vk = 0xE8;
                inputs[1].Type = 1;
                inputs[1].Ki.Vk = 0xE8;
                inputs[1].Ki.Flags = 2;
                if (SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT))) == 2) sent++;
                Thread.Sleep(gapMs);
            }
            return sent;
        }

        private static void RunHookLab(string output)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 低级钩子宿主挂起台架 ===");
            sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                + "  OS " + Environment.OSVersion.Version);
            string dir = Path.Combine(Path.GetTempPath(), "PaviseHookLab_" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(dir);
            string counterFile = Path.Combine(dir, "count.txt");
            string self = Process.GetCurrentProcess().MainModule.FileName;
            Process child = null;
            Form focus = null;
            try
            {
                Application.EnableVisualStyles();
                focus = new Form();
                focus.Text = "Pavise HookLab";
                focus.TopMost = true;
                focus.SetBounds(80, 80, 320, 140);
                focus.Show();
                for (int i = 0; i < 20 && GetForegroundWindow() != focus.Handle; i++)
                {
                    SetForegroundWindow(focus.Handle);
                    focus.Activate();
                    Application.DoEvents();
                    Thread.Sleep(100);
                }
                if (GetForegroundWindow() != focus.Handle)
                {
                    sb.AppendLine("结论：无法取得前台焦点，为避免向其他窗口注入按键，测试中止");
                    Environment.ExitCode = 1;
                    return;
                }

                double[] clean = MeasureKeyLatency(focus, 15);
                sb.AppendLine("无钩子基线：      " + LatencyRow(clean));

                child = Process.Start(new ProcessStartInfo(self, "--hook-child \"" + counterFile + "\"")
                { UseShellExecute = false, CreateNoWindow = true });
                for (int i = 0; i < 100 && !File.Exists(counterFile); i++) Thread.Sleep(50);
                Thread.Sleep(300);
                focus.Activate();
                Application.DoEvents();

                double[] hooked = MeasureKeyLatency(focus, 15);
                long countHooked = ReadHookCount(counterFile);
                sb.AppendLine("钩子活跃：        " + LatencyRow(hooked) + "  回调计数 " + countHooked);

                Native.NtSuspendProcess(child.Handle);
                Thread.Sleep(200);
                double[] frozen1 = MeasureKeyLatency(focus, 15);
                sb.AppendLine("宿主挂起后第一批：" + LatencyRow(frozen1));

                Thread.Sleep(2000);
                double[] frozen2 = MeasureKeyLatency(focus, 15);
                sb.AppendLine("挂起 2 秒后第二批：" + LatencyRow(frozen2));

                Native.NtResumeProcess(child.Handle);
                Thread.Sleep(600);
                long before = ReadHookCount(counterFile);
                MeasureKeyLatency(focus, 5);
                Thread.Sleep(600);
                long after = ReadHookCount(counterFile);
                sb.AppendLine("恢复后钩子存活：  " + (after > before ? "是（计数 " + before + " → " + after + "）"
                    : "否（系统已摘除该钩子，计数停在 " + after + "）"));

                double[] resumed = MeasureKeyLatency(focus, 15);
                sb.AppendLine("恢复后延迟：      " + LatencyRow(resumed));

                object originalTimeout = null;
                bool hadTimeout = false;
                using (var desk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Control Panel\Desktop", true))
                {
                    if (desk != null)
                    {
                        originalTimeout = desk.GetValue("LowLevelHooksTimeout");
                        hadTimeout = originalTimeout != null;
                        desk.SetValue("LowLevelHooksTimeout", 20,
                            Microsoft.Win32.RegistryValueKind.DWord);
                    }
                }
                try
                {
                    Thread.Sleep(300);
                    Native.NtSuspendProcess(child.Handle);
                    Thread.Sleep(200);
                    double[] shortTimeout = MeasureKeyLatency(focus, 15);
                    sb.AppendLine("Timeout=20 挂起： " + LatencyRow(shortTimeout));
                    Thread.Sleep(1500);
                    double[] shortTimeout2 = MeasureKeyLatency(focus, 15);
                    sb.AppendLine("Timeout=20 续测： " + LatencyRow(shortTimeout2));
                    Native.NtResumeProcess(child.Handle);
                }
                finally
                {
                    using (var desk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Control Panel\Desktop", true))
                        if (desk != null)
                        {
                            if (hadTimeout) desk.SetValue("LowLevelHooksTimeout", originalTimeout);
                            else desk.DeleteValue("LowLevelHooksTimeout", false);
                        }
                }
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR=" + ex.Message);
                Environment.ExitCode = 1;
            }
            finally
            {
                try { if (child != null && !child.HasExited) { Native.NtResumeProcess(child.Handle); child.Kill(); } }
                catch { }
                if (child != null) child.Dispose();
                if (focus != null) focus.Dispose();
                try { Directory.Delete(dir, true); } catch { }
                File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
            }
        }
    }
}

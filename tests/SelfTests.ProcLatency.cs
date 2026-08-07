// @author bdth 2074055628@qq.com
// 文件用途 同一批进程上对比 WMI 与 ETW 两条发现路径的延迟

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void RunProcLatencyProbe(string output, string countArg)
        {
            int count;
            if (!int.TryParse(countArg ?? "", out count) || count < 1) count = 30;

            var sb = new StringBuilder();
            var started = new Dictionary<int, double>();
            var wmi = new List<double>();
            var etw = new List<double>();
            var sync = new object();
            var clock = Stopwatch.StartNew();
            ManagementEventWatcher watcher = null;
            EtwProcessWatcher etwWatcher = null;

            sb.AppendLine("=== 新进程发现延迟：WMI vs ETW ===");
            sb.AppendLine("测量: CreateProcess 返回 到 事件抵达用户代码");
            sb.AppendLine("样本: " + count + " 个进程，两条路径同时订阅同一批进程");
            sb.AppendLine();

            try
            {
                etwWatcher = new EtwProcessWatcher("Pavise.LatProbe");
                etwWatcher.ProcessStarted += delegate(int pid, long stamp)
                {
                    double now = clock.Elapsed.TotalMilliseconds;
                    lock (sync)
                    {
                        double t0;
                        if (started.TryGetValue(pid, out t0)) etw.Add(now - t0);
                    }
                };
                bool etwOk = etwWatcher.Start();
                sb.AppendLine("ETW 会话: " + (etwOk ? "已启动" : "启动失败 —— " + etwWatcher.LastError));

                watcher = new ManagementEventWatcher(
                    new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
                watcher.Scope.Options.EnablePrivileges = true;
                watcher.EventArrived += delegate(object s, EventArrivedEventArgs e)
                {
                    double now = clock.Elapsed.TotalMilliseconds;
                    try
                    {
                        object raw = e.NewEvent.Properties["ProcessID"].Value;
                        if (raw == null) return;
                        int pid = Convert.ToInt32(raw);
                        lock (sync)
                        {
                            double t0;
                            if (started.TryGetValue(pid, out t0)) wmi.Add(now - t0);
                        }
                    }
                    catch { }
                    finally { try { e.NewEvent.Dispose(); } catch { } }
                };
                watcher.Start();
                sb.AppendLine("WMI 订阅: 已启动");
                sb.AppendLine();
                Thread.Sleep(3000);

                for (int i = 0; i < count; i++)
                {
                    var psi = new ProcessStartInfo("cmd.exe", "/c exit")
                    { UseShellExecute = false, CreateNoWindow = true };
                    Process p = Process.Start(psi);
                    double t0 = clock.Elapsed.TotalMilliseconds;
                    lock (sync) started[p.Id] = t0;
                    try { p.WaitForExit(3000); }
                    catch { }
                    try { p.Dispose(); }
                    catch { }
                    Thread.Sleep(200);
                }
                Thread.Sleep(8000);

                double[] w, t;
                lock (sync) { w = wmi.ToArray(); t = etw.ToArray(); }
                sb.AppendLine("=== 结果 ===");
                sb.AppendLine("路径   收到    最小ms    中位ms     p99ms     最大ms");
                sb.AppendLine(LatRow("WMI ", w, count));
                sb.AppendLine(LatRow("ETW ", t, count));

                if (w.Length > 0 && t.Length > 0)
                {
                    double wm = Median(w), tm = Median(t);
                    sb.AppendLine();
                    sb.AppendLine("=== 对比 ===");
                    sb.AppendLine("  WMI 中位 " + wm.ToString("F1") + " ms → ETW 中位 "
                        + tm.ToString("F1") + " ms");
                    if (tm > 0)
                        sb.AppendLine("  提速 " + (wm / tm).ToString("F1") + " 倍，绝对省下 "
                            + (wm - tm).ToString("F1") + " ms");
                    sb.AppendLine();
                    sb.AppendLine("=== 端到端（Pavise 实际路径）===");
                    sb.AppendLine("  现状: WMI " + wm.ToString("F1") + " + 合并窗口 750 = "
                        + (wm + 750).ToString("F1") + " ms");
                    sb.AppendLine("  换 ETW 后: " + tm.ToString("F1") + " + 合并窗口 750 = "
                        + (tm + 750).ToString("F1") + " ms");
                    sb.AppendLine("  合并窗口占比: 由 " + (750 / (wm + 750) * 100).ToString("F0")
                        + "% 升至 " + (750 / (tm + 750) * 100).ToString("F0") + "%");
                    if (750 / (tm + 750) > 0.7)
                        sb.AppendLine("  判定: 换 ETW 后合并窗口成为主瓶颈，WindowMs 必须一起调，"
                            + "否则端到端只从 " + (wm + 750).ToString("F0") + " 降到 "
                            + (tm + 750).ToString("F0") + " ms，收益被窗口吃掉大半。");
                }
                else if (t.Length == 0)
                    sb.AppendLine("\nETW 一个事件都没收到，需检查提供程序 GUID、关键字或权限。");
            }
            catch (Exception ex) { sb.AppendLine("异常: " + ex); }
            finally
            {
                try { if (watcher != null) { watcher.Stop(); watcher.Dispose(); } }
                catch { }
                try { if (etwWatcher != null) etwWatcher.Dispose(); }
                catch { }
                File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
                Console.Write(sb.ToString());
            }
        }

        private static double Median(double[] a)
        {
            if (a == null || a.Length == 0) return 0;
            var s = (double[])a.Clone();
            Array.Sort(s);
            return s[s.Length / 2];
        }

        private static string LatRow(string name, double[] a, int expected)
        {
            if (a == null || a.Length == 0)
                return name + "     0/" + expected + "  —— 未收到任何事件";
            var s = (double[])a.Clone();
            Array.Sort(s);
            double p99 = s[(int)Math.Min(s.Length - 1, Math.Floor(s.Length * 0.99))];
            return name + (s.Length + "/" + expected).PadLeft(7)
                + s[0].ToString("F1").PadLeft(10)
                + Median(s).ToString("F1").PadLeft(10)
                + p99.ToString("F1").PadLeft(10)
                + s[s.Length - 1].ToString("F1").PadLeft(10);
        }
    }
}

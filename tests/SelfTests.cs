// @author bdth 2074055628@qq.com
// 文件用途 运行不依赖测试框架的项目自测

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private sealed class TestSkippedException : Exception
        {
            public TestSkippedException(string reason) : base(reason) { }
        }

        public static bool TryHandleRuntimeMode(string[] args)
        {
            if (args == null || args.Length == 0) return false;
            if (args[0] == "--test-heartbeat-probe" && args.Length >= 2)
            {
                RunProbe(args[1]);
                return true;
            }
            if (args[0] == "--cpu-burn")
            {
                RunCpuBurn();
                return true;
            }
            if (args[0] == "--selftest")
            {
                string report = args.Length >= 2 ? args[1] : Path.Combine(Path.GetTempPath(), "Pavise.selftest.txt");
                Run(report);
                return true;
            }
            if (args[0] == "--detector-probe" && args.Length >= 4)
            {
                int pid;
                if (!int.TryParse(args[1], out pid)) { Environment.ExitCode = 2; return true; }
                RunDetectorProbe(pid, args[2], args[3]);
                return true;
            }
            if (args[0] == "--gpu-demote-probe" && args.Length >= 3)
            {
                int pid;
                if (!int.TryParse(args[1], out pid)) { Environment.ExitCode = 2; return true; }
                RunGpuDemoteProbe(pid, args[2]);
                return true;
            }
            if (args[0] == "--live-repro" && args.Length >= 4)
            {
                RunLiveRepro(args[1], args[2], args[3], args.Length >= 5 ? args[4] : null);
                return true;
            }
            if (args[0] == "--detect-live" && args.Length >= 2)
            {
                RunDetectLive(args[1], args.Length >= 3 ? args[2] : null);
                return true;
            }
            if (args[0] == "--irq-probe" && args.Length >= 2)
            {
                RunIrqProbe(args[1], args.Length >= 3 && args[2] == "--restart-device");
                return true;
            }
            if (args[0] == "--net-probe" && args.Length >= 2)
            {
                RunNetProbe(args[1]);
                return true;
            }
            if (args[0] == "--qos-probe" && args.Length >= 3)
            {
                RunQosProbe(args[1], args[2]);
                return true;
            }
            if (args[0] == "--nv-probe" && args.Length >= 2)
            {
                RunNvProbe(args[1], args.Length >= 3 ? args[2] : null);
                return true;
            }
            if (args[0] == "--white-shot" && args.Length >= 2)
            {
                RunWhitelistShot(args[1]);
                return true;
            }
            if (args[0] == "--irq-map" && args.Length >= 2)
            {
                RunIrqMap(args[1], args.Length >= 3 ? args[2] : null,
                    args.Length >= 4 ? args[3] : null);
                return true;
            }
            if (args[0] == "--contention-lab" && args.Length >= 2)
            {
                RunContentionLab(args[1], args.Length >= 3 ? args[2] : null,
                    args.Length >= 4 ? args[3] : null, args.Length >= 5 ? args[4] : null);
                return true;
            }
            if (args[0] == "--lane-live" && args.Length >= 3)
            {
                RunLaneLive(args[1], args[2]);
                return true;
            }
            if (args[0] == "--lane-probe" && args.Length >= 2)
            {
                RunLaneProbe(args[1], args.Length >= 3 ? args[2] : null,
                    args.Length >= 4 ? args[3] : null);
                return true;
            }
            if (args[0] == "--host-probe" && args.Length >= 2)
            {
                RunGameHostProbe(args[1], args.Length >= 3 ? args[2] : null);
                return true;
            }
            if (args[0] == "--intro-probe" && args.Length >= 2)
            {
                RunIntroProbe(args[1]);
                return true;
            }
            if (args[0] == "--menu-probe" && args.Length >= 2)
            {
                RunMenuProbe(args[1], args.Length >= 3 ? args[2] : null);
                return true;
            }
            if (args[0] == "--notes-probe" && args.Length >= 2)
            {
                RunNotesProbe(args[1], args.Length >= 3 ? args[2] : "zh");
                return true;
            }
            if (args[0] == "--profile-probe" && args.Length >= 3)
            {
                try
                {
                    var store = new GameProfileStore(args[1]);
                    List<GameProfile> profiles = store.LoadOrMigrate(Path.Combine(args[1], "Pavise.games.txt"));
                    int entries = 0;
                    foreach (GameProfile profile in profiles) entries += profile.Entries.Count;
                    File.WriteAllText(args[2], "PROFILES=" + profiles.Count + "\r\nENTRIES=" + entries
                        + "\r\nFORMAT=V2", Encoding.UTF8);
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(args[2], "ERROR=" + ex.Message, Encoding.UTF8);
                    Environment.ExitCode = 1;
                }
                return true;
            }
            return false;
        }

        private static void RunLiveRepro(string scratchDir, string exePath, string displayName, string output)
        {
            var sb = new System.Text.StringBuilder();
            Process probe = null;
            GameMode mode = null;
            string prevLogPath = Logger.LogPath;
            try
            {
                Directory.CreateDirectory(scratchDir);
                Logger.LogPath = Path.Combine(scratchDir, "repro.log");
                mode = new GameMode(scratchDir, new SuppressionCore(Path.Combine(scratchDir, "repro.state")));
                if (!mode.AddGameExecutable(displayName, exePath))
                {
                    sb.AppendLine("AddGameExecutable 失败：" + exePath);
                    return;
                }
                sb.AppendLine("已注册目标：" + displayName + " -> " + exePath);
                sb.AppendLine("当前生效预设（读共享注册表）：" + mode.ActivePreset);

                probe = Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                if (probe == null) { sb.AppendLine("目标进程启动失败"); return; }
                Thread.Sleep(500);
                probe.Refresh();
                sb.AppendLine("目标进程已启动 pid=" + probe.Id);

                mode.Start();
                mode.Enabled = true;

                for (int i = 0; i < 8; i++)
                {
                    Thread.Sleep(4300);
                    string state = "pid 已失效";
                    IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, probe.Id);
                    if (h != IntPtr.Zero)
                    {
                        try
                        {
                            uint pri = Native.GetPriorityClass(h);
                            int ctrl = 0, st = 0;
                            bool ecoOk = Native.TryQueryPowerThrottling(h, out ctrl, out st);
                            state = "优先级=0x" + pri.ToString("X") + (ecoOk ? " EcoQoS(ctrl=" + ctrl + ",state=" + st + ")" : " EcoQoS读取失败");
                        }
                        finally { Native.CloseHandle(h); }
                    }
                    sb.AppendLine("[第 " + (i + 1) + " 轮] IsActive=" + mode.IsActive + " ActiveGame=" + mode.ActiveGame + " | 目标进程 " + state);
                }

                mode.Enabled = false;
                Thread.Sleep(1200);
                sb.AppendLine();
                sb.AppendLine("=== repro.log 全文 ===");
                try { sb.AppendLine(File.ReadAllText(Logger.LogPath)); } catch { }
            }
            catch (Exception ex) { sb.AppendLine("异常：" + ex); }
            finally
            {
                try { if (mode != null) { mode.Enabled = false; mode.Stop(); } } catch { }
                try { if (probe != null && !probe.HasExited) probe.Kill(); } catch { }
                Logger.LogPath = prevLogPath;
            }
            string text = sb.ToString();
            try { if (output != null) File.WriteAllText(output, text, Encoding.UTF8); } catch { }
            Environment.ExitCode = 0;
        }

        private static void RunDetectLive(string dataDir, string output)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var store = new GameProfileStore(dataDir);
                List<GameProfile> profiles = store.LoadOrMigrate(Path.Combine(dataDir, "Pavise.games.txt"));
                Process[] all = Process.GetProcesses();
                int fg = GameSessionDetector.ForegroundPid();
                sb.AppendLine("foreground pid=" + fg);
                GameDetection hit = GameSessionDetector.Detect(all, profiles);
                sb.AppendLine("DETECT RESULT: " + (hit == null ? "NULL (无活动游戏)"
                    : hit.Profile.Name + " | renderer=" + hit.RendererName + " pid=" + hit.RendererPid));
                sb.AppendLine();
                foreach (GameProfile profile in profiles)
                {
                    sb.AppendLine("=== profile: " + profile.Name + " (exe=" + profile.ExecutablePath + ")");
                    foreach (Process p in all)
                    {
                        try
                        {
                            int pid = p.Id;
                            string path = null;
                            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                            if (h != IntPtr.Zero) { try { path = Native.ImagePath(h); } finally { Native.CloseHandle(h); } }
                            if (path == null || !profile.ContainsPath(path)) continue;
                            bool visible = GameSessionDetector.HasUserFacingWindow(p);
                            bool foreground = pid == fg;
                            bool vetoed = GameSessionDetector.ElectionVetoed(p.ProcessName, path);
                            sb.AppendLine("   " + p.ProcessName + " pid=" + pid + " vis=" + visible
                                + " fg=" + foreground + " vetoed=" + vetoed);
                        }
                        catch { }
                    }
                }
                foreach (Process p in all) { try { p.Dispose(); } catch { } }
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex); }
            string text = sb.ToString();
            try { if (output != null) File.WriteAllText(output, text, Encoding.UTF8); } catch { }
            Environment.ExitCode = 0;
        }

        private static string ReadIrqRegSnapshot(string deviceId)
        {
            string path = @"SYSTEM\CurrentControlSet\Enum\" + deviceId + @"\Device Parameters\Interrupt Management\Affinity Policy";
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
            {
                if (k == null) return "  (键不存在)";
                object policy = k.GetValue("DevicePolicy");
                object mask = k.GetValue("AssignmentSetOverride");
                string maskStr = mask is byte[] ? BitConverter.ToString((byte[])mask) : (mask == null ? "(无)" : mask.ToString());
                return "  DevicePolicy=" + (policy == null ? "(无)" : policy.ToString() + " (0x" + Convert.ToInt32(policy).ToString("X") + ")")
                    + "  AssignmentSetOverride=" + maskStr;
            }
        }

        private static void RunIrqProbe(string output, bool alsoRestartDevice)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                sb.AppendLine("=== CpuTopology ===");
                sb.AppendLine("Hybrid=" + CpuTopology.Hybrid + " AsymCache=" + CpuTopology.AsymCache + " MultiGroup=" + CpuTopology.MultiGroup);
                sb.AppendLine("AllMask=0x" + CpuTopology.AllMask.ToString("X") + " BoostMask=0x" + CpuTopology.BoostMask.ToString("X")
                    + " ThrottleMask=0x" + CpuTopology.ThrottleMask.ToString("X") + " StrictBoostMask=0x" + CpuTopology.StrictBoostMask.ToString("X"));
                bool expectedUseMask = !CpuTopology.MultiGroup && CpuTopology.BoostMask != 0 && CpuTopology.BoostMask != CpuTopology.AllMask;
                sb.AppendLine("expectedUseMask=" + expectedUseMask);
                sb.AppendLine();

                List<string> ids = InterruptAffinityTweak.EnumerateGpuDeviceIds();
                sb.AppendLine("=== EnumerateGpuDeviceIds ===");
                foreach (string id in ids) sb.AppendLine("  " + id);
                if (ids.Count == 0) sb.AppendLine("  (未找到任何 Status=OK 的显卡设备)");
                sb.AppendLine();

                sb.AppendLine("=== 写入前基线（直接读注册表）===");
                foreach (string id in ids) { sb.AppendLine(id); sb.AppendLine(ReadIrqRegSnapshot(id)); }
                sb.AppendLine();

                bool enableOk = InterruptAffinityTweak.Enable();
                sb.AppendLine("Enable() 返回=" + enableOk);
                sb.AppendLine("EnabledByPavise=" + InterruptAffinityTweak.EnabledByPavise);
                sb.AppendLine("=== Enable 后（直接读注册表，独立于内部回读）===");
                foreach (string id in ids) { sb.AppendLine(id); sb.AppendLine(ReadIrqRegSnapshot(id)); }
                sb.AppendLine();

                if (alsoRestartDevice && ids.Count > 0)
                {
                    string err;
                    bool restarted = InterruptAffinityTweak.RestartDevice(ids[0], out err);
                    sb.AppendLine("RestartDevice(" + ids[0] + ") 返回=" + restarted + (err != null ? " err=" + err : ""));
                    Thread.Sleep(1500);
                    sb.AppendLine("=== 设备重启后（直接读注册表）===");
                    sb.AppendLine(ids[0]); sb.AppendLine(ReadIrqRegSnapshot(ids[0]));
                }

                bool disableOk = InterruptAffinityTweak.Disable();
                sb.AppendLine("Disable() 返回=" + disableOk);
                sb.AppendLine("EnabledByPavise=" + InterruptAffinityTweak.EnabledByPavise);
                sb.AppendLine("=== Disable/Restore 后（直接读注册表，独立于内部回读，应恢复到写入前基线）===");
                foreach (string id in ids) { sb.AppendLine(id); sb.AppendLine(ReadIrqRegSnapshot(id)); }
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex); }
            string text = sb.ToString();
            try { if (output != null) File.WriteAllText(output, text, Encoding.UTF8); } catch { }
            Environment.ExitCode = 0;
        }

        private static bool RunPlainPowerShell(string script, out string stdout)
        {
            stdout = "";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + "\"" + script.Replace("\"", "\\\"") + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    stdout = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(10000);
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private static void RunLaneLive(string output, string pidArg)
        {
            var sb = new System.Text.StringBuilder();
            int pid;
            if (!int.TryParse(pidArg, out pid)) { File.WriteAllText(output, "pid 无效", Encoding.UTF8); Environment.ExitCode = 2; return; }
            try
            {
                long creation;
                string name;
                using (Process target = Process.GetProcessById(pid))
                {
                    creation = target.StartTime.ToUniversalTime().Ticks;
                    name = target.ProcessName;
                }
                sb.AppendLine("=== 渲染主权域实机回路 ===");
                sb.AppendLine("目标：" + name + " (pid " + pid + ")");

                RenderLane.Candidate best;
                bool identified = RenderLane.TryIdentify(pid, out best);
                sb.AppendLine("识别：" + (identified
                    ? "线程 " + best.Tid + " 占 " + (best.Share * 100).ToString("F1") + "%，共 " + best.ThreadCount + " 线程"
                    : "失败"));
                if (!identified) { File.WriteAllText(output, sb.ToString(), Encoding.UTF8); Environment.ExitCode = 3; return; }

                int before = ReadThreadPriority(best.Tid);
                sb.AppendLine("介入前线程优先级：" + before);

                RenderLane.EnsureForGame(pid, creation, name);
                bool active = RenderLane.IsActiveFor(pid, creation);
                int during = ReadThreadPriority(best.Tid);
                sb.AppendLine("建立通道：" + (active ? "成功" : "未建立（可能已自带高权重或被拒）"));
                sb.AppendLine("介入后线程优先级：" + during);

                bool released = RenderLane.Release();
                int after = ReadThreadPriority(best.Tid);
                sb.AppendLine("撤销：" + (released ? "成功" : "失败"));
                sb.AppendLine("撤销后线程优先级：" + after);
                sb.AppendLine();
                bool clean = after == before;
                sb.AppendLine("结论：" + (active && during > before && clean
                    ? "写入生效且完整还原，渲染主权域在本游戏上可用"
                    : !active ? "未建立通道，见上方原因"
                    : clean ? "已还原，但未观察到优先级抬升" : "还原不一致，需排查"));
                Environment.ExitCode = clean ? 0 : 4;
            }
            catch (Exception ex) { sb.AppendLine("异常：" + ex.Message); Environment.ExitCode = 5; }
            File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
        }

        private static int ReadThreadPriority(int tid)
        {
            IntPtr h = Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION, false, tid);
            if (h == IntPtr.Zero) return int.MinValue;
            try { return Native.GetThreadPriority(h); }
            finally { Native.CloseHandle(h); }
        }

        private static void RunLaneProbe(string output, string target, string roundsArg)
        {
            int rounds;
            if (!int.TryParse(roundsArg ?? "", out rounds) || rounds < 3) rounds = 12;
            int pid = -1;
            if (!string.IsNullOrEmpty(target) && !int.TryParse(target, out pid)) pid = -1;
            if (pid <= 0 && !string.IsNullOrEmpty(target))
            {
                string want = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? target.Substring(0, target.Length - 4) : target;
                Process[] found = Process.GetProcessesByName(want);
                try { if (found.Length > 0) pid = found[0].Id; }
                finally { foreach (Process p in found) p.Dispose(); }
            }
            if (pid <= 0)
            {
                File.WriteAllText(output, "未找到目标进程，请传入进程名或 pid。", Encoding.UTF8);
                Environment.ExitCode = 2;
                return;
            }
            ThreadLaneProbe.Report report = ThreadLaneProbe.Run(pid, rounds, 500);
            File.WriteAllText(output, ThreadLaneProbe.Format(report), Encoding.UTF8);
            Environment.ExitCode = string.IsNullOrEmpty(report.Error) ? 0 : 3;
        }

        private static void RunNetProbe(string output)
        {
            var sb = new System.Text.StringBuilder();
            string dummyExe = null;
            try
            {
                sb.AppendLine("=== Get-Command New-NetQosPolicy（前置能力检查）===");
                string cmdCheck;
                RunPlainPowerShell("if (Get-Command New-NetQosPolicy -ErrorAction SilentlyContinue) { 'FOUND' } else { 'MISSING' }", out cmdCheck);
                sb.AppendLine("  " + cmdCheck.Trim());
                sb.AppendLine();

                List<string> ids = NetworkAffinityTweak.EnumerateNicDeviceIds();
                sb.AppendLine("=== EnumerateNicDeviceIds ===");
                foreach (string id in ids) sb.AppendLine("  " + id);
                if (ids.Count == 0) sb.AppendLine("  (未找到任何真实 PCI/USB 网卡)");
                sb.AppendLine();

                sb.AppendLine("=== 写入前基线（直接读注册表）===");
                foreach (string id in ids) { sb.AppendLine(id); sb.AppendLine(ReadIrqRegSnapshot(id)); }
                sb.AppendLine();

                dummyExe = Path.Combine(Path.GetTempPath(), "PaviseNetProbeDummy_" + Guid.NewGuid().ToString("N") + ".exe");
                File.WriteAllBytes(dummyExe, new byte[] { 0x4D, 0x5A });
                string dummyName = NetworkAffinityTweak.SanitizePolicyName("PaviseNetProbeDummyGame", dummyExe);
                var games = new List<GameProfile> { new GameProfile { Name = "PaviseNetProbeDummyGame", ExecutablePath = dummyExe } };

                bool enableOk = NetworkAffinityTweak.Enable(games);
                sb.AppendLine("Enable() 返回=" + enableOk);
                sb.AppendLine("EnabledByPavise=" + NetworkAffinityTweak.EnabledByPavise);
                sb.AppendLine("=== Enable 后网卡寄存器（直接读注册表，独立于内部回读）===");
                foreach (string id in ids) { sb.AppendLine(id); sb.AppendLine(ReadIrqRegSnapshot(id)); }

                string qosCheck;
                RunPlainPowerShell("if (Get-NetQosPolicy -Name '" + dummyName.Replace("'", "''") + "' -ErrorAction SilentlyContinue) { 'EXISTS' } else { 'ABSENT' }", out qosCheck);
                sb.AppendLine("独立查询 QoS 策略 " + dummyName + " ：" + qosCheck.Trim());
                sb.AppendLine();

                bool disableOk = NetworkAffinityTweak.Disable();
                sb.AppendLine("Disable() 返回=" + disableOk);
                sb.AppendLine("EnabledByPavise=" + NetworkAffinityTweak.EnabledByPavise);
                sb.AppendLine("=== Disable 后网卡寄存器（直接读注册表，应恢复到写入前基线）===");
                foreach (string id in ids) { sb.AppendLine(id); sb.AppendLine(ReadIrqRegSnapshot(id)); }

                RunPlainPowerShell("if (Get-NetQosPolicy -Name '" + dummyName.Replace("'", "''") + "' -ErrorAction SilentlyContinue) { 'EXISTS' } else { 'ABSENT' }", out qosCheck);
                sb.AppendLine("独立查询 QoS 策略 " + dummyName + "（应已删除）：" + qosCheck.Trim());
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex); }
            finally { try { if (dummyExe != null) File.Delete(dummyExe); } catch { } }
            string text = sb.ToString();
            try { if (output != null) File.WriteAllText(output, text, Encoding.UTF8); } catch { }
            Environment.ExitCode = 0;
        }

        private static void RunIntroProbe(string output)
        {
            var sb = new System.Text.StringBuilder();
            string data = Path.Combine(Path.GetTempPath(), "PaviseIntroProbe_" + Process.GetCurrentProcess().Id);
            try
            {
                Directory.CreateDirectory(data);
                Logger.LogPath = Path.Combine(data, "intro.log");
                Dpi.Init();
                Lang.Init();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var core = new SuppressionCore();
                var tamer = new Tamer(core);
                var mode = new GameMode(data, core);
                using (var f = new PanelForm(tamer, mode, IconArt.MakeIcon(Dpi.S(24)), true))
                {
                    GC.KeepAlive(f.Handle);
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(-20000, -20000);
                    f.ShowPanel();
                    int settledTop = 0;
                    var samples = new List<string>();
                    double minOpacity = 2d, maxOpacity = -1d;
                    int topSpread = 0, firstTop = f.Top;
                    for (int i = 0; i < 40; i++)
                    {
                        Application.DoEvents();
                        double op = f.Opacity;
                        int top = f.Top;
                        if (op < minOpacity) minOpacity = op;
                        if (op > maxOpacity) maxOpacity = op;
                        int delta = top - firstTop;
                        if (Math.Abs(delta) > Math.Abs(topSpread)) topSpread = delta;
                        if (i % 4 == 0) samples.Add("  frame " + i + ": opacity=" + op.ToString("0.000") + " top=" + top);
                        settledTop = top;
                        Thread.Sleep(20);
                    }
                    Application.DoEvents();
                    sb.AppendLine("=== 开场动画逐帧采样 ===");
                    foreach (string s in samples) sb.AppendLine(s);
                    sb.AppendLine();
                    sb.AppendLine("opacity 区间: " + minOpacity.ToString("0.000") + " → " + maxOpacity.ToString("0.000"));
                    sb.AppendLine("Top 相对起点最大位移: " + topSpread + " px");
                    sb.AppendLine("最终 opacity=" + f.Opacity.ToString("0.000") + " 最终 Top=" + settledTop);
                    sb.AppendLine();
                    sb.AppendLine("判定 渐变生效: " + (minOpacity < 0.35d && maxOpacity > 0.95d));
                    sb.AppendLine("判定 上浮生效: " + (Math.Abs(topSpread) >= 4));
                    sb.AppendLine("判定 最终完全不透明: " + (Math.Abs(f.Opacity - 1d) < 0.001d));
                }
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex); }
            finally { try { Directory.Delete(data, true); } catch { } }
            string text = sb.ToString();
            try { if (output != null) File.WriteAllText(output, text, Encoding.UTF8); } catch { }
            Environment.ExitCode = 0;
        }

        private static void RunMenuProbe(string output, string dumpPath)
        {
            string data = Path.Combine(Path.GetTempPath(), "PaviseMenuProbe_" + Process.GetCurrentProcess().Id);
            var sb = new System.Text.StringBuilder();
            try
            {
                Directory.CreateDirectory(data);
                Logger.LogPath = Path.Combine(data, "menu.log");
                Dpi.Init();
                Lang.Init();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var core = new SuppressionCore();
                var tamer = new Tamer(core);
                var mode = new GameMode(data, core);
                var tray = new TrayMenu(tamer, mode, delegate { }, delegate { }, delegate { });
                ContextMenuStrip strip = tray.Strip;
                strip.Show(new Point(-20000, -20000));
                for (int i = 0; i < 12; i++) { Application.DoEvents(); Thread.Sleep(20); }

                sb.AppendLine("strip size=" + strip.Size + " padding=" + strip.Padding);
                foreach (ToolStripItem it in strip.Items)
                {
                    if (it is ToolStripSeparator) { sb.AppendLine("  ---- separator h=" + it.Height); continue; }
                    Size pref = it.GetPreferredSize(Size.Empty);
                    Size text = TextRenderer.MeasureText(it.Text, it.Font, Size.Empty, TextFormatFlags.NoPadding);
                    int topGap = it.Padding.Top;
                    int bottomGap = it.Padding.Bottom;
                    int slack = it.Height - it.Padding.Top - it.Padding.Bottom - text.Height;
                    sb.AppendLine("  \"" + it.Text.Trim() + "\" h=" + it.Height
                        + " pad=(t" + topGap + ",b" + bottomGap + ")"
                        + " textH=" + text.Height + " pref=" + pref.Height
                        + " 余量=" + slack + " textAlign=" + it.TextAlign);
                }

                using (var bmp = new Bitmap(strip.Width, strip.Height))
                {
                    strip.DrawToBitmap(bmp, new Rectangle(0, 0, strip.Width, strip.Height));
                    bmp.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                }
                strip.Close();
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex); }
            finally { try { Directory.Delete(data, true); } catch { } }
            try { if (dumpPath != null) File.WriteAllText(dumpPath, sb.ToString(), Encoding.UTF8); } catch { }
            Environment.ExitCode = 0;
        }

        private static void RunNotesProbe(string output, string language)
        {
            const string seenKey = "LastSeenNotesVersion";
            string prevSeen = null;
            try
            {
                Dpi.Init();
                Paths.Init();
                Lang.Init();
                Lang.Cur = language == "en" ? 1 : (language == "ja" ? 2 : 0);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                prevSeen = Settings.LoadStr(seenKey, "");
                using (var dlg = new ReleaseNotesDialog())
                {
                    dlg.StartPosition = FormStartPosition.Manual;
                    dlg.Location = new Point(-20000, -20000);
                    dlg.Show();
                    for (int i = 0; i < 25; i++) { Application.DoEvents(); Thread.Sleep(20); }
                    using (var bmp = new Bitmap(dlg.ClientSize.Width, dlg.ClientSize.Height))
                    {
                        dlg.DrawToBitmap(bmp, new Rectangle(Point.Empty, dlg.ClientSize));
                        bmp.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    dlg.Hide();
                }
            }
            catch (Exception ex) { try { File.WriteAllText(output + ".err.txt", ex.ToString(), Encoding.UTF8); } catch { } }
            finally { try { if (prevSeen != null) Settings.SaveStr(seenKey, prevSeen); } catch { } }
            Environment.ExitCode = 0;
        }

        private static void RunGameHostProbe(string dataDir, string output)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var store = new GameProfileStore(dataDir);
                List<GameProfile> profiles = store.LoadOrMigrate(Path.Combine(dataDir, "Pavise.games.txt"));
                Process[] all = Process.GetProcesses();
                GameDetection hit = GameSessionDetector.Detect(all, profiles);
                sb.AppendLine("DETECT RESULT: " + (hit == null ? "NULL (无活动游戏)"
                    : hit.Profile.Name + " | renderer=" + hit.RendererName + " pid=" + hit.RendererPid));

                int selfSession = -1;
                try { selfSession = Process.GetCurrentProcess().SessionId; } catch { }

                var parents = new Dictionary<int, int>();
                var names = new Dictionary<int, string>();
                foreach (Process p in all)
                {
                    try
                    {
                        int pid = p.Id;
                        names[pid] = p.ProcessName;
                        if (selfSession < 0 || p.SessionId != selfSession) continue;
                        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                        if (h == IntPtr.Zero) continue;
                        try { parents[pid] = Native.ParentProcessId(h); }
                        finally { Native.CloseHandle(h); }
                    }
                    catch { }
                }

                if (hit != null && hit.RendererPid > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("=== 原始父进程链（从渲染进程往上，不受任何逻辑过滤）===");
                    int cur = hit.RendererPid;
                    var seen = new HashSet<int>();
                    for (int i = 0; i < 30 && cur > 4 && seen.Add(cur); i++)
                    {
                        string nm;
                        names.TryGetValue(cur, out nm);
                        sb.AppendLine("  " + (i == 0 ? "renderer" : "parent^" + i) + ": " + (nm ?? "?") + " (pid " + cur + ")");
                        int parent;
                        if (!parents.TryGetValue(cur, out parent)) break;
                        cur = parent;
                    }

                    HashSet<int> ancestors = GameMode.WalkAncestorChain(parents, hit.RendererPid, -999999, 24);
                    sb.AppendLine();
                    sb.AppendLine("=== WalkAncestorChain 判定为“游戏宿主祖先”（豁免压制）的进程 ===");
                    if (ancestors.Count == 0) sb.AppendLine("  (空)");
                    foreach (int pid in ancestors)
                    {
                        string nm;
                        names.TryGetValue(pid, out nm);
                        sb.AppendLine("  " + (nm ?? "?") + " (pid " + pid + ")");
                    }

                    sb.AppendLine();
                    sb.AppendLine("=== 兜底通道：结构上够不到、但按通用启动器类别豁免的进程 ===");
                    bool anyFallback = false;
                    foreach (var pair in names)
                    {
                        if (ancestors.Contains(pair.Key)) continue;
                        if (!GameMode.IsKnownLauncherShell(pair.Value)) continue;
                        sb.AppendLine("  " + pair.Value + " (pid " + pair.Key + ")");
                        anyFallback = true;
                    }
                    if (!anyFallback) sb.AppendLine("  (空)");
                }
                foreach (Process p in all) { try { p.Dispose(); } catch { } }
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex); }
            string text = sb.ToString();
            try { if (output != null) File.WriteAllText(output, text, Encoding.UTF8); } catch { }
            Environment.ExitCode = 0;
        }

        private static void RunDetectorProbe(int pid, string configuredRoot, string output)
        {
            Process[] all = null;
            try
            {
                using (Process target = Process.GetProcessById(pid))
                {
                    var profile = GameProfileStore.NewProfile("DetectorProbe", configuredRoot);
                    profile.Entries.Add(target.ProcessName);
                    all = Process.GetProcesses();
                    GameDetection hit = GameSessionDetector.Detect(all, new[] { profile });
                    string result = hit == null ? "NONE" : (hit.RendererPid > 0 ? "MATCH" : "SESSION") + "|" + hit.RendererName + "|" + hit.RendererPath;
                    File.WriteAllText(output, result, Encoding.UTF8);
                    Environment.ExitCode = hit == null ? 0 : 3;
                }
            }
            catch (Exception ex) { File.WriteAllText(output, "ERROR|" + ex.Message, Encoding.UTF8); Environment.ExitCode = 4; }
            finally { if (all != null) foreach (Process p in all) p.Dispose(); }
        }

        private static int CountPlaceholders(string s)
        {
            int n = 0;
            for (int i = 0; i + 2 < (s ?? "").Length; i++)
                if (s[i] == '{' && char.IsDigit(s[i + 1]) && s[i + 2] == '}') n++;
            return n;
        }

        private static void Run(string reportPath)
        {
            var log = new List<string>();
            int passed = 0, failed = 0, skipped = 0;
            Action<string, Action> test = (name, body) =>
            {
                try { body(); log.Add("PASS  " + name); passed++; }
                catch (TestSkippedException ex) { log.Add("SKIP  " + name + " :: " + ex.Message); skipped++; }
                catch (Exception ex) { log.Add("FAIL  " + name + " :: " + ex.Message); failed++; }
            };

            test("strict mask: ordinary CPU partitions background cores", () =>
                Eq(0x3FUL, CpuPartitionPolicy.StrictMask(0xFF, 0xC0, 0, 0)));
            test("strict mask: hybrid prefers reported P cores", () =>
                Eq(0x0FUL, CpuPartitionPolicy.StrictMask(0xFF, 0xF0, 0x0F, 0)));
            test("strict mask: X3D cache CCD wins", () =>
                Eq(0xF0UL, CpuPartitionPolicy.StrictMask(0xFF, 0x0F, 0, 0xF0)));
            test("strict mask: invalid empty partition falls back all", () =>
                Eq(0x03UL, CpuPartitionPolicy.StrictMask(0x03, 0x03, 0, 0)));
            test("CPU tiering: low-core homogeneous CPUs are never hard-partitioned", () =>
            {
                Eq(0, CpuPartitionPolicy.BackgroundCoreCount(4));
                Eq(0, CpuPartitionPolicy.BackgroundCoreCount(6));
            });
            test("CPU tiering: high-core homogeneous CPUs reserve proportionally", () =>
            {
                Eq(1, CpuPartitionPolicy.BackgroundCoreCount(8));
                Eq(1, CpuPartitionPolicy.BackgroundCoreCount(10));
                Eq(2, CpuPartitionPolicy.BackgroundCoreCount(12));
                Eq(3, CpuPartitionPolicy.BackgroundCoreCount(24));
                Eq(4, CpuPartitionPolicy.BackgroundCoreCount(64));
            });
            test("whitelist rules: legacy names, versioned paths and exact boundaries", TestWhitelistRules);
            test("whitelist family: descendants persist only while PID identity matches", TestWhitelistFamilyIdentity);
            test("whitelist family events: order and parent creation prevent PID inheritance", TestWhitelistFamilyEvents);
            test("process events: delayed starts cannot splice stale parent identity", TestProcNotifyParentIdentity);
            test("whitelist storage: corrupt data fails safe and writes are transactional", TestWhitelistStorageSafety);
            test("whitelist concurrency: edits serialize with in-flight policy snapshots", TestWhitelistMutationSerialization);
            test("extreme exclusions: anti-cheat names are case-insensitive", () =>
            {
                Eq(true, AntiCheatCatalog.IsKnownProcess("VGC"));
                Eq(true, AntiCheatCatalog.IsKnownProcess("EasyAntiCheat_EOS"));
                Eq(true, AntiCheatCatalog.IsKnownProcess("ace-helper"));
                Eq(false, AntiCheatCatalog.IsKnownProcess("ordinary-app"));
            });
            test("game family: generic multi-folder layouts share one protected root", TestMultiFolderGameRoot);
            test("game catalog: protected root survives save format and legacy entries", TestGameCatalogFormat);
            test("startup task: running binary replaces a stale executable target", () =>
            {
                Eq(false, TaskHelper.NeedsStartupTaskRefresh(
                    @"C:\Code\Pavise\Pavise.exe",
                    @"c:\code\pavise\PAVISE.exe"));
                Eq(true, TaskHelper.NeedsStartupTaskRefresh(
                    @"C:\Code\Pavise\Pavise.exe",
                    @"C:\Users\Star\Desktop\Pavise.exe"));
                Eq(false, TaskHelper.NeedsStartupTaskRefresh(
                    @"C:\Code\Pavise\Pavise.exe", null));
                Eq(@"C:\Apps\A & B\Pavise.exe",
                    TaskHelper.ParseTaskCommandXml(
                        "\uFEFF<?xml version=\"1.0\"?>"
                        + "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
                        + "<Actions><Exec><Command>\"C:\\Apps\\A &amp; B\\Pavise.exe\""
                        + "</Command></Exec></Actions></Task>"));
                Eq(null, TaskHelper.ParseTaskCommandXml(
                    "<Task><Actions /></Task>"));
            });
            test("LoL runtime: LCU credentials reject malformed input", TestLolCredentialParsing);
            test("LoL runtime: cleanup targets never include core, game or ACE paths", TestLolCleanupBoundary);
            test("environment tweaks: a failing step backs off instead of retrying every scan", () =>
            {
                string envDir = Path.Combine(
                    Path.GetTempPath(), "PaviseEnvRetry_" + Process.GetCurrentProcess().Id);
                Directory.CreateDirectory(envDir);
                var mode = new GameMode(envDir, new SuppressionCore());
                mode.ClearEnvRetryStateForTest();
                int attempts = mode.EnvAttemptCountForTest(
                    "probe-fail", true, false,
                    delegate { return false; }, delegate { return true; }, 50);
                if (attempts != 1)
                    throw new Exception("失败项在 50 轮扫描里尝试了 " + attempts + " 次，应为 1 次");

                mode.ClearEnvRetryStateForTest();
                int okAttempts = mode.EnvAttemptCountForTest(
                    "probe-ok", true, false,
                    delegate { return true; }, delegate { return true; }, 50);
                Eq(1, okAttempts);

                mode.ClearEnvRetryStateForTest();
                int restoreAttempts = mode.EnvAttemptCountForTest(
                    "probe-restore", false, true,
                    delegate { return true; }, delegate { return false; }, 50);
                if (restoreAttempts != 1)
                    throw new Exception("还原失败项尝试了 " + restoreAttempts + " 次，应为 1 次");
            });
            test("render detector: Office and launchers cannot masquerade as games", TestRenderScoring);
            test("render detector: parallel instances and PID reuse stay isolated", TestGameSessionInstanceIsolation);
            test("autostart task: the logon instance is distinguishable from a manual launch", () =>
            {
                const string withArgs =
                    "<Task><Actions Context=\"Author\"><Exec>"
                    + "<Command>\"C:\\A\\Pavise.exe\"</Command>"
                    + "<Arguments>--autostart</Arguments></Exec></Actions></Task>";
                Eq("--autostart", TaskHelper.ParseTaskArgumentsXml(withArgs));
                Eq("C:\\A\\Pavise.exe", TaskHelper.ParseTaskCommandXml(withArgs));

                const string legacy =
                    "<Task><Actions Context=\"Author\"><Exec>"
                    + "<Command>\"C:\\A\\Pavise.exe\"</Command></Exec></Actions></Task>";
                Eq("", TaskHelper.ParseTaskArgumentsXml(legacy));

                Eq(null, TaskHelper.ParseTaskArgumentsXml(""));
                Eq(null, TaskHelper.ParseTaskArgumentsXml("不是 XML"));
                Eq(null, TaskHelper.ParseTaskArgumentsXml("<Task><Actions/></Task>"));
            });
            test("release metadata: product and file versions are present", TestReleaseMetadata);
            test("mode theme: graphite stays fixed while Standard, Competitive and Custom accents differ", () =>
            {
                Color bg = Theme.Bg;
                if (Theme.ModeColor(PerformancePreset.Standard) == Theme.ModeColor(PerformancePreset.Competitive)) throw new Exception("Standard and Competitive accents match");
                if (Theme.ModeColor(PerformancePreset.Competitive) == Theme.ModeColor(PerformancePreset.Custom)) throw new Exception("Competitive and Custom accents match");
                Theme.SetMode(PerformancePreset.Competitive, false);
                Eq(Theme.ModeColor(PerformancePreset.Competitive), Theme.Accent);
                Eq(bg, Theme.Bg);
                Theme.SetMode(PerformancePreset.Custom, true);
                Color start = Theme.Accent;
                Theme.StepTheme();
                if (Theme.Accent == start || Theme.Accent == Theme.ModeColor(PerformancePreset.Custom)) throw new Exception("theme transition did not interpolate");
                while (Theme.StepTheme()) { }
                Eq(Theme.ModeColor(PerformancePreset.Custom), Theme.Accent);
                Theme.SetMode(PerformancePreset.Standard, false);
            });
            test("runtime icon: tray artwork fills the canvas and changes with effective mode", TestModeIcons);
            test("dashboard motion: independent layers advance between frames", TestDashboardMotion);
            test("high-DPI typography: body sizes land on whole device pixels from 100% to 200%", () =>
            {
                float old = Dpi.Scale;
                try
                {
                    foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
                    {
                        Dpi.Scale = scale;
                        foreach (float size in new[] { 6.75f, 7.5f, 8.25f, 9.5f, 10f, 14.5f })
                        {
                            double pixels = Dpi.CrispPoint(size) * scale * 96d / 72d;
                            if (Math.Abs(pixels - Math.Round(pixels)) > 0.001d)
                                throw new Exception(size + "pt is fractional at " + scale);
                        }
                    }
                }
                finally { Dpi.Scale = old; }
            });
            test("DPI change: scale updates only on a real change and cached fonts are dropped", () =>
            {
                float old = Dpi.Scale;
                try
                {
                    Dpi.Scale = 1f;
                    Eq(false, Dpi.Update(96));
                    Eq(1f, Dpi.Scale);
                    Eq(true, Dpi.Update(144));
                    Eq(1.5f, Dpi.Scale);
                    Eq(false, Dpi.Update(144));
                    Eq(true, Dpi.Update(72));
                    Eq(1f, Dpi.Scale);
                    Eq(false, Dpi.Update(0));
                    Eq(false, Dpi.Update(-96));
                    Eq(1f, Dpi.Scale);

                    Dpi.Scale = 1f;
                    float at100 = Theme.UI(9.5f, false).SizeInPoints;
                    Eq(true, Dpi.Update(192));
                    Theme.DropFontCache();
                    float at200 = Theme.UI(9.5f, false).SizeInPoints;
                    if (Math.Abs(at100 - at200) < 0.01f)
                        throw new Exception("font cache survived a DPI change: " + at100 + " vs " + at200);
                }
                finally { Dpi.Scale = old; Theme.DropFontCache(); }
            });
            test("DPI scale: only real changes count and probing never mutates the scale", () =>
            {
                float old = Dpi.Scale;
                try
                {
                    Dpi.Scale = 1f;
                    Eq(false, Dpi.WouldChange(96));
                    Eq(true, Dpi.WouldChange(144));
                    Eq(false, Dpi.WouldChange(0));
                    Eq(false, Dpi.WouldChange(-96));
                    Eq(false, Dpi.WouldChange(72));
                    Eq(1f, Dpi.Scale);

                    Eq(true, Dpi.Update(144));
                    Eq(1.5f, Dpi.Scale);
                    Eq(true, Dpi.WouldChange(72));
                    Eq(true, Dpi.Update(72));
                    Eq(1f, Dpi.Scale);
                    Eq(0, Dpi.WindowDpi(IntPtr.Zero));
                }
                finally { Dpi.Scale = old; Theme.DropFontCache(); }
            });
            test("background controller: sustained pressure escalates and cools down", TestPressureController);
            test("game-mode event budget: ordinary process churn stays on the 20-second reconciliation", () =>
            {
                Eq(4000, GameMode.ProcessScanIntervalMs(false));
                Eq(20000, GameMode.ProcessScanIntervalMs(true));
                Eq(false, GameMode.ProcessEventNeedsImmediateScan(false));
                Eq(true, GameMode.ProcessEventNeedsImmediateScan(true));
                Eq(5000, GameMode.GameTransitionScanIntervalMs);
                Eq(1000, GameMode.FailedProcessScanRetryMs);
                Eq(8000, Tamer.OverflowSweepIntervalMs);
                Eq(1000, Tamer.FailedSweepRetryMs);
                Eq(5000, LolOptimizationService.ProcessEventWakeThrottleMs);
                DateTime wakeNow = new DateTime(638000000000000000L, DateTimeKind.Utc);
                Eq(1250, LolOptimizationService.ProcessWakeDelayMs(
                    wakeNow, wakeNow.AddMilliseconds(1250)));
                GameProfile eventProfile = GameProfileStore.NewProfile(
                    "EventProbe", @"C:\Games\EventProbe",
                    @"C:\Games\EventProbe\EventProbe.exe");
                eventProfile.Entries.Clear();
                eventProfile.Entries.Add("EventProbe");
                Eq(true, GameSessionDetector.IsProfileEntryName(
                    eventProfile, "EventProbe"));
                Eq(true, GameSessionDetector.IsProfileEntryName(
                    eventProfile, "EventProbe_x64"));
                Eq(false, GameSessionDetector.IsProfileEntryName(
                    eventProfile, "EventProbeHelper"));
                Eq(true, GameSessionDetector.IsProfileEntryProcess(
                    eventProfile, "anything",
                    @"C:\Games\EventProbe\EventProbe.exe"));
                Eq(true, GameSessionDetector.IsProfileEntryProcess(
                    eventProfile, "EventProbe_x64",
                    @"C:\Games\EventProbe\EventProbe_x64.exe"));
                Eq(true, GameSessionDetector.IsProfileEntryProcess(
                    eventProfile, "EventProbeHelper",
                    @"C:\Games\EventProbe\EventProbeHelper.exe"));
                Eq(false, GameSessionDetector.IsProfileEntryProcess(
                    eventProfile, "EventProbe_x64",
                    @"C:\Other\EventProbe_x64.exe"));
                var detection = new GameDetection();
                detection.RendererPid = 41;
                detection.RendererName = "RiotClientServices";
                detection.RendererCreation = 1000;
                Eq(true, GameMode.ShouldCaptureLauncherParentIdentity(
                    detection, 41));
                Eq(false, GameMode.ShouldCaptureLauncherParentIdentity(
                    detection, 40));
                detection.RendererCreation = 0;
                Eq(false, GameMode.ShouldCaptureLauncherParentIdentity(
                    detection, 41));
                detection.RendererCreation = 1000;
                Eq(true, GameMode.IsActiveFamilyChildStart(
                    detection, new ProcessChange
                    {
                        Kind = ProcessChangeKind.Started,
                        Pid = 42,
                        ParentPid = 41,
                        ParentCreation = 1000,
                        Creation = 1100,
                        Session = 7
                    }, 7));
                Eq(false, GameMode.IsActiveFamilyChildStart(
                    detection, new ProcessChange
                    {
                        Kind = ProcessChangeKind.Started,
                        Pid = 42,
                        ParentPid = 41,
                        ParentCreation = 999,
                        Creation = 1100,
                        Session = 7
                    }, 7));
                Eq(false, GameMode.IsActiveFamilyChildStart(
                    detection, new ProcessChange
                    {
                        Kind = ProcessChangeKind.Started,
                        Pid = 42,
                        ParentPid = 41,
                        ParentCreation = 1000,
                        Creation = 1100,
                        Session = 8
                    }, 7));
                Eq(false, GameMode.IsActiveFamilyChildStart(
                    detection, new ProcessChange
                    {
                        Kind = ProcessChangeKind.Started,
                        Pid = 42,
                        ParentPid = 41,
                        ParentCreation = 1000,
                        Creation = 900,
                        Session = 7
                    }, 7));
                detection.FamilyPids.Add(43);
                Eq(false, GameMode.IsActiveFamilyChildStart(
                    detection, new ProcessChange
                    {
                        Kind = ProcessChangeKind.Started,
                        Pid = 44,
                        ParentPid = 43,
                        ParentCreation = 1000,
                        Creation = 1100,
                        Session = 7
                    }, 7));
                detection.RendererName = "EventProbe";
                Eq(false, GameMode.ShouldCaptureLauncherParentIdentity(
                    detection, 41));
                Eq(false, GameMode.IsActiveFamilyChildStart(
                    detection, new ProcessChange
                    {
                        Kind = ProcessChangeKind.Stopped,
                        Pid = 42,
                        ParentPid = 41,
                        ParentCreation = 1000,
                        Creation = 1100,
                        Session = 7
                    }, 7));
                Eq(true, GameMode.IsSameTransitionEpoch(
                    41, 1000, 41, 1000));
                Eq(false, GameMode.IsSameTransitionEpoch(
                    41, 1000, 41, 2000));
                var returnedLauncher = new GameDetection
                {
                    RendererPid = 41,
                    RendererName = "RiotClientServices"
                };
                Eq(true, GameMode.ShouldRearmLauncherTransition(
                    detection, returnedLauncher));
                Eq(false, GameMode.ShouldRearmLauncherTransition(
                    returnedLauncher, returnedLauncher));
            });
            test("preset policy: eligible background escalates and Competitive isolates immediately", TestPresetBackgroundPolicy);
            test("background boundary: foreground and user-facing apps stay protected", TestBackgroundBoundary);
            test("game protection: sticky-launcher guard and any-process targeting", TestGameProtectionRedesign);
            test("CPU Sets: game and background partitions never overlap", TestCpuSetPartition);
            test("DPC policy: only sustained outlier cores are avoided", () =>
            {
                Eq(-1, DpcSampler.FindNoisy(new[] { 0.01, 0.02, 0.015, 0.01 }));
                Eq(2, DpcSampler.FindNoisy(new[] { 0.01, 0.02, 0.20, 0.01 }));
                var sampler = new DpcSampler();
                sampler.ObserveCandidate(2);
                Eq(0UL, sampler.NoisyPhysicalMask);
                sampler.ObserveCandidate(2);
                if (sampler.NoisyPhysicalMask == 0) throw new Exception("sustained DPC spike was not activated");
                sampler.ObserveCandidate(-1);
                if (sampler.NoisyPhysicalMask == 0) throw new Exception("DPC avoidance cooled down too early");
                sampler.ObserveCandidate(-1);
                Eq(0UL, sampler.NoisyPhysicalMask);
            });
            test("interrupt core avoidance: only a clearly outlying core is given up", () =>
            {
                var cores = new ulong[] { 0x3, 0xC, 0x30, 0xC0, 0x300, 0xC00, 0x3000, 0xC000 };
                var measured = new double[16];
                measured[4] = 0.0265; measured[5] = 0.0265;
                measured[8] = 0.0089;
                measured[1] = 0.0010;
                Eq(0x30UL, CpuPartitionPolicy.FindInterruptCore(measured, cores, 16));

                Eq(0UL, CpuPartitionPolicy.FindInterruptCore(measured, cores, 6));

                var faint = new double[16];
                faint[4] = 0.004; faint[5] = 0.004;
                Eq(0UL, CpuPartitionPolicy.FindInterruptCore(faint, cores, 16));

                var tied = new double[16];
                tied[4] = 0.03; tied[8] = 0.025;
                Eq(0UL, CpuPartitionPolicy.FindInterruptCore(tied, cores, 16));

                Eq(0UL, CpuPartitionPolicy.FindInterruptCore(null, cores, 16));
                Eq(0UL, CpuPartitionPolicy.FindInterruptCore(measured, null, 16));
            });
            test("interrupt core avoidance: rate is the peak across a core's SMT threads", () =>
            {
                var rates = new double[] { 0.001, 0.020, 0.003, 0.004 };
                if (Math.Abs(CpuPartitionPolicy.CoreInterruptRate(rates, 0x3) - 0.020) > 1e-9)
                    throw new Exception("SMT peak was not taken");
                Eq(0.0, CpuPartitionPolicy.CoreInterruptRate(rates, 0));
                Eq(0.0, CpuPartitionPolicy.CoreInterruptRate(null, 0x3));
            });
            test("interrupt core avoidance: median ignores zero-heavy distributions correctly", () =>
            {
                Eq(0.0, CpuPartitionPolicy.Median(new double[] { 0, 0, 0, 0.5 }));
                Eq(2.0, CpuPartitionPolicy.Median(new double[] { 1, 3 }));
                Eq(3.0, CpuPartitionPolicy.Median(new double[] { 1, 3, 9 }));
                Eq(0.0, CpuPartitionPolicy.Median(null));
            });
            test("interrupt core probe: low-core machines never give up a core", () =>
            {
                var cores = new ulong[] { 0x3, 0xC, 0x30, 0xC0 };
                var rates = new double[8];
                rates[4] = 0.05;
                Eq(0UL, CpuPartitionPolicy.FindInterruptCore(rates, cores,
                    CpuPartitionPolicy.InterruptAvoidMinPhysicalCores - 1));
                if (CpuPartitionPolicy.FindInterruptCore(rates, cores,
                    CpuPartitionPolicy.InterruptAvoidMinPhysicalCores) == 0)
                    throw new Exception("at the core-count threshold the outlier should be picked");
            });
            test("game resolver: unreadable store executables are still accepted", () =>
            {
                string dir = Path.Combine(Path.GetTempPath(), "PaviseAclTest_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                string exe = Path.Combine(dir, "StoreGame.exe");
                try
                {
                    var pe = new byte[512];
                    pe[0] = 0x4D; pe[1] = 0x5A;
                    pe[0x3C] = 0x80;
                    pe[0x80] = 0x50; pe[0x81] = 0x45;
                    File.WriteAllBytes(exe, pe);
                    if (!GameExecutableResolver.IsPortableExecutable(exe))
                        throw new Exception("readable PE was not recognised");

                    var acl = File.GetAccessControl(exe);
                    acl.SetAccessRuleProtection(true, false);
                    acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        System.Security.Principal.WindowsIdentity.GetCurrent().User,
                        System.Security.AccessControl.FileSystemRights.Read,
                        System.Security.AccessControl.AccessControlType.Deny));
                    File.SetAccessControl(exe, acl);

                    if (!File.Exists(exe)) throw new Exception("file vanished after ACL change");
                    if (GameExecutableResolver.IsPortableExecutable(exe))
                        throw new Exception("PE check should fail once reading is denied");
                    if (!GameExecutableResolver.IsUnreadable(exe))
                        throw new Exception("denied file was not classified as unreadable");

                    string resolved, error;
                    if (!GameExecutableResolver.TryResolve(exe, out resolved, out error))
                        throw new Exception("unreadable store exe was rejected: " + error);
                    Eq(exe, resolved);
                }
                finally
                {
                    try
                    {
                        var acl = File.GetAccessControl(exe);
                        acl.SetAccessRuleProtection(false, true);
                        foreach (System.Security.AccessControl.FileSystemAccessRule r in acl.GetAccessRules(
                            true, false, typeof(System.Security.Principal.SecurityIdentifier)))
                            if (r.AccessControlType == System.Security.AccessControl.AccessControlType.Deny)
                                acl.RemoveAccessRule(r);
                        File.SetAccessControl(exe, acl);
                    }
                    catch { }
                    try { Directory.Delete(dir, true); } catch { }
                }
            });
            test("game resolver: a non-existent path is still rejected", () =>
            {
                string missing = Path.Combine(Path.GetTempPath(), "PaviseMissing_" + Guid.NewGuid().ToString("N") + ".exe");
                string resolved, error;
                if (GameExecutableResolver.TryResolve(missing, out resolved, out error))
                    throw new Exception("a missing path must not resolve");
                string txt = Path.Combine(Path.GetTempPath(), "PaviseNot_" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(txt, "not an exe");
                try
                {
                    if (GameExecutableResolver.TryResolve(txt, out resolved, out error))
                        throw new Exception("a non-exe file must not resolve");
                }
                finally { try { File.Delete(txt); } catch { } }
            });
            test("instance takeover: only a strictly newer build replaces the running one", () =>
            {
                if (Program.CompareVersions("1.6.3", "1.6.2") <= 0) throw new Exception("newer build must win");
                if (Program.CompareVersions("1.7.0", "1.6.9") <= 0) throw new Exception("minor bump must win");
                if (Program.CompareVersions("1.6.3", "1.6.3") != 0) throw new Exception("same build must tie");
                if (Program.CompareVersions("1.6.2", "1.6.3") >= 0) throw new Exception("older build must lose");
                if (Program.CompareVersions("v1.6.3", "1.6.2") <= 0) throw new Exception("v-prefix must parse");
                if (Program.CompareVersions("1.6.3", "1.6.3.0") != 0) throw new Exception("1.6.3 must equal 1.6.3.0");
                if (Program.CompareVersions("1.0", null) <= 0) throw new Exception("unknown version must be treated as older");
                if (Program.CompareVersions("1.0", "garbage") <= 0) throw new Exception("unparsable version must be treated as older");
            });
            test("audit page: rebuilding a scrolled list starts back at the top", TestScrolledRebuild);
            test("audit page: the entry slide never flashes a horizontal scrollbar", TestEnterSlideKeepsScrollbarsStable);
            test("language table: no page shows a raw lang key", TestNoUntranslatedKeysOnScreen);
            test("system audit: EcoQoS capability separates interface from full behaviour", () =>
            {
                int build = SystemAudit.WindowsBuild();
                if (build <= 0) throw new Exception("windows build was not resolved");
                if (SystemAudit.EcoQosFullBuild != 22000)
                    throw new Exception("EcoQoS full-behaviour boundary moved unexpectedly");

                AuditReport report = SystemAudit.Collect(300);
                AuditRow eco = null;
                foreach (AuditRow row in report.Capability)
                    if (row.Name.IndexOf("EcoQoS", StringComparison.Ordinal) >= 0) eco = row;
                if (eco == null) throw new Exception("EcoQoS row missing from the capability group");

                bool supported = Native.PowerThrottlingSupported;
                if (!supported) { if (eco.Value != "不支持") throw new Exception("unsupported machine must say so"); }
                else if (build >= SystemAudit.EcoQosFullBuild)
                {
                    if (eco.Value != "支持") throw new Exception("modern build should report full support");
                }
                else if (eco.Value != "接口可用")
                    throw new Exception("older build must not claim full EcoQoS, got: " + eco.Value);
            });
            test("system audit: interrupt tiers split at 1% and 5%", () =>
            {
                Eq(0, SystemAudit.InterruptTier(0.0));
                Eq(0, SystemAudit.InterruptTier(0.0099));
                Eq(1, SystemAudit.InterruptTier(0.01));
                Eq(1, SystemAudit.InterruptTier(0.0265));
                Eq(1, SystemAudit.InterruptTier(0.0499));
                Eq(2, SystemAudit.InterruptTier(0.05));
                Eq(2, SystemAudit.InterruptTier(0.30));
                Eq("干净", SystemAudit.InterruptTierText(0));
                Eq("正常", SystemAudit.InterruptTierText(1));
                Eq("异常", SystemAudit.InterruptTierText(2));
            });
            test("system audit: report always carries all four groups with evidence tags", () =>
            {
                AuditReport report = SystemAudit.Collect(300);
                if (report.Capability.Count < 3) throw new Exception("capability rows missing");
                if (report.Machine.Count < 2) throw new Exception("machine rows missing");
                if (report.Persistent.Count < 5) throw new Exception("persistent rows missing");
                if (report.Verdicts.Count < 3) throw new Exception("verdict rows missing");
                var all = new List<AuditRow>();
                all.AddRange(report.Capability); all.AddRange(report.Machine);
                all.AddRange(report.Persistent); all.AddRange(report.Verdicts);
                foreach (AuditRow row in all)
                {
                    if (string.IsNullOrEmpty(row.Name) || string.IsNullOrEmpty(row.Value))
                        throw new Exception("row missing name or value");
                    if (row.Evidence != SystemAudit.EvMeasuredLocal && row.Evidence != SystemAudit.EvMeasuredBench
                        && row.Evidence != SystemAudit.EvMechanism && row.Evidence != SystemAudit.EvUnverified)
                        throw new Exception("row \"" + row.Name + "\" has unknown evidence tag: " + row.Evidence);
                }
            });
            test("interrupt affinity: mask/byte round-trip is little-endian and lossless", () =>
            {
                Eq(0x000000FFUL, InterruptAffinityTweak.BytesToMask(InterruptAffinityTweak.MaskToBytes(0x000000FFUL)));
                Eq(0x0FUL, InterruptAffinityTweak.BytesToMask(InterruptAffinityTweak.MaskToBytes(0x0FUL)));
                Eq(0xFFFFFFFFFFFFFFFFUL, InterruptAffinityTweak.BytesToMask(InterruptAffinityTweak.MaskToBytes(0xFFFFFFFFFFFFFFFFUL)));
                Eq(0UL, InterruptAffinityTweak.BytesToMask(InterruptAffinityTweak.MaskToBytes(0UL)));
                byte[] b = InterruptAffinityTweak.MaskToBytes(0x0102030405060708UL);
                Eq((byte)0x08, b[0]);
                Eq((byte)0x01, b[7]);
                Eq(0UL, InterruptAffinityTweak.BytesToMask(null));
                Eq(0UL, InterruptAffinityTweak.BytesToMask(new byte[] { 1, 2, 3 }));
            });
            test("visual effects: the animation snapshot is persisted so a crash can recover it", () =>
            {

                int before = 0;
                if (!Native.SystemParametersInfoGet(Native.SPI_GETUIEFFECTS, 0, ref before, 0))
                    throw new TestSkippedException("SPI_GETUIEFFECTS unavailable");
                if (before == 0)
                    throw new TestSkippedException("window animations are already off on this machine");
                if (Settings.LoadStr("PrevUiEffects", "").Length > 0
                    || Settings.LoadStr("PrevTransparency", "").Length > 0)
                    throw new TestSkippedException("another Pavise instance is holding a visual effects snapshot");
                try
                {
                    if (!VisualFx.Activate()) throw new TestSkippedException("visual downgrade unavailable");
                    int during = 0;
                    Native.SystemParametersInfoGet(Native.SPI_GETUIEFFECTS, 0, ref during, 0);
                    Eq(0, during);
                    if (Settings.LoadStr("PrevUiEffects", "").Length == 0)
                        throw new Exception("animation snapshot was not persisted; a crash would strand it");
                }
                finally { VisualFx.Restore(); }
                int after = 0;
                Native.SystemParametersInfoGet(Native.SPI_GETUIEFFECTS, 0, ref after, 0);
                Eq(before, after);
                Eq("", Settings.LoadStr("PrevUiEffects", ""));
            });
            test("powershell bridge: user data is passed as data, never parsed as script", () =>
            {

                string evil = "D:" + "\\" + "Evil" + (char)0x2019 + ";Write-Output PWNED;" + (char)0x2019;
                var argv = new Dictionary<string, string> { { "PAVISE_PATH", evil } };
                string outText;
                if (!PsRunner.Run("Write-Output $env:PAVISE_PATH\r\n", "注入自测", 20000, argv, out outText))
                    throw new TestSkippedException("powershell unavailable");
                foreach (string line in outText.Split('\n'))
                    if (line.Trim() == "PWNED")
                        throw new Exception("injected command executed: user data reached the parser");
                if (outText.IndexOf(evil, StringComparison.Ordinal) < 0)
                    throw new Exception("payload was not echoed verbatim; quoting altered the data");
            });
            test("language table: every entry is complete and format placeholders are consistent", () =>
            {
                int languages = 0;
                foreach (string key in Lang.AllKeys())
                {
                    string[] row = Lang.Row(key);
                    if (row != null && row.Length > languages) languages = row.Length;
                }
                if (languages == 0) throw new Exception("文案表为空");

                var missing = new List<string>();
                foreach (string key in Lang.AllKeys())
                {
                    string[] row = Lang.Row(key);
                    if (row == null || row.Length != languages)
                    {
                        missing.Add(key + "(译文不足" + languages + "种)");
                        continue;
                    }
                    for (int i = 0; i < languages; i++)
                        if (string.IsNullOrEmpty(row[i])) missing.Add(key + "(第" + i + "种语言为空)");

                    int zh = CountPlaceholders(row[0]);
                    for (int i = 1; i < languages; i++)
                        if (CountPlaceholders(row[i]) != zh)
                            missing.Add(key + "(占位符数量各语言不一致)");
                }
                if (missing.Count > 0) throw new Exception(string.Join("; ", missing.ToArray()));
            });

            Settings.UseTransientStoreForCurrentProcess();
            test("crash journal: old 9-field records still load after the QoS fields were added", () =>
            {
                string name = Convert.ToBase64String(Encoding.UTF8.GetBytes("game"));

                Eq("1|-1|-1", CrashGuard.ProbeParse("111|222|" + name + "|32|255|2|5|-1|"));

                Eq("1|1|1", CrashGuard.ProbeParse("111|222|" + name + "|32|255|2|5|-1||1|1"));

                Eq("1|-1|-1", CrashGuard.ProbeParse("111|222|" + name + "|32|255|2|5|-1||x|y"));

                Eq("1|1|0", CrashGuard.ProbeParse("111|222|" + name + "|32|255|2|5|-1|3,4,5|1|0"));

                Eq("0", CrashGuard.ProbeParse("111|222|" + name + "|32"));
            });
            test("GPU tuning: BuildDesired maps every switch to its DRS keys", TestNvBuildDesired);
            test("GPU tuning: FRL/DLSS mode round-trips keep every option including 240", TestGpuModeRoundTrips);
            test("GPU tuning: an empty plan never reaches the driver", TestNvPlanEmpty);
            test("GPU throttle: verdict needs enough samples and formats percentages", TestGpuThrottleSummary);
            test("ADLX: a machine without AMD driver degrades to safe no-ops", TestAdlxDegrade);
            test("ReBAR probe: PCI filtering, thresholds and a live window read", TestRebarProbe);
            test("whitelist: scope is decided automatically, shell and script hosts never get family", TestWhitelistAutoScope);
            test("whitelist: only exe and shortcut drops are accepted", TestWhitelistDropTargets);
            test("whitelist: auto-add then narrow and widen keep one rule per program", TestWhitelistAutoAddAndReshape);
            test("whitelist picker: system, anti-cheat and already-listed programs are hidden", TestRunningPickerHidesSystemAndDuplicates);
            test("whitelist picker: memory sizes format correctly", TestMemoryFormatting);
            test("suppression: game-root containment is anchored on a path segment", () =>
            {

                Eq(true, GameMode.UnderRoot(@"D:\Games\Apex\bin\game.exe", @"D:\Games\Apex"));
                Eq(true, GameMode.UnderRoot(@"D:\Games\Apex\bin\game.exe", @"D:\Games\Apex\"));
                Eq(false, GameMode.UnderRoot(@"D:\Games\ApexBackup\sync.exe", @"D:\Games\Apex"));
                Eq(false, GameMode.UnderRoot(@"D:\Games\ApexTools\updater.exe", @"D:\Games\Apex\"));
                Eq(false, GameMode.UnderRoot(@"D:\SteamLibrary\x\y.exe", @"D:\Steam"));

                Eq(false, GameMode.UnderRoot(@"D:\Games\Apex", @"D:\Games\Apex"));
                Eq(false, GameMode.UnderRoot(null, @"D:\Games\Apex"));
                Eq(false, GameMode.UnderRoot(@"D:\Games\Apex\a.exe", null));
                Eq(false, GameMode.UnderRoot(@"D:\Games\Apex\a.exe", ""));

                const string win = @"C:\Windows\";
                Eq(false, GameMode.BasicBackgroundEligible(10, 99, "game", @"D:\Games\Apex\bin\game.exe",
                    1, 1, 20, false, win, false, @"D:\Games\Apex"));
                Eq(true, GameMode.BasicBackgroundEligible(10, 99, "sync", @"D:\Games\ApexBackup\sync.exe",
                    1, 1, 20, false, win, false, @"D:\Games\Apex"));
            });
            test("suppression: every library game root is exempt, not just the active one", () =>
            {
                var roots = new List<string> { @"D:\Games\Apex", @"E:\Genshin Impact\Genshin Impact Game" };
                Eq(@"E:\Genshin Impact\Genshin Impact Game", GameMode.LibraryRootOf(
                    @"E:\Genshin Impact\Genshin Impact Game\YuanShen.exe", roots));
                Eq(@"D:\Games\Apex", GameMode.LibraryRootOf(@"D:\Games\Apex\bin\game.exe", roots));
                Eq((string)null, GameMode.LibraryRootOf(@"E:\Genshin Impact\Genshin Impact GameBackup\x.exe", roots));
                Eq((string)null, GameMode.LibraryRootOf(@"C:\Apps\a.exe", roots));
                Eq((string)null, GameMode.LibraryRootOf(null, roots));
                Eq((string)null, GameMode.LibraryRootOf(@"D:\Games\Apex\bin\game.exe", null));
                Eq((string)null, GameMode.LibraryRootOf(@"D:\anything\x.exe", new List<string> { @"D:\", @"D:" }));
                Eq(true, TaskHelper.IsVolatileAutostartPath(@"D:\应用\微信\xwechat_files\wxid_x\msg\file\2026-07\Pavise(1).exe"));
                Eq(true, TaskHelper.IsVolatileAutostartPath(@"C:\Users\a\AppData\Local\Temp\Pavise.exe"));
                Eq(false, TaskHelper.IsVolatileAutostartPath(@"D:\游戏\Pavise.exe"));
                Eq(false, GameMode.BasicBackgroundEligible(10, 99, "YuanShen", @"E:\Genshin Impact\Genshin Impact Game\YuanShen.exe",
                    1, 1, 20, false, @"C:\Windows\", false,
                    GameMode.LibraryRootOf(@"E:\Genshin Impact\Genshin Impact Game\YuanShen.exe", roots), true));
            });
            test("suppression: anti-cheat exemption is as broad as anti-cheat detection", () =>
            {
                const string win = @"C:\Windows\";

                string[] names = { "GameAntiCheat", "BattlEye", "SGuard64Helper", "EasyAntiCheat_x64",
                                   "vgtray", "GameMon64", "TenSafe_1", "ACE-Helper" };
                foreach (string n in names)
                {
                    if (!GameSessionDetector.IsAntiCheatLikeName(n))
                        throw new Exception("detector no longer treats " + n + " as anti-cheat; test premise broken");

                    Eq(false, GameMode.BasicBackgroundEligible(10, 99, n, @"C:\Program Files\AC\" + n + ".exe",
                        1, 1, 20, false, win));

                    Eq(false, GameMode.BasicBackgroundEligible(10, 99, n, @"C:\Program Files\AC\" + n + ".exe",
                        1, 1, 20, false, win, false, null, true));
                }
            });
            test("suppression: the foreground window is never background material, aggressive or not", () =>
            {
                const string win = @"C:\Windows\";

                const string roblox =
                    @"C:\Users\a\AppData\Local\Roblox\Versions\version-1a2b\RobloxPlayerBeta.exe";
                foreach (bool aggressive in new[] { false, true })
                {

                    Eq(false, GameMode.BasicBackgroundEligible(4321, 99, "RobloxPlayerBeta", roblox,
                        1, 1, 4321, false, win, false, null, aggressive));

                    Eq(true, GameMode.BasicBackgroundEligible(4321, 99, "RobloxPlayerBeta", roblox,
                        1, 1, 20, false, win, false, null, aggressive));
                }
            });
            test("suppression: network accelerators are exempt as broadly as anti-cheat", () =>
            {
                const string win = @"C:\Windows\";

                string[] names = { "uu", "uu_ball", "xunyou", "leigod", "leigod_launcher",
                                   "leishenSdk", "qiyou", "biubiu", "bbservice", "DolphinQ",
                                   "wtfast", "ExitLag", "NoPing", "GameAccelerator", "网易加速器" };
                foreach (string n in names)
                {
                    if (!NetAcceleratorCatalog.IsAcceleratorLikeName(n))
                        throw new Exception("catalog no longer treats " + n + " as an accelerator; test premise broken");

                    foreach (bool aggressive in new[] { false, true })
                        Eq(false, GameMode.BasicBackgroundEligible(10, 99, n,
                            @"C:\Program Files\Acc\" + n + ".exe",
                            1, 1, 20, false, win, false, null, aggressive));
                }

                string[] innocent = { "chrome", "explorer", "worker", "steam", "discord", "obs64" };
                foreach (string n in innocent)
                    if (NetAcceleratorCatalog.IsAcceleratorLikeName(n))
                        throw new Exception(n + " must not be mistaken for an accelerator");

                string[] displayNames = { "网易UU加速器", "腾讯网游加速器", "雷神加速器", "迅游加速器", "GearUP Booster" };
                foreach (string n in displayNames)
                    if (!NetAcceleratorCatalog.IsAcceleratorLikeName(n))
                        throw new Exception("display name " + n + " must be recognized as an accelerator");
            });
            test("freeze: nothing under the Windows directory is ever suspended", () =>
            {
                const string win = @"C:\Windows\";

                Eq(true, GameMode.FreezeForbidden("ChsIME", @"C:\Windows\System32\InputMethod\CHS\ChsIME.exe", win));

                Eq(true, GameMode.FreezeForbidden("atieclxx", @"C:\Windows\System32\atieclxx.exe", win));
                Eq(true, GameMode.FreezeForbidden("SearchIndexer", @"C:\Windows\System32\SearchIndexer.exe", win));
                Eq(true, GameMode.FreezeForbidden("SystemSettings", @"C:\Windows\ImmersiveControlPanel\SystemSettings.exe", win));

                Eq(true, GameMode.FreezeForbidden("adb", @"D:\Emulator\adb.exe", win));
                Eq(true, GameMode.FreezeForbidden("CAudioFilterAgent64", @"C:\Program Files\Conexant\CAudioFilterAgent64.exe", win));

                Eq(false, GameMode.FreezeForbidden("SogouCloud", @"C:\Program Files (x86)\SogouInput\16.6.0.4385\SogouCloud.exe", win));

                Eq(false, GameMode.FreezeForbidden("QQ", @"C:\Program Files\Tencent\QQ\QQ.exe", win));
                Eq(false, GameMode.FreezeForbidden("worker", @"D:\Apps\worker.exe", win));
                Eq(false, GameMode.FreezeForbidden("crashpad_handler", @"D:\Apps\crashpad_handler.exe", win));

                Eq(true, GameMode.BasicBackgroundEligible(10, 99, "SearchIndexer",
                    @"C:\Windows\System32\SearchIndexer.exe", 1, 1, 20, false, win, false, null, true));
                Eq(true, GameMode.BasicBackgroundEligible(9652, 99, "ChsIME",
                    @"C:\Windows\System32\InputMethod\CHS\ChsIME.exe", 1, 1, 20, false, win, false, null, true));
            });
            test("theme fonts: the shared font cache survives repeated painting", () =>
            {

                using (var panel = new EmptyStatePanel())
                {
                    panel.Size = new Size(320, 220);
                    panel.ShowEmpty = true;
                    panel.EmptyTitle = "TITLE";
                    panel.EmptyDetail = "DETAIL";
                    for (int i = 0; i < 3; i++)
                        using (var bmp = new Bitmap(320, 220))
                            panel.DrawToBitmap(bmp, new Rectangle(0, 0, 320, 220));
                }

                foreach (float size in new[] { 9.25f, 8.4f, 10.2f, 7.8f, 7.6f })
                {
                    if (Theme.UI(size, true).Height <= 0) throw new Exception(size + "pt bold font is unusable");
                    if (Theme.UI(size, false).Height <= 0) throw new Exception(size + "pt font is unusable");
                }
            });
            test("defender exclusion: path matching never mistakes a neighbour for an owned entry", () =>
            {

                Eq(@"C:\Games\Foo", DefenderExclusion.Normalize(@"C:\Games\Foo\"));
                Eq(@"C:\Games\Foo", DefenderExclusion.Normalize(@"  ""C:\Games\Foo""  "));
                Eq("", DefenderExclusion.Normalize(null));
                Eq("", DefenderExclusion.Normalize("   "));

                Eq(@"C:\", DefenderExclusion.Normalize(@"C:\"));

                var owned = new List<string> { @"C:\Games\Foo", @"D:\Steam\Bar\" };
                Eq(true, DefenderExclusion.Contains(owned, @"c:\games\foo"));
                Eq(true, DefenderExclusion.Contains(owned, @"C:\Games\Foo\"));
                Eq(true, DefenderExclusion.Contains(owned, @"D:\Steam\Bar"));

                Eq(false, DefenderExclusion.Contains(owned, @"C:\Games\Foobar"));
                Eq(false, DefenderExclusion.Contains(owned, @"C:\Games"));
                Eq(false, DefenderExclusion.Contains(owned, @"C:\Games\Foo\Sub"));
                Eq(false, DefenderExclusion.Contains(new List<string>(), @"C:\Games\Foo"));
            });
            test("per-game GPU preference: merging never destroys fields Windows owns", () =>
            {

                Eq("AppStatus=0;GpuPreference=2;", GameExeTweaks.MergeField("AppStatus=0;", "GpuPreference", "2"));
                Eq("AppStatus=4096;GpuPreference=2;", GameExeTweaks.MergeField("AppStatus=4096;GpuPreference=0;", "GpuPreference", "2"));

                Eq("GpuPreference=2;AppStatus=0;", GameExeTweaks.MergeField("GpuPreference=0;AppStatus=0;", "GpuPreference", "2"));

                Eq("GpuPreference=2;", GameExeTweaks.MergeField(null, "GpuPreference", "2"));
                Eq("GpuPreference=2;", GameExeTweaks.MergeField("", "GpuPreference", "2"));
                Eq("GpuPreference=2;", GameExeTweaks.MergeField(";;", "GpuPreference", "2"));

                Eq("Garbage;GpuPreference=2;", GameExeTweaks.MergeField("Garbage;", "GpuPreference", "2"));

                Eq("GpuPreference=2;", GameExeTweaks.MergeField("GpuPreference=0;GpuPreference=1;", "GpuPreference", "2"));

                Eq("2", GameExeTweaks.ReadField("AppStatus=4096;GpuPreference=2;", "GpuPreference"));
                Eq("0", GameExeTweaks.ReadField("AppStatus=0;", "AppStatus"));
                Eq(null, GameExeTweaks.ReadField("AppStatus=0;", "GpuPreference"));
                Eq(null, GameExeTweaks.ReadField(null, "GpuPreference"));

                Eq(null, GameExeTweaks.ReadField("XGpuPreference=2;", "GpuPreference"));
            });
            test("release notes: current version is documented and fully translated", () =>
            {
                if (ReleaseNotes.All.Length == 0) throw new Exception("no release notes are bundled");
                ReleaseNote cur = ReleaseNotes.Current;
                if (cur == null) throw new Exception("shipping version " + App.Version + " has no release-note entry");
                if (cur.Count == 0) throw new Exception("current version's entry has no items");

                List<string> missing = ReleaseNotes.MissingTranslations();
                if (missing.Count > 0) throw new Exception("untranslated notes: " + string.Join(", ", missing.ToArray()));

                for (int i = 1; i < ReleaseNotes.All.Length; i++)
                    if (!UpdateChecker.IsNewer(ReleaseNotes.All[i - 1].Version, ReleaseNotes.All[i].Version))
                        throw new Exception("notes are not ordered newest-first at index " + i);
                foreach (ReleaseNote n in ReleaseNotes.All)
                {
                    if (string.IsNullOrEmpty(n.Date)) throw new Exception(n.Version + " has no date");
                    if (n.Tag != "v" + n.Version) throw new Exception("bad tag for " + n.Version);
                }

                Eq("", cur.Item(-1));
                Eq("", cur.Item(cur.Count));
            });
            test("auto-hide: fires once per game session and re-arms only on the next one", () =>
            {
                bool last = false, armed = false;

                Eq(AutoHideAction.None, PanelForm.NextAutoHide(true, ref last, ref armed, false, true));
                Eq(AutoHideAction.Cancel, PanelForm.NextAutoHide(false, ref last, ref armed, false, true));

                last = false; armed = false;
                Eq(AutoHideAction.Schedule, PanelForm.NextAutoHide(true, ref last, ref armed, true, true));

                Eq(AutoHideAction.None, PanelForm.NextAutoHide(true, ref last, ref armed, true, true));
                Eq(AutoHideAction.None, PanelForm.NextAutoHide(true, ref last, ref armed, true, true));

                Eq(AutoHideAction.Cancel, PanelForm.NextAutoHide(false, ref last, ref armed, true, true));
                Eq(false, armed);

                Eq(AutoHideAction.Schedule, PanelForm.NextAutoHide(true, ref last, ref armed, true, true));

                last = false; armed = false;
                Eq(AutoHideAction.None, PanelForm.NextAutoHide(true, ref last, ref armed, true, false));
                Eq(true, armed);

                Eq(AutoHideAction.None, PanelForm.NextAutoHide(true, ref last, ref armed, true, true));
            });
            test("UI dormancy: hidden/minimized windows cannot revive animation timers", TestUiDormancyState);
            test("network QoS: policy names stay unique, ASCII-safe and bounded in length", () =>
            {
                string a = NetworkAffinityTweak.SanitizePolicyName("Valorant", @"C:\Games\Valorant\VALORANT.exe");
                string b = NetworkAffinityTweak.SanitizePolicyName("Valorant", @"C:\Games\Valorant2\VALORANT.exe");
                if (a == b) throw new Exception("different exe paths collided into the same policy name");
                Eq(a, NetworkAffinityTweak.SanitizePolicyName("Valorant", @"C:\Games\Valorant\VALORANT.exe"));
                string weird = NetworkAffinityTweak.SanitizePolicyName("!!!///###", @"C:\g.exe");
                foreach (char c in weird) if (!(char.IsLetterOrDigit(c) || c == '_'))
                    throw new Exception("sanitized name contains an unsafe character: " + c);
                string longName = NetworkAffinityTweak.SanitizePolicyName(new string('A', 200), @"C:\g.exe");
                if (longName.Length > 64) throw new Exception("policy name is too long: " + longName.Length);
                string empty = NetworkAffinityTweak.SanitizePolicyName("", @"C:\g.exe");
                if (!empty.StartsWith("Pavise_Game")) throw new Exception("empty game name did not fall back to a placeholder");
            });
            test("anti-cheat tiers: level tags round-trip and priority mapping per tier", () =>
            {
                Eq(SuppressionLevel.Eco, Tamer.ParseLevel(Tamer.LevelTag(SuppressionLevel.Eco)));
                Eq(SuppressionLevel.Restrained, Tamer.ParseLevel(Tamer.LevelTag(SuppressionLevel.Restrained)));
                Eq(SuppressionLevel.Isolated, Tamer.ParseLevel(Tamer.LevelTag(SuppressionLevel.Isolated)));
                Eq(SuppressionLevel.Isolated, Tamer.ParseLevel("garbage"));
                Eq(SuppressionLevel.Isolated, Tamer.ParseLevel(null));

                Eq(Native.NORMAL_PRIORITY_CLASS, SuppressionCore.DesiredPriority(SuppressionLevel.Eco, Native.NORMAL_PRIORITY_CLASS));
                Eq(Native.HIGH_PRIORITY_CLASS, SuppressionCore.DesiredPriority(SuppressionLevel.Eco, Native.HIGH_PRIORITY_CLASS));
                Eq(Native.BELOW_NORMAL_PRIORITY_CLASS, SuppressionCore.DesiredPriority(SuppressionLevel.Restrained, Native.NORMAL_PRIORITY_CLASS));
                Eq(Native.IDLE_PRIORITY_CLASS, SuppressionCore.DesiredPriority(SuppressionLevel.Isolated, Native.NORMAL_PRIORITY_CLASS));
                Eq(Native.NORMAL_PRIORITY_CLASS, SuppressionCore.DesiredPriority(SuppressionLevel.Eco, 0));
            });
            test("frame cap and DRS snapshots: value mapping round-trips", () =>
            {
                Lang.Init();
                Eq(60, GameMode.ResolveFrlFps("60"));
                Eq(120, GameMode.ResolveFrlFps("120"));
                Eq(240, GameMode.ResolveFrlFps("240"));
                Eq(0, GameMode.ResolveFrlFps("off"));
                Eq(0, GameMode.ResolveFrlFps("junk"));
                int screenFps = GameMode.ResolveFrlFps("screen");
                if (screenFps != 0 && screenFps < 45)
                    throw new Exception("screen frl out of range: " + screenFps);

                var snap = NvDrsTweaks.ParseSnapshot("pstate=absent;prerender=2");
                Eq("absent", snap["pstate"]);
                Eq("2", snap["prerender"]);
                Eq("prerender=2;pstate=absent", NvDrsTweaks.SerializeSnapshot(snap));
                Eq(0, NvDrsTweaks.ParseSnapshot("").Count);
            });
            test("windowed optimization: field merges and removes without touching siblings", () =>
            {
                string shared = "VRROptimizeEnable=1;AutoHDREnable=0;";
                string on = GameExeTweaks.MergeField(shared, "SwapEffectUpgradeEnable", "1");
                Eq("1", GameExeTweaks.ReadField(on, "SwapEffectUpgradeEnable"));
                Eq("1", GameExeTweaks.ReadField(on, "VRROptimizeEnable"));
                Eq("0", GameExeTweaks.ReadField(on, "AutoHDREnable"));

                string off = GameExeTweaks.RemoveField(on, "SwapEffectUpgradeEnable");
                Eq(null, GameExeTweaks.ReadField(off, "SwapEffectUpgradeEnable"));
                Eq("1", GameExeTweaks.ReadField(off, "VRROptimizeEnable"));

                string wasZero = GameExeTweaks.MergeField("SwapEffectUpgradeEnable=0;", "SwapEffectUpgradeEnable", "1");
                Eq("1", GameExeTweaks.ReadField(wasZero, "SwapEffectUpgradeEnable"));
                Eq("0", GameExeTweaks.ReadField(
                    GameExeTweaks.RestoreField(wasZero, "SwapEffectUpgradeEnable=0;", "SwapEffectUpgradeEnable"),
                    "SwapEffectUpgradeEnable"));
                Eq("", GameExeTweaks.RemoveField("SwapEffectUpgradeEnable=1;", "SwapEffectUpgradeEnable"));
            });
            test("steam shortcut: rungameid/vdf parsing and main-exe heuristics", () =>
            {
                long appId;
                Eq(true, SteamShortcut.TryParseUrlFile(
                    "[InternetShortcut]\r\nURL=steam://rungameid/730\r\nIconIndex=0", out appId));
                Eq(730L, appId);
                Eq(false, SteamShortcut.TryParseUrlFile(
                    "[InternetShortcut]\r\nURL=https://example.com", out appId));
                Eq(false, SteamShortcut.TryParseUrlFile("", out appId));

                var libs = SteamShortcut.ParseLibraryPaths(
                    "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}");
                Eq(2, libs.Count);
                Eq(@"C:\Program Files (x86)\Steam", libs[0]);
                Eq(@"D:\SteamLibrary", libs[1]);
                Eq("Counter-Strike Global Offensive", SteamShortcut.ParseVdfValue(
                    "\"AppState\"\n{\n\t\"appid\"\t\t\"730\"\n\t\"installdir\"\t\t\"Counter-Strike Global Offensive\"\n}", "installdir"));

                string exeRoot = Path.Combine(Path.GetTempPath(),
                    "PaviseSteamPick_" + Process.GetCurrentProcess().Id);
                try
                {
                    Directory.CreateDirectory(Path.Combine(exeRoot, @"game\bin\win64"));
                    Directory.CreateDirectory(Path.Combine(exeRoot, "redist"));
                    File.WriteAllBytes(Path.Combine(exeRoot, @"game\bin\win64\cs2.exe"), new byte[6 * 1024 * 1024]);
                    File.WriteAllBytes(Path.Combine(exeRoot, @"redist\vc_redist.x64.exe"), new byte[20 * 1024 * 1024]);
                    File.WriteAllBytes(Path.Combine(exeRoot, "crashhandler64.exe"), new byte[1024]);
                    string picked = SteamShortcut.PickMainExecutable(exeRoot, "Counter-Strike Global Offensive");
                    if (picked == null || !picked.EndsWith("cs2.exe", StringComparison.OrdinalIgnoreCase))
                        throw new Exception("main exe heuristic picked: " + picked);
                }
                finally { try { Directory.Delete(exeRoot, true); } catch { } }
            });
            test("render election: windowed sibling waits for GPU; fullscreen elects at once (Bannerlord pattern)", () =>
            {
                Lang.Init();
                string gameDir = @"C:\g\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client";
                var profile = GameProfileStore.NewProfile("Bannerlord", gameDir,
                    Path.Combine(gameDir, "Bannerlord.exe"));
                long now = DateTime.UtcNow.ToFileTimeUtc();
                long created = now - 60L * 10000000L;
                var launcher = new GameProcessSnapshot
                {
                    Pid = 4242, ParentPid = 1, Creation = created,
                    Name = "TaleWorlds.MountAndBlade.Launcher",
                    Path = Path.Combine(gameDir, "TaleWorlds.MountAndBlade.Launcher.exe"),
                    Visible = true, Foreground = true
                };
                bool armed;
                GameDetection hit = GameSessionDetector.DetectSnapshot(
                    new[] { launcher }, new[] { profile }, out armed);
                if (hit == null) throw new Exception("windowed in-root candidate disappeared");
                Eq(true, armed);
                Eq(true, hit.RequiresGpuConfirm);
                Eq(false, hit.RendererCandidateSelected);
                Eq("TaleWorlds.MountAndBlade.Launcher", hit.RendererName);

                launcher.FullscreenLike = true;
                hit = GameSessionDetector.DetectSnapshot(
                    new[] { launcher }, new[] { profile }, out armed);
                if (hit == null) throw new Exception("fullscreen candidate was not elected");
                Eq(false, hit.RequiresGpuConfirm);
                Eq(true, hit.RendererCandidateSelected);
                Eq(true, hit.RendererLearnable);
                Eq("TaleWorlds.MountAndBlade.Launcher", hit.RendererName);

                var otherDir = new GameProcessSnapshot
                {
                    Pid = 4243, ParentPid = 1, Creation = created,
                    Name = "SomeClientLauncher",
                    Path = @"C:\g\Mount & Blade II Bannerlord\ux\SomeClientLauncher.exe",
                    Visible = true, Foreground = true, FullscreenLike = true
                };
                if (GameSessionDetector.DetectSnapshot(new[] { otherDir }, new[] { profile }, out armed) != null)
                    throw new Exception("out-of-root process must not anchor");
                Eq(false, armed);

                var headless = new GameProcessSnapshot
                {
                    Pid = 4244, ParentPid = 1, Creation = created,
                    Name = "TaleWorlds.MountAndBlade.Launcher",
                    Path = Path.Combine(gameDir, "TaleWorlds.MountAndBlade.Launcher.exe"),
                    Visible = false, Foreground = false
                };
                if (GameSessionDetector.DetectSnapshot(new[] { headless }, new[] { profile }, out armed) != null)
                    throw new Exception("windowless family must stay armed, not engaged");
                Eq(true, armed);

                var updater = new GameProcessSnapshot
                {
                    Pid = 4245, ParentPid = 1, Creation = created,
                    Name = "BannerlordUninstall",
                    Path = Path.Combine(gameDir, "BannerlordUninstall.exe"),
                    Visible = true, Foreground = true, FullscreenLike = true
                };
                if (GameSessionDetector.DetectSnapshot(new[] { updater }, new[] { profile }, out armed) != null)
                    throw new Exception("non-game role must never be elected");
            });
            test("render detector: learned renderer anchors without the launcher (LOL pattern)", () =>
            {
                Lang.Init();
                string lolRoot = @"C:\g\WeGameApps\英雄联盟";
                var profile = GameProfileStore.NewProfile("英雄联盟", lolRoot,
                    Path.Combine(lolRoot, "Riot Client\\RiotClientServices.exe"));
                profile.LearnedExecutablePath = Path.Combine(lolRoot, "Game\\League of Legends.exe");
                long now = DateTime.UtcNow.ToFileTimeUtc();
                long created = now - 60L * 10000000L;

                var game = new GameProcessSnapshot
                {
                    Pid = 5301, ParentPid = 1, Creation = created,
                    Name = "League of Legends",
                    Path = Path.Combine(lolRoot, "Game\\League of Legends.exe"),
                    Visible = true, Foreground = true
                };
                bool armed;
                GameDetection hit = GameSessionDetector.DetectSnapshot(
                    new[] { game }, new[] { profile }, out armed);
                if (hit == null) throw new Exception("learned renderer did not anchor the session");
                Eq("League of Legends", hit.RendererName);
                Eq(true, hit.RendererUserSelected);
                Eq(false, hit.RendererLearnable);
                Eq(false, hit.RequiresGpuConfirm);

                game.Foreground = false;
                hit = GameSessionDetector.DetectSnapshot(
                    new[] { game }, new[] { profile }, out armed);
                if (hit == null) throw new Exception("learned renderer must elect on visibility alone");
                Eq(false, hit.RequiresGpuConfirm);

                var stranger = new GameProcessSnapshot
                {
                    Pid = 5302, ParentPid = 1, Creation = created,
                    Name = "LeagueClientUxRender",
                    Path = Path.Combine(lolRoot, "LeagueClient\\LeagueClientUxRender.exe"),
                    Visible = false, Foreground = false
                };
                if (GameSessionDetector.DetectSnapshot(new[] { stranger }, new[] { profile }, out armed) != null)
                    throw new Exception("unlearned sibling must not anchor");
                Eq(true, armed);

                var impostor = new GameProcessSnapshot
                {
                    Pid = 5303, ParentPid = 1, Creation = created,
                    Name = "League of Legends",
                    Path = @"D:\Fake\Game\League of Legends.exe",
                    Visible = true, Foreground = true, FullscreenLike = true
                };
                if (GameSessionDetector.DetectSnapshot(new[] { impostor }, new[] { profile }, out armed) != null)
                    throw new Exception("same-name impostor outside the root must not anchor");
            });
            test("render election: client shell arms only; the real game engages and is learnable", () =>
            {
                Lang.Init();
                string lolRoot = @"C:\g\WeGameApps\英雄联盟";
                var profile = GameProfileStore.NewProfile("英雄联盟", lolRoot,
                    Path.Combine(lolRoot, "Riot Client\\RiotClientServices.exe"));
                long now = DateTime.UtcNow.ToFileTimeUtc();
                long created = now - 60L * 10000000L;

                var launcher = new GameProcessSnapshot
                {
                    Pid = 6001, ParentPid = 1, Creation = created,
                    Name = "RiotClientServices",
                    Path = Path.Combine(lolRoot, "Riot Client\\RiotClientServices.exe"),
                    Visible = true, Foreground = true, FullscreenLike = true
                };
                bool armed;
                GameDetection launcherOnly = GameSessionDetector.DetectSnapshot(
                    new[] { launcher }, new[] { profile }, out armed);
                Eq<GameDetection>(null, launcherOnly);
                Eq(true, armed);

                launcher.Foreground = false;
                launcher.FullscreenLike = false;
                var game = new GameProcessSnapshot
                {
                    Pid = 6002, ParentPid = 6001, Creation = created + 1000,
                    Name = "League of Legends",
                    Path = Path.Combine(lolRoot, "Game\\League of Legends.exe"),
                    Visible = true, Foreground = true
                };
                GameDetection pending = GameSessionDetector.DetectSnapshot(
                    new[] { launcher, game }, new[] { profile }, out armed);
                if (pending == null) throw new Exception("real game candidate disappeared");
                Eq(true, pending.RequiresGpuConfirm);
                Eq("League of Legends", pending.RendererName);

                game.FullscreenLike = true;
                GameDetection hit = GameSessionDetector.DetectSnapshot(
                    new[] { launcher, game }, new[] { profile }, out armed);
                if (hit == null) throw new Exception("fullscreen game was not elected");
                Eq("League of Legends", hit.RendererName);
                Eq(false, hit.RequiresGpuConfirm);
                Eq(true, hit.RendererLearnable);
                Eq(true, hit.FamilyPids.Contains(6001));
                Eq(true, hit.FamilyPids.Contains(6002));
            });
            test("render election: fullscreen game outranks a stale learned launcher (Bannerlord handover)", () =>
            {
                Lang.Init();
                string blRoot = @"C:\g\Mount & Blade II Bannerlord";
                string binDir = Path.Combine(blRoot, "bin", "Win64_Shipping_Client");
                var profile = GameProfileStore.NewProfile("Bannerlord", blRoot,
                    Path.Combine(binDir, "Launcher.Native.exe"));
                profile.LearnedExecutablePath = Path.Combine(binDir, "TaleWorlds.MountAndBlade.Launcher.exe");
                long now = DateTime.UtcNow.ToFileTimeUtc();
                long created = now - 60L * 10000000L;

                var stale = new GameProcessSnapshot
                {
                    Pid = 8001, ParentPid = 1, Creation = created,
                    Name = "TaleWorlds.MountAndBlade.Launcher",
                    Path = profile.LearnedExecutablePath,
                    Visible = true
                };
                var game = new GameProcessSnapshot
                {
                    Pid = 8002, ParentPid = 8001, Creation = created + 1000,
                    Name = "Bannerlord",
                    Path = Path.Combine(binDir, "Bannerlord.exe"),
                    Visible = true, Foreground = true, FullscreenLike = true
                };
                bool armed;
                GameDetection hit = GameSessionDetector.DetectSnapshot(
                    new[] { stale, game }, new[] { profile }, out armed);
                if (hit == null) throw new Exception("handover election failed");
                Eq("Bannerlord", hit.RendererName);
                Eq(true, hit.RendererLearnable);
                Eq(false, hit.RequiresGpuConfirm);

                game.Foreground = false;
                game.FullscreenLike = false;
                hit = GameSessionDetector.DetectSnapshot(
                    new[] { stale, game }, new[] { profile }, out armed);
                if (hit == null) throw new Exception("learned fallback disappeared");
                Eq("TaleWorlds.MountAndBlade.Launcher", hit.RendererName);
            });
            test("evidence plumbing: GPU pid parse, fullscreen geometry, library candidate filter", () =>
            {
                Eq(4242, GpuEvidence.ParsePid("pid_4242_luid_0x00000000_0x0000ABCD_phys_0_eng_0_engtype_3D"));
                Eq(0, GpuEvidence.ParsePid("luid_0x0_phys_0"));
                Eq(0, GpuEvidence.ParsePid(null));
                Eq(0, GpuEvidence.ParsePid("pid__"));

                var monitor = new GameSessionDetector.NativeRect { Left = 0, Top = 0, Right = 2560, Bottom = 1440 };
                Eq(true, GameSessionDetector.RectCoversMonitor(monitor, monitor));
                var windowed = new GameSessionDetector.NativeRect { Left = 100, Top = 100, Right = 1380, Bottom = 820 };
                Eq(false, GameSessionDetector.RectCoversMonitor(windowed, monitor));
                var spill = new GameSessionDetector.NativeRect { Left = -8, Top = -8, Right = 2568, Bottom = 1448 };
                Eq(true, GameSessionDetector.RectCoversMonitor(spill, monitor));

                string win = @"C:\Windows\";
                Eq(false, GameSessionDetector.IsLibraryCandidate("chrome", @"D:\Apps\chrome.exe", win));
                Eq(false, GameSessionDetector.IsLibraryCandidate("steam", @"C:\Program Files (x86)\Steam\steam.exe", win));
                Eq(false, GameSessionDetector.IsLibraryCandidate("dwm", @"C:\Windows\System32\dwm.exe", win));
                Eq(false, GameSessionDetector.IsLibraryCandidate("SGuard64", @"D:\g\SGuard64.exe", win));
                Eq(false, GameSessionDetector.IsLibraryCandidate("vlc", @"D:\Apps\vlc.exe", win));
                Eq(false, GameSessionDetector.IsLibraryCandidate("LeagueClientUx", @"D:\g\lol\LeagueClient\LeagueClientUx.exe", win));
                Eq(true, GameSessionDetector.IsLibraryCandidate("cs2", @"D:\Steam\steamapps\common\cs2\game\bin\win64\cs2.exe", win));
                Eq(true, GameSessionDetector.IsLibraryCandidate("League of Legends", @"D:\g\lol\Game\League of Legends.exe", win));
                Eq(false, GameSessionDetector.IsLibraryCandidate(null, @"D:\x.exe", win));
                Eq(false, GameSessionDetector.IsLibraryCandidate("x", null, win));
            });

            string root = Path.Combine(Path.GetTempPath(), "PaviseSelfTest_" + Process.GetCurrentProcess().Id + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                test("game catalog: legacy list is cleared with a backup; fresh add persists install root", () => TestGameCatalogUpgrade(root));
                test("game profiles: pre-election legacy list is cleared; fresh saves deduplicate", () => TestProfileStore(root));
                test("game profiles: learned renderer survives the V4 roundtrip", () => TestLearnedRendererStore(root));
                test("game profiles: a newer-format file is read-only and never overwritten", () => TestFutureProfileFormatProtected(root));
                test("game profiles: pre-election era stores are cleared with a backup", () => TestV3ProfileMigration(root));
                test("game scan: uninstall registry hits drop net accelerators", () => TestUninstallScanFiltersAccelerators(root));
                test("game scan: main-exe pick follows structure, not size (LOL / Unity patterns)", () => TestPickMainExeStructure(root));
                test("game scan: fake Steam library resolves games across libraries and filters junk", () => TestSteamLibraryScan(root));
                test("game scan: store package repository accepts Xbox fingerprints only", () => TestStorePackageScan(root));
                test("game profiles: an unreadable file is never overwritten by a save", () => TestProfileLoadFailure(root));
                test("game library: EXE/LNK resolve without executing the target", () => TestExecutableResolver(root));
                test("LoL addons: delete touches only add-on layers, never the game core", () => TestLolAddonDelete(root));
                test("render detector: headless entry arms only; sessions need window evidence", () => TestHeadlessEntry(root));
                test("render detector: family follows the profile root, not name prefixes", () => TestFallbackEntryRootBoundary(root));
                test("renderer boost: HIGH priority and IO3 are verified by readback", () => TestBoostReadback(root));
                test("renderer boost: retained crash snapshot is re-adopted exactly", TestCrashBoostReAdoption);
                test("EcoQoS restore: a process' own power-saving opt-in survives suppression", () => TestEcoQoSRestore(root));
                test("legacy freeze journal: corrupt evidence is retained", () => TestCorruptJournal(root));
                test("legacy freeze journal: PID reuse identity is never resumed", () => TestPidReuseJournal(root));
                test("strict placement: hard-affinity fallback restores exactly", () => TestAffinityRestore(root));
                test("CPU Sets: pre-existing process policy restores exactly", () => TestExistingCpuSetRestore(root));
                test("staged suppression: queryable state and CPU Sets restore", () => TestStagedSuppression(root));
            test("competitive suppression: target resets are re-applied", () => TestSuppressionReapply(root));
            test("suppression journal: failed persistence blocks every kernel write", () => TestSuppressionJournalGate(root));
            test("suppression: a fully write-refused detail is recognized as self-protected", TestFullyBlockedDetailJudgement);
            test("suppression: an untouched process matches its own snapshot exactly", () => TestSnapshotMatchJudgement(root));
            test("self-protected roster: mark/contains round-trips case-insensitively", TestSelfProtectedRoster);
            test("staged suppression: crash journal restores a live process", () => TestSuppressionCrashRecovery(root));
            test("GPU demote: class mapping follows the background tier only", TestGpuDemoteMapping);
            test("GPU demote: journal parses the gpu field and accepts legacy lines", TestGpuJournalField);
            test("GPU demote: scheduling class write and restore verified on self", TestGpuPriorityRoundtrip);
            test("GPU demote: a GPU-less process still suppresses and restores cleanly", () => TestGpuDemoteGpulessProcess(root));
            test("freeze: dwell gate needs uninterrupted quiet before it opens", TestFreezeDwellGate);
            test("freeze: an anti-cheat reason can never reach the frozen tier", TestAntiCheatNeverFreezes);
            test("freeze: crash journal wakes a process left suspended", TestFrozenJournalThaw);
            test("freeze: crash recovery never resumes a reused pid", TestFrozenJournalRejectsPidReuse);
            test("freeze: one resume wakes a singly-suspended process", TestSuspendIsNotReentrant);
            test("nv drs: key-to-settingid mapping is exact and collision-free", TestDrsKeyIdMapping);
            test("nv drs: snapshot codec round-trips all four keys", TestDrsSnapshotRoundtrip);
            test("nagle: interface list codec handles empty and multi entries", TestNagleListCodec);
            test("ifeo: sandbox roundtrip registers priority and leaves zero residue", TestIfeoSandboxRoundtrip);
            test("render lane: the busy thread is the one identified", TestRenderLaneIdentifiesBusyThread);
            test("render lane: journal codec rejects malformed lines", TestRenderLaneJournalCodec);
            test("sweep: a game's detached descendant is never suppressed", TestGameDescendantsExemption);
            test("boost: a process already in efficiency mode is brought out of it", TestBoostClearsEfficiencyMode);
            test("net throttle: only out-of-range values are flagged for repair", TestNetThrottleRangeJudgement);
            test("device power: only the no-power-down bit is touched", TestDevicePowerBitMerge);
            test("msi mode: scan yields PCI display/net devices only", TestMsiScanClassFilter);
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            log.Insert(0, "Pavise " + App.Version + " self-test @ " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            log.Add("");
            log.Add("TOTAL " + (passed + failed + skipped) + "  PASS " + passed
                + "  FAIL " + failed + "  SKIP " + skipped);
            try
            {
                string dir = Path.GetDirectoryName(reportPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(reportPath, log.ToArray(), Encoding.UTF8);
            }
            catch { }
            Environment.ExitCode = failed == 0 ? 0 : 1;
        }

        private static void TestReleaseMetadata()
        {
            var declared = new Version(App.Version);
            string expected = new Version(
                declared.Major,
                declared.Minor,
                declared.Build < 0 ? 0 : declared.Build,
                declared.Revision < 0 ? 0 : declared.Revision).ToString();
            Version assemblyVersion = typeof(App).Assembly.GetName().Version;
            Eq(expected, assemblyVersion == null ? "" : assemblyVersion.ToString());
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(Application.ExecutablePath);
            Eq(expected, info.FileVersion);
            Eq("Pavise", info.ProductName);
            Eq("bdth", info.CompanyName);
        }

        private static void TestRenderScoring()
        {
            Lang.Init();
            var profile = GameProfileStore.NewProfile("Example", Path.Combine(Path.GetTempPath(), "ExampleGame"));
            profile.Entries.Add("ExampleGame");
            string game = Path.Combine(profile.Root, "Binaries", "Win64", "ExampleGame-Win64-Shipping.exe");
            Eq(true, profile.ContainsPath(game));
            Eq(false, profile.ContainsPath(Path.Combine(profile.Root + "-backup", "game.exe")));

            Eq(true, GameSessionDetector.ElectionVetoed("POWERPNT", @"C:\Program Files\Microsoft Office\POWERPNT.EXE"));
            Eq(true, GameSessionDetector.ElectionVetoed("ACE-Helper", Path.Combine(profile.Root, "ACE-Helper.exe")));
            Eq(true, GameSessionDetector.ElectionVetoed("LeagueClientUxRender", Path.Combine(profile.Root, "LeagueClient", "LeagueClientUxRender.exe")));
            Eq(true, GameSessionDetector.ElectionVetoed("cs2CrashHandler64", Path.Combine(profile.Root, "cs2CrashHandler64.exe")));
            Eq(true, GameSessionDetector.ElectionVetoed("chrome", @"D:\Apps\chrome.exe"));
            Eq(true, GameSessionDetector.ElectionVetoed("steam", @"C:\Program Files (x86)\Steam\steam.exe"));
            Eq(false, GameSessionDetector.ElectionVetoed("ExampleGame", game));
            Eq(false, GameSessionDetector.ElectionVetoed("League of Legends", @"C:\g\lol\Game\League of Legends.exe"));

            long created = DateTime.UtcNow.ToFileTimeUtc() - 60L * 10000000L;
            var snapshot = new[]
            {
                new GameProcessSnapshot
                {
                    Pid = 7001, ParentPid = 1, Creation = created,
                    Name = "ExampleGame", Path = game,
                    Visible = true, Foreground = true, FullscreenLike = true
                },
                new GameProcessSnapshot
                {
                    Pid = 7002, ParentPid = 7001, Creation = created + 1000,
                    Name = "ExampleHelperWorker",
                    Path = Path.Combine(profile.Root, "Binaries", "Win64", "ExampleHelperWorker.exe")
                }
            };
            bool armed;
            GameDetection hit = GameSessionDetector.DetectSnapshot(snapshot, new[] { profile }, out armed);
            if (hit == null) throw new Exception("fullscreen root process was not elected");
            Eq(7001, hit.RendererPid);
            Eq(true, hit.FamilyPids.Contains(7002));
        }

        private static void TestGameSessionInstanceIsolation()
        {
            const long now = 140000000000000000L;
            string root = @"C:\Games\League";
            string launcher = Path.Combine(
                root, "LeagueClient.exe");
            string renderer = Path.Combine(
                root, "Game", "League of Legends.exe");
            var profile = GameProfileStore.NewProfile(
                "League", root, launcher);
            profile.Entries.Clear();
            profile.Entries.Add("LeagueClient");
            Eq("LeagueClient",
                GameSessionDetector.ImageNameFromVerifiedPath(
                    launcher));
            Eq<string>(null,
                GameSessionDetector.ImageNameFromVerifiedPath(
                    root + "\\"));
            Eq<string>(null,
                GameSessionDetector.ImageNameFromVerifiedPath(null));

            var parallel = new[]
            {
                new GameProcessSnapshot
                {
                    Pid = 100, ParentPid = 10,
                    Creation = now - 20000000,
                    Name = "LeagueClient", Path = launcher
                },
                new GameProcessSnapshot
                {
                    Pid = 101, ParentPid = 100,
                    Creation = now - 19000000,
                    Name = "League of Legends", Path = renderer,
                    Visible = true
                },
                new GameProcessSnapshot
                {
                    Pid = 200, ParentPid = 10,
                    Creation = now - 10000000,
                    Name = "LeagueClient", Path = launcher
                },
                new GameProcessSnapshot
                {
                    Pid = 201, ParentPid = 200,
                    Creation = now - 9000000,
                    Name = "League of Legends", Path = renderer,
                    Visible = true, Foreground = true,
                    FullscreenLike = true
                }
            };
            bool armed;
            GameDetection hit = GameSessionDetector.DetectSnapshot(
                parallel, new[] { profile }, out armed);
            if (hit == null)
                throw new Exception("parallel instance was not detected");
            Eq(201, hit.RendererPid);
            Eq(true, hit.FamilyPids.Contains(200));
            Eq(true, hit.FamilyPids.Contains(201));
            Eq(true, hit.FamilyPids.Contains(100));
            Eq(true, hit.FamilyPids.Contains(101));
            Eq(true, GameMode.RendererIdentityMatches(
                201, parallel[3].Creation,
                parallel[3].Name, 201,
                parallel[3].Creation,
                parallel[3].Name.ToUpperInvariant()));
            Eq(false, GameMode.RendererIdentityMatches(
                201, parallel[3].Creation,
                parallel[3].Name, 201,
                parallel[3].Creation + 1,
                parallel[3].Name));
            Eq(false, GameMode.RendererIdentityMatches(
                201, parallel[3].Creation,
                parallel[3].Name, 202,
                parallel[3].Creation,
                parallel[3].Name));
            Eq(false, GameMode.RendererIdentityMatches(
                201, parallel[3].Creation,
                parallel[3].Name, 201,
                parallel[3].Creation,
                "reused-process"));

            var otherInstance = new GameDetection
            {
                Profile = profile.Clone(),
                RendererPid = 101,
                RendererCreation = parallel[1].Creation,
                RendererName = parallel[1].Name,
                RendererPath = parallel[1].Path,
                RendererForeground = true,
                RendererCandidateSelected = true,
                Evidence = "other"
            };
            otherInstance.FamilyPids.Add(100);
            otherInstance.FamilyPids.Add(101);
            otherInstance.FamilyNames.Add("other-family");
            Eq(true, GameMode.FreshRendererMayReplaceSticky(
                otherInstance, otherInstance.RendererName,
                otherInstance.RendererCreation,
                hit.RendererCreation));
            Eq(true, otherInstance.FamilyPids.Contains(100));
            Eq(true, otherInstance.FamilyPids.Contains(101));
            Eq(false, otherInstance.FamilyPids.Contains(200));
            Eq(false, otherInstance.FamilyPids.Contains(201));

            otherInstance.RendererForeground = false;
            Eq(false, GameMode.FreshRendererMayReplaceSticky(
                otherInstance, otherInstance.RendererName,
                otherInstance.RendererCreation,
                hit.RendererCreation));
            Eq(true, GameMode.ReanchorToStickyInstance(
                otherInstance, hit, new[] { 200, 201 }));
            Eq(201, otherInstance.RendererPid);
            Eq(false, otherInstance.FamilyPids.Contains(100));
            Eq(false, otherInstance.FamilyPids.Contains(101));
            Eq(true, otherInstance.FamilyPids.Contains(200));
            Eq(true, otherInstance.FamilyPids.Contains(201));
            Eq(false,
                otherInstance.FamilyNames.Contains(
                    "other-family"));

            var unverifiable = new GameDetection
            {
                RendererPid = 101
            };
            unverifiable.FamilyPids.Add(100);
            Eq(false, GameMode.ReanchorToStickyInstance(
                unverifiable, hit, new[] { 200 }));
            Eq(true, unverifiable.FamilyPids.Contains(100));

            var outsideChildren = new[]
            {
                new GameProcessSnapshot
                {
                    Pid = 300, ParentPid = 10,
                    Creation = now - 1000000,
                    Name = "League of Legends", Path = renderer,
                    Visible = true, Foreground = true,
                    FullscreenLike = true
                },
                new GameProcessSnapshot
                {
                    Pid = 301, ParentPid = 300,
                    Creation = now - 2000000,
                    Name = "LeagueWorkerStale",
                    Path = @"C:\Other\LeagueWorkerStale.exe"
                },
                new GameProcessSnapshot
                {
                    Pid = 302, ParentPid = 300,
                    Creation = now - 500000,
                    Name = "LeagueWorker",
                    Path = @"C:\Other\LeagueWorker.exe"
                }
            };
            hit = GameSessionDetector.DetectSnapshot(
                outsideChildren, new[] { profile }, out armed);
            if (hit == null)
                throw new Exception("fullscreen renderer was not elected");
            Eq(300, hit.RendererPid);
            Eq(false, hit.FamilyPids.Contains(301));
            Eq(true, hit.FamilyPids.Contains(302));

            var detachedOnly = new[]
            {
                new GameProcessSnapshot
                {
                    Pid = 401, ParentPid = 999,
                    Creation = now - 10 * TimeSpan.TicksPerSecond,
                    Name = "League of Legends", Path = renderer,
                    Visible = true, Foreground = true,
                    FullscreenLike = true
                }
            };
            hit = GameSessionDetector.DetectSnapshot(
                detachedOnly, new[] { profile }, out armed);
            if (hit == null)
                throw new Exception("detached renderer was not elected");
            Eq(401, hit.RendererPid);
            Eq(detachedOnly[0].Creation, hit.RendererCreation);

            var legacy = GameProfileStore.NewProfile(
                "Legacy", root);
            legacy.ExecutablePath = null;
            legacy.Entries.Clear();
            legacy.Entries.Add("LegacyGame");
            var outOfRoot = new[]
            {
                new GameProcessSnapshot
                {
                    Pid = 600, ParentPid = 10,
                    Creation = now
                        - TimeSpan.TicksPerSecond,
                    Name = "LegacyGame",
                    Path = @"C:\Other\Game\LegacyGame.exe",
                    Visible = true, Foreground = true,
                    FullscreenLike = true
                }
            };
            Eq<GameDetection>(null,
                GameSessionDetector.DetectSnapshot(
                    outOfRoot, new[] { legacy }));
            outOfRoot[0].Path = Path.Combine(
                root, "Game", "LegacyGame.exe");
            if (GameSessionDetector.DetectSnapshot(
                    outOfRoot, new[] { legacy }) == null)
                throw new Exception(
                    "in-root legacy entry was not detected");
        }

        private static void TestPressureController()
        {
            var c = new BackgroundPressureController();
            long second = TimeSpan.TicksPerSecond;
            long cpu = 0;
            Eq(SuppressionLevel.None, c.Observe(8, "worker", 1, cpu, 0, 100 * second, PerformancePreset.Standard));
            cpu += (long)(second * 4 * 0.10);
            Eq(SuppressionLevel.Eco, c.Observe(8, "worker", 1, cpu, 0, 104 * second, PerformancePreset.Standard));
            cpu += (long)(second * 4 * 0.10);
            Eq(SuppressionLevel.Restrained, c.Observe(8, "worker", 1, cpu, 0, 108 * second, PerformancePreset.Standard));
            cpu += (long)(second * 4 * 0.10);
            Eq(SuppressionLevel.Isolated, c.Observe(8, "worker", 1, cpu, 0, 112 * second, PerformancePreset.Standard));
            Eq(SuppressionLevel.Isolated, c.Observe(8, "worker", 1, cpu, 0, 116 * second, PerformancePreset.Standard));
            Eq(SuppressionLevel.Restrained, c.Observe(8, "worker", 1, cpu, 0, 120 * second, PerformancePreset.Standard));
            Eq(SuppressionLevel.None, c.Observe(8, "worker", 2, 99 * second, 0, 124 * second, PerformancePreset.Standard));

            var fast = new BackgroundPressureController();
            long t = 200 * second, used = 0;
            Eq(SuppressionLevel.None, fast.Observe(9, "burst", 1, used, 0, t, PerformancePreset.Standard));
            for (int i = 0; i < 4; i++)
            {
                t += second / 5;
                used += (long)(second / 5 * 0.10);
                Eq(SuppressionLevel.None, fast.Observe(9, "burst", 1, used, 0, t, PerformancePreset.Standard));
            }

            t += second / 5;
            used += (long)(second / 5 * 0.10);
            Eq(SuppressionLevel.Eco, fast.Observe(9, "burst", 1, used, 0, t, PerformancePreset.Standard));

            var keep = new BackgroundPressureController();
            long k = 400 * second, kused = 0;
            Eq(SuppressionLevel.None, keep.Observe(12, "hot", 1, kused, 0, k, PerformancePreset.Standard));
            for (int i = 0; i < 3; i++)
            {
                k += 4 * second;
                kused += (long)(4 * second * 0.10);
                keep.Observe(12, "hot", 1, kused, 0, k, PerformancePreset.Standard);
            }
            Eq(SuppressionLevel.Isolated, keep.Observe(12, "hot", 1, kused, 0, k, PerformancePreset.Standard));
            Eq(SuppressionLevel.Isolated, keep.Observe(12, "hot", 1, kused, 0, k + second / 5, PerformancePreset.Standard));

            var stale = new BackgroundPressureController();
            Eq(SuppressionLevel.None, stale.Observe(11, "idle", 1, 0, 0, 300 * second, PerformancePreset.Standard));
            Eq(SuppressionLevel.None, stale.Observe(11, "idle", 1, (long)(60 * second * 0.5), 0, 360 * second, PerformancePreset.Standard));
        }

        private static void TestPresetBackgroundPolicy()
        {
            Eq(SuppressionLevel.Eco, GameMode.ResolveBackgroundLevel(PerformancePreset.Standard, false, SuppressionLevel.None, true));
            Eq(SuppressionLevel.Restrained, GameMode.ResolveBackgroundLevel(PerformancePreset.Standard, false, SuppressionLevel.Restrained, true));
            Eq(SuppressionLevel.Isolated, GameMode.ResolveBackgroundLevel(PerformancePreset.Competitive, false, SuppressionLevel.None, true));
            Eq(SuppressionLevel.Isolated, GameMode.ResolveBackgroundLevel(PerformancePreset.Competitive, false, SuppressionLevel.None, false));
            Eq(SuppressionLevel.Eco, GameMode.ResolveBackgroundLevel(PerformancePreset.Custom, false, SuppressionLevel.Isolated, true));
            Eq(SuppressionLevel.Isolated, GameMode.ResolveBackgroundLevel(PerformancePreset.Custom, true, SuppressionLevel.None, true));
            Eq(SuppressionLevel.Isolated, GameMode.ResolveBackgroundLevel(PerformancePreset.Custom, true, SuppressionLevel.None, false));

            Eq(true, GameMode.IsAggressive(PerformancePreset.Competitive, false));
            Eq(true, GameMode.IsAggressive(PerformancePreset.Competitive, true));
            Eq(false, GameMode.IsAggressive(PerformancePreset.Standard, true));
            Eq(false, GameMode.IsAggressive(PerformancePreset.Custom, false));
            Eq(true, GameMode.IsAggressive(PerformancePreset.Custom, true));
        }

        private static void TestModeIcons()
        {
            long standard, competitive, custom;
            using (Bitmap s = IconArt.Render(32, PerformancePreset.Standard, true)) standard = IconFingerprint(s, true);
            using (Bitmap c = IconArt.Render(32, PerformancePreset.Competitive, true)) competitive = IconFingerprint(c, false);
            using (Bitmap x = IconArt.Render(32, PerformancePreset.Custom, true)) custom = IconFingerprint(x, false);
            if (standard == competitive || competitive == custom || standard == custom) throw new Exception("mode icons are not visually distinct");
        }

        private static void TestEnterSlideKeepsScrollbarsStable()
        {
            using (var scroll = new Panel())
            {
                scroll.AutoScroll = true;
                scroll.SetBounds(0, 0, 300, 400);
                scroll.CreateControl();
                int rowWidth = scroll.ClientSize.Width - 6;
                for (int i = 0; i < 3; i++)
                {
                    var row = new Panel();
                    row.SetBounds(6, i * 40, rowWidth, 32);
                    scroll.Controls.Add(row);
                }
                scroll.PerformLayout();
                if (scroll.HorizontalScroll.Visible)
                    throw new Exception("precondition failed: rows already overflow horizontally");

                foreach (Control row in scroll.Controls) row.Left = 6 - 22;
                scroll.PerformLayout();
                if (scroll.HorizontalScroll.Visible)
                    throw new Exception("负向入场偏移不应撑出横向滚动条");

                foreach (Control row in scroll.Controls) row.Left = 6 + 22;
                scroll.PerformLayout();
                if (!scroll.HorizontalScroll.Visible)
                    throw new Exception("precondition failed: 正向偏移本应撑出横向滚动条");

                foreach (Control row in scroll.Controls) row.Left = 6;
                scroll.PerformLayout();
            }
        }

        private static void TestScrolledRebuild()
        {
            using (var scroll = new Panel())
            {
                scroll.AutoScroll = true;
                scroll.SetBounds(0, 0, 300, 160);
                scroll.CreateControl();
                for (int i = 0; i < 24; i++)
                {
                    var filler = new Label();
                    filler.SetBounds(0, i * 40, 200, 32);
                    scroll.Controls.Add(filler);
                }
                scroll.PerformLayout();
                scroll.AutoScrollPosition = new Point(0, 500);
                if (scroll.AutoScrollPosition.Y == 0)
                    throw new Exception("panel did not scroll, precondition not met");

                scroll.AutoScrollPosition = Point.Empty;
                var stale = new Control[scroll.Controls.Count];
                scroll.Controls.CopyTo(stale, 0);
                scroll.Controls.Clear();
                int disposed = 0;
                foreach (Control c in stale) { c.Dispose(); disposed++; }
                if (disposed != stale.Length) throw new Exception("not every stale control was released");
                if (scroll.Controls.Count != 0) throw new Exception("controls survived the clear");

                var first = new Label();
                first.SetBounds(0, 2, 200, 32);
                scroll.Controls.Add(first);
                if (first.Top != 2)
                    throw new Exception("rebuilt content starts at " + first.Top + " instead of 2");
            }
        }

        private static void TestDashboardMotion()
        {
            Theme.SetMode(PerformancePreset.Competitive, false);
            try
            {
                using (var core = new PaviseCore())
                using (var first = new Bitmap(360, 342))
                using (var second = new Bitmap(360, 342))
                {
                    core.SetBounds(0, 0, 360, 342);
                    core.SetState(PerformancePreset.Competitive, true, true);
                    core.CreateControl();
                    core.SetAnimationEnabled(false);
                    core.DrawToBitmap(first, new Rectangle(0, 0, first.Width, first.Height));
                    Thread.Sleep(175);
                    core.DrawToBitmap(second, new Rectangle(0, 0, second.Width, second.Height));

                    int changed = 0;
                    for (int y = 0; y < first.Height; y += 2)
                        for (int x = 0; x < first.Width; x += 2)
                            if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) changed++;
                    if (changed < 180) throw new Exception("only " + changed + " sampled pixels changed");
                }
            }
            finally { Theme.SetMode(PerformancePreset.Standard, false); }
        }

        private static long IconFingerprint(Bitmap bitmap, bool verifyBounds)
        {
            int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1;
            long hash = 17;
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                {
                    Color p = bitmap.GetPixel(x, y);
                    hash = unchecked(hash * 31 + p.ToArgb());
                    if (p.A > 12) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
                }
            if (verifyBounds && (maxX - minX + 1 < bitmap.Width - 1 || maxY - minY + 1 < bitmap.Height - 1))
                throw new Exception("tray artwork still has excessive transparent padding");
            return hash;
        }

        private static void TestCpuSetPartition()
        {
            if (!CpuTopology.HasSafeBackgroundPartition()) Skip("no safe background CPU Set partition");
            uint[] background = CpuTopology.BackgroundCpuSetIds();
            uint[] game = CpuTopology.AdaptiveGameCpuSetIds(true);
            if (background == null || game == null) throw new Exception("partition was reported safe without CPU Set IDs");
            var occupied = new HashSet<uint>(background);
            foreach (uint id in game) if (occupied.Contains(id)) throw new Exception("game/background CPU Set overlap: " + id);
            if (CpuTopology.Hybrid)
            {
                uint[] expectedPerformance = CpuTopology.CpuSetIdsFor(CpuTopology.PerfMask);
                if (expectedPerformance != null)
                    Eq(expectedPerformance.Length, game.Length);
            }
        }

        private static void TestBackgroundBoundary()
        {
            const string win = @"C:\Windows\";
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "worker", @"D:\Apps\worker.exe", 1, 1, 20, false, win));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "worker", @"D:\Apps\worker.exe", 1, 1, 10, false, win));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "worker", @"D:\Apps\worker.exe", 1, 1, 20, true, win));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "worker", @"C:\Windows\worker.exe", 1, 1, 20, false, win));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "worker", @"D:\Apps\worker.exe", 0, 1, 20, false, win));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "discord", @"D:\Apps\discord.exe", 1, 1, 20, true, win));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "EasyAntiCheat_EOS", @"D:\Games\eac.exe", 1, 1, 20, false, win));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "SGuard64", @"D:\WeGame\SGuard64.exe", 1, 1, 20, false, win));

            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "wegame", @"C:\WeGame\wegame.exe", 1, 1, 20, false, win));

            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "wegame", @"C:\WeGame\wegame.exe", 1, 1, 20, false, win, true, null));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "anylauncher", @"D:\Anything\launcher.exe", 1, 1, 20, false, win, true, null));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "railhelper", @"D:\SomeGame\TCLS\rail.exe", 1, 1, 20, false, win, false, @"D:\SomeGame\"));

            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "worker", @"D:\Apps\worker.exe", 1, 1, 10, false, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "discord", @"D:\Apps\discord.exe", 1, 1, 20, true, win, false, null, true));
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "discord", @"D:\Apps\discord.exe", 1, 1, 20, false, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "dwm", @"C:\Windows\System32\dwm.exe", 1, 1, 20, false, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "lsass", @"C:\Windows\System32\lsass.exe", 1, 1, 20, false, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "audiodg", @"C:\Windows\System32\audiodg.exe", 1, 1, 20, false, win, false, null, true));
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "explorer", @"C:\Windows\explorer.exe", 1, 1, 20, false, win, false, null, true));
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "SearchIndexer", @"C:\Windows\System32\SearchIndexer.exe", 1, 1, 20, false, win, false, null, true));
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "svchost", @"D:\Malware\svchost.exe", 1, 1, 20, false, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "SGuard64", @"D:\WeGame\SGuard64.exe", 1, 1, 20, false, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "wegame", @"C:\WeGame\wegame.exe", 1, 1, 20, false, win, true, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "railhelper", @"D:\SomeGame\TCLS\rail.exe", 1, 1, 20, false, win, false, @"D:\SomeGame\", true));

            var hostParents = new Dictionary<int, int> { { 100, 50 }, { 50, 20 }, { 20, 7 }, { 7, 3 } };
            HashSet<int> ancestors = GameMode.WalkAncestorChain(hostParents, 100, 99, 24);
            Eq(true, ancestors.Contains(50));
            Eq(true, ancestors.Contains(20));
            Eq(true, ancestors.Contains(7));
            Eq(false, ancestors.Contains(100));
            Eq(false, ancestors.Contains(3));
            Eq(0, GameMode.WalkAncestorChain(hostParents, 3, 99, 24).Count);
            Eq(0, GameMode.WalkAncestorChain(new Dictionary<int, int>(), 100, 99, 24).Count);
            Eq(0, GameMode.WalkAncestorChain(hostParents, 4, 99, 24).Count);
            var selfLoop = new Dictionary<int, int> { { 100, 50 }, { 50, 99 } };
            Eq(false, GameMode.WalkAncestorChain(selfLoop, 100, 99, 24).Contains(99));
            var cycle = new Dictionary<int, int> { { 100, 50 }, { 50, 20 }, { 20, 100 } };
            HashSet<int> cycleResult = GameMode.WalkAncestorChain(cycle, 100, 99, 24);
            Eq(true, cycleResult.Contains(50));
            Eq(true, cycleResult.Contains(20));
            var longChain = new Dictionary<int, int>();
            for (int i = 1001; i <= 1039; i++) longChain[i] = i - 1;
            Eq(24, GameMode.WalkAncestorChain(longChain, 1039, 99, 24).Count);

            Eq(true, GameMode.IsKnownLauncherShell("wegame"));
            Eq(true, GameMode.IsKnownLauncherShell("Steam"));
            Eq(true, GameMode.IsKnownLauncherShell("EpicGamesLauncher"));
            Eq(true, GameMode.IsKnownLauncherShell("Battle.net"));
            Eq(false, GameMode.IsKnownLauncherShell("chrome"));
            Eq(false, GameMode.IsKnownLauncherShell("League of Legends"));
            Eq(false, GameMode.IsKnownLauncherShell(null));
            Eq(false, GameMode.IsKnownLauncherShell(""));

            var parents = new Dictionary<int, int> { { 2, 1 }, { 10, 1 }, { 11, 10 }, { 12, 11 }, { 20, 1 } };
            var names = new Dictionary<int, string>
            {
                { 1, "explorer" }, { 2, "worker" }, { 10, "chrome" },
                { 11, "chrome" }, { 12, "gpu-helper" }, { 20, "unrelated" }
            };
            HashSet<int> family = GameMode.ExpandUserFacingFamily(parents, names, new HashSet<int> { 1, 10 });
            Eq(true, family.Contains(10));
            Eq(true, family.Contains(11));
            Eq(true, family.Contains(12));
            Eq(false, family.Contains(2));
            Eq(false, family.Contains(20));
        }

        private static void TestGameProtectionRedesign()
        {
            Eq(true, GameSessionDetector.IsLauncherLikeName("LeagueClient"));
            Eq(true, GameSessionDetector.IsLauncherLikeName("RiotClientServices"));
            Eq(true, GameSessionDetector.IsLauncherLikeName("EpicGamesLauncher"));
            Eq(false, GameSessionDetector.IsLauncherLikeName("League of Legends"));
            Eq(false, GameSessionDetector.IsLauncherLikeName(null));
            Eq(false, GameSessionDetector.IsLauncherLikeName(""));
            Eq(true, GameSessionDetector.IsLauncherLikeName("wegame"));
            Eq(true, GameSessionDetector.IsLauncherLikeName("Steam"));
            Eq(true, GameSessionDetector.IsLauncherLikeName("Battle.net"));

            Eq(true, GameSessionDetector.IsAntiCheatLikeName("SGuard64"));
            Eq(true, GameSessionDetector.IsAntiCheatLikeName("BattlEye"));
            Eq(true, GameSessionDetector.IsAntiCheatLikeName("GameAntiCheat"));
            Eq(false, GameSessionDetector.IsAntiCheatLikeName("League of Legends"));
            Eq(false, GameSessionDetector.IsAntiCheatLikeName(null));

            Eq(true, GameSessionDetector.ElectionVetoed("chrome", @"D:\Apps\chrome.exe"));
            Eq(true, GameSessionDetector.ElectionVetoed("SGuard64", @"D:\Games\SGuard64.exe"));
            Eq(true, GameSessionDetector.ElectionVetoed("BattlEye", @"D:\Games\BattlEye.exe"));
            Eq(false, GameSessionDetector.ElectionVetoed("Bannerlord", @"D:\Games\Bannerlord.exe"));
            Eq(false, GameSessionDetector.ElectionVetoed(
                "TaleWorlds.MountAndBlade.Launcher", @"D:\Games\TaleWorlds.MountAndBlade.Launcher.exe"));

            var steamRoots = new List<string> { @"C:\Program Files (x86)\Steam" };
            Eq(true, SteamCatalog.IsSteamFamilyWithRoots(
                "steam", @"C:\Program Files (x86)\Steam\steam.exe", steamRoots));
            Eq(true, SteamCatalog.IsSteamFamilyWithRoots(
                "gameoverlayui", @"C:\Program Files (x86)\Steam\gameoverlayui.exe", steamRoots));
            Eq(true, SteamCatalog.IsSteamFamilyWithRoots(
                "steamwebhelper", @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe", steamRoots));
            Eq(false, SteamCatalog.IsSteamFamilyWithRoots(
                "steam", @"D:\Malware\steam.exe", steamRoots));
            Eq(false, SteamCatalog.IsSteamFamilyWithRoots(
                "cs2", @"C:\Program Files (x86)\Steam\steamapps\common\cs2\cs2.exe", steamRoots));
            Eq(false, SteamCatalog.IsSteamFamilyWithRoots("steam", @"C:\Program Files (x86)\Steam\steam.exe", null));
        }

        private static void TestProfileStore(string root)
        {
            string dir = Path.Combine(root, "profiles");
            Directory.CreateDirectory(dir);
            string legacy = Path.Combine(dir, "Pavise.games.txt");
            string gameRoot = Path.Combine(dir, "GenericGame");
            File.WriteAllLines(legacy, new[] { GameMode.EncodeGameLine("GenericGame", gameRoot), GameMode.EncodeGameLine("GenericHelper", gameRoot) }, Encoding.UTF8);
            var store = new GameProfileStore(dir);
            List<GameProfile> first = store.LoadOrMigrate(legacy);
            Eq(0, first.Count);
            Eq(true, store.ClearedLegacyLibrary);
            Eq(true, File.Exists(legacy + ".pre-election.bak"));

            var fresh = GameProfileStore.NewProfile("GenericGame", gameRoot,
                Path.Combine(gameRoot, "GenericGame.exe"));
            var duplicate = fresh.Clone();
            duplicate.Entries.Add("DuplicateHelper");
            store.Save(new[] { fresh, duplicate });
            List<GameProfile> second = new GameProfileStore(dir).LoadOrMigrate(legacy);
            Eq(1, second.Count);
            Eq(2, second[0].Entries.Count);
        }

        private static void TestLearnedRendererStore(string root)
        {
            string dir = Path.Combine(root, "profilesV3");
            Directory.CreateDirectory(dir);
            string legacy = Path.Combine(dir, "Pavise.games.txt");
            string gameRoot = Path.Combine(dir, "英雄联盟");

            var store = new GameProfileStore(dir);
            GameProfile p = GameProfileStore.NewProfile("英雄联盟", gameRoot,
                Path.Combine(gameRoot, "Riot Client\\RiotClientServices.exe"));
            p.LearnedExecutablePath = Path.Combine(gameRoot, "Game\\League of Legends.exe");
            store.Save(new[] { p });

            string[] raw = File.ReadAllLines(Path.Combine(dir, GameProfileStore.FileName), Encoding.UTF8);
            Eq("PAVISE_PROFILES_V4", raw[0]);
            int pLines = 0, lLines = 0;
            for (int i = 1; i < raw.Length; i++)
            {
                if (raw[i].StartsWith("P|")) { Eq(6, raw[i].Split('|').Length); pLines++; }
                else if (raw[i].StartsWith("L|")) { Eq(3, raw[i].Split('|').Length); lLines++; }
                else throw new Exception("unexpected profile line: " + raw[i]);
            }
            Eq(1, pLines);
            Eq(1, lLines);

            var reload = new GameProfileStore(dir);
            List<GameProfile> loaded = reload.LoadOrMigrate(legacy);
            Eq(1, loaded.Count);
            Eq(Path.Combine(gameRoot, "Game\\League of Legends.exe"), loaded[0].LearnedExecutablePath);

            Directory.CreateDirectory(gameRoot);
            List<GameProfile> pruned = new GameProfileStore(dir).LoadOrMigrate(legacy);
            Eq(1, pruned.Count);
            Eq(null, pruned[0].LearnedExecutablePath);

            Directory.CreateDirectory(Path.Combine(gameRoot, "Game"));
            File.WriteAllBytes(Path.Combine(gameRoot, "Game\\League of Legends.exe"), new byte[16]);
            pruned[0].LearnedExecutablePath = Path.Combine(gameRoot, "Game\\League of Legends.exe");
            new GameProfileStore(dir).Save(pruned);
            List<GameProfile> kept = new GameProfileStore(dir).LoadOrMigrate(legacy);
            Eq(1, kept.Count);
            Eq(Path.Combine(gameRoot, "Game\\League of Legends.exe"), kept[0].LearnedExecutablePath);

            kept[0].LearnedExecutablePath = kept[0].ExecutablePath;
            new GameProfileStore(dir).Save(kept);
            List<GameProfile> again = new GameProfileStore(dir).LoadOrMigrate(legacy);
            Eq(1, again.Count);
            Eq(null, again[0].LearnedExecutablePath);
        }

        private static void TestFutureProfileFormatProtected(string root)
        {
            string dir = Path.Combine(root, "profilesFuture");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, GameProfileStore.FileName);
            File.WriteAllLines(file, new[] { "PAVISE_PROFILES_V9", "X|future|payload" }, Encoding.UTF8);

            var store = new GameProfileStore(dir);
            List<GameProfile> loaded = store.LoadOrMigrate(Path.Combine(dir, "Pavise.games.txt"));
            Eq(0, loaded.Count);

            store.Save(new[] { GameProfileStore.NewProfile("Nope", null) });
            string[] raw = File.ReadAllLines(file, Encoding.UTF8);
            Eq(2, raw.Length);
            Eq("PAVISE_PROFILES_V9", raw[0]);
            Eq("X|future|payload", raw[1]);
        }

        private static void TestV3ProfileMigration(string root)
        {
            string dir = Path.Combine(root, "profilesV3migrate");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, GameProfileStore.FileName);
            string gameRoot = Path.Combine(dir, "GameX");
            Func<string, string> b64 = delegate(string s)
            {
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
            };
            File.WriteAllLines(file, new[]
            {
                "PAVISE_PROFILES_V3",
                "P|" + b64("id123") + "|" + b64("GameX") + "|" + b64(gameRoot)
                    + "|" + b64(Path.Combine(gameRoot, "GameX.exe"))
                    + "|" + b64("GameX") + "|" + b64(Path.Combine(gameRoot, "Real.exe"))
            }, Encoding.UTF8);

            var store = new GameProfileStore(dir);
            List<GameProfile> loaded = store.LoadOrMigrate(Path.Combine(dir, "Pavise.games.txt"));
            Eq(0, loaded.Count);
            Eq(true, store.ClearedLegacyLibrary);
            Eq(true, File.Exists(file + ".pre-election.bak"));

            string[] raw = File.ReadAllLines(file, Encoding.UTF8);
            Eq("PAVISE_PROFILES_V4", raw[0]);
            Eq(1, raw.Length);
        }

        private static void TestUninstallScanFiltersAccelerators(string root)
        {
            string gameDir = Path.Combine(root, "uninstall\\SomeNetEaseGame");
            string accDir = Path.Combine(root, "uninstall\\UUBooster");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(accDir);
            File.WriteAllBytes(Path.Combine(gameDir, "SomeNetEaseGame.exe"), new byte[160 * 1024]);
            File.WriteAllBytes(Path.Combine(accDir, "uu.exe"), new byte[160 * 1024]);

            const string upKey = "Software\\PaviseSelfTest\\Uninstall";
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(upKey + "\\NetEaseGame"))
                {
                    k.SetValue("DisplayName", "永劫无间");
                    k.SetValue("Publisher", "网易(杭州)网络有限公司");
                    k.SetValue("InstallLocation", gameDir);
                }
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(upKey + "\\UU"))
                {
                    k.SetValue("DisplayName", "网易UU加速器");
                    k.SetValue("Publisher", "网易(杭州)网络有限公司");
                    k.SetValue("InstallLocation", accDir);
                }

                var hits = new List<ScanHit>();
                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                GameScan.ScanUninstallHive(Microsoft.Win32.Registry.CurrentUser, upKey, null, hits, roots, seen, null);
                Eq(1, hits.Count);
                Eq("永劫无间", hits[0].Name);
                Eq(Path.Combine(gameDir, "SomeNetEaseGame.exe"), hits[0].Exe);
            }
            finally
            {
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree("Software\\PaviseSelfTest", false); }
                catch { }
            }
        }

        private static void TestPickMainExeStructure(string root)
        {
            string sandbox = Path.Combine(root, "scanpick");
            var big = new byte[300 * 1024];

            string lol = Path.Combine(sandbox, "英雄联盟");
            Directory.CreateDirectory(Path.Combine(lol, "Game"));
            Directory.CreateDirectory(Path.Combine(lol, "LeagueClient"));
            Directory.CreateDirectory(Path.Combine(lol, "Riot Client"));
            Directory.CreateDirectory(Path.Combine(lol, "TCLS"));
            File.WriteAllBytes(Path.Combine(lol, "Game\\League of Legends.exe"), new byte[160 * 1024]);
            File.WriteAllBytes(Path.Combine(lol, "LeagueClient\\LeagueClient.exe"), big);
            File.WriteAllBytes(Path.Combine(lol, "Riot Client\\RiotClientServices.exe"), new byte[400 * 1024]);
            File.WriteAllBytes(Path.Combine(lol, "TCLS\\tcls_core.exe"), big);
            Eq(Path.Combine(lol, "Game\\League of Legends.exe"), GameScan.PickMainExe(lol));

            string unity = Path.Combine(sandbox, "SomeIndie");
            Directory.CreateDirectory(Path.Combine(unity, "SomeIndie_Data"));
            File.WriteAllBytes(Path.Combine(unity, "SomeIndie.exe"), new byte[160 * 1024]);
            File.WriteAllBytes(Path.Combine(unity, "UnityCrashHandler64.exe"), big);
            Eq(Path.Combine(unity, "SomeIndie.exe"), GameScan.PickMainExe(unity));

            string ue = Path.Combine(sandbox, "GenericUE");
            Directory.CreateDirectory(Path.Combine(ue, "Binaries\\Win64"));
            File.WriteAllBytes(Path.Combine(ue, "GenericUE.exe"), big);
            File.WriteAllBytes(Path.Combine(ue, "Binaries\\Win64\\Generic-Win64-Shipping.exe"), new byte[160 * 1024]);
            Eq(Path.Combine(ue, "Binaries\\Win64\\Generic-Win64-Shipping.exe"), GameScan.PickMainExe(ue));

            string named = Path.Combine(sandbox, "StardewValley");
            Directory.CreateDirectory(named);
            File.WriteAllBytes(Path.Combine(named, "StardewValley.exe"), new byte[160 * 1024]);
            File.WriteAllBytes(Path.Combine(named, "MapEditor.exe"), big);
            Eq(Path.Combine(named, "StardewValley.exe"), GameScan.PickMainExe(named));
        }

        private static void TestSteamLibraryScan(string root)
        {
            string steam = Path.Combine(root, "fakesteam\\Steam");
            string lib2 = Path.Combine(root, "fakesteam\\SteamLibrary");
            string sa1 = Path.Combine(steam, "steamapps");
            string sa2 = Path.Combine(lib2, "steamapps");
            Directory.CreateDirectory(sa1);
            Directory.CreateDirectory(sa2);

            File.WriteAllText(Path.Combine(sa1, "libraryfolders.vdf"),
                "\"libraryfolders\"\n{\n"
                + "\t\"0\"\n\t{\n\t\t\"path\"\t\t\"" + steam.Replace("\\", "\\\\") + "\"\n"
                + "\t\t\"label\"\t\t\"\"\n\t\t\"contentid\"\t\t\"7484950635125073964\"\n\t}\n"
                + "\t\"1\"\n\t{\n\t\t\"path\"\t\t\"" + lib2.Replace("\\", "\\\\") + "\"\n\t}\n}\n",
                Encoding.UTF8);

            Action<string, string, string, string> acf = delegate(string dir, string appid, string name, string installdir)
            {
                File.WriteAllText(Path.Combine(dir, "appmanifest_" + appid + ".acf"),
                    "\"AppState\"\n{\n"
                    + "\t\"appid\"\t\t\"" + appid + "\"\n"
                    + "\t\"universe\"\t\t\"1\"\n"
                    + "\t\"name\"\t\t\"" + name + "\"\n"
                    + "\t\"StateFlags\"\t\t\"4\"\n"
                    + "\t\"installdir\"\t\t\"" + installdir + "\"\n"
                    + "\t\"buildid\"\t\t\"14160737\"\n}\n", Encoding.UTF8);
            };

            acf(sa1, "367520", "Hollow Knight", "Hollow Knight");
            acf(sa1, "228980", "Steamworks Common Redistributables", "Steamworks Shared");
            string hk = Path.Combine(sa1, "common\\Hollow Knight");
            Directory.CreateDirectory(Path.Combine(hk, "hollow_knight_Data"));
            Directory.CreateDirectory(Path.Combine(sa1, "common\\Steamworks Shared"));
            File.WriteAllBytes(Path.Combine(hk, "hollow_knight.exe"), new byte[160 * 1024]);
            File.WriteAllBytes(Path.Combine(hk, "UnityCrashHandler64.exe"), new byte[300 * 1024]);

            acf(sa2, "261550", "Mount & Blade II: Bannerlord", "Mount & Blade II Bannerlord");
            string mb = Path.Combine(sa2, "common\\Mount & Blade II Bannerlord");
            Directory.CreateDirectory(Path.Combine(mb, "bin\\Win64_Shipping_Client"));
            Directory.CreateDirectory(Path.Combine(mb, "Modules\\Native"));
            File.WriteAllBytes(Path.Combine(mb, "bin\\Win64_Shipping_Client\\Bannerlord.exe"), new byte[160 * 1024]);
            File.WriteAllBytes(Path.Combine(mb, "bin\\Win64_Shipping_Client\\TaleWorlds.MountAndBlade.Launcher.exe"), new byte[300 * 1024]);

            var hits = new List<ScanHit>();
            var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            GameScan.FromSteamLibraries(steam, null, hits, seenRoots);

            Eq(2, hits.Count);
            ScanHit hollow = null, bannerlord = null;
            foreach (ScanHit h in hits)
            {
                if (h.Name == "Hollow Knight") hollow = h;
                if (h.Name == "Mount & Blade II: Bannerlord") bannerlord = h;
            }
            if (hollow == null || bannerlord == null)
                throw new Exception("steam hits missing: " + hits.Count);
            Eq(Path.Combine(hk, "hollow_knight.exe"), hollow.Exe);
            Eq(Path.Combine(mb, "bin\\Win64_Shipping_Client\\Bannerlord.exe"), bannerlord.Exe);
        }

        private static void TestStorePackageScan(string root)
        {
            string appsDir = Path.Combine(root, "WindowsApps");
            string pkgFull = "FakeStudio.MineTest_1.2.0.0_x64__abc123def456";
            string gamePkg = Path.Combine(appsDir, pkgFull);
            string appPkg = Path.Combine(appsDir, "Vendor.NetdiskService_1.0.0.0_x64__zzz999");
            Directory.CreateDirectory(gamePkg);
            Directory.CreateDirectory(appPkg);
            File.WriteAllText(Path.Combine(gamePkg, "xboxservices.config"),
                "{\r\n  \"TitleId\": \"1828326430\",\r\n  \"PrimaryServiceConfigId\": \"00000000-0000-0000-0000-00006ca0f6ac\"\r\n}", Encoding.UTF8);
            File.WriteAllText(Path.Combine(gamePkg, "AppxManifest.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\">\r\n"
                + "  <Applications>\r\n    <Application Id=\"App\" Executable=\"MineTest.exe\" EntryPoint=\"GameActivate\" />\r\n  </Applications>\r\n</Package>", Encoding.UTF8);
            File.WriteAllBytes(Path.Combine(gamePkg, "MineTest.exe"), new byte[160 * 1024]);
            File.WriteAllBytes(Path.Combine(appPkg, "NetdiskService.exe"), new byte[160 * 1024]);

            const string repoKey = "Software\\PaviseSelfTest\\Repository\\Packages";
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(repoKey + "\\" + pkgFull))
                {
                    k.SetValue("PackageRootFolder", gamePkg);
                    k.SetValue("DisplayName", "@{" + pkgFull + "?ms-resource://MineTest/AppName}");
                }
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(repoKey + "\\Vendor.NetdiskService_1.0.0.0_x64__zzz999"))
                {
                    k.SetValue("PackageRootFolder", appPkg);
                    k.SetValue("DisplayName", "NetdiskService");
                }

                var hits = new List<ScanHit>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                GameScan.FromPackageRepository(null, repoKey, hits, seen);
                Eq(1, hits.Count);
                Eq("MineTest", hits[0].Name);
                Eq(Path.Combine(gamePkg, "MineTest.exe"), hits[0].Exe);
            }
            finally
            {
                try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree("Software\\PaviseSelfTest", false); }
                catch { }
            }
        }

        private static void TestMultiFolderGameRoot()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "PaviseFamily_" + Guid.NewGuid().ToString("N"));
            string install = Path.Combine(sandbox, "英雄联盟");
            try
            {
                Directory.CreateDirectory(Path.Combine(install, "Game"));
                Directory.CreateDirectory(Path.Combine(install, "LeagueClient"));
                Directory.CreateDirectory(Path.Combine(install, "Riot Client"));
                Directory.CreateDirectory(Path.Combine(install, "WeGameLauncher"));

                string selected = Path.Combine(install, "LeagueClient", "LeagueClient.exe");
                string root = GameScan.InferGameRoot(selected);
                Eq(Path.GetFullPath(install), root);

                Directory.CreateDirectory(Path.Combine(install, "TCLS"));
                Eq(Path.GetFullPath(install), GameScan.InferGameRoot(
                    Path.Combine(install, "TCLS", "client.exe")));
                Eq(Path.GetFullPath(install), GameScan.InferGameRoot(
                    Path.Combine(install, "Game", "League of Legends.exe")));

                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.TrimEnd('\\') + "\\" };
                Eq(true, GameMode.IsGameFamily(Path.Combine(install, "Riot Client", "RiotClientServices.exe"), roots));
                Eq(true, GameMode.IsGameFamily(Path.Combine(install, "Game", "League of Legends.exe"), roots));
                Eq(true, GameMode.IsGameFamily(Path.Combine(install, "WeGameLauncher", "launcher.exe"), roots));
                Eq(false, GameMode.IsGameFamily(Path.Combine(install, "ACE", "ACE-Helper.exe"), roots, "ACE-Helper"));
                Eq(false, GameMode.IsGameFamily(Path.Combine(sandbox, "WeGame", "wegame.exe"), roots));
                Eq(false, GameMode.IsGameFamily(Path.Combine(install + "-backup", "Game", "game.exe"), roots));

                string other = Path.Combine(sandbox, "GenericUnrealGame");
                string shipping = Path.Combine(other, "Binaries", "Win64", "Generic-Win64-Shipping.exe");
                Eq(Path.GetFullPath(other), GameScan.InferGameRoot(shipping));

                Eq(@"C:\g\Mount & Blade II Bannerlord", GameScan.InferGameRoot(
                    @"C:\g\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client\Bannerlord.exe"));
                Eq(@"C:\g\SomeGame\Win64_Shipping_Server", GameScan.InferGameRoot(
                    @"C:\g\SomeGame\Win64_Shipping_Server\server.exe"));
            }
            finally { try { Directory.Delete(sandbox, true); } catch { } }
        }

        private static void TestGameCatalogFormat()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseGames", "英雄联盟");
            string line = GameMode.EncodeGameLine("LeagueClient.exe", root);
            string name, parsed;
            Eq(true, GameMode.TryParseGameLine(line, out name, out parsed));
            Eq("LeagueClient", name);
            Eq(Path.GetFullPath(root), parsed);

            Eq(true, GameMode.TryParseGameLine("League of Legends", out name, out parsed));
            Eq("League of Legends", name);
            Eq<string>(null, parsed);
        }

        private static void TestGameCatalogUpgrade(string root)
        {
            string data = Path.Combine(root, "catalog-upgrade");
            Directory.CreateDirectory(data);
            string games = Path.Combine(data, "Pavise.games.txt");
            File.WriteAllText(games, "LeagueClient\r\n", Encoding.UTF8);

            var mode = new GameMode(data, new SuppressionCore());
            Eq(true, File.Exists(games + ".pre-election.bak"));
            string[] afterClear = File.ReadAllLines(games, Encoding.UTF8);
            Eq(0, afterClear.Length);
            string install = Path.Combine(data, "英雄联盟");
            Directory.CreateDirectory(Path.Combine(install, "Game"));
            Directory.CreateDirectory(Path.Combine(install, "LeagueClient"));
            string executable = Path.Combine(install, "LeagueClient", "LeagueClient.exe");
            File.Copy(Application.ExecutablePath, executable, true);
            Eq(true, mode.AddGameExecutable("LeagueClient", executable));

            string[] lines = File.ReadAllLines(games, Encoding.UTF8);
            Eq(1, lines.Length);
            string name, parsedRoot;
            Eq(true, GameMode.TryParseGameLine(lines[0], out name, out parsedRoot));
            Eq("LeagueClient", name);
            Eq(Path.GetFullPath(install), parsedRoot);
            Eq(false, mode.AddGameExecutable("LeagueClient", executable));
        }

        private static void TestExecutableResolver(string root)
        {
            string dir = Path.Combine(root, "resolver");
            Directory.CreateDirectory(dir);
            string executable = Path.Combine(dir, "SampleGame.exe");
            File.Copy(Application.ExecutablePath, executable, true);
            string resolved, error;
            Eq(true, GameExecutableResolver.TryResolve(executable, out resolved, out error));
            Eq(Path.GetFullPath(executable), resolved);

            string shortcut = Path.Combine(dir, "Sample Game.lnk");
            Eq(true, GameExecutableResolver.CreateShortcutForTest(shortcut, executable));
            Eq(true, GameExecutableResolver.TryResolve(shortcut, out resolved, out error));
            Eq(Path.GetFullPath(executable), resolved);

            string invalid = Path.Combine(dir, "not-a-game.txt");
            File.WriteAllText(invalid, "x");
            Eq(false, GameExecutableResolver.TryResolve(invalid, out resolved, out error));
        }

        private static void TestHeadlessEntry(string root)
        {
            string dir = Path.Combine(root, "headless-entry");
            Directory.CreateDirectory(dir);
            string executable = Path.Combine(dir, "HeadlessProbe.exe");
            string beat = Path.Combine(dir, "headless.beat");
            File.Copy(Application.ExecutablePath, executable, true);
            Process probe = null;
            Process[] all = null;
            try
            {
                ProcessStartInfo start = Hidden(
                    "--test-heartbeat-probe " + Quote(beat));
                start.FileName = executable;
                probe = Process.Start(start);
                if (probe == null) throw new Exception("headless entry did not start");
                WaitAdvance(beat, -1, 4000);

                all = Process.GetProcesses();
                var selectedProfile = GameProfileStore.NewProfile("Headless", dir, executable);
                selectedProfile.Entries.Clear();
                selectedProfile.Entries.Add("HeadlessProbe");
                int currentSession =
                    Process.GetCurrentProcess().SessionId;
                GameProcessSnapshot identity;
                if (!GameSessionDetector.TryCaptureProcessIdentity(
                        probe.Id, currentSession, out identity))
                    throw new Exception(
                        "same-handle process identity was unavailable");
                if (identity.Creation <= 0
                    || !string.Equals(
                        "HeadlessProbe", identity.Name,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        Path.GetFullPath(executable),
                        Path.GetFullPath(identity.Path),
                        StringComparison.OrdinalIgnoreCase))
                    throw new Exception(
                        "same-handle process identity fields disagree");
                if (GameSessionDetector.TryCaptureProcessIdentity(
                        probe.Id, currentSession + 1,
                        out identity))
                    throw new Exception(
                        "same-handle identity crossed login sessions");
                bool armed;
                if (GameSessionDetector.Detect(all, new[] { selectedProfile }, out armed) != null)
                    throw new Exception("headless exe engaged a session (must stay armed only)");
                if (!armed)
                    throw new Exception("user-selected headless exe did not arm the profile");
                bool crossArmed;
                if (GameSessionDetector.Detect(
                        all, new[] { selectedProfile },
                        currentSession + 1, out crossArmed) != null || crossArmed)
                    throw new Exception(
                        "another login session armed or activated game policy");

                var legacyProfile = GameProfileStore.NewProfile("HeadlessLegacy", dir);
                legacyProfile.Entries.Clear();
                legacyProfile.Entries.Add("HeadlessProbe");
                legacyProfile.ExecutablePath = null;
                if (GameSessionDetector.Detect(all, new[] { legacyProfile }) != null)
                    throw new Exception("legacy headless entry wrongly activated game policy");
            }
            finally
            {
                if (all != null) foreach (Process process in all) process.Dispose();
                if (probe != null) { StopOwned(probe); probe.Dispose(); }
            }
        }

        private static void TestFallbackEntryRootBoundary(string root)
        {
            string dir = Path.Combine(root, "fallback-entry");
            string gameRoot = Path.Combine(dir, "game");
            string elsewhere = Path.Combine(dir, "elsewhere");
            Directory.CreateDirectory(gameRoot);
            Directory.CreateDirectory(elsewhere);
            string stubExe = Path.Combine(gameRoot, "pavisefbtest.exe");
            string realExe = Path.Combine(gameRoot, "pavisefbtest64.exe");
            string rogueExe = Path.Combine(elsewhere, "pavisefbtest_x64.exe");
            string updaterExe = Path.Combine(elsewhere, "pavisefbtest_updater.exe");
            File.Copy(Application.ExecutablePath, stubExe, true);
            File.Copy(Application.ExecutablePath, realExe, true);
            File.Copy(Application.ExecutablePath, rogueExe, true);
            File.Copy(Application.ExecutablePath, updaterExe, true);
            string beatReal = Path.Combine(dir, "real.beat");
            string beatRogue = Path.Combine(dir, "rogue.beat");
            string beatUpdater = Path.Combine(dir, "updater.beat");
            Process real = null, rogue = null, updater = null;
            Process[] all = null;
            try
            {
                ProcessStartInfo startReal = Hidden(
                    "--test-heartbeat-probe " + Quote(beatReal));
                startReal.FileName = realExe;
                real = Process.Start(startReal);
                if (real == null) throw new Exception("fallback probe did not start");
                WaitAdvance(beatReal, -1, 4000);

                ProcessStartInfo startRogue = Hidden(
                    "--test-heartbeat-probe " + Quote(beatRogue));
                startRogue.FileName = rogueExe;
                rogue = Process.Start(startRogue);
                if (rogue == null)
                    throw new Exception("out-of-root suffix probe did not start");
                WaitAdvance(beatRogue, -1, 4000);

                ProcessStartInfo startUpdater = Hidden(
                    "--test-heartbeat-probe " + Quote(beatUpdater));
                startUpdater.FileName = updaterExe;
                updater = Process.Start(startUpdater);
                if (updater == null) throw new Exception("updater probe did not start");
                WaitAdvance(beatUpdater, -1, 4000);

                all = Process.GetProcesses();
                var profile = GameProfileStore.NewProfile("FallbackTest", gameRoot, stubExe);
                profile.Entries.Clear();
                profile.Entries.Add("pavisefbtest");

                bool armed;
                if (GameSessionDetector.Detect(all, new[] { profile }, out armed) != null)
                    throw new Exception("windowless in-root process engaged a session");
                if (!armed)
                    throw new Exception("in-root process did not arm the profile");

                int session = Process.GetCurrentProcess().SessionId;
                GameProcessSnapshot realId, rogueId, updaterId;
                if (!GameSessionDetector.TryCaptureProcessIdentity(real.Id, session, out realId)
                    || !GameSessionDetector.TryCaptureProcessIdentity(rogue.Id, session, out rogueId)
                    || !GameSessionDetector.TryCaptureProcessIdentity(updater.Id, session, out updaterId))
                    throw new Exception("probe identities unavailable");
                realId.Visible = true;
                realId.Foreground = true;
                realId.FullscreenLike = true;
                rogueId.Visible = true;
                updaterId.Visible = true;
                GameDetection hit = GameSessionDetector.DetectSnapshot(
                    new[] { realId, rogueId, updaterId }, new[] { profile }, out armed);
                if (hit == null)
                    throw new Exception("in-root fullscreen process was not elected");
                if (!string.Equals(hit.RendererName, "pavisefbtest64", StringComparison.OrdinalIgnoreCase))
                    throw new Exception(
                        "renderer should resolve to the in-root process, got "
                        + hit.RendererName);
                if (!hit.FamilyPids.Contains(real.Id))
                    throw new Exception(
                        "in-root process must be in the game family");
                if (hit.FamilyPids.Contains(rogue.Id))
                    throw new Exception(
                        "out-of-root prefix-collision process must not enter the game family");
                if (hit.FamilyPids.Contains(updater.Id))
                    throw new Exception("underscore-plus-word name (_updater) must NOT be treated as the same app — an unrelated third-party process could collide on prefix alone");
                if (!hit.RendererLearnable)
                    throw new Exception("geometry-elected non-anchor renderer must be learnable");
            }
            finally
            {
                if (all != null) foreach (Process process in all) process.Dispose();
                if (real != null) { StopOwned(real); real.Dispose(); }
                if (rogue != null) { StopOwned(rogue); rogue.Dispose(); }
                if (updater != null) { StopOwned(updater); updater.Dispose(); }
            }
        }

    }
}

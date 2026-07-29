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

namespace AegisApp
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
                string report = args.Length >= 2 ? args[1] : Path.Combine(Path.GetTempPath(), "Aegis.selftest.txt");
                Run(report);
                return true;
            }
            if (args[0] == "--config-screenshot" && args.Length >= 2)
            {
                RunConfigScreenshot(args[1], args.Length >= 3 ? args[2] : "zh");
                return true;
            }
            if (args[0] == "--detector-probe" && args.Length >= 4)
            {
                int pid;
                if (!int.TryParse(args[1], out pid)) { Environment.ExitCode = 2; return true; }
                RunDetectorProbe(pid, args[2], args[3]);
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
                    List<GameProfile> profiles = store.LoadOrMigrate(Path.Combine(args[1], "Aegis.games.txt"));
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
                List<GameProfile> profiles = store.LoadOrMigrate(Path.Combine(dataDir, "Aegis.games.txt"));
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
                            int score = GameSessionDetector.Score(profile, p.ProcessName, path, visible, foreground);
                            bool qual = GameSessionDetector.QualifiesRenderer(profile, p.ProcessName, path, visible, foreground);
                            sb.AppendLine("   " + p.ProcessName + " pid=" + pid + " vis=" + visible
                                + " fg=" + foreground + " score=" + score + " renderer=" + qual);
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
                sb.AppendLine("EnabledByAegis=" + InterruptAffinityTweak.EnabledByAegis);
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
                sb.AppendLine("EnabledByAegis=" + InterruptAffinityTweak.EnabledByAegis);
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

                dummyExe = Path.Combine(Path.GetTempPath(), "AegisNetProbeDummy_" + Guid.NewGuid().ToString("N") + ".exe");
                File.WriteAllBytes(dummyExe, new byte[] { 0x4D, 0x5A });
                string dummyName = NetworkAffinityTweak.SanitizePolicyName("AegisNetProbeDummyGame", dummyExe);
                var games = new List<GameProfile> { new GameProfile { Name = "AegisNetProbeDummyGame", ExecutablePath = dummyExe } };

                bool enableOk = NetworkAffinityTweak.Enable(games);
                sb.AppendLine("Enable() 返回=" + enableOk);
                sb.AppendLine("EnabledByAegis=" + NetworkAffinityTweak.EnabledByAegis);
                sb.AppendLine("=== Enable 后网卡寄存器（直接读注册表，独立于内部回读）===");
                foreach (string id in ids) { sb.AppendLine(id); sb.AppendLine(ReadIrqRegSnapshot(id)); }

                string qosCheck;
                RunPlainPowerShell("if (Get-NetQosPolicy -Name '" + dummyName.Replace("'", "''") + "' -ErrorAction SilentlyContinue) { 'EXISTS' } else { 'ABSENT' }", out qosCheck);
                sb.AppendLine("独立查询 QoS 策略 " + dummyName + " ：" + qosCheck.Trim());
                sb.AppendLine();

                bool disableOk = NetworkAffinityTweak.Disable();
                sb.AppendLine("Disable() 返回=" + disableOk);
                sb.AppendLine("EnabledByAegis=" + NetworkAffinityTweak.EnabledByAegis);
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

        // 真实构造主窗口并走一次 ShowPanel()，逐帧采样 Opacity/Top，
        // 验证开场动画确实在渐变+上浮（而不是卡在 0、或直接跳到 1 等于没动画）。
        private static void RunIntroProbe(string output)
        {
            var sb = new System.Text.StringBuilder();
            string data = Path.Combine(Path.GetTempPath(), "AegisIntroProbe_" + Process.GetCurrentProcess().Id);
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

        // 把真实托盘右键菜单显示出来截图，并打印每项的高度/内边距/文字矩形，
        // 用来判断文字到底有没有垂直居中——靠肉眼猜容易改错方向。
        private static void RunMenuProbe(string output, string dumpPath)
        {
            string data = Path.Combine(Path.GetTempPath(), "AegisMenuProbe_" + Process.GetCurrentProcess().Id);
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

        // 真实弹出版本说明窗口并截图。窗口构造时会把"已读版本"写进用户配置，
        // 这里先存后还原，免得诊断run顺手把用户的 NEW 标记吃掉。
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
                List<GameProfile> profiles = store.LoadOrMigrate(Path.Combine(dataDir, "Aegis.games.txt"));
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

        private static void RunConfigScreenshot(string output, string language)
        {
            string data = Path.Combine(Path.GetTempPath(), "AegisConfigShot_" + Process.GetCurrentProcess().Id);
            try
            {
                Directory.CreateDirectory(data);
                Dpi.Init();
                Lang.Init();
                Lang.Cur = language == "en" ? 1 : (language == "ja" ? 2 : 0);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var dlg = new GameModeConfigDialog(new GameMode(data, new SuppressionCore())))
                {
                    dlg.StartPosition = FormStartPosition.Manual;
                    dlg.Location = new Point(-20000, -20000);
                    dlg.Show();
                    DateTime paintReady = DateTime.UtcNow.AddMilliseconds(450);
                    while (DateTime.UtcNow < paintReady)
                    {
                        Application.DoEvents();
                        Thread.Sleep(15);
                    }
                    using (var bmp = new Bitmap(dlg.ClientSize.Width, dlg.ClientSize.Height))
                    {
                        dlg.DrawToBitmap(bmp, new Rectangle(Point.Empty, dlg.ClientSize));
                        bmp.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    dlg.Hide();
                }
            }
            finally { try { Directory.Delete(data, true); } catch { } }
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
            test("LoL runtime: LCU credentials reject malformed input", TestLolCredentialParsing);
            test("LoL runtime: cleanup targets never include core, game or ACE paths", TestLolCleanupBoundary);
            test("LoL quarantine: manifest fields round-trip without ambiguity", () =>
            {
                string error;
                if (!LolQuarantineManager.SelfTestManifest(out error)) throw new Exception(error);
            });
            test("render detector: Office and launchers cannot masquerade as games", TestRenderScoring);
            test("render detector: parallel instances and PID reuse stay isolated", TestGameSessionInstanceIsolation);
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
                Eq(false, GameSessionDetector.IsProfileEntryProcess(
                    eventProfile, "EventProbeHelper",
                    @"C:\Games\EventProbe\EventProbeHelper.exe"));
                Eq(false, GameSessionDetector.IsProfileEntryProcess(
                    eventProfile, "EventProbe_x64",
                    @"C:\Other\EventProbe_x64.exe"));
                var detection = new GameDetection();
                detection.RendererPid = 41;
                detection.RendererName = "EventProbeLauncher";
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
                    RendererName = "EventProbeLauncher"
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
                // 关键回归：窗口动画的原值必须落盘。只存在内存里的话，Aegis 被强杀后重启，
                // Activate 会把"当前已关闭"当成用户本来的设置，动画整个登录会话都开不回来。
                int before = 0;
                if (!Native.SystemParametersInfoGet(Native.SPI_GETUIEFFECTS, 0, ref before, 0))
                    throw new TestSkippedException("SPI_GETUIEFFECTS unavailable");
                if (before == 0)
                    throw new TestSkippedException("window animations are already off on this machine");
                if (Settings.LoadStr("PrevUiEffects", "").Length > 0
                    || Settings.LoadStr("PrevTransparency", "").Length > 0)
                    throw new TestSkippedException("another Aegis instance is holding a visual effects snapshot");
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
                // PowerShell 把 U+2019 等排版引号也当单引号定界符，所以"把 ' 翻倍"挡不住注入。
                // Aegis 以管理员运行，被注入等于交出管理员权限，因此数据一律走环境变量。
                string evil = "D:" + "\\" + "Evil" + (char)0x2019 + ";Write-Output PWNED;" + (char)0x2019;
                var argv = new Dictionary<string, string> { { "AEGIS_PATH", evil } };
                string outText;
                if (!PsRunner.Run("Write-Output $env:AEGIS_PATH\r\n", "注入自测", 20000, argv, out outText))
                    throw new TestSkippedException("powershell unavailable");
                foreach (string line in outText.Split('\n'))
                    if (line.Trim() == "PWNED")
                        throw new Exception("injected command executed: user data reached the parser");
                if (outText.IndexOf(evil, StringComparison.Ordinal) < 0)
                    throw new Exception("payload was not echoed verbatim; quoting altered the data");
            });
            test("language table: every entry is trilingual and format placeholders are consistent", () =>
            {
                // 缺键时 Lang.T 会把键名本身当文本返回，界面上就会出现 "btn.open" 这种字样，
                // 三种语言下都一样，光靠肉眼看很难发现，所以由测试兜住。
                var missing = new List<string>();
                foreach (string key in Lang.AllKeys())
                {
                    string[] row = Lang.Row(key);
                    if (row == null || row.Length != 3) { missing.Add(key + "(译文不足3种)"); continue; }
                    for (int i = 0; i < 3; i++)
                        if (string.IsNullOrEmpty(row[i])) missing.Add(key + "(第" + i + "种语言为空)");
                    // 同一条目的各语言占位符数量必须一致，否则切语言后 string.Format 会抛异常
                    int zh = CountPlaceholders(row[0]);
                    for (int i = 1; i < 3; i++)
                        if (CountPlaceholders(row[i]) != zh)
                            missing.Add(key + "(占位符数量各语言不一致)");
                }
                if (missing.Count > 0) throw new Exception(string.Join("; ", missing.ToArray()));
            });
            test("crash journal: old 9-field records still load after the QoS fields were added", () =>
            {
                string name = Convert.ToBase64String(Encoding.UTF8.GetBytes("game"));
                // 旧格式（9 段，无 QoS）：必须照常读出，QoS 退化为 -1 = 交给系统托管，
                // 也就是加字段之前的行为，升级不会作废已有的待恢复记录
                Eq("1|-1|-1", CrashGuard.ProbeParse("111|222|" + name + "|32|255|2|5|-1|"));
                // 新格式（11 段）：QoS 原样读回
                Eq("1|1|1", CrashGuard.ProbeParse("111|222|" + name + "|32|255|2|5|-1||1|1"));
                // 尾部字段损坏时退回 -1，而不是整条丢弃
                Eq("1|-1|-1", CrashGuard.ProbeParse("111|222|" + name + "|32|255|2|5|-1||x|y"));
                // CpuSets 列表正常携带时也不受影响
                Eq("1|1|0", CrashGuard.ProbeParse("111|222|" + name + "|32|255|2|5|-1|3,4,5|1|0"));
                // 段数不足仍然拒绝
                Eq("0", CrashGuard.ProbeParse("111|222|" + name + "|32"));
            });
            test("suppression: game-root containment is anchored on a path segment", () =>
            {
                // 兄弟目录不能因为前缀相同就被当成同一个游戏目录
                Eq(true, GameMode.UnderRoot(@"D:\Games\Apex\bin\game.exe", @"D:\Games\Apex"));
                Eq(true, GameMode.UnderRoot(@"D:\Games\Apex\bin\game.exe", @"D:\Games\Apex\"));
                Eq(false, GameMode.UnderRoot(@"D:\Games\ApexBackup\sync.exe", @"D:\Games\Apex"));
                Eq(false, GameMode.UnderRoot(@"D:\Games\ApexTools\updater.exe", @"D:\Games\Apex\"));
                Eq(false, GameMode.UnderRoot(@"D:\SteamLibrary\x\y.exe", @"D:\Steam"));
                // 根目录本身不算"在根目录之下"，空值一律不匹配
                Eq(false, GameMode.UnderRoot(@"D:\Games\Apex", @"D:\Games\Apex"));
                Eq(false, GameMode.UnderRoot(null, @"D:\Games\Apex"));
                Eq(false, GameMode.UnderRoot(@"D:\Games\Apex\a.exe", null));
                Eq(false, GameMode.UnderRoot(@"D:\Games\Apex\a.exe", ""));

                // 走真实资格判定：同名前缀的兄弟目录不该被豁免
                const string win = @"C:\Windows\";
                Eq(false, GameMode.BasicBackgroundEligible(10, 99, "game", @"D:\Games\Apex\bin\game.exe",
                    1, 1, 20, false, win, false, @"D:\Games\Apex"));
                Eq(true, GameMode.BasicBackgroundEligible(10, 99, "sync", @"D:\Games\ApexBackup\sync.exe",
                    1, 1, 20, false, win, false, @"D:\Games\Apex"));
            });
            test("suppression: anti-cheat exemption is as broad as anti-cheat detection", () =>
            {
                const string win = @"C:\Windows\";
                // 检测器认定为反作弊的名字，压制侧必须同样豁免。
                // 两边用不同宽度的判定，就会出现"检测器说是反作弊、压制器照压不误"的致命缝隙。
                string[] names = { "GameAntiCheat", "BattlEye", "SGuard64Helper", "EasyAntiCheat_x64",
                                   "vgtray", "GameMon64", "TenSafe_1", "ACE-Helper" };
                foreach (string n in names)
                {
                    if (!GameSessionDetector.IsAntiCheatLikeName(n))
                        throw new Exception("detector no longer treats " + n + " as anti-cheat; test premise broken");
                    // 常规强度
                    Eq(false, GameMode.BasicBackgroundEligible(10, 99, n, @"C:\Program Files\AC\" + n + ".exe",
                        1, 1, 20, false, win));
                    // 竞技级强度（前台/可见窗口豁免全部取消，最容易误伤）
                    Eq(false, GameMode.BasicBackgroundEligible(10, 99, n, @"C:\Program Files\AC\" + n + ".exe",
                        1, 1, 20, false, win, false, null, true));
                }
            });
            test("theme fonts: the shared font cache survives repeated painting", () =>
            {
                // Theme.UI 返回的是全局缓存实例。绘制代码若把它 using 掉，
                // 缓存里留下的就是已释放对象，下一次取到它再绘制就会抛异常。
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
                // 缓存中的字体必须仍然可用：对已释放的 Font 取 Height 会抛异常
                foreach (float size in new[] { 9.25f, 8.4f, 10.2f, 7.8f, 7.6f })
                {
                    if (Theme.UI(size, true).Height <= 0) throw new Exception(size + "pt bold font is unusable");
                    if (Theme.UI(size, false).Height <= 0) throw new Exception(size + "pt font is unusable");
                }
            });
            test("defender exclusion: path matching never mistakes a neighbour for an owned entry", () =>
            {
                // 大小写、尾部反斜杠、引号、空白都不该影响判定
                Eq(@"C:\Games\Foo", DefenderExclusion.Normalize(@"C:\Games\Foo\"));
                Eq(@"C:\Games\Foo", DefenderExclusion.Normalize(@"  ""C:\Games\Foo""  "));
                Eq("", DefenderExclusion.Normalize(null));
                Eq("", DefenderExclusion.Normalize("   "));
                // 盘符根目录不能被削成 "C:"
                Eq(@"C:\", DefenderExclusion.Normalize(@"C:\"));

                var owned = new List<string> { @"C:\Games\Foo", @"D:\Steam\Bar\" };
                Eq(true, DefenderExclusion.Contains(owned, @"c:\games\foo"));
                Eq(true, DefenderExclusion.Contains(owned, @"C:\Games\Foo\"));
                Eq(true, DefenderExclusion.Contains(owned, @"D:\Steam\Bar"));
                // 前缀相同但不是同一个目录，绝不能算命中——否则会误删用户自己的排除
                Eq(false, DefenderExclusion.Contains(owned, @"C:\Games\Foobar"));
                Eq(false, DefenderExclusion.Contains(owned, @"C:\Games"));
                Eq(false, DefenderExclusion.Contains(owned, @"C:\Games\Foo\Sub"));
                Eq(false, DefenderExclusion.Contains(new List<string>(), @"C:\Games\Foo"));
            });
            test("per-game GPU preference: merging never destroys fields Windows owns", () =>
            {
                // 回归钉子：Windows 把逐游戏「窗口化优化」存在 AppStatus 里，
                // 写 GpuPreference 时整值覆盖会把它抹掉（本机真有游戏是 AppStatus=0; 这个形态）
                Eq("AppStatus=0;GpuPreference=2;", GameExeTweaks.MergeField("AppStatus=0;", "GpuPreference", "2"));
                Eq("AppStatus=4096;GpuPreference=2;", GameExeTweaks.MergeField("AppStatus=4096;GpuPreference=0;", "GpuPreference", "2"));
                // 顺序保持不变，己方字段就地替换而不是挪到末尾
                Eq("GpuPreference=2;AppStatus=0;", GameExeTweaks.MergeField("GpuPreference=0;AppStatus=0;", "GpuPreference", "2"));
                // 空值/空白/仅分号
                Eq("GpuPreference=2;", GameExeTweaks.MergeField(null, "GpuPreference", "2"));
                Eq("GpuPreference=2;", GameExeTweaks.MergeField("", "GpuPreference", "2"));
                Eq("GpuPreference=2;", GameExeTweaks.MergeField(";;", "GpuPreference", "2"));
                // 畸形段（没有等号）原样保留，不因为解析不了就丢弃
                Eq("Garbage;GpuPreference=2;", GameExeTweaks.MergeField("Garbage;", "GpuPreference", "2"));
                // 重复字段只保留一份
                Eq("GpuPreference=2;", GameExeTweaks.MergeField("GpuPreference=0;GpuPreference=1;", "GpuPreference", "2"));

                // 读取：用于判断"已经是想要的值就别动"
                Eq("2", GameExeTweaks.ReadField("AppStatus=4096;GpuPreference=2;", "GpuPreference"));
                Eq("0", GameExeTweaks.ReadField("AppStatus=0;", "AppStatus"));
                Eq(null, GameExeTweaks.ReadField("AppStatus=0;", "GpuPreference"));
                Eq(null, GameExeTweaks.ReadField(null, "GpuPreference"));
                // 不能被前缀相同的字段名骗到
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

                // 版本按从新到旧排列，且日期字段不能为空
                for (int i = 1; i < ReleaseNotes.All.Length; i++)
                    if (!UpdateChecker.IsNewer(ReleaseNotes.All[i - 1].Version, ReleaseNotes.All[i].Version))
                        throw new Exception("notes are not ordered newest-first at index " + i);
                foreach (ReleaseNote n in ReleaseNotes.All)
                {
                    if (string.IsNullOrEmpty(n.Date)) throw new Exception(n.Version + " has no date");
                    if (n.Tag != "v" + n.Version) throw new Exception("bad tag for " + n.Version);
                }

                // 越界索引必须返回空串而不是抛异常
                Eq("", cur.Item(-1));
                Eq("", cur.Item(cur.Count));
            });
            test("auto-hide: fires once per game session and re-arms only on the next one", () =>
            {
                bool last = false, armed = false;
                // 开关关着：整局都不该收
                Eq(AutoHideAction.None, PanelForm.NextAutoHide(true, ref last, ref armed, false, true));
                Eq(AutoHideAction.Cancel, PanelForm.NextAutoHide(false, ref last, ref armed, false, true));

                // 开关开着、窗口可见：这局收一次
                last = false; armed = false;
                Eq(AutoHideAction.Schedule, PanelForm.NextAutoHide(true, ref last, ref armed, true, true));
                // 同一局内反复轮询不得重复安排
                Eq(AutoHideAction.None, PanelForm.NextAutoHide(true, ref last, ref armed, true, true));
                Eq(AutoHideAction.None, PanelForm.NextAutoHide(true, ref last, ref armed, true, true));
                // 这局结束 → 撤销并重新武装
                Eq(AutoHideAction.Cancel, PanelForm.NextAutoHide(false, ref last, ref armed, true, true));
                Eq(false, armed);
                // 下一局重新收一次
                Eq(AutoHideAction.Schedule, PanelForm.NextAutoHide(true, ref last, ref armed, true, true));

                // 游戏开始时窗口本来就没显示：消耗掉本局机会但不安排收起
                last = false; armed = false;
                Eq(AutoHideAction.None, PanelForm.NextAutoHide(true, ref last, ref armed, true, false));
                Eq(true, armed);
                // 用户对局中途自己打开窗口，也不该被再次收走
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
                if (!empty.StartsWith("Aegis_Game")) throw new Exception("empty game name did not fall back to a placeholder");
            });

            string root = Path.Combine(Path.GetTempPath(), "AegisSelfTest_" + Process.GetCurrentProcess().Id + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                test("game catalog: legacy entry upgrades to a persisted install root", () => TestGameCatalogUpgrade(root));
                test("game profiles: migration removes learning state and deduplicates", () => TestProfileStore(root));
                test("game profiles: an unreadable file is never overwritten by a save", () => TestProfileLoadFailure(root));
                test("game library: EXE/LNK resolve without executing the target", () => TestExecutableResolver(root));
                test("LoL quarantine: atomic move, exact restore and no-overwrite conflict", () => TestLolQuarantineRoundTrip(root));
                test("render detector: user-selected headless exe activates; legacy headless does not", () => TestHeadlessEntry(root));
                test("render detector: suffix fallback stays inside the configured profile root", () => TestFallbackEntryRootBoundary(root));
                test("session reports: legacy frame telemetry is archived", () => TestReportMigration(root));
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
            test("staged suppression: crash journal restores a live process", () => TestSuppressionCrashRecovery(root));
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            log.Insert(0, "Aegis " + App.Version + " self-test @ " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
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
            Version assemblyVersion = typeof(App).Assembly.GetName().Version;
            Eq("1.5.0.0", assemblyVersion == null ? "" : assemblyVersion.ToString());
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(Application.ExecutablePath);
            Eq("1.5.0.0", info.FileVersion);
            Eq("Aegis", info.ProductName);
            Eq("bdth", info.CompanyName);
        }

        private static void TestRenderScoring()
        {
            var profile = GameProfileStore.NewProfile("Example", Path.Combine(Path.GetTempPath(), "ExampleGame"));
            profile.Entries.Add("ExampleGame");
            string game = Path.Combine(profile.Root, "Binaries", "Win64", "ExampleGame-Win64-Shipping.exe");
            string launcher = Path.Combine(profile.Root, "Launcher", "ExampleLauncher.exe");
            Eq(true, profile.ContainsPath(game));
            Eq(false, profile.ContainsPath(Path.Combine(profile.Root + "-backup", "game.exe")));
            if (GameSessionDetector.Score(profile, "ExampleGame", game, true, true) < 65) throw new Exception("game candidate score too low");
            if (GameSessionDetector.Score(profile, "ExampleLauncher", launcher, true, true) >= 65) throw new Exception("launcher activated profile");
            Eq(true, GameSessionDetector.QualifiesRenderer(profile, "ExampleGame", game, true, true));
            Eq(false, GameSessionDetector.QualifiesRenderer(profile, "ExampleLauncher", launcher, true, true));
            Eq(-1000, GameSessionDetector.Score(profile, "POWERPNT", @"C:\Program Files\Microsoft Office\POWERPNT.EXE", true, true));
            Eq(-1000, GameSessionDetector.Score(profile, "ACE-Helper", Path.Combine(profile.Root, "ACE-Helper.exe"), true, true));
            Eq(-1000, GameSessionDetector.Score(profile, "LeagueClientUxRender", Path.Combine(profile.Root, "LeagueClient", "LeagueClientUxRender.exe"), true, true));
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

            // Two live launchers under one root form two instances.  A renderer
            // may select only the entry reached by a creation-valid parent tree.
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
                    Visible = true, Foreground = true
                }
            };
            GameDetection hit = GameSessionDetector.DetectSnapshot(
                parallel, new[] { profile }, now);
            if (hit == null)
                throw new Exception("parallel instance was not detected");
            Eq(201, hit.RendererPid);
            Eq(true, hit.FamilyPids.Contains(200));
            Eq(true, hit.FamilyPids.Contains(201));
            Eq(false, hit.FamilyPids.Contains(100));
            Eq(false, hit.FamilyPids.Contains(101));
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

            // If stickiness keeps instance B while a later fresh scan prefers
            // instance A, rebuilding the anchor must replace—not union—the two
            // trees.
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

            // A stale child whose recorded parent PID now names a newer process
            // must not inherit that new process's session.
            var reusedParent = new[]
            {
                new GameProcessSnapshot
                {
                    Pid = 300, ParentPid = 10,
                    Creation = now - 1000000,
                    Name = "LeagueClient", Path = launcher
                },
                new GameProcessSnapshot
                {
                    Pid = 301, ParentPid = 300,
                    Creation = now - 2000000,
                    Name = "League of Legends", Path = renderer,
                    Visible = true, Foreground = true
                }
            };
            hit = GameSessionDetector.DetectSnapshot(
                reusedParent, new[] { profile }, now);
            if (hit == null)
                throw new Exception("launcher anchor disappeared");
            Eq(300, hit.RendererPid);
            Eq(false, hit.FamilyPids.Contains(301));

            // Some launch stacks hand off through a service and lose the parent
            // edge.  Admit that migration only while it is new, foreground and
            // has exactly one possible launcher instance.
            var detached = new[]
            {
                new GameProcessSnapshot
                {
                    Pid = 400, ParentPid = 10,
                    Creation = now
                        - 5 * TimeSpan.TicksPerHour,
                    Name = "LeagueClient", Path = launcher
                },
                new GameProcessSnapshot
                {
                    Pid = 401, ParentPid = 999,
                    Creation = now
                        - 10 * TimeSpan.TicksPerSecond,
                    Name = "League of Legends", Path = renderer,
                    Visible = true, Foreground = true
                },
                new GameProcessSnapshot
                {
                    Pid = 402, ParentPid = 401,
                    Creation = now
                        - 9 * TimeSpan.TicksPerSecond,
                    Name = "LeagueWorker",
                    Path = Path.Combine(
                        root, "Game", "LeagueWorker.exe")
                }
            };
            hit = GameSessionDetector.DetectSnapshot(
                detached, new[] { profile }, now);
            if (hit == null)
                throw new Exception(
                    "safe launcher handoff was not detected");
            Eq(401, hit.RendererPid);
            Eq(detached[1].Creation, hit.RendererCreation);
            Eq(true, hit.FamilyPids.Contains(400));
            Eq(true, hit.FamilyPids.Contains(401));
            Eq(true, hit.FamilyPids.Contains(402));

            var ambiguous = new[]
            {
                detached[0],
                new GameProcessSnapshot
                {
                    Pid = 500, ParentPid = 10,
                    Creation = now
                        - 4 * TimeSpan.TicksPerMinute,
                    Name = "LeagueClient", Path = launcher
                },
                detached[1]
            };
            hit = GameSessionDetector.DetectSnapshot(
                ambiguous, new[] { profile }, now);
            if (hit == null)
                throw new Exception(
                    "parallel launchers lost their safe anchors");
            Eq(false, hit.RendererPid == 401);
            Eq(false, hit.FamilyPids.Contains(401));

            // A legacy name-only profile may use its saved root, but the same
            // executable name outside that root is not an entry.
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
                    Visible = true, Foreground = true
                }
            };
            Eq<GameDetection>(null,
                GameSessionDetector.DetectSnapshot(
                    outOfRoot, new[] { legacy }, now));
            outOfRoot[0].Path = Path.Combine(
                root, "Game", "LegacyGame.exe");
            if (GameSessionDetector.DetectSnapshot(
                    outOfRoot, new[] { legacy }, now) == null)
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

            // 采样窗口过短时速率会被放大：扫描由进程启停事件驱动（200ms 合并窗口），
            // 不做下限保护的话，几百毫秒内的一点 CPU 就会被算成越阈值并一路升到 Isolated。
            var fast = new BackgroundPressureController();
            long t = 200 * second, used = 0;
            Eq(SuppressionLevel.None, fast.Observe(9, "burst", 1, used, 0, t, PerformancePreset.Standard));
            for (int i = 0; i < 4; i++)
            {
                t += second / 5;                       // 200ms 一轮，累计不足 1 秒
                used += (long)(second / 5 * 0.10);     // 期间真实占用 0.10 核，远超 0.08 阈值
                Eq(SuppressionLevel.None, fast.Observe(9, "burst", 1, used, 0, t, PerformancePreset.Standard));
            }
            // 窗口不足时基线必须留着：如果每次都把 At 前移，进程频繁启停的机器上
            // dt 永远攒不到 1 秒，热度再也不会增长，自适应隔离等于被彻底关掉。
            // 累计满 1 秒后算出的 0.10 核是真实占用率，不是被短窗口放大的假值。
            t += second / 5;
            used += (long)(second / 5 * 0.10);
            Eq(SuppressionLevel.Eco, fast.Observe(9, "burst", 1, used, 0, t, PerformancePreset.Standard));

            // 亚秒采样不得把已经生效的等级撤销：调用方把返回值直接当作目标等级，
            // 回报 None 会让已经隔离到小核的进程被放回全部核心。
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

            // 窗口过长（中间断过档）同样不该凭一次跨度极大的差值就判热
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

        private static void TestDashboardMotion()
        {
            Theme.SetMode(PerformancePreset.Competitive, false);
            try
            {
                using (var core = new AegisCore())
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
            // 没有被判定为"当前游戏的进程树祖先"时，任何名字（含 wegame）都不享受特殊待遇
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "wegame", @"C:\WeGame\wegame.exe", 1, 1, 20, false, win));
            // 只要被判定为祖先，任何名字（不只是 wegame）都会被豁免——不认平台名字，只认进程树结构
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "wegame", @"C:\WeGame\wegame.exe", 1, 1, 20, false, win, true, null));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "anylauncher", @"D:\Anything\launcher.exe", 1, 1, 20, false, win, true, null));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "railhelper", @"D:\SomeGame\TCLS\rail.exe", 1, 1, 20, false, win, false, @"D:\SomeGame\"));

            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "worker", @"D:\Apps\worker.exe", 1, 1, 10, false, win, false, null, true));
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "discord", @"D:\Apps\discord.exe", 1, 1, 20, true, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "dwm", @"C:\Windows\System32\dwm.exe", 1, 1, 20, false, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "lsass", @"C:\Windows\System32\lsass.exe", 1, 1, 20, false, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "audiodg", @"C:\Windows\System32\audiodg.exe", 1, 1, 20, false, win, false, null, true));
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "explorer", @"C:\Windows\explorer.exe", 1, 1, 20, false, win, false, null, true));
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "SearchIndexer", @"C:\Windows\System32\SearchIndexer.exe", 1, 1, 20, false, win, false, null, true));
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "svchost", @"D:\Malware\svchost.exe", 1, 1, 20, false, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "SGuard64", @"D:\WeGame\SGuard64.exe", 1, 1, 20, false, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "wegame", @"C:\WeGame\wegame.exe", 1, 1, 20, false, win, true, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "railhelper", @"D:\SomeGame\TCLS\rail.exe", 1, 1, 20, false, win, false, @"D:\SomeGame\", true));

            // WalkAncestorChain：不查任何平台名单，纯粹沿父进程链网上走
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

            // 兜底通道：链路断掉够不到的常驻启动器外壳，仅在有活跃对局时按通用启动器类别豁免
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

            var selected = new GameProfile { Name = "MyTool", ExecutablePath = @"D:\Apps\chrome.exe" };
            Eq(true, GameSessionDetector.QualifiesRenderer(selected, "chrome", @"D:\Apps\chrome.exe", true, false));
            Eq(false, GameSessionDetector.QualifiesRenderer(selected, "chrome", @"D:\Other\chrome.exe", true, false));
            var acSel = new GameProfile { Name = "AC", ExecutablePath = @"D:\Games\SGuard64.exe" };
            Eq(false, GameSessionDetector.QualifiesRenderer(acSel, "SGuard64", @"D:\Games\SGuard64.exe", true, true));
            var acVariant = new GameProfile { Name = "BE", ExecutablePath = @"D:\Games\BattlEye.exe" };
            Eq(false, GameSessionDetector.QualifiesRenderer(acVariant, "BattlEye", @"D:\Games\BattlEye.exe", true, true));
        }

        private static void TestProfileStore(string root)
        {
            string dir = Path.Combine(root, "profiles");
            Directory.CreateDirectory(dir);
            string legacy = Path.Combine(dir, "Aegis.games.txt");
            string gameRoot = Path.Combine(dir, "GenericGame");
            File.WriteAllLines(legacy, new[] { GameMode.EncodeGameLine("GenericGame", gameRoot), GameMode.EncodeGameLine("GenericHelper", gameRoot) }, Encoding.UTF8);
            var store = new GameProfileStore(dir);
            List<GameProfile> first = store.LoadOrMigrate(legacy);
            Eq(1, first.Count);
            Eq(2, first[0].Entries.Count);
            first[0].ExecutablePath = Path.Combine(gameRoot, "GenericGame.exe");
            var duplicate = first[0].Clone();
            duplicate.Entries.Add("DuplicateHelper");
            store.Save(new[] { first[0], duplicate });
            List<GameProfile> second = store.LoadOrMigrate(legacy);
            Eq(1, second.Count);
            Eq(3, second[0].Entries.Count);
        }

        private static void TestMultiFolderGameRoot()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "AegisFamily_" + Guid.NewGuid().ToString("N"));
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
            }
            finally { try { Directory.Delete(sandbox, true); } catch { } }
        }

        private static void TestGameCatalogFormat()
        {
            string root = Path.Combine(Path.GetTempPath(), "AegisGames", "英雄联盟");
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
            string games = Path.Combine(data, "Aegis.games.txt");
            File.WriteAllText(games, "LeagueClient\r\n", Encoding.UTF8);

            var mode = new GameMode(data, new SuppressionCore());
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
                if (GameSessionDetector.Detect(all, new[] { selectedProfile }) == null)
                    throw new Exception("user-selected headless exe did NOT activate (regression: 客户端/大厅将无法被识别)");
                if (GameSessionDetector.Detect(
                        all, new[] { selectedProfile },
                        currentSession + 1) != null)
                    throw new Exception(
                        "another login session activated game policy");

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
            string stubExe = Path.Combine(gameRoot, "aegisfbtest.exe");
            string realExe = Path.Combine(gameRoot, "aegisfbtest64.exe");
            string rogueExe = Path.Combine(elsewhere, "aegisfbtest_x64.exe");
            string updaterExe = Path.Combine(elsewhere, "aegisfbtest_updater.exe");
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
                profile.Entries.Add("aegisfbtest");

                GameDetection hit = GameSessionDetector.Detect(all, new[] { profile });
                if (hit == null)
                    throw new Exception(
                        "in-root suffix fallback was not detected");
                if (!string.Equals(hit.RendererName, "aegisfbtest64", StringComparison.OrdinalIgnoreCase))
                    throw new Exception(
                        "renderer should resolve to the in-root suffix process, got "
                        + hit.RendererName);
                if (!hit.FamilyPids.Contains(real.Id))
                    throw new Exception(
                        "in-root suffix process must be in the game family");
                if (hit.FamilyPids.Contains(rogue.Id))
                    throw new Exception(
                        "out-of-root suffix process must not enter the game family");
                if (hit.FamilyPids.Contains(updater.Id))
                    throw new Exception("underscore-plus-word name (_updater) must NOT be treated as the same app — an unrelated third-party process could collide on prefix alone");
                if (!hit.RendererUserSelected)
                    throw new Exception("fallback-matched renderer must be marked RendererUserSelected (sticky anchor depends on this truth, not a path string guess)");
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

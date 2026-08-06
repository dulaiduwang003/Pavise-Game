// @author bdth 2074055628@qq.com
// 文件用途 白名单自动判定作用域 拖放目标解析与逐条范围调整的自测

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestWhitelistAutoScope()
        {
            Eq(WhitelistRuleKind.ApplicationFamily, GameMode.ResolveAutoKind(@"D:\Tencent\WeChat\WeChat.exe"));
            Eq(WhitelistRuleKind.ApplicationFamily, GameMode.ResolveAutoKind(@"C:\Program Files\Google\Chrome\chrome.exe"));
            Eq(WhitelistRuleKind.ApplicationFamily, GameMode.ResolveAutoKind(@"D:\OBS\obs64.exe"));

            Eq(WhitelistRuleKind.ExactPath, GameMode.ResolveAutoKind(@"C:\Windows\explorer.exe"));
            Eq(WhitelistRuleKind.ExactPath, GameMode.ResolveAutoKind(@"C:\Windows\System32\cmd.exe"));
            Eq(WhitelistRuleKind.ExactPath, GameMode.ResolveAutoKind(@"D:\Py\python.exe"));
            Eq(WhitelistRuleKind.ExactPath, GameMode.ResolveAutoKind(@"D:\Py\python3.11.exe"));
            Eq(WhitelistRuleKind.ExactPath, GameMode.ResolveAutoKind(@"D:\Node\node.exe"));
            Eq(WhitelistRuleKind.ExactPath, GameMode.ResolveAutoKind(@"C:\Java\javaw.exe"));
            Eq(WhitelistRuleKind.ExactPath, GameMode.ResolveAutoKind(@"C:\Windows\System32\svchost.exe"));
        }

        private static void TestWhitelistDropTargets()
        {
            Eq(@"D:\a\game.exe", PanelForm.ResolveWhitelistTarget(@"D:\a\game.exe"));
            Eq(@"D:\a\game.exe", PanelForm.ResolveWhitelistTarget("  \"D:\\a\\game.exe\"  "));
            Eq(null, PanelForm.ResolveWhitelistTarget(@"D:\a\readme.txt"));
            Eq(null, PanelForm.ResolveWhitelistTarget(@"D:\a\folder"));
            Eq(null, PanelForm.ResolveWhitelistTarget(""));
            Eq(null, PanelForm.ResolveWhitelistTarget(null));
        }

        private static void TestWhitelistAutoAddAndReshape()
        {
            string dir = Path.Combine(
                Path.GetTempPath(), "PaviseWl_" + Process.GetCurrentProcess().Id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            string previousLog = Logger.LogPath;
            try
            {
                Logger.LogPath = Path.Combine(dir, "wl.log");
                var mode = new GameMode(dir, new SuppressionCore());

                string app = Path.Combine(dir, "MyApp.exe");
                File.WriteAllText(app, "stub");
                if (!mode.AddWhitelistAuto(app))
                    throw new Exception("普通程序自动加入失败：" + mode.WhitelistLastError);

                WhitelistRuleView added = FindRule(mode, app);
                if (added == null) throw new Exception("加进去的规则没出现在列表里");
                Eq(WhitelistRuleKind.ApplicationFamily, added.Rule.Kind);

                if (!mode.NarrowWhitelistRule(added.Rule.Key))
                    throw new Exception("收窄为仅此程序失败");
                WhitelistRuleView narrowed = FindRule(mode, app);
                if (narrowed == null) throw new Exception("收窄后规则丢失");
                Eq(WhitelistRuleKind.ExactPath, narrowed.Rule.Kind);

                if (!mode.WidenWhitelistRule(narrowed.Rule.Key))
                    throw new Exception("放宽回家族失败");
                WhitelistRuleView widened = FindRule(mode, app);
                if (widened == null) throw new Exception("放宽后规则丢失");
                Eq(WhitelistRuleKind.ApplicationFamily, widened.Rule.Kind);

                string host = Path.Combine(dir, "python.exe");
                File.WriteAllText(host, "stub");
                if (!mode.AddWhitelistAuto(host))
                    throw new Exception("脚本宿主自动加入失败：" + mode.WhitelistLastError);
                WhitelistRuleView hostRule = FindRule(mode, host);
                if (hostRule == null) throw new Exception("脚本宿主规则没出现");
                Eq(WhitelistRuleKind.ExactPath, hostRule.Rule.Kind);

                if (mode.WidenWhitelistRule(hostRule.Rule.Key))
                    throw new Exception("脚本宿主竟然被放宽成了家族豁免");
                Eq(WhitelistRuleKind.ExactPath, FindRule(mode, host).Rule.Kind);
            }
            finally
            {
                Logger.LogPath = previousLog;
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static WhitelistRuleView FindRule(GameMode mode, string path)
        {
            foreach (WhitelistRuleView view in mode.GetWhitelistRulesFast())
                if (view.Rule.Kind != WhitelistRuleKind.LegacyName
                    && WhitelistRule.PathEquals(view.Rule.Value, path)) return view;
            return null;
        }

        private static void TestRunningPickerHidesSystemAndDuplicates()
        {
            List<RunningPickerDialog.Entry> found;
            try { found = RunningPickerDialog.Scan(null); }
            catch (Exception ex) { throw new TestSkippedException("无法枚举进程：" + ex.GetType().Name); }
            if (found.Count == 0) throw new TestSkippedException("当前没有带窗口的用户程序");

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string prefix = string.IsNullOrEmpty(windows) ? @"C:\Windows\" : windows.TrimEnd('\\') + "\\";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RunningPickerDialog.Entry entry in found)
            {
                if (entry.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("选取列表里出现了 Windows 目录下的进程：" + entry.Path);
                if (GameSessionDetector.IsAntiCheatLikeName(Path.GetFileNameWithoutExtension(entry.Path)))
                    throw new Exception("选取列表里出现了反作弊：" + entry.Path);
                if (!seen.Add(entry.Path))
                    throw new Exception("同一个程序被列了多次：" + entry.Path);
            }

            var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { found[0].Path };
            foreach (RunningPickerDialog.Entry entry in RunningPickerDialog.Scan(exclude))
                if (WhitelistRule.PathEquals(entry.Path, found[0].Path))
                    throw new Exception("已在白名单的程序仍然出现在选取列表里");
        }

        private static void RunWhitelistShot(string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            traceFile = Path.Combine(outputDir, "trace.txt");
            Trace("开始");
            string dir = Path.Combine(Path.GetTempPath(),
                "PaviseWlShot_" + Process.GetCurrentProcess().Id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            Logger.LogPath = Path.Combine(dir, "shot.log");
            Dpi.Init();
            Lang.Init();

            var core = new SuppressionCore();
            var mode = new GameMode(dir, core);
            foreach (string sample in new[]
            {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Windows\System32\notepad.exe",
                @"C:\Windows\explorer.exe"
            })
                if (File.Exists(sample)) mode.AddWhitelistAuto(sample);

            Trace("构造面板");
            var form = new PanelForm(new Tamer(core), mode, IconArt.MakeIcon(Dpi.S(24)), true);
            form.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-20000, -20000);
            Trace("面板已构造");

            var stage = new System.Windows.Forms.Timer();
            int step = 0;
            stage.Interval = 500;
            stage.Tick += delegate
            {
                step++;
                try
                {
                    if (step == 1) { form.SelectPageForTest((int)PageId.Whitelist); Trace("已切页"); }
                    else if (step == 3)
                    {
                        ShootControl(form, Path.Combine(outputDir, "whitelist-page.png"));
                        Trace("页面已出图");
                    }
                    else if (step == 4)
                    {
                        var picker = new RunningPickerDialog(null);
                        picker.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                        picker.Location = new System.Drawing.Point(-20000, -20000);
                        picker.Show();
                        picker.PrimeForShot();
                        System.Windows.Forms.Application.DoEvents();
                        ShootControl(picker, Path.Combine(outputDir, "whitelist-picker.png"));
                        picker.Close();
                        Trace("选取框已出图");
                    }
                    else if (step >= 5)
                    {
                        stage.Stop();
                        Console.WriteLine("已导出到 " + outputDir);
                        Console.Out.Flush();
                        Environment.Exit(0);
                    }
                }
                catch (Exception ex) { Trace("出错: " + ex.Message); Environment.Exit(1); }
            };
            stage.Start();
            System.Windows.Forms.Application.Run(form);
        }

        private static string traceFile;

        private static void Trace(string message)
        {
            try { File.AppendAllText(traceFile, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine); }
            catch { }
        }

        private static void Pump(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(50);
            }
        }

        private static void ShootControl(System.Windows.Forms.Control target, string file)
        {
            using (var bmp = new System.Drawing.Bitmap(target.Width, target.Height))
            {
                target.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, target.Width, target.Height));
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private static void TestMemoryFormatting()
        {
            Eq("—", RunningPickerDialog.FormatMemory(0));
            Eq("512 MB", RunningPickerDialog.FormatMemory(512L * 1024 * 1024));
            Eq("1.5 GB", RunningPickerDialog.FormatMemory(1610612736L));
        }
    }
}

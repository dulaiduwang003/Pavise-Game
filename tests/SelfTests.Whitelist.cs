// @author bdth 2074055628@qq.com
// 文件用途 白名单规则格式 路径边界和应用家族身份自测

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestWhitelistRules()
        {
            WhitelistRule legacy;
            Eq(true, WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, "SampleWorker.EXE", out legacy));
            Eq("SampleWorker", legacy.Value);
            Eq(true, legacy.MatchesDirect("sampleworker", null));

            WhitelistRule exact;
            Eq(true, WhitelistRule.TryCreate(WhitelistRuleKind.ExactPath,
                @"C:\Apps\Capture\Capture.exe", out exact));
            Eq(true, exact.MatchesDirect("anything", @"c:\apps\capture\CAPTURE.exe"));

            Eq(false, exact.MatchesDirect("anything", @"C:\Apps\CaptureBackup\Capture.exe"));

            WhitelistRule family;
            Eq(true, WhitelistRule.TryCreate(WhitelistRuleKind.ApplicationFamily,
                @"C:\Apps\Capture\Capture.exe", out family));
            WhitelistRule parsed;
            Eq(true, WhitelistRule.TryParseVersioned(family.Serialize(), out parsed));
            Eq(WhitelistRuleKind.ApplicationFamily, parsed.Kind);
            Eq(family.Value, parsed.Value);
            Eq(false, WhitelistRule.TryParseVersioned("F|not-base64!", out parsed));
            Eq(false, WhitelistRule.TryCreate(
                WhitelistRuleKind.ExactPath, @"relative\Capture.exe", out parsed));
            Eq(false, WhitelistRule.TryCreate(
                WhitelistRuleKind.ExactPath, @"C:relative\Capture.exe", out parsed));
            Eq(false, WhitelistRule.TryCreate(
                WhitelistRuleKind.ExactPath, @"\relative\Capture.exe", out parsed));
            Eq(false, WhitelistRule.TryCreate(
                WhitelistRuleKind.ExactPath, @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\Capture.exe",
                out parsed));
            Eq(false, WhitelistRule.TryCreate(
                WhitelistRuleKind.ExactPath, @"C:\Apps\Capture", out parsed));
            Eq(true, WhitelistRule.IsUnsafeFamilyAnchor(@"C:\Windows\explorer.exe"));
            Eq(true, WhitelistRule.IsUnsafeFamilyAnchor(@"C:\Tools\python.exe"));
            Eq(true, WhitelistRule.IsUnsafeFamilyAnchor(
                @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell_ise.exe"));
            Eq(true, WhitelistRule.IsUnsafeFamilyAnchor(
                @"C:\Windows\System32\wsl.exe"));
            Eq(true, WhitelistRule.IsUnsafeFamilyAnchor(
                @"C:\Program Files\Git\bin\bash.exe"));
            Eq(true, WhitelistRule.IsUnsafeFamilyAnchor(
                @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal\WindowsTerminal.exe"));
            Eq(true, WhitelistRule.IsUnsafeFamilyAnchor(
                @"C:\Tools\Python\python3.12.exe"));
            Eq(true, WhitelistRule.IsUnsafeFamilyAnchor(@"C:\Tools\Python\pyw.exe"));
            Eq(true, WhitelistRule.IsUnsafeFamilyAnchor(@"C:\Tools\PHP\php-win.exe"));
            Eq(true, WhitelistRule.IsUnsafeFamilyAnchor(@"C:\Tools\Ruby\rubyw.exe"));
            Eq(false, WhitelistRule.IsUnsafeFamilyAnchor(
                @"C:\Apps\PythonNotes\PythonNotes.exe"));
            Eq(false, WhitelistRule.IsUnsafeFamilyAnchor(@"C:\Apps\Capture\Capture.exe"));
            Eq(false, WhitelistRule.TryCreate(WhitelistRuleKind.ApplicationFamily,
                @"C:\Windows\explorer.exe", out parsed));
        }

        private static void TestWhitelistFamilyIdentity()
        {
            var parents = new Dictionary<int, int>
            {
                { 11, 10 }, { 12, 11 }, { 21, 20 }, { 31, 30 },
                { 41, 10 }, { 42, 11 }
            };
            var anchors = new HashSet<int> { 10 };
            var retained = new Dictionary<int, long>
            {
                { 20, 2000 },
                { 30, 3000 }
            };
            var current = new Dictionary<int, long>
            {
                { 10, 1000 }, { 11, 1100 }, { 12, 1200 },
                { 20, 2000 }, { 21, 2100 }, { 30, 3001 }, { 31, 3100 },
                { 41, 900 }, { 42, 1100 }
            };

            HashSet<int> family = GameMode.ExpandApplicationFamily(parents, anchors, retained, current);
            Eq(true, family.Contains(10));
            Eq(true, family.Contains(11));
            Eq(true, family.Contains(12));
            Eq(true, family.Contains(20));
            Eq(true, family.Contains(21));
            Eq(false, family.Contains(30));
            Eq(false, family.Contains(31));
            Eq(false, family.Contains(41));
            Eq(false, family.Contains(42));

            Eq(false, GameMode.IsTrustedPresetProcessPath(
                "explorer", @"C:\Temp\explorer.exe"));
        }

        private static void TestWhitelistStorageSafety()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "PaviseWhitelist_" + Process.GetCurrentProcess().Id + "_"
                + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                string white = Path.Combine(root, "Pavise.whitelist.txt");
                string state = Path.Combine(root, "suppress.state");

                File.WriteAllText(white, "N|c3lzdGVt\r\n");
                var damaged = new GameMode(root, new SuppressionCore(state));
                List<string> fallback = damaged.GetWhitelist();
                Eq(true, fallback.Contains("system"));
                damaged.Stop();

                File.WriteAllText(white, WhitelistRule.Header + "\r\n"
                    + GameMode.BuildWhitelistFooter(new List<WhitelistRule>()) + "\r\n");
                var empty = new GameMode(root, new SuppressionCore(state));
                Eq(true, empty.GetWhitelist().Contains("system"));
                empty.Stop();

                File.WriteAllText(white, WhitelistRule.Header + "\r\n");
                var truncated = new GameMode(root, new SuppressionCore(state));
                Eq(true, truncated.GetWhitelist().Contains("system"));
                truncated.Stop();

                WhitelistRule one;
                Eq(true, WhitelistRule.TryCreate(
                    WhitelistRuleKind.LegacyName, "only-one", out one));
                File.WriteAllText(white, WhitelistRule.Header + "\r\n"
                    + one.Serialize() + "\r\n");
                var partial = new GameMode(root, new SuppressionCore(state));
                Eq(true, partial.GetWhitelist().Contains("system"));
                Eq(false, partial.GetWhitelist().Contains("only-one"));
                partial.Stop();

                File.Delete(white);
                var transactional = new GameMode(root, new SuppressionCore(state));
                int before = transactional.GetWhitelistRulesFast().Count;
                string candidate = Path.Combine(root, "NeverRunningProbe.exe");
                using (new FileStream(white, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Eq(false, transactional.AddWhitelistPath(candidate));
                    Eq(before, transactional.GetWhitelistRulesFast().Count);
                }
                Eq(true, transactional.AddWhitelistPath(candidate));
                Eq(before + 1, transactional.GetWhitelistRulesFast().Count);
                transactional.Stop();
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void TestWhitelistMutationSerialization()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "PaviseWhitelistLock_" + Process.GetCurrentProcess().Id + "_"
                + Guid.NewGuid().ToString("N"));
            GameMode mode = null;
            var held = new ManualResetEvent(false);
            var release = new ManualResetEvent(false);
            var attempting = new ManualResetEvent(false);
            var finished = new ManualResetEvent(false);
            Thread holder = null;
            Thread writer = null;
            try
            {
                Directory.CreateDirectory(root);
                mode = new GameMode(root, new SuppressionCore(
                    Path.Combine(root, "suppress.state")));
                FieldInfo field = typeof(GameMode).GetField(
                    "whiteEvalSync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null) throw new Exception(
                    "whiteEvalSync field is missing");
                object gate = field.GetValue(mode);

                holder = new Thread(new ThreadStart(delegate
                {
                    lock (gate)
                    {
                        held.Set();
                        release.WaitOne();
                    }
                }));
                holder.IsBackground = true;
                holder.Start();
                if (!held.WaitOne(3000)) throw new Exception(
                    "failed to hold whitelist evaluation gate");

                bool added = false;
                Exception writerError = null;
                writer = new Thread(new ThreadStart(delegate
                {
                    attempting.Set();
                    try
                    {
                        added = mode.AddWhitelistPath(
                            Path.Combine(root, "SerializedProbe.exe"));
                    }
                    catch (Exception error) { writerError = error; }
                    finally { finished.Set(); }
                }));
                writer.IsBackground = true;
                writer.Start();
                if (!attempting.WaitOne(3000)) throw new Exception(
                    "whitelist writer did not start");
                if (finished.WaitOne(250)) throw new Exception(
                    "whitelist mutation bypassed an in-flight policy snapshot");

                release.Set();
                if (!finished.WaitOne(5000)) throw new Exception(
                    "whitelist writer did not resume");
                if (writerError != null) throw writerError;
                Eq(true, added);
            }
            finally
            {
                release.Set();
                if (holder != null) try { holder.Join(3000); } catch { }
                if (writer != null) try { writer.Join(3000); } catch { }
                if (mode != null) mode.Stop();
                held.Dispose();
                release.Dispose();
                attempting.Dispose();
                finished.Dispose();
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void TestWhitelistFamilyEvents()
        {
            WhitelistRule rule;
            Eq(true, WhitelistRule.TryCreate(WhitelistRuleKind.ApplicationFamily,
                @"C:\Apps\Capture\Capture.exe", out rule));
            var members = new Dictionary<int, long>();

            Dictionary<int, long> admitted =
                GameMode.ApplyApplicationFamilyEvents(
                rule, members, new[]
            {
                new ProcessChange
                {
                    Pid = 10, Session = 1, Name = "Capture",
                    Path = @"C:\Apps\Capture\Capture.exe",
                    Creation = 1000, Sequence = 1, Kind = ProcessChangeKind.Started
                },
                new ProcessChange
                {

                    Pid = 11, ParentPid = 10, ParentCreation = 0,
                    Session = 1, Name = "CaptureWorker",
                    Path = @"C:\Apps\Capture\CaptureWorker.exe",
                    Creation = 1100, Sequence = 2, Kind = ProcessChangeKind.Started
                },
                new ProcessChange
                {
                    Pid = 10, Sequence = 3, Kind = ProcessChangeKind.Stopped
                }
            }, 1);

            Eq(true, members.ContainsKey(10));
            Eq(true, members.ContainsKey(11));
            Eq(true, admitted.ContainsKey(10));
            Eq(true, admitted.ContainsKey(11));
            Eq(1100L, admitted[11]);

            GameMode.ApplyApplicationFamilyEvents(rule, members, new[]
            {
                new ProcessChange
                {
                    Pid = 11, Sequence = 4, Kind = ProcessChangeKind.Stopped
                },
                new ProcessChange
                {
                    Pid = 11, Session = 1, Name = "Unrelated",
                    Path = @"C:\Other\Unrelated.exe",
                    Creation = 2100, Sequence = 5, Kind = ProcessChangeKind.Started
                }
            }, 1);
            Eq(false, members.ContainsKey(11));

            members[20] = 2000;
            GameMode.ApplyApplicationFamilyEvents(rule, members, new[]
            {
                new ProcessChange
                {
                    Pid = 21, ParentPid = 20, ParentCreation = 2999,
                    Session = 1, Name = "UnrelatedChild",
                    Path = @"C:\Other\UnrelatedChild.exe",
                    Creation = 3000, Sequence = 6, Kind = ProcessChangeKind.Started
                }
            }, 1);
            Eq(false, members.ContainsKey(21));

            GameMode.ApplyApplicationFamilyEvents(rule, members, new[]
            {
                new ProcessChange { Pid = 30, Sequence = 7, Kind = ProcessChangeKind.Stopped },
                new ProcessChange
                {
                    Pid = 30, Session = 1, Name = "Capture",
                    Path = @"C:\Apps\Capture\Capture.exe",
                    Creation = 3100, Sequence = 8, Kind = ProcessChangeKind.Started
                }
            }, 1);
            Eq(true, members.ContainsKey(30));
            GameMode.ApplyApplicationFamilyEvents(rule, members, new[]
            {
                new ProcessChange
                {
                    Pid = 31, Session = 1, Name = "Capture",
                    Path = @"C:\Apps\Capture\Capture.exe",
                    Creation = 3200, Sequence = 9, Kind = ProcessChangeKind.Started
                },
                new ProcessChange { Pid = 31, Sequence = 10, Kind = ProcessChangeKind.Stopped }
            }, 1);
            Eq(true, members.ContainsKey(31));

            members[40] = 4000;
            GameMode.ApplyApplicationFamilyEvents(
                rule, members, new[]
                {
                    new ProcessChange
                    {
                        Pid = 40, Sequence = 11,
                        Kind = ProcessChangeKind.Stopped
                    }
                }, 1, new Dictionary<int, long> { { 40, 0 } });
            Eq(false, members.ContainsKey(40));
            members[41] = 4100;
            GameMode.ApplyApplicationFamilyEvents(
                rule, members, new[]
                {
                    new ProcessChange
                    {
                        Pid = 41, Sequence = 12,
                        Kind = ProcessChangeKind.Stopped
                    }
                }, 1, new Dictionary<int, long> { { 41, 4100 } });
            Eq(true, members.ContainsKey(41));
            GameMode.ApplyApplicationFamilyEvents(
                rule, members, new[]
                {
                    new ProcessChange
                    {
                        Pid = 41, Sequence = 13,
                        Kind = ProcessChangeKind.Stopped
                    }
                }, 1, new Dictionary<int, long> { { 41, 4200 } });
            Eq(false, members.ContainsKey(41));
        }
    }
}

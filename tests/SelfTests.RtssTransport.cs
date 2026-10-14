using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int rtssAssertions;
        private static void TransportAssert(bool value, string name)
        { rtssAssertions++; if (!value) throw new InvalidOperationException("RTSS: " + name); }
        private sealed class RtssMapFixture : IDisposable
        {
            internal const int OsdOffset = 96, OsdSize = 8192, AppOffset = 32864, AppSize = 16384;
            internal readonly string Name = "Pavise.Rtss.Test." + Guid.NewGuid().ToString("N");
            internal MemoryMappedFile Map;
            internal MemoryMappedViewAccessor View;
            internal RtssMapFixture()
            {
                Map = MemoryMappedFile.CreateNew(Name, 65536);
                View = Map.CreateViewAccessor();
                View.Write(0, RtssMemorySession.Signature); View.Write(4, 0x00020015u);
                View.Write(8, AppSize); View.Write(12, AppOffset); View.Write(16, 1);
                View.Write(20, OsdSize); View.Write(24, OsdOffset); View.Write(28, 4);
                View.Write(64, 0); View.Write(68, 1234);
                View.Write(AppOffset, 1234); Text(AppOffset + 4, "fixture.exe");
                Fresh(); View.Write(AppOffset + 276, 120u); View.Write(AppOffset + 280, 8333u);
                View.Write(AppOffset + RtssMemorySession.NativeFramerateOffset, 1200u);
                Position(10);
            }
            internal void Fresh()
            { uint tick = unchecked((uint)Environment.TickCount); View.Write(AppOffset + 268, tick - 1000); View.Write(AppOffset + 272, tick); }
            internal void Position(int value)
            {
                for (int i = 0; i < 1024; i++) View.Write(AppOffset + RtssMemorySession.FrameBufferOffset + i * 4, 8333u);
                View.Write(AppOffset + RtssMemorySession.FrameBufferPositionOffset, value);
            }
            internal void Text(long offset, string text)
            { var bytes = Encoding.ASCII.GetBytes(text); View.WriteArray(offset, bytes, 0, bytes.Length); View.Write(offset + bytes.Length, (byte)0); }
            internal string Text(long offset, int size)
            { var bytes = new byte[size]; View.ReadArray(offset, bytes, 0, size); int end = Array.IndexOf(bytes, (byte)0); return Encoding.ASCII.GetString(bytes, 0, end < 0 ? size : end); }
            internal string OsdText(long offset, int size)
            {
                var bytes = new byte[size]; View.ReadArray(offset, bytes, 0, size);
                var text = new StringBuilder();
                for (int i = 0; i < bytes.Length && bytes[i] != 0; i++)
                {
                    if (bytes[i] == 0xC2 && i + 2 < bytes.Length && bytes[i + 1] == 0xB0 && bytes[i + 2] == (byte)'C')
                    { text.Append('\u00B0'); i++; }
                    else if (bytes[i] == 0xB0 && i + 1 < bytes.Length && bytes[i + 1] == (byte)'C') text.Append('\u00B0');
                    else text.Append(bytes[i] <= 127 ? (char)bytes[i] : '?');
                }
                return text.ToString();
            }
            public void Dispose() { View.Dispose(); Map.Dispose(); }
        }
        private static byte[] ReadRtssMapBytes(RtssMapFixture fixture)
        {
            var bytes = new byte[fixture.View.Capacity];
            fixture.View.ReadArray(0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static void AssertRtssPublicationRejected(RtssMapFixture fixture, Action publication, string context)
        {
            string before = Convert.ToBase64String(ReadRtssMapBytes(fixture));
            bool rejected = false;
            try { publication(); }
            catch (ArgumentException) { rejected = true; }
            TransportAssert(rejected, "Invalid native text is rejected before publication: " + context);
            TransportAssert(before == Convert.ToBase64String(ReadRtssMapBytes(fixture)),
                "Rejected publication never claims an owner, changes text/counter or takes the server busy bit: " + context);
        }

        private static void RtssCelsiusEncodingTests()
        {
            foreach (uint codePage in new uint[] { 1252, 936, 65001 })
            {
                byte[] expected = codePage == 65001
                    ? new byte[] { 56, 51, 32, 0xC2, 0xB0, 67 }
                    : new byte[] { 56, 51, 32, 0xB0, 67 };
                byte[] actual = RtssTextEncoding.Encode("83 \u00B0C", codePage);
                TransportAssert(Convert.ToBase64String(actual) == Convert.ToBase64String(expected)
                    && RtssTextEncoding.GetByteCount("83 \u00B0C", codePage) == expected.Length,
                    "Celsius uses the official ACP degree bytes, never '?' or GBK A1E3: " + codePage);
                string unsupported = "\u00B0F|\u2103|\u4E2D|\u00B0 C|\u00B0";
                byte[] localized = Encoding.GetEncoding((int)codePage).GetBytes(unsupported);
                TransportAssert(Convert.ToBase64String(RtssTextEncoding.Encode(unsupported, codePage)) == Convert.ToBase64String(localized)
                    && RtssTextEncoding.GetByteCount(unsupported, codePage) == localized.Length,
                    "Other Unicode text follows the native code page without disturbing the Celsius exception");
                foreach (string empty in new[] { (string)null, "" })
                    TransportAssert(RtssTextEncoding.Encode(empty, codePage).Length == 0
                        && RtssTextEncoding.GetByteCount(empty, codePage) == 0,
                        "Empty native text has no BOM, terminator or encoding overhead");
                string ascii = "<FNT=Bahnschrift,-24,700,1><C=FFC857>83<S>\b\r\n";
                TransportAssert(Convert.ToBase64String(RtssTextEncoding.Encode(ascii, codePage))
                    == Convert.ToBase64String(Encoding.ASCII.GetBytes(ascii))
                    && RtssTextEncoding.GetByteCount(ascii, codePage) == ascii.Length,
                    "ASCII hypertext, control bytes and line endings remain byte-identical across ACP choices");
                using (var fixture = new RtssMapFixture())
                using (var session = new RtssMemorySession(fixture.Name, "Pavise.Celsius.Test", codePage))
                {
                    long slot = RtssMapFixture.OsdOffset + RtssMapFixture.OsdSize, text = slot + 512;
                    fixture.Text(slot, "legacy predecessor");
                    fixture.View.Write(text + 4096, (byte)0xA5);
                    foreach (int busy in new[] { 0, 1 })
                    {
                        fixture.View.Write(36, busy);
                        string tooManyBytes = new string('X', codePage == 65001 ? 4093 : 4094) + "\u00B0C";
                        TransportAssert(RtssTextEncoding.GetByteCount(tooManyBytes, codePage) == 4096,
                            "Overflow fixture crosses byte capacity even when UTF8 character count is under 4096");
                        AssertRtssPublicationRejected(fixture, delegate { session.Publish(tooManyBytes); }, "OSD byte overflow / ACP=" + codePage + " / busy=" + busy);
                        AssertRtssPublicationRejected(fixture, delegate { session.Publish("83 \u00B0C\0hidden"); }, "OSD embedded NUL / ACP=" + codePage + " / busy=" + busy);
                    }
                    fixture.View.Write(36, 0);
                    TransportAssert(session.Publish("83 \u00B0C"), "Degree-bearing native publication claims its own slot");
                    var observed = new byte[expected.Length]; fixture.View.ReadArray(text, observed, 0, observed.Length);
                    TransportAssert(Convert.ToBase64String(observed) == Convert.ToBase64String(expected)
                        && fixture.View.ReadByte(text + observed.Length) == 0 && fixture.View.ReadByte(text + 4096) == 0xA5
                        && fixture.OsdText(text, 4096) == "83 \u00B0C" && fixture.Text(slot + 256, 256) == "Pavise.Celsius.Test" && fixture.View.ReadByte(slot) == 0,
                        "Celsius writes exact raw bytes and one trailing NUL while preserving the surrounding guard and owner");
                    uint frame = fixture.View.ReadUInt32(32);
                    TransportAssert(session.Publish("83 \u00B0C") && fixture.View.ReadUInt32(32) == frame,
                        "Unchanged Celsius publication keeps the native counter clean");
                    string boundary = new string('X', codePage == 65001 ? 4092 : 4093) + "\u00B0C";
                    TransportAssert(RtssTextEncoding.GetByteCount(boundary, codePage) == 4095 && session.Publish(boundary),
                        "4095 bytes including a final Celsius degree fit with the mandatory terminator");
                    var boundaryExpected = RtssTextEncoding.Encode(boundary, codePage);
                    var boundaryObserved = new byte[4095]; fixture.View.ReadArray(text, boundaryObserved, 0, boundaryObserved.Length);
                    TransportAssert(Convert.ToBase64String(boundaryExpected) == Convert.ToBase64String(boundaryObserved)
                        && fixture.View.ReadByte(text + 4095) == 0 && fixture.View.ReadByte(text + 4096) == 0xA5
                        && fixture.OsdText(text, 4096) == boundary,
                        "Boundary publication never truncates the UTF8 degree sequence or overruns the native slot");
                    AssertRtssPublicationRejected(fixture, delegate { session.Publish(boundary + "X"); }, "Claimed slot byte overflow / ACP=" + codePage);
                    TransportAssert(session.Clear() && fixture.View.ReadByte(text) == 0 && fixture.View.ReadByte(slot + 256) == 0,
                        "Celsius cleanup releases only the client text and ownership");
                }
                using (var fixture = new RtssMapFixture())
                using (var session = new RtssMemorySession(fixture.Name, new string('O', 255), codePage))
                {
                    TransportAssert(session.Publish("83 \u00B0C")
                        && fixture.Text(RtssMapFixture.OsdOffset + RtssMapFixture.OsdSize + 256, 256) == new string('O', 255),
                        "Maximum ASCII owner is 255 bytes with a terminator and can publish Celsius");
                }
                foreach (string invalidOwner in new[] { new string('O', 256), new string('O', 254) + "\u00B0C", "owner\0hidden" })
                using (var fixture = new RtssMapFixture())
                {
                    foreach (int busy in new[] { 0, 1 })
                    {
                        fixture.View.Write(36, busy);
                        AssertRtssPublicationRejected(fixture, delegate {
                            using (var session = new RtssMemorySession(fixture.Name, invalidOwner, codePage)) session.Publish("83 \u00B0C");
                        }, "Owner byte overflow or embedded NUL / ACP=" + codePage + " / busy=" + busy);
                    }
                }
            }
        }

        private static void RtssQuietStartupTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseRtssStartupChecks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string expected = Path.Combine(root, "RTSS.exe");
                string quietReason;
                TransportAssert(RtssOverlayClient.QuietStartInfo(expected, out quietReason) == null && quietReason.Length > 0,
                    "An installation missing both config and official template is never launched blindly");
                string templates = Path.Combine(root, "ProfileTemplates"); Directory.CreateDirectory(templates);
                string template = Path.Combine(templates, "Config");
                string defaults = "[Settings]\r\nFirstRun=0\r\nStartMinimized=0\r\nStartWithWindows=0\r\n[Other]\r\nKeep=template\r\n";
                File.WriteAllText(template, defaults, Encoding.Default);
                string profile = Path.Combine(root, "Profiles");
                string config = Path.Combine(profile, "Config");
                TransportAssert(RtssOverlayClient.QuietStartInfo(expected, out quietReason) == null && !Directory.Exists(profile),
                    "A missing executable cannot prepare or approve a background launch");
                File.WriteAllBytes(expected, new byte[0]);
                TransportAssert(RtssStartup.PrepareBackgroundConfiguration(expected, out quietReason)
                    && quietReason.Length == 0 && File.ReadAllText(config).Contains("StartMinimized=1")
                    && File.ReadAllText(config).Contains("StartWithWindows=0"),
                    "Component setup prepares minimized startup without enabling RTSS autostart or launching the executable");
                TransportAssert(RtssOverlayClient.QuietStartInfo(expected, out quietReason) != null && quietReason == ""
                    && File.ReadAllText(config).Contains("FirstRun=0") && File.ReadAllText(config).Contains("Keep=template")
                    && File.ReadAllText(template) == defaults,
                    "First startup initializes official defaults and preserves the source template");
                foreach (string content in new[] { "[Settings]\r\nStartMinimized=0", "[Settings]\r\nStartMinimized=2",
                    "[Other]\r\nStartMinimized=1", "[Settings]\r\nStartMinimized=bad", "[Settings]\r\nStartWithWindows=1" })
                {
                    File.WriteAllText(config, content, Encoding.Default);
                    TransportAssert(RtssOverlayClient.QuietStartInfo(expected, out quietReason) != null && quietReason.Length == 0
                        && File.ReadAllText(config).Contains("StartMinimized=1"),
                        "Startup enables the minimized setting without user setup: " + content);
                    if (content.Contains("StartWithWindows=1"))
                        TransportAssert(File.ReadAllText(config).Contains("StartWithWindows=1"), "Autostart preference is not changed");
                }
                File.WriteAllText(config, "[Other]\r\nStartMinimized=0\r\n[Settings]\r\nStartWithWindows=0\r\nStartMinimized=1\r\n");
                byte[] unchanged = File.ReadAllBytes(config);
                ProcessStartInfo quietStart = RtssOverlayClient.QuietStartInfo(expected, out quietReason);
                TransportAssert(quietStart != null && quietReason == "" && quietStart.FileName == expected
                    && quietStart.Arguments == "" && !quietStart.UseShellExecute && quietStart.CreateNoWindow
                    && quietStart.WindowStyle == ProcessWindowStyle.Hidden && !quietStart.ErrorDialog
                    && String.IsNullOrEmpty(quietStart.Verb) && quietStart.WorkingDirectory == root,
                    "Minimized launch has no /s, visible shell, error dialog or runas and ignores StartWithWindows");
                TransportAssert(Convert.ToBase64String(unchanged) == Convert.ToBase64String(File.ReadAllBytes(config)),
                    "An already minimized configuration is not rewritten");
                foreach (Encoding encoding in new[] { Encoding.Default, Encoding.Unicode })
                {
                    string source = "; keep comment\r\n[Other]\r\nLabel=閰嶇疆銉绘祴璇昞r\nStartMinimized=0\r\n[Settings]\r\nStartMinimized=0\r\nStartWithWindows=1\r\nPlugin=淇濈暀\r\n";
                    File.WriteAllText(config, source, encoding);
                    source = File.ReadAllText(config, encoding);
                    TransportAssert(RtssOverlayClient.QuietStartInfo(expected, out quietReason) != null,
                        "Native INI update supports original encoding " + encoding.WebName);
                    string changed = File.ReadAllText(config, encoding);
                    TransportAssert(changed == source.Replace("[Settings]\r\nStartMinimized=0", "[Settings]\r\nStartMinimized=1"),
                        "Only Settings.StartMinimized changes; comments, other sections, Unicode and startup preference remain intact: " + encoding.WebName);
                    if (encoding == Encoding.Unicode)
                    {
                        byte[] bytes = File.ReadAllBytes(config);
                        TransportAssert(bytes[0] == 255 && bytes[1] == 254, "UTF-16 config retains its byte-order mark");
                    }
                }
                File.WriteAllText(config, "[Settings]\r\nStartMinimized=0\r\n", Encoding.Default);
                byte[] beforeFailure = File.ReadAllBytes(config);
                TransportAssert(RtssComponent.TryRunMaintenance(delegate {
                    string busyReason;
                    return !RtssStartup.PrepareBackgroundConfiguration(expected, out busyReason) && busyReason.Length > 0;
                }) && Convert.ToBase64String(beforeFailure) == Convert.ToBase64String(File.ReadAllBytes(config)),
                    "Startup preparation never writes settings while another component operation owns its gate");
                using (var blocked = new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.Read))
                    TransportAssert(RtssOverlayClient.QuietStartInfo(expected, out quietReason) == null && quietReason.Length > 0,
                        "A denied settings write prevents launching a potentially visible RTSS");
                TransportAssert(Convert.ToBase64String(beforeFailure) == Convert.ToBase64String(File.ReadAllBytes(config)),
                    "Failed settings preparation preserves the original configuration");
                File.WriteAllText(config, "[Settings]\r\nStartMinimized=1\r\n", Encoding.Default);
                RtssStartupRetryTests(expected, root);
                RtssOwnedSessionTests(expected);
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static void RtssStartupRetryTests(string executable, string root)
        {
            long now = 1000; int state = 0, launches = 0, probes = 0;
            bool failLaunch = true;
            var startup = new RtssStartup(delegate { return now; }, delegate(string path) { probes++; return state; },
                delegate(ProcessStartInfo info) { launches++; if (failLaunch) throw new InvalidOperationException("fixture launch failure"); });
            string reason;
            TransportAssert(!startup.EnsureRunning(executable, out reason) && launches == 1 && reason.Length > 0,
                "An absent server attempts background startup even if old shared memory may exist");
            long failureAt = now;
            int failureProbes = probes;
            for (int index = 0; index < 4; index++)
            {
                now += 200; startup.ForgetLiveServer();
                TransportAssert(!startup.EnsureRunning(executable, out reason) && probes == failureProbes && launches == 1,
                    "Fast readiness polling after a launch failure neither enumerates processes nor retries CreateProcess");
            }
            startup.ForgetLiveServer();
            now = failureAt + 29999;
            TransportAssert(!startup.EnsureRunning(executable, out reason) && launches == 1,
                "Closing and quickly reopening monitoring cannot bypass the 30-second failure backoff");
            now++; failLaunch = false;
            TransportAssert(!startup.EnsureRunning(executable, out reason) && launches == 2 && reason == RtssStartup.StartingStatus,
                "A failed launch is retried at 30 seconds and does not permanently disable automatic startup");
            state = 1;
            TransportAssert(startup.EnsureRunning(executable, out reason) && launches == 2,
                "The next tick accepts a newly running server without waiting out startup cooldown");
            int steadyProbes = probes;
            now += 4999;
            TransportAssert(startup.EnsureRunning(executable, out reason) && probes == steadyProbes,
                "Steady monitoring caches positive process identity for five seconds");
            now++; state = 0;
            TransportAssert(!startup.EnsureRunning(executable, out reason) && probes == steadyProbes + 1 && launches == 2,
                "Server exit invalidates readiness even when an old mapping remains open");
            now += 25000;
            TransportAssert(!startup.EnsureRunning(executable, out reason) && launches == 3,
                "A stopped server is restarted after the independent retry deadline");
            state = -1; now += 30000;
            TransportAssert(!startup.EnsureRunning(executable, out reason) && launches == 3 && reason.Length > 0,
                "Unverifiable process identity never starts a duplicate server");

            int preparationLaunches = 0;
            var missing = new RtssStartup(delegate { return now; }, delegate { return 0; }, delegate { preparationLaunches++; });
            string missingRoot = Path.Combine(root, "missing"), missingExe = Path.Combine(missingRoot, "RTSS.exe");
            TransportAssert(!missing.EnsureRunning(missingExe, out reason) && preparationLaunches == 0 && reason.Length > 0,
                "Settings preparation failure does not reach Process.Start");
            Directory.CreateDirectory(Path.Combine(missingRoot, "ProfileTemplates"));
            File.WriteAllBytes(missingExe, new byte[0]);
            File.WriteAllText(Path.Combine(missingRoot, "ProfileTemplates", "Config"), "[Settings]\r\nStartMinimized=1\r\n");
            missing.ForgetLiveServer(); now += 29999;
            TransportAssert(!missing.EnsureRunning(missingExe, out reason) && preparationLaunches == 0,
                "Repairing settings plus rapid toggles still respects the existing cooldown");
            now++;
            TransportAssert(!missing.EnsureRunning(missingExe, out reason) && preparationLaunches == 1,
                "A repaired configuration automatically starts on the next retry deadline");

            int raceProbes = 0, raceLaunches = 0;
            var race = new RtssStartup(delegate { return now; }, delegate { return ++raceProbes == 1 ? 0 : 1; }, delegate { raceLaunches++; });
            TransportAssert(race.EnsureRunning(executable, out reason) && raceLaunches == 0,
                "A server appearing during settings preparation is reused without a duplicate launch");
            string existingConfig = Path.Combine(root, "Profiles", "Config");
            File.WriteAllText(existingConfig, "[Settings]\r\nStartMinimized=0\r\nStartWithWindows=1\r\n", Encoding.Default);
            byte[] existingBytes = File.ReadAllBytes(existingConfig);
            int borrowedLaunches = 0;
            var borrowed = new RtssStartup(delegate { return now; }, delegate(string path) {
                TransportAssert(path == executable, "Running-server checks use the detected installation's full path");
                return 1;
            }, delegate { borrowedLaunches++; });
            TransportAssert(borrowed.EnsureRunning(executable, out reason) && borrowedLaunches == 0
                && Convert.ToBase64String(existingBytes) == Convert.ToBase64String(File.ReadAllBytes(existingConfig)),
                "Borrowing a running RTSS neither launches it nor changes its persisted startup settings");
            var field = typeof(RtssOverlayClient).GetField("startup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            object shared = field.GetValue(null);
            RtssOverlayClient.ComponentChanged(); RtssOverlayClient.Close();
            TransportAssert(Object.ReferenceEquals(shared, field.GetValue(null)),
                "Connection and component resets retain the independent startup retry clock");
        }
        private sealed class RtssFakeOwnedProcess : IRtssOwnedProcess
        {
            internal bool Exited, Disposed;
            internal int StopRequests;
            public bool HasExited { get { return Exited; } }
            public void RequestStop() { StopRequests++; }
            public void Dispose() { Disposed = true; }
        }

        private static void RtssOwnedSessionTests(string executable)
        {
            long now = 1000; int state = 0, launches = 0, probes = 0;
            var first = new RtssFakeOwnedProcess();
            var second = new RtssFakeOwnedProcess();
            Func<ProcessStartInfo, IRtssOwnedProcess> launch = delegate { launches++; return launches == 1 ? first : second; };
            var startup = new RtssStartup(delegate { return now; }, delegate { probes++; return state; }, launch);
            string reason;
            startup.StopOwned();
            TransportAssert(launches == 0, "Idle shutdown cannot launch RTSS");
            startup.EnsureRunning(executable, out reason); state = 1;
            TransportAssert(startup.EnsureRunning(executable, out reason) && launches == 1, "Owned server becomes ready");
            int readyProbes = probes;
            for (int index = 0; index < 40; index++)
            {
                startup.ForgetLiveServer();
                TransportAssert(startup.EnsureRunning(executable, out reason), "Retained process is live while the RTSS map initializes");
            }
            TransportAssert(probes == readyProbes, "Startup map checks reuse the retained handle without process enumeration");
            startup.ForgetLiveServer();
            TransportAssert(first.StopRequests == 0, "A mapping reconnect does not terminate the game server");
            startup.StopOwned();
            TransportAssert(first.StopRequests == 1 && !first.Disposed, "Session end requests exit only on the launched instance");
            TransportAssert(!startup.EnsureRunning(executable, out reason) && launches == 1,
                "An exiting owned server is never borrowed or relaunched during rapid re-entry");
            first.Exited = true; state = 0;
            startup.EnsureRunning(executable, out reason);
            TransportAssert(first.Disposed && launches == 2 && now == 1000,
                "Confirmed session shutdown releases the handle and allows the next game without a 30-second wait");
            second.Exited = true; state = 1;
            TransportAssert(startup.EnsureRunning(executable, out reason), "A replacement external server may be borrowed");
            startup.StopOwned();
            TransportAssert(second.Disposed && second.StopRequests == 0 && launches == 2,
                "The exited owned lease cannot shut down a replacement server, even if its PID/name was reused");

            int borrowedLaunches = 0;
            var borrowed = new RtssStartup(delegate { return now; }, delegate { return 1; }, delegate { borrowedLaunches++; });
            borrowed.EnsureRunning(executable, out reason); borrowed.StopOwned();
            TransportAssert(borrowedLaunches == 0 && first.StopRequests > 0 && second.StopRequests == 0,
                "A pre-existing RTSS instance is never adopted for shutdown");
        }

#if PAVISE_MONITORING_RUNNER
        private static void RtssOwnedNativeProcessTests()
        {
            var info = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--rtss-owned-process-fixture") {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            };
            using (Process borrowedFixture = Process.Start(info))
            {
                var profile = new GameProfile { Id = "native-fixture", ExecutablePath = info.FileName };
                var entry = new ProcEntry { Pid = borrowedFixture.Id, Creation = borrowedFixture.StartTime.ToUniversalTime().ToFileTimeUtc(),
                    Session = borrowedFixture.SessionId, Name = Path.GetFileNameWithoutExtension(info.FileName), Path = info.FileName };
                var preparation = new MonitoringGamePreparation();
                TransportAssert(preparation.Observe(profile, entry, entry.Session)
                    && preparation.IsPending(delegate { return profile; }), "Native launch validation accepts the exact live fixture identity");
                var stale = new MonitoringGamePreparation(); entry.Creation++;
                TransportAssert(stale.Observe(profile, entry, entry.Session)
                    && !stale.IsPending(delegate { return profile; }), "A real live PID with the wrong birth cannot warm RTSS");
                IRtssOwnedProcess ownedFixture = RtssOwnedProcess.Start(info);
                try
                {
                    var clock = Stopwatch.StartNew();
                    ownedFixture.RequestStop(); ownedFixture.RequestStop();
                    TransportAssert(clock.ElapsedMilliseconds < 1000, "Owned RTSS stop returns without waiting on the UI thread");
                    while (!ownedFixture.HasExited && clock.ElapsedMilliseconds < 8000) System.Threading.Thread.Sleep(25);
                    TransportAssert(ownedFixture.HasExited && !borrowedFixture.HasExited,
                        "Bounded native shutdown terminates only the retained owned handle, preserving a same-executable sibling");
                }
                finally
                {
                    ownedFixture.Dispose();
                    if (!borrowedFixture.HasExited) { borrowedFixture.Kill(); borrowedFixture.WaitForExit(2000); }
                }
            }
        }
#endif

        private static void RtssGuardedRendererTests()
        {
            Func<int> oldPid = RtssOverlayClient.PreferredPid;
            Func<int, long> oldBirth = RtssOverlayClient.PreferredBirth;
            int pid = 1234; long creation = 99;
            try
            {
                RtssOverlayClient.PreferredPid = delegate { return pid; };
                RtssOverlayClient.PreferredBirth = delegate(int candidate) { return candidate == pid ? creation : 0; };
                int selected; long birth;
                TransportAssert(RtssOverlayClient.TryGetGuardedRenderer(out selected, out birth) && selected == pid && birth == creation,
                    "Only the active guarded PID and process birth are admitted");
                TransportAssert(RtssOverlayClient.MatchesGuardedRenderer(1234, 99)
                    && !RtssOverlayClient.MatchesGuardedRenderer(4321, 99), "Client snapshots cannot publish for the guarded game");
                creation = 100;
                TransportAssert(!RtssOverlayClient.MatchesGuardedRenderer(1234, 99)
                    && RtssOverlayClient.MatchesGuardedRenderer(1234, 100), "PID reuse cannot republish an old snapshot");
                pid = 0;
                TransportAssert(!RtssOverlayClient.TryGetGuardedRenderer(out selected, out birth)
                    && !RtssOverlayClient.MatchesGuardedRenderer(1234, 100), "Leaving the session rejects existing snapshots");
                RtssFrameSample sample;
                TransportAssert(!RtssOverlayClient.TryReadTarget(out sample) && sample == null,
                    "No guarded target returns without opening RTSS or consulting the desktop foreground");
                pid = 1234; creation = 0;
                TransportAssert(!RtssOverlayClient.TryGetGuardedRenderer(out selected, out birth), "An unverifiable guarded birth is rejected");
                creation = 100; RtssOverlayClient.PreferredBirth = null;
                TransportAssert(!RtssOverlayClient.TryGetGuardedRenderer(out selected, out birth), "Missing guard identity cannot enable monitoring");
                RtssOverlayClient.PreferredBirth = delegate { return 100; }; RtssOverlayClient.PreferredPid = null;
                TransportAssert(!RtssOverlayClient.TryGetGuardedRenderer(out selected, out birth), "Missing guard provider never admits a foreground process");
                int pidReads = 0;
                RtssOverlayClient.PreferredPid = delegate { return ++pidReads == 1 ? 1234 : 4321; };
                TransportAssert(!RtssOverlayClient.TryGetGuardedRenderer(out selected, out birth), "A handoff between callbacks fails closed");
                int birthReads = 0;
                RtssOverlayClient.PreferredPid = delegate { return 1234; };
                RtssOverlayClient.PreferredBirth = delegate { return ++birthReads == 1 ? 99 : 100; };
                TransportAssert(!RtssOverlayClient.TryGetGuardedRenderer(out selected, out birth), "A process replacement between callbacks fails closed");
            }
            finally { RtssOverlayClient.PreferredPid = oldPid; RtssOverlayClient.PreferredBirth = oldBirth; }
        }
        private static void RtssResolutionTests()
        {
            using (var fixture = new RtssMapFixture())
            using (var session = new RtssMemorySession(fixture.Name, "Pavise.Test.Resolution"))
            {
                RtssFrameSample sample;
                long app = RtssMapFixture.AppOffset;
                foreach (int[] size in new[] { new[] { 3840, 2160 }, new[] { 1920, 1080 }, new[] { 3840, 2160 } })
                {
                    fixture.View.Write(app + RtssMemorySession.ResolutionXOffset, size[0]);
                    fixture.View.Write(app + RtssMemorySession.ResolutionYOffset, size[1]);
                    fixture.Fresh();
                    TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample)
                        && sample.ViewportWidth == size[0] && sample.ViewportHeight == size[1],
                        "RTSS renderer resolution follows display-mode changes");
                }
                foreach (int[] size in new[] { new[] { 0, 1080 }, new[] { 1920, 0 }, new[] { 16385, 1080 }, new[] { 1920, -1 } })
                {
                    fixture.View.Write(app + RtssMemorySession.ResolutionXOffset, size[0]);
                    fixture.View.Write(app + RtssMemorySession.ResolutionYOffset, size[1]);
                    TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample)
                        && sample.ViewportWidth == 0 && sample.ViewportHeight == 0,
                        "Invalid renderer dimensions are not retained from a previous frame");
                }
                fixture.View.Write(app + RtssMemorySession.ResolutionXOffset, 1920);
                fixture.View.Write(app + RtssMemorySession.ResolutionYOffset, 1080);
                fixture.View.Write(4, 0x00020013u);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample)
                    && sample.ViewportWidth == 0 && sample.ViewportHeight == 0,
                    "Shared-memory versions before v2.20 do not expose resolution fields");
                fixture.View.Write(4, 0x00020014u);
                fixture.View.Write(8, RtssMemorySession.ResolutionYOffset);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample)
                    && sample.ViewportWidth == 0 && sample.ViewportHeight == 0,
                    "Truncated application stride cannot read a partial resolution pair");
            }
        }

        private static void RtssConnectionRecoveryTests()
        {
            Func<int> savedPid = RtssOverlayClient.PreferredPid;
            Func<int, long> savedBirth = RtssOverlayClient.PreferredBirth;
            Func<bool> savedActive = RtssOverlayClient.SessionActive;
            try
            {
                RtssOverlayClient.Close();
                using (var fixture = new RtssMapFixture())
                {
                    int opens = 0, foreground = 1234, probes = 0;
                    long clock = 100000;
                    long actualBirth = 99;
                    RtssOverlayClient.StartupForTest = new RtssStartup(delegate { return clock; },
                        delegate { probes++; return 1; }, new Action<ProcessStartInfo>(delegate { throw new InvalidOperationException("Borrowed server must not launch"); }));
                    RtssOverlayClient.PreferredPid = delegate { return 1234; };
                    RtssOverlayClient.PreferredBirth = delegate { return 99; };
                    RtssOverlayClient.SessionActive = delegate { return true; };
                    RtssOverlayClient.IdentityForTest = delegate { return actualBirth; };
                    RtssOverlayClient.ForegroundViewportForTest = delegate { return new[] { foreground, 3840, 2160 }; };
                    RtssOverlayClient.SessionFactoryForTest = delegate {
                        opens++; return new RtssMemorySession(fixture.Name, "Pavise.Test.ClientRecovery");
                    };
                    RtssFrameSample sample;
                    TransportAssert(RtssOverlayClient.TryReadTarget(out sample) && opens == 1, "Mock renderer connection opens once");
                    fixture.View.Write(0, 0xDEADu);
                    TransportAssert(!RtssOverlayClient.TryReadTarget(out sample)
                        && RtssOverlayClient.Status.Contains("reconnecting"), "A dead RTSS mapping is released with an explicit reconnect state");
                    for (int retry = 0; retry < 10; retry++)
                    {
                        clock += 200;
                        TransportAssert(!RtssOverlayClient.TryReadTarget(out sample) && probes == 1,
                            "Invalid-map retries keep the five-second server-liveness cache");
                    }
                    int brokenOpens = opens;
                    fixture.View.Write(0, RtssMemorySession.Signature); fixture.Fresh();
                    TransportAssert(RtssOverlayClient.TryReadTarget(out sample) && opens == brokenOpens + 1 && probes == 1,
                        "The next bounded sample reconnects after the server replaces its mapping");
                    int healthyOpens = opens;
                    clock += 3000;
                    TransportAssert(RtssOverlayClient.TryReadTarget(out sample) && probes == 2 && opens == healthyOpens,
                        "A healthy connection still performs its normal five-second liveness check");
                    fixture.View.Write(RtssMapFixture.AppOffset, 5678);
                    TransportAssert(!RtssOverlayClient.TryReadTarget(out sample)
                        && RtssOverlayClient.Status.Contains("not connected"), "Renderer missing is distinguished from disabled monitoring");
                    fixture.View.Write(RtssMapFixture.AppOffset, 1234); fixture.Fresh();
                    TransportAssert(RtssOverlayClient.TryReadTarget(out sample) && opens == healthyOpens, "Late renderer attachment reuses a healthy mapping");
                    fixture.View.Write(RtssMapFixture.AppOffset + 272, unchecked((uint)Environment.TickCount - 5000));
                    TransportAssert(!RtssOverlayClient.TryReadTarget(out sample)
                        && RtssOverlayClient.Status.Contains("current frame"), "Stale frames report a data wait without reconnect churn");
                    fixture.Fresh(); actualBirth = 100;
                    TransportAssert(!RtssOverlayClient.TryReadTarget(out sample)
                        && RtssOverlayClient.Status.Contains("identity"), "PID reuse still fails closed");
                    actualBirth = 99; foreground = 5678;
                    TransportAssert(!RtssOverlayClient.TryReadTarget(out sample)
                        && RtssOverlayClient.Status.Contains("not foreground"), "Foreground loss explains why the overlay is cleared");
                    foreground = 1234;
                    TransportAssert(RtssOverlayClient.TryReadTarget(out sample), "Returning to the guarded renderer resumes frames");
                    var snapshot = new MonitoringSnapshot(1234, "fixture.exe", DateTime.UtcNow, "monitoring", fps: 120,
                        creation: 99, viewportWidth: 1920, viewportHeight: 1080);
                    for (int slot = 1; slot < 4; slot++)
                        fixture.Text(RtssMapFixture.OsdOffset + slot * RtssMapFixture.OsdSize + 256, "Other." + slot);
                    RtssOverlayClient.Publish(snapshot, MonitoringLayout.TopLeft);
                    TransportAssert(RtssOverlayClient.Status.Contains("slots are full"), "Exhausted OSD slots are not reported as successful monitoring");
                    fixture.Text(RtssMapFixture.OsdOffset + RtssMapFixture.OsdSize + 256, "");
                    fixture.View.Write(36, 1);
                    RtssOverlayClient.Publish(snapshot, MonitoringLayout.TopLeft);
                    TransportAssert(RtssOverlayClient.Status.Contains("busy"), "A shared-memory writer reports a bounded retry");
                    fixture.View.Write(36, 0);
                    RtssOverlayClient.Publish(snapshot, MonitoringLayout.TopLeft);
                    TransportAssert(RtssOverlayClient.Status.Length == 0 && opens == healthyOpens, "Publication recovers automatically without restarting RTSS");
                    TransportAssert(fixture.Text(RtssMapFixture.OsdOffset + 2 * RtssMapFixture.OsdSize + 256, 256) == "Other.2",
                        "Recovery does not take another OSD client's slot");
                    RtssOverlayClient.Close();
                }
                TransportAssert(RtssOverlayClient.ComponentUnavailableText(new RtssComponentInfo()).Contains("not found"),
                    "Missing component reports an install/detect cause");
            }
            finally
            {
                RtssOverlayClient.Close();
                RtssOverlayClient.StartupForTest = null;
                RtssOverlayClient.SessionFactoryForTest = null; RtssOverlayClient.ForegroundViewportForTest = null;
                RtssOverlayClient.IdentityForTest = null; RtssOverlayClient.PreferredPid = savedPid;
                RtssOverlayClient.PreferredBirth = savedBirth; RtssOverlayClient.SessionActive = savedActive;
            }
        }

        internal static void RunRtssTransportTests()
        {
            int savedLanguage = Lang.Cur;
            Lang.Cur = 1;
            Settings.UseTransientStoreForCurrentProcess(); MonitoringPreferences.TrySetMetrics(MonitoringMetrics.All);
            rtssAssertions = 0;
            RtssQuietStartupTests();
#if PAVISE_MONITORING_RUNNER
            RtssOwnedNativeProcessTests();
#endif
            RtssGuardedRendererTests();
            RtssCelsiusEncodingTests();
            RtssExportedStatisticsTests();
            RtssNativeTimingTests();
            RtssGpuTimingTests();
            RtssProcessMemoryTests();
            RtssResolutionTests();
            RtssConnectionRecoveryTests();
            using (var fixture = new RtssMapFixture())
            using (var first = new RtssMemorySession(fixture.Name, "Pavise.Test.First"))
            using (var second = new RtssMemorySession(fixture.Name, "Pavise.Test.Second"))
            {
                fixture.Text(RtssMapFixture.OsdOffset + 256, "ReservedPrimary");
                fixture.Text(RtssMapFixture.OsdOffset + 512, "PrimaryText");
                fixture.Text(RtssMapFixture.OsdOffset + 3 * RtssMapFixture.OsdSize + 256, "AnotherClient");
                fixture.Text(RtssMapFixture.OsdOffset + 3 * RtssMapFixture.OsdSize + 512, "KeepOtherText");
                TransportAssert(first.Publish("<P0>PAVISE"), "claim empty secondary slot");
                TransportAssert(second.Publish("<P2>SECOND"), "independent second owner");
                TransportAssert(fixture.Text(RtssMapFixture.OsdOffset + 512, 4096) == "PrimaryText", "primary untouched");
                TransportAssert(fixture.Text(RtssMapFixture.OsdOffset + 3 * RtssMapFixture.OsdSize + 512, 4096) == "KeepOtherText", "other client untouched");
                uint frame = fixture.View.ReadUInt32(32);
                TransportAssert(first.Publish("<P0>PAVISE") && fixture.View.ReadUInt32(32) == frame, "unchanged update does not dirty global frame");
                fixture.View.Write(36, 1);
                TransportAssert(!first.Publish("BUSY") && !first.Clear(), "busy lock not stolen");
                TransportAssert(fixture.View.ReadInt32(36) == 1, "other owner busy bit preserved");
                fixture.View.Write(36, 0);
                TransportAssert(first.Clear(), "owner cleanup succeeds");
                TransportAssert(fixture.Text(RtssMapFixture.OsdOffset + 2 * RtssMapFixture.OsdSize + 512, 4096) == "<P2>SECOND", "clear preserves another Pavise client");
                TransportAssert(fixture.View.ReadInt32(36) == 0, "always release acquired busy bit");
                RtssFrameSample sample;
                TransportAssert(first.TryRead(1234, delegate { return 7654321; }, out sample), "read preferred renderer");
                TransportAssert(sample.Pid == 1234 && sample.Creation == 7654321 && sample.Name == "fixture.exe", "PID and birth bound");
                TransportAssert(Math.Abs(sample.Fps.Value - 120) < .01 && Math.Abs(sample.FrameTimeMs.Value - 8.333) < .001, "SDK FPS and microsecond units");
                TransportAssert(sample.FrameTimes != null && sample.FrameTimes.Length == 0, "first sample avoids old frame history");
                fixture.Position(370); fixture.Fresh();
                TransportAssert(first.TryRead(1234, delegate { return 7654321; }, out sample) && sample.FrameTimes.Length == 360, "new frame intervals only");
                TransportAssert(sample.FrameSequence == 360 && Math.Abs(sample.FrameTimes[0] - 8.333) < .001, "monotonic sequence and real intervals");
                fixture.Fresh();
                TransportAssert(first.TryRead(1234, delegate { return 7654321; }, out sample) && sample.FrameTimes.Length == 0, "same ring position deduplicated");
                fixture.Position(370 + 2048); fixture.Fresh();
                TransportAssert(first.TryRead(1234, delegate { return 7654321; }, out sample) && sample.FrameTimes == null, "overrun invalidates continuous frame window");
                TransportAssert(!first.TryRead(1234, delegate { return 0; }, out sample), "unverifiable identity rejected");
                fixture.View.Write(RtssMapFixture.AppOffset + 272, unchecked((uint)Environment.TickCount - 5000));
                TransportAssert(!first.TryRead(1234, delegate { return 7654321; }, out sample), "stale renderer rejected");
                fixture.Fresh();
                int identityCalls = 0;
                Func<int, long> noFallbackIdentity = delegate { identityCalls++; return 7654321; };
                TransportAssert(!first.TryRead(0, noFallbackIdentity, out sample) && sample == null && identityCalls == 0,
                    "No guarded renderer cannot fall back to a valid RTSS foreground entry");
                TransportAssert(!first.TryRead(5678, noFallbackIdentity, out sample) && sample == null && identityCalls == 0,
                    "A missing guarded PID cannot fall back to another live foreground renderer");
                fixture.View.Write(RtssMapFixture.AppOffset, 4321); fixture.View.Write(68, 4321);
                fixture.Text(RtssMapFixture.AppOffset + 4, "LeagueClientUx.exe"); fixture.Fresh();
                TransportAssert(!first.TryRead(1234, noFallbackIdentity, out sample) && sample == null && identityCalls == 0,
                    "The client cannot substitute for the departed game renderer");
                TransportAssert(!first.TryRead(0, noFallbackIdentity, out sample) && sample == null && identityCalls == 0,
                    "Leaving the guarded session cannot select the League client");
                fixture.View.Write(RtssMapFixture.AppOffset, 1234); fixture.View.Write(68, 1234);
                fixture.Text(RtssMapFixture.AppOffset + 4, "fixture.exe");
                fixture.Position(20);
                TransportAssert(first.TryRead(1234, delegate { return 7654321; }, out sample)
                    && sample.FrameTimes.Length == 0, "A new admitted session never reuses interrupted frame history");
                TransportAssert(!first.TryRead(1234, delegate { fixture.View.Write(RtssMapFixture.AppOffset, 9999); return 7654321; }, out sample), "entry replacement during read rejected");
                fixture.View.Write(RtssMapFixture.AppOffset, 1234);
                uint[] versions = { 0x00010000, 0x0002000d, 0x00030000 };
                foreach (uint version in versions)
                { fixture.View.Write(4, version); TransportAssert(!first.Publish("unsupported"), "unsupported schema rejected"); }
                fixture.View.Write(4, 0x00020015u);
                int[] badOffsets = { -1, Int32.MaxValue, 4, 32865 };
                foreach (int value in badOffsets)
                { fixture.View.Write(12, value); TransportAssert(!first.Publish("invalid"), "bad range rejected"); }
                fixture.View.Write(12, RtssMapFixture.AppOffset);
                fixture.View.Write(16, Int32.MaxValue);
                TransportAssert(!first.Publish("overflow"), "array multiplication overflow rejected"); fixture.View.Write(16, 1);
                fixture.View.Write(0, 0xDEADu);
                TransportAssert(!first.Publish("dead"), "deallocated mapping rejected"); fixture.View.Write(0, RtssMemorySession.Signature);
                TransportAssert(first.Publish("cleanup retry"), "reacquire released slot");
                fixture.View.Write(36, 1);
                var release = new Thread(delegate() { Thread.Sleep(30); fixture.View.Write(36, 0); });
                release.Start(); first.Dispose(); release.Join();
                TransportAssert(fixture.Text(RtssMapFixture.OsdOffset + RtssMapFixture.OsdSize + 256, 256) == "", "dispose waits for render contention before cleanup");
            }
            const int extendedStride = 299520, extendedApps = 96 + extendedStride * 2;
            string extendedName = "Pavise.Rtss.Extended.Test." + Guid.NewGuid().ToString("N");
            using (var extendedMap = MemoryMappedFile.CreateNew(extendedName, extendedApps + 16384))
            using (var extendedView = extendedMap.CreateViewAccessor())
            {
                extendedView.Write(0, RtssMemorySession.Signature); extendedView.Write(4, 0x00020015u);
                extendedView.Write(8, 16384); extendedView.Write(12, extendedApps); extendedView.Write(16, 1);
                extendedView.Write(20, extendedStride); extendedView.Write(24, 96); extendedView.Write(28, 2);
                long text2 = 96 + extendedStride + RtssMemorySession.ExtendedText2Offset;
                extendedView.Write(text2, (byte)'S'); extendedView.Write(text2 + 1, (byte)0);
                using (var client = new RtssMemorySession(extendedName, "Pavise.Test.Extended"))
                {
                    TransportAssert(client.Publish("New owner text"), "current SDK extended slot stride");
                    TransportAssert(extendedView.ReadByte(text2) == 0, "claimed slot clears predecessor's 32 KB text");
                    extendedView.Write(text2, (byte)'X');
                    TransportAssert(client.Clear() && extendedView.ReadByte(text2) == 0, "clear releases both extended text fields");
                }
            }
            var snapshot = new MonitoringSnapshot(1234, "<untrusted>", DateTime.UtcNow, "", 144, 6.94, null, 28, 94, 62, 2460, 185, 12698, 6349);
            foreach (MonitoringLayout layout in Enum.GetValues(typeof(MonitoringLayout)))
            {
                string output = RtssOverlayFormat.Format(snapshot, layout, MonitoringMetrics.All);
                TransportAssert(output.StartsWith("<P0>") && output.Length < 4096, "viewport anchor and bounded hypertext");
                TransportAssert(output.Contains("--") && !output.Contains("<untrusted>"), "unavailable honest and target name never injected");
                TransportAssert(HasRtssReading(output, "144", 0xAE70FF) && Regex.IsMatch(output, @"<C=[0-9A-F]{8}><B=[1-9]\d*,[1-9]\d*>"),
                    "FPS uses the supplied mode accent while the background remains available");
            }
            string fpsOnly = RtssOverlayFormat.Format(snapshot, MonitoringLayout.TopLeft, MonitoringMetrics.Fps);
            TransportAssert(fpsOnly.Contains("144") && !fpsOnly.Contains("GPU") && !fpsOnly.Contains("CPU")
                && !fpsOnly.Contains("MEMORY"), "only the selected metric is rendered without hardware placeholders");
            RtssFormatterViewportTests(snapshot);
            RtssBalancedBarTests();
            RtssExtremeViewportPayloadTests();
            RtssFanGeometryStabilityTests();
            RtssRetainedHardwarePresentationTests();
            RtssSensorAvailabilityGeometryTests();
            RtssAppearanceGeometryTests(snapshot);
            RtssScaleAndBorderTests(snapshot);
            RtssSpacingAndRasterTests();
            RtssActiveModeAccentTests();
            RtssSemanticColorsTests();
            RtssMergedMemoryTests();
            TransportAssert(RtssComponent.SupportsOverlayVersion("7.3.2.1") && RtssComponent.SupportsOverlayVersion("7, 3, 5, 28314"), "supported metadata versions");
            TransportAssert(!RtssComponent.SupportsOverlayVersion("7.3.1") && !RtssComponent.SupportsOverlayVersion("bad"), "legacy version declined without overwrite");
            TransportAssert(AppDataReset.IsUserPreference(MonitoringPreferences.EnabledKey) && AppDataReset.IsUserPreference(MonitoringPreferences.LayoutKey)
                && AppDataReset.IsUserPreference(MonitoringPreferences.MetricsKey) && AppDataReset.IsUserPreference(MonitoringPreferences.RetiredIntervalKey)
                && AppDataReset.IsUserPreference(MonitoringPreferences.AppearanceKey), "monitoring settings and retired interval reset");
            RtssLocalizedLabelsTests();
            Lang.Cur = savedLanguage;
            Console.WriteLine("PASS RTSS transport assertions=" + rtssAssertions);
        }

        private static void RtssLocalizedLabelsTests()
        {
            int language = Lang.Cur;
            try
            {
                var snapshot = MonitoringMetricPresentation.Example;
                Lang.Cur = 0;
                TransportAssert(MonitoringMetricPresentation.Name(MonitoringMetrics.GpuTemperature) == "显卡温度"
                    && MonitoringMetricPresentation.Name(MonitoringMetrics.CpuTemperature) == "CPU 温度"
                    && MonitoringMetricPresentation.Name(MonitoringMetrics.GpuUsage) == "显卡占用", "Chinese labels describe the metrics");
                RtssLabelAtlas first = RtssLabelAtlas.Get(24);
                TransportAssert(first != null && File.Exists(first.FileName), "Localized atlas is available to RTSS");
                DateTime written = File.GetLastWriteTimeUtc(first.FileName);
                for (int i = 0; i < 100; i++)
                    TransportAssert(object.ReferenceEquals(first, RtssLabelAtlas.Get(24)), "Fixed labels reuse their atlas");
                TransportAssert(File.GetLastWriteTimeUtc(first.FileName) == written, "Telemetry does not rewrite fixed labels");
                using (var bitmap = new Bitmap(first.FileName))
                    TransportAssert(bitmap.Width > 30 && bitmap.Width < 512 && bitmap.Height < 2048
                        && bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1).A == 0, "Atlas is small and transparent");
                foreach (int lang in new[] { 0, 1, 0 })
                {
                    Lang.Cur = lang;
                    foreach (MonitoringLayout layout in new[] { MonitoringLayout.TopLeft, MonitoringLayout.TopRight, MonitoringLayout.TopBar })
                        foreach (int width in new[] { 640, 1280, 1920, 3840 })
                            foreach (int scale in new[] { 60, 100, 180 })
                            {
                                int height = width * 9 / 16;
                                var geometry = MonitoringOverlayGeometry.Calculate(layout, MonitoringMetrics.All, width, height, 0, 0, scale, snapshot);
                                string text = RtssOverlayFormat.Format(snapshot, layout, MonitoringMetrics.All, true, width, height,
                                    0xAE70FF, 62, 0, 0, scale);
                                TransportAssert(text.Contains("<LI=") == (lang == 0) && text.Contains("<I=") == (lang == 0), "Native localized sprites follow language switches");
                                TransportAssert(!text.Contains("显卡温度") && !text.Contains("系统内存"), "Unicode glyphs never enter RTSS's ANSI font parser");
                                foreach (uint codePage in new[] { 936u, 1252u, 65001u })
                                    TransportAssert(RtssTextEncoding.GetByteCount(text, codePage) < 4096, "Full localized OSD fits the native text slot");
                                var cells = MonitoringMetricPresentation.Cells(snapshot, MonitoringMetrics.All);
                                for (int i = 0; i < cells.Count; i++)
                                {
                                    Rectangle label = geometry.LabelBounds(i);
                                    TransportAssert(label.Right <= geometry.Bounds.Width && label.Bottom <= geometry.Bounds.Height, "Localized labels fit the surface");
                                    if (lang == 0)
                                    {
                                        string sprite = RtssLabelAtlas.Get(geometry.FontPixels).Sprite(MonitoringMetricPresentation.ShortName(cells[i]));
                                        Match size = Regex.Match(sprite ?? "", @"^<I=(\d+),(\d+),");
                                        TransportAssert(size.Success && int.Parse(size.Groups[1].Value) <= label.Width
                                            && int.Parse(size.Groups[2].Value) <= label.Height, "Translated glyphs fit reserved label bounds");
                                    }
                                }
                            }
                }
                Lang.Cur = 1;
                TransportAssert(MonitoringMetricPresentation.Name(MonitoringMetrics.GpuTemperature) == "GPU TEMP"
                    && MonitoringMetricPresentation.Name(MonitoringMetrics.Ram) == "RAM" && RtssLabelAtlas.Get(24) == null,
                    "English retains native lightweight text");
                Lang.Cur = 0;
                TransportAssert(object.ReferenceEquals(first, RtssLabelAtlas.Get(24)), "Returning to Chinese reuses the cached atlas");
                string directory = RtssLabelAtlas.TestDirectory;
                string blocked = Path.Combine(directory, "blocked-" + Guid.NewGuid().ToString("N"));
                try
                {
                    File.WriteAllText(blocked, "owned test file");
                    RtssLabelAtlas.TestDirectory = blocked;
                    TransportAssert(RtssLabelAtlas.Get(24) == null, "Locked atlas storage degrades without throwing");
                    string fallback = RtssOverlayFormat.Format(snapshot, MonitoringLayout.TopLeft, MonitoringMetrics.All, true);
                    TransportAssert(!fallback.Contains("<LI=") && fallback.Contains("GPU TEMP") && fallback.Contains("CPU TEMP"),
                        "Storage failures retain readable native labels and live measurements");
                }
                finally { RtssLabelAtlas.TestDirectory = directory; File.Delete(blocked); }
            }
            finally { Lang.Cur = language; }
        }

        private static void RtssFixtureStatistics(RtssMapFixture fixture)
        {
            uint now = unchecked((uint)Environment.TickCount);
            long app = RtssMapFixture.AppOffset;
            fixture.Fresh(); fixture.View.Write(app + 284, 1u);
            fixture.View.Write(app + 288, now - 30000); fixture.View.Write(app + 292, now);
            fixture.View.Write(app + 296, 4000u); fixture.View.Write(app + 300, 30u);
            fixture.View.Write(app + 308, 1337u);
            fixture.View.Write(app + RtssMemorySession.StatisticsLow1Offset, 724u);
            fixture.View.Write(app + RtssMemorySession.StatisticsLow01Offset, 518u);
        }
        private static void RtssExportedStatisticsTests()
        {
            using (var fixture = new RtssMapFixture())
            using (var session = new RtssMemorySession(fixture.Name, "Pavise.Test.ExportedStatistics"))
            {
                RtssFixtureStatistics(fixture);
                RtssFrameSample sample;
                foreach (MonitoringMetrics selected in new[] { MonitoringMetrics.AverageFps, MonitoringMetrics.Low1, MonitoringMetrics.Low01 })
                {
                    MonitoringPreferences.TrySetMetrics(selected);
                    TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample), "read isolated exported statistic");
                    TransportAssert(sample.AverageFps == (selected == MonitoringMetrics.AverageFps ? (double?)133.7 : null)
                        && sample.Low1 == (selected == MonitoringMetrics.Low1 ? (double?)72.4 : null)
                        && sample.Low01 == (selected == MonitoringMetrics.Low01 ? (double?)51.8 : null),
                        "SDK exported statistics use tenths of FPS and selected fields only");
                    TransportAssert(sample.FrameTimes != null && sample.FrameTimes.Length == 0,
                        "frame ring stays ready for automatic fallback without old frames");
                }
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.AverageFps | MonitoringMetrics.Low1 | MonitoringMetrics.Low01);
                long app = RtssMapFixture.AppOffset;
                foreach (int field in new[] { 284, 288, 292, 296, 300 })
                {
                    RtssFixtureStatistics(fixture); fixture.View.Write(app + field, 0u);
                    TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample)
                        && !sample.AverageFps.HasValue && !sample.Low1.HasValue && !sample.Low01.HasValue,
                        "stopped/uninitialized recording does not export historical statistics: " + field);
                    TransportAssert(sample.BenchmarkActive == (field != 284),
                        "Uninitialized active statistics retain their source without mixing in rolling values");
                    TransportAssert(fixture.View.ReadUInt32(app + field) == 0, "Pavise never starts recording or rewrites statistics");
                }
                RtssFixtureStatistics(fixture);
                fixture.View.Write(app + 292, unchecked((uint)Environment.TickCount - 5000));
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.AverageFps == null && sample.Low1 == null
                    && sample.Low01 == null && sample.Fps.HasValue, "stale recording is invalid even with a fresh live renderer");
                RtssFixtureStatistics(fixture); fixture.View.Write(app + 288, unchecked((uint)Environment.TickCount + 1000));
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.AverageFps == null,
                    "invalid recording interval rejected");
                RtssFixtureStatistics(fixture); fixture.View.Write(app + 308, UInt32.MaxValue);
                fixture.View.Write(app + RtssMemorySession.StatisticsLow1Offset, 0u);
                fixture.View.Write(app + RtssMemorySession.StatisticsLow01Offset, 1u);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.AverageFps == null
                    && sample.Low1 == null && sample.Low01 == .1, "invalid/zero exported values unavailable; valid tiny lows retained");
                RtssFixtureStatistics(fixture);
                fixture.View.Write(8, 312);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.AverageFps == 133.7
                    && sample.Low1 == null && sample.Low01 == null, "small APP entry supports average without reading missing low fields");
                fixture.View.Write(8, RtssMemorySession.StatisticsLow1Offset + 4);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.Low1 == 72.4 && sample.Low01 == null,
                    "each low field respects its own APP boundary");
                fixture.View.Write(8, RtssMapFixture.AppSize); fixture.View.Write(4, 0x0002000eu);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.Low1 == 72.4 && sample.Low01 == 51.8,
                    "v2.14 baseline supports v2.13 exported lows without newer buffer positions");
                fixture.View.Write(4, 0x00020015u);
                RtssFixtureStatistics(fixture); fixture.View.Write(app + 284, 0u);
                fixture.Fresh();
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.AverageFps == null
                    && sample.Low1 == null && sample.Low01 == null && !sample.BenchmarkActive,
                    "fresh completed recording never masquerades as current statistics");
            }
            MonitoringPreferences.TrySetMetrics(MonitoringMetrics.All);
        }
        private static void RtssNativeTimingTests()
        {
            MonitoringPreferences.TrySetMetrics(MonitoringMetrics.All);
            using (var fixture = new RtssMapFixture())
            using (var session = new RtssMemorySession(fixture.Name, "Pavise.Test.NativeTiming"))
            {
                long app = RtssMapFixture.AppOffset; RtssFrameSample sample;
                fixture.View.Write(app + RtssMemorySession.NativeFramerateOffset, 999u);
                fixture.View.Write(app + 280, 50000u);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample)
                    && sample.Fps == 99.9 && Math.Abs(sample.FrameTimeMs.Value - 8.333) < .001,
                    "Native smoothed FPS and newest actual interval match the official RTSS sources");
                TransportAssert(fixture.View.ReadUInt32(app + RtssMemorySession.NativeFramerateOffset) == 999
                    && fixture.View.ReadUInt32(app + 280) == 50000, "Reading never rewrites RTSS smoothing or display counters");
                foreach (uint invalid in new[] { 0u, UInt32.MaxValue })
                {
                    fixture.View.Write(app + RtssMemorySession.NativeFramerateOffset, invalid);
                    TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.Fps == 120,
                        "Unavailable native FPS falls back to the current official frame-count interval");
                }
                fixture.View.Write(app + 268, 0u);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.Fps == null,
                    "An uninitialized base interval cannot manufacture fallback FPS");
                fixture.Fresh(); fixture.View.Write(app + 276, 0u);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.Fps == null,
                    "An empty frame counter cannot manufacture fallback FPS");
                fixture.View.Write(app + 276, 120u);
                fixture.View.Write(app + RtssMemorySession.NativeFramerateOffset, 1200u);
                fixture.View.Write(app + RtssMemorySession.FrameBufferOffset + 9 * 4, 0u);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.FrameTimeMs == null,
                    "Missing actual interval is not replaced with a possible peak frametime");
                fixture.Position(20); fixture.Fresh();
                session.AfterFrameCopyForTest = delegate { fixture.Position(21); };
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample)
                    && sample.FrameTimes != null && sample.FrameTimes.Length == 10,
                    "Appending a frame during the copy preserves older intervals that were not overwritten");
                session.AfterFrameCopyForTest = null;
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample)
                    && sample.FrameTimes.Length == 1 && sample.FrameSequence == 11,
                    "A frame appended during copying is included exactly once in the next read");
                fixture.Position(30);
                session.AfterFrameCopyForTest = delegate { fixture.Position(30 + 1024); };
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.FrameTimes == null,
                    "Actual overwrite during copying invalidates the incomplete window");
                session.AfterFrameCopyForTest = null;
                fixture.View.Write(8, 312); fixture.Fresh();
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample)
                    && sample.Fps == 120 && sample.FrameTimeMs == 50,
                    "A shorter APP schema uses the SDK legacy counters without reading beyond its boundary");
                fixture.View.Write(app + 268, 0u);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.Fps == null,
                    "Legacy FPS requires the SDK's nonzero measurement start time");
            }
            using (var fixture = new RtssMapFixture())
            using (var session = new RtssMemorySession(fixture.Name, "Pavise.Test.WrappedHistory"))
            {
                RtssFrameSample sample; fixture.Position(1020);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample), "Seed a wrapping history cursor");
                fixture.Position(1026);
                for (int i = 0; i < 6; i++)
                    fixture.View.Write(RtssMapFixture.AppOffset + RtssMemorySession.FrameBufferOffset + ((1020 + i) & 1023) * 4,
                        (uint)(1000 + i * 1000));
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample)
                    && sample.FrameTimes.Length == 6, "Copy only new history across the ring boundary");
                for (int i = 0; i < 6; i++)
                    TransportAssert(sample.FrameTimes[i] == i + 1, "Ring intervals retain chronological order: " + i);
            }
        }
        private static void RtssGpuTimingTests()
        {
            MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Fps | MonitoringMetrics.FrameTime);
            using (var fixture = new RtssMapFixture())
            using (var session = new RtssMemorySession(fixture.Name, "Pavise.Test.ReflexGpuTiming"))
            {
                long app = RtssMapFixture.AppOffset; uint clock = 10000; ulong marker = 100000;
                session.GpuClockForTest = delegate { return clock; };
                Action<ulong, uint> write = delegate(ulong end, uint time) {
                    fixture.Fresh(); fixture.View.Write(app + RtssMemorySession.GpuRenderStartOffset, end - 9000);
                    fixture.View.Write(app + RtssMemorySession.GpuRenderEndOffset, end);
                    fixture.View.Write(app + RtssMemorySession.GpuFrameTimeOffset - 4, 9000u);
                    fixture.View.Write(app + RtssMemorySession.GpuFrameTimeOffset, time);
                };
                fixture.View.Write(app + RtssMemorySession.NativeFramerateOffset, 2690u);
                fixture.View.Write(app + RtssMemorySession.FrameBufferOffset + 9 * 4, 400u);
                RtssFrameSample sample; write(marker, 11500);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample)
                    && sample.GpuFrameTimeMs == null && sample.Fps == 269 && sample.FrameTimeMs == .4,
                    "A retained Reflex marker requires observed progression before it can be presented as fresh");
                clock += 1000; marker += 11500; write(marker, 11500);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample)
                    && sample.GpuFrameTimeMs == 11.5 && sample.Fps == 269 && sample.FrameTimeMs == .4,
                    "RTSS Reflex GPU completion period is neither Present time, GPU busy nor the inverse of generated FPS");
                fixture.View.Write(app + RtssMemorySession.GpuFrameTimeOffset, 12000u);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GpuFrameTimeMs == 11.5,
                    "Changing a value without advancing its marker cannot publish a mismatched frame report");
                fixture.View.Write(app + RtssMemorySession.GpuFrameTimeOffset, 11500u);
                clock += 2500; fixture.Fresh();
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GpuFrameTimeMs == 11.5,
                    "A recently progressing GPU report stays valid through its bounded refresh age");
                clock++; fixture.Fresh();
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GpuFrameTimeMs == null,
                    "Live FPS cannot keep a stopped GPU marker fresh indefinitely");
                marker += 12000; write(marker, 12000);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GpuFrameTimeMs == 12,
                    "GPU timing recovers from a stopped report only after a new marker");
                foreach (uint invalid in new[] { 0u, 10000001u, UInt32.MaxValue })
                {
                    marker += 11500; write(marker, invalid);
                    TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GpuFrameTimeMs == null,
                        "Invalid Reflex GPU frame times never become telemetry: " + invalid);
                }
                marker += 11500; write(marker, 11500);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GpuFrameTimeMs == null,
                    "A repaired GPU stream reestablishes its baseline before publishing");
                marker += 11500; write(marker, 11500);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GpuFrameTimeMs == 11.5,
                    "A repaired GPU stream returns a fresh measured interval");
                fixture.View.Write(app + RtssMemorySession.GpuRenderStartOffset, 0UL);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GpuFrameTimeMs == null,
                    "The official SDK requires a GPU stage, not just a nonzero time DWORD");
                write(marker, 11500); fixture.View.Write(app + RtssMemorySession.GpuRenderEndOffset, marker - 10000);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GpuFrameTimeMs == null,
                    "A backwards GPU stage is rejected");
                write(marker, 11500); fixture.View.Write(8, RtssMemorySession.GpuFrameTimeOffset);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GpuFrameTimeMs == null,
                    "GPU time requires its entire exported field within the APP entry");
                fixture.View.Write(8, RtssMapFixture.AppSize); fixture.View.Write(4, 0x00020014u); write(marker, 11500);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GpuFrameTimeMs == null,
                    "Pre-Reflex shared-memory versions never read newer GPU fields");
                fixture.View.Write(4, 0x00020015u); write(marker, 11500);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GpuFrameTimeMs == null, "Restore GPU timing baseline");
                marker += 11500; write(marker, 11500);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GpuFrameTimeMs == 11.5, "Restore progressing GPU timing");
                TransportAssert(!session.TryRead(4321, delegate { return 99; }, out sample), "Leaving the target invalidates its GPU marker");
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GpuFrameTimeMs == null,
                    "A returning target cannot reuse old GPU freshness");
                marker += 11500; write(marker, 11500);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GpuFrameTimeMs == 11.5, "A returning target needs new GPU evidence");
                TransportAssert(session.TryRead(1234, delegate { return 100; }, out sample) && sample.GpuFrameTimeMs == null,
                    "PID reuse cannot carry GPU timing from an older process");
            }
            foreach (MonitoringLayout layout in Enum.GetValues(typeof(MonitoringLayout)))
            {
                var gpu = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", fps: 269, frameTimeMs: 11.5, frameTimeFromGpu: true);
                var present = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", fps: 269, frameTimeMs: .4);
                string gpuText = RtssOverlayFormat.Format(gpu, layout, MonitoringMetrics.Fps | MonitoringMetrics.FrameTime);
                string presentText = RtssOverlayFormat.Format(present, layout, MonitoringMetrics.Fps | MonitoringMetrics.FrameTime);
                TransportAssert(gpuText.Contains("GPU TIME") && !gpuText.Contains("PRESENT") && gpuText.Contains("11.5 ms"),
                    "Native GPU frame time identifies its source in the real RTSS payload: " + layout);
                TransportAssert(presentText.Contains("PRESENT") && !presentText.Contains("GPU TIME") && presentText.Contains("0.4 ms"),
                    "Present intervals cannot masquerade as engine-rendered frame time: " + layout);
            }
            MonitoringPreferences.TrySetMetrics(MonitoringMetrics.All);
        }
        private static void RtssFixtureProcessMemory(RtssMapFixture fixture)
        {
            long app = RtssMapFixture.AppOffset;
            fixture.Fresh(); fixture.View.Write(4, 0x00020015u); fixture.View.Write(8, RtssMapFixture.AppSize);
            fixture.View.Write(72, 16); fixture.View.Write(76, 9400);
            fixture.View.Write(app + RtssMemorySession.ProcessCountersCountOffset, 2);
            fixture.View.Write(app + RtssMemorySession.ProcessCountersPeriodOffset, 1000u);
            fixture.View.Write(app + RtssMemorySession.ProcessCountersTimestampOffset, unchecked((uint)Environment.TickCount));
            fixture.View.Write(app + 9400, 0x100u); fixture.View.Write(app + 9404, 0u); fixture.View.Write(app + 9408, 999u);
            fixture.View.Write(app + 9416, 1u); fixture.View.Write(app + 9420, 0u); fixture.View.Write(app + 9424, 2097664u);
        }
        private static void RtssProcessMemoryTests()
        {
            MonitoringPreferences.TrySetMetrics(MonitoringMetrics.GameRam);
            using (var fixture = new RtssMapFixture())
            using (var session = new RtssMemorySession(fixture.Name, "Pavise.Test.ProcessMemory"))
            {
                RtssFrameSample sample;
                long app = RtssMapFixture.AppOffset;
                RtssFixtureProcessMemory(fixture);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GameRamMiB == 2048.5,
                    "RAM counter uses header stride/APP-relative array and converts KiB to MiB");
                TransportAssert(fixture.View.ReadUInt32(app + RtssMemorySession.ProcessCountersPeriodOffset) == 1000
                    && fixture.View.ReadUInt32(app + RtssMemorySession.ProcessCountersFlagsOffset) == 0,
                    "read leaves counter cadence and flags untouched");
                foreach (int field in new[] { RtssMemorySession.ProcessCountersPeriodOffset, RtssMemorySession.ProcessCountersTimestampOffset,
                    RtssMemorySession.ProcessCountersCountOffset })
                {
                    RtssFixtureProcessMemory(fixture); fixture.View.Write(app + field, 0u);
                    TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GameRamMiB == null,
                        "disabled/uninitialized process counter unavailable: " + field);
                    TransportAssert(fixture.View.ReadUInt32(app + field) == 0, "does not activate disabled counter");
                }
                RtssFixtureProcessMemory(fixture);
                fixture.View.Write(app + RtssMemorySession.ProcessCountersTimestampOffset, unchecked((uint)Environment.TickCount - 5000));
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GameRamMiB == null, "stale process counter unavailable");
                foreach (int count in new[] { -1, 257, Int32.MaxValue })
                {
                    RtssFixtureProcessMemory(fixture); fixture.View.Write(app + RtssMemorySession.ProcessCountersCountOffset, count);
                    TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GameRamMiB == null, "invalid counter count bounded");
                }
                foreach (int stride in new[] { 0, 8, 13, Int32.MaxValue })
                {
                    RtssFixtureProcessMemory(fixture); fixture.View.Write(72, stride);
                    TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GameRamMiB == null, "invalid counter stride bounded");
                }
                foreach (int array in new[] { -1, 0, 9192, 9401, RtssMapFixture.AppSize - 16, Int32.MaxValue })
                {
                    RtssFixtureProcessMemory(fixture); fixture.View.Write(76, array);
                    TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GameRamMiB == null, "counter array cannot alias fixed fields or cross APP boundary");
                }
                RtssFixtureProcessMemory(fixture); fixture.View.Write(4, 0x00020011u);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GameRamMiB == null, "counter version gate before v2.18");
                RtssFixtureProcessMemory(fixture); fixture.View.Write(8, 9212);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GameRamMiB == null, "array respects truncated APP entry");
                RtssFixtureProcessMemory(fixture); fixture.View.Write(app + 9420, 1u);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GameRamMiB == null, "RAM counter parameters must match");
                RtssFixtureProcessMemory(fixture); fixture.View.Write(app + 9424, 0u);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GameRamMiB == 0, "valid zero process RAM retained");
                RtssFixtureProcessMemory(fixture); MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Fps);
                TransportAssert(session.TryRead(1234, delegate { return 99; }, out sample) && sample.GameRamMiB == null, "unselected counter not consumed");
            }
            MonitoringPreferences.TrySetMetrics(MonitoringMetrics.All);
        }

        private static void RtssAppearanceGeometryTests(MonitoringSnapshot snapshot)
        {
            foreach (MonitoringLayout layout in Enum.GetValues(typeof(MonitoringLayout)))
            {
                string clear = RtssOverlayFormat.Format(snapshot, layout, MonitoringMetrics.Fps, true, 1920, 1080, 0x12ABCD, 0);
                string solid = RtssOverlayFormat.Format(snapshot, layout, MonitoringMetrics.Fps, true, 1920, 1080, 0x12ABCD, 100);
                TransportAssert(!ParseRtssPixelLayers(clear)[0].HasFill && !Regex.IsMatch(clear, @"<C=00[0-9A-F]{6}>")
                    && HasRtssReading(clear, "144", 0x12ABCD), "Zero opacity only removes surface, preserving theme-colored FPS text: " + layout);
                TransportAssert(Regex.IsMatch(solid, @"<C=FF0A0C12><B=[1-9]\d*,[1-9]\d*>"), "Full opacity produces solid panel: " + layout);
                TransportAssert(RtssOverlayFormat.Format(snapshot, layout, MonitoringMetrics.Fps, true, 1920, 1080, 0x12ABCD, -20) == clear
                    && RtssOverlayFormat.Format(snapshot, layout, MonitoringMetrics.Fps, true, 1920, 1080, 0x12ABCD, 500) == solid,
                    "Formatter bounds opacity without altering text or layout: " + layout);
            }
            RtssTextOpacityIndependenceTests();
            int[,] viewports = { { 320, 240 }, { 640, 480 }, { 1280, 720 }, { 1366, 768 }, { 1920, 1080 }, { 2560, 1440 }, { 3840, 2160 } };
            foreach (MonitoringLayout layout in Enum.GetValues(typeof(MonitoringLayout)))
            for (int index = 0; index < viewports.GetLength(0); index++)
            foreach (MonitoringMetrics metrics in new[] { MonitoringMetrics.Fps, MonitoringMetrics.All })
            foreach (Point shift in new[] { Point.Empty, new Point(1000, 1000), new Point(-1000, -1000), new Point(-175, 280) })
            {
                int width = viewports[index, 0], height = viewports[index, 1];
                MonitoringOverlayGeometry model = MonitoringOverlayGeometry.Calculate(layout, metrics, width, height, shift.X, shift.Y);
                Rectangle box = model.Bounds;
                TransportAssert(box.Left >= 0 && box.Top >= 0 && box.Right <= width && box.Bottom <= height,
                    "Shared editor/RTSS surface remains inside viewport: " + layout + " " + width + "x" + height + " " + shift);
                if (shift == Point.Empty)
                {
                    int inset = Math.Max(4, model.FontPixels / 2);
                    TransportAssert(box.Top == inset && (layout == MonitoringLayout.TopBar
                        ? Math.Abs(box.Left * 2 + box.Width - width) <= 1
                        : layout == MonitoringLayout.TopRight ? box.Right == width - inset : box.Left == inset),
                        "Default corner presets use scaled screen insets and the content-sized top bar is centered");
                }
                if (shift == new Point(-1000, -1000))
                    TransportAssert((layout == MonitoringLayout.TopBar || box.Left == 0) && box.Top == 0,
                        "Dragging to the upper edge clamps the HUD while preserving top-bar centering");
                if (shift == new Point(1000, 1000))
                    TransportAssert((layout == MonitoringLayout.TopBar || box.Right == width) && box.Bottom == height,
                        "Dragging to the lower edge clamps the HUD while preserving top-bar centering");
                TransportAssert(model.FontPixels >= 8 && model.FontPixels <= 36 && model.Groups.Count >= 1,
                    "Viewport clamp retains readable font and every metric");
                Point recovered = MonitoringOverlayGeometry.GetOffsets(layout, metrics, width, height, box.X, box.Y);
                Rectangle roundtrip = MonitoringOverlayGeometry.Calculate(layout, metrics, width, height, recovered.X, recovered.Y).Bounds;
                TransportAssert(Math.Abs(roundtrip.X - box.X) <= Math.Max(1, width / 1000)
                    && Math.Abs(roundtrip.Y - box.Y) <= Math.Max(1, height / 1000),
                    "Dragging round-trips normalized viewport coordinates within one permille");
                if (box.Left == 0 || box.Right == width)
                    TransportAssert(roundtrip.X == box.X, "Saving an edge position must not introduce a horizontal rounding gap");
                if (box.Top == 0 || box.Bottom == height)
                    TransportAssert(roundtrip.Y == box.Y, "Saving an edge position must not introduce a vertical rounding gap");
                string text = RtssOverlayFormat.Format(snapshot, layout, metrics, true, width, height, 0x12ABCD, 38, shift.X, shift.Y);
                TransportAssert(text.StartsWith("<FNT=" + MonitoringOverlayGeometry.FontFamily + ",-" + (model.FontPixels * RtssOverlayFormat.FontRasterFactor) + "," + MonitoringOverlayGeometry.FontWeight + ",1>")
                    && Regex.IsMatch(text, @"<C=610A0C12><B=[1-9]\d*,[1-9]\d*>") && text.Length < 4096,
                    "RTSS output follows geometry font and percent opacity without overrunning owner slot");
                if (shift != Point.Empty)
                    TransportAssert(text.Contains("<P0><M=" + box.X + "," + box.Y + "," + -box.X + "," + -box.Y + "><L0>")
                        && !text.Contains("<P="), "Custom position uses viewport sticky origin unaffected by global RTSS origin");
            }
            MonitoringOverlayGeometry corner = MonitoringOverlayGeometry.Calculate(MonitoringLayout.TopRight, MonitoringMetrics.LegacyAll, 1280, 720);
            TransportAssert(corner.Bounds.Right == 1280 - Math.Max(4, corner.FontPixels / 2)
                && corner.Bounds.Top == Math.Max(4, corner.FontPixels / 2) && corner.Groups.Count == 4,
                "720p corner uses scaled screen insets and retains every semantic family");
            MonitoringOverlayGeometry bar = MonitoringOverlayGeometry.Calculate(MonitoringLayout.TopBar, MonitoringMetrics.LegacyAll, 1280, 720);
            TransportAssert(bar.Groups.Count == 4 && bar.IsCentered && bar.Bounds.Width < 1280
                && Math.Abs(bar.Bounds.Left * 2 + bar.Bounds.Width - 1280) <= 1,
                "720p top bar wraps its actual content and remains centered without dropping any family");
            MonitoringOverlayGeometry narrow = MonitoringOverlayGeometry.Calculate(MonitoringLayout.TopLeft, MonitoringMetrics.LegacyAll, 320, 240);
            TransportAssert(narrow.Bounds.Width <= 316 && narrow.Bounds.Height <= 236 && narrow.Groups.Count == 4,
                "Narrow corner retains all families within the viewport instead of clipping content");
            MonitoringOverlayGeometry fallback = MonitoringOverlayGeometry.Calculate(MonitoringLayout.TopLeft, MonitoringMetrics.All, 0, 0);
            TransportAssert(fallback.ViewportWidth == 1920 && fallback.ViewportHeight == 1080,
                "Missing viewport uses identical defaults in preview and renderer");
        }

        private static void RtssTextOpacityIndependenceTests()
        {
            TransportAssert(MonitoringMetricPresentation.LabelRgb == 0xE1EAF5
                && MonitoringMetricPresentation.LabelRgb != MonitoringMetricPresentation.DimRgb,
                "Readable labels have their own high-contrast color while unavailable values remain separately dim");
            for (int opacity = -2; opacity <= 102; opacity++)
            {
                int alpha = (Math.Max(0, Math.Min(100, opacity)) * 255 + 50) / 100;
                TransportAssert(MonitoringOverlayGeometry.SurfaceAlpha(opacity) == alpha
                    && MonitoringOverlayGeometry.CoveredAlpha(opacity, 128) == (alpha * 128 + 127) / 255,
                    "Preview and native background use exact half-up alpha with independent half-covered edge pixels");
            }
            foreach (MonitoringLayout layout in Enum.GetValues(typeof(MonitoringLayout)))
            foreach (MonitoringMetrics metrics in new[] { MonitoringMetrics.Default, MonitoringMetrics.All })
            foreach (bool gpuTime in new[] { false, true })
            {
                var snapshot = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", fps: 269,
                    frameTimeMs: gpuTime ? 11.5 : .4, cpuUsage: 85, gpuUsage: 97,
                    gpuTemperature: 83, cpuTemperature: 95, cpuPower: 180,
                    ramMiB: 17400, vramMiB: 4600, ramUsage: 95, vramUsage: 85,
                    frameTimeFromGpu: gpuTime);
                var geometry = MonitoringOverlayGeometry.Calculate(layout, metrics, 1920, 1080, snapshot: snapshot);
                string baseline = RtssOverlayFormat.Format(snapshot, layout, metrics, true, 1920, 1080, 0x12ABCD, 0);
                List<RtssPixelLayer> baselineText = RtssTextLayers(ParseRtssPixelLayers(baseline));
                TransportAssert(HasRtssReading(baseline, "269", 0x12ABCD)
                    && HasRtssReading(baseline, "// FPS", 0x12ABCD),
                    "Theme FPS number and caption remain visible with completely transparent background");
                foreach (int opacity in new[] { 0, 5, 20, 62, 100 })
                {
                    string output = RtssOverlayFormat.Format(snapshot, layout, metrics, true, 1920, 1080, 0x12ABCD, opacity);
                    List<RtssPixelLayer> layers = ParseRtssPixelLayers(output), text = RtssTextLayers(layers);
                    TransportAssert(text.Count == baselineText.Count && output.Length < 4096,
                        "Panel opacity never adds glyph passes and the native RTSS payload remains bounded");
                    AssertRtssSurface(layers, geometry, opacity, "text opacity / " + layout + " / " + metrics + " / " + opacity);
                    for (int i = 0; i < text.Count; i++)
                    {
                        RtssPixelLayer actual = text[i], expected = baselineText[i];
                        TransportAssert(actual.Bounds == expected.Bounds && actual.Text == expected.Text
                            && actual.Scale == expected.Scale && actual.ColorRgb == expected.ColorRgb
                            && !actual.HasFill,
                            "Changing background opacity leaves every label, number, unit and semantic color identical");
                    }
                    var cells = MonitoringMetricPresentation.Cells(snapshot, metrics, 0x12ABCD);
                    foreach (MonitoringMetricCell cell in cells)
                    {
                        TransportAssert(HasRtssReading(output, cell.Value, cell.ColorRgb),
                            "Theme FPS, warnings, critical values and missing readings retain their exact colors at all opacities");
                        TransportAssert(HasRtssReading(output, MonitoringMetricPresentation.ShortName(cell),
                            cell.Metric == MonitoringMetrics.Fps ? 0x12ABCD : 0xE1EAF5),
                            "Only the FPS caption adopts the theme; other captions keep their fixed readable color");
                    }
                    TransportAssert(Regex.Matches(output, @"<C=[0-9A-F]{8}>").Count == (opacity > 0 ? geometry.SurfaceFragments.Count : 0)
                        && Regex.Matches(output, @"<C=[0-9A-F]{6}>").Count == cells.Count * 2,
                        "Every native glyph color is opaque RGB; explicit alpha is confined to optional background fragments");
                }
            }
        }

        private static void RtssSpacingAndRasterTests()
        {
            int language = Lang.Cur;
            try
            {
                foreach (int lang in new[] { 0, 1 })
                {
                    Lang.Cur = lang;
                    foreach (MonitoringLayout layout in Enum.GetValues(typeof(MonitoringLayout)))
                    foreach (Size viewport in new[] { new Size(320, 240), new Size(1920, 1080), new Size(2560, 1440), new Size(3840, 2160) })
                    foreach (int scale in new[] { 40, 60, 65, 80, 200 })
                    foreach (int spacing in new[] { 50, 85, 90, 100, 180 })
                    {
                        var geometry = MonitoringOverlayGeometry.Calculate(layout, MonitoringMetrics.All, viewport.Width, viewport.Height,
                            scale: scale, snapshot: MonitoringMetricPresentation.Example, spacing: spacing);
                        string output = RtssOverlayFormat.Format(MonitoringMetricPresentation.Example, layout, MonitoringMetrics.All, true,
                            viewport.Width, viewport.Height, scale: scale, spacing: spacing);
                        TransportAssert(new Rectangle(Point.Empty, viewport).Contains(geometry.Bounds), "Small text and spacing extremes remain inside the viewport");
                        for (int i = 0; i < 18; i++)
                        {
                            TransportAssert(new Rectangle(Point.Empty, geometry.Bounds.Size).Contains(geometry.LabelBounds(i))
                                && new Rectangle(Point.Empty, geometry.Bounds.Size).Contains(geometry.ValueBounds(i)), "Spacing controls cannot crop reserved labels or readings");
                        }
                        TransportAssert(output.Contains(",-" + geometry.FontPixels * 2 + ",700,1>")
                            && Regex.Matches(output, "<FNT=").Count == 1, "One doubled native font supplies the entire HUD");
                        foreach (Match match in Regex.Matches(output, @"<S=(\d+)>") )
                            TransportAssert(int.Parse(match.Groups[1].Value) <= 100, "Native glyphs are reduced from their font texture instead of enlarged");
                        TransportAssert(RtssTextEncoding.GetByteCount(output, 65001) < 4096, "All metrics fit the native owner slot with spacing and small fonts");
                    }
                    var tight = MonitoringOverlayGeometry.Calculate(MonitoringLayout.TopLeft, MonitoringMetrics.All, 3840, 2160, scale: 60, spacing: 50);
                    var airy = MonitoringOverlayGeometry.Calculate(MonitoringLayout.TopLeft, MonitoringMetrics.All, 3840, 2160, scale: 60, spacing: 180);
                    TransportAssert(tight.FontPixels == airy.FontPixels && tight.InlineGap < airy.InlineGap
                        && tight.Bounds.Height < airy.Bounds.Height, "Roomy viewport spacing changes gaps independently of font size");
                    Point offset = MonitoringOverlayGeometry.GetOffsets(MonitoringLayout.TopLeft, MonitoringMetrics.All, 3840, 2160, 200, 150, 60, 180);
                    var moved = MonitoringOverlayGeometry.Calculate(MonitoringLayout.TopLeft, MonitoringMetrics.All, 3840, 2160, offset.X, offset.Y, 60, spacing: 180);
                    TransportAssert(Math.Abs(moved.Bounds.X - 200) <= 4 && Math.Abs(moved.Bounds.Y - 150) <= 3, "Spacing-aware drag coordinates round-trip");
                    if (lang == 0)
                    {
                        var atlas = RtssLabelAtlas.Get(24);
                        var sprite = Regex.Match(atlas.Sprite(MonitoringMetricPresentation.ShortName(MonitoringMetrics.Fps)), @"<I=(\d+),(\d+),(\d+),(\d+),(\d+),(\d+)>");
                        TransportAssert(sprite.Success && int.Parse(sprite.Groups[5].Value) == int.Parse(sprite.Groups[1].Value) * 2
                            && int.Parse(sprite.Groups[6].Value) == int.Parse(sprite.Groups[2].Value) * 2, "Chinese labels reduce a twofold source texture");
                        DateTime written = File.GetLastWriteTimeUtc(atlas.FileName);
                        TransportAssert(Object.ReferenceEquals(atlas, RtssLabelAtlas.Get(24)) && written == File.GetLastWriteTimeUtc(atlas.FileName), "Repeated publications reuse the atlas without writing it again");
                    }
                }
            }
            finally { Lang.Cur = language; }
        }

        private static void RtssScaleAndBorderTests(MonitoringSnapshot snapshot)
        {
            int[,] viewports = { { 320, 240 }, { 640, 480 }, { 1280, 720 }, { 1920, 1080 }, { 2560, 1440 }, { 3840, 2160 } };
            foreach (MonitoringLayout layout in Enum.GetValues(typeof(MonitoringLayout)))
            for (int index = 0; index < viewports.GetLength(0); index++)
            foreach (MonitoringMetrics metrics in new[] { MonitoringMetrics.Fps, MonitoringMetrics.All })
            foreach (int scale in new[] { 60, 100, 200 })
            {
                int width = viewports[index, 0], height = viewports[index, 1];
                MonitoringOverlayGeometry geometry = MonitoringOverlayGeometry.Calculate(layout, metrics, width, height, 150, 230, scale);
                Rectangle box = geometry.Bounds;
                string text = RtssOverlayFormat.Format(snapshot, layout, metrics, true, width, height, 0x12ABCD, 62, 150, 230, scale);
                TransportAssert(box.Left >= 0 && box.Top >= 0 && box.Right <= width && box.Bottom <= height
                    && geometry.FontPixels >= 8 && geometry.FontPixels <= 72,
                    "Scaled HUD font, wrap and normalized position remain within viewport: " + layout + " " + width + " " + scale);
                TransportAssert(text.StartsWith("<FNT=" + MonitoringOverlayGeometry.FontFamily + ",-" + (geometry.FontPixels * RtssOverlayFormat.FontRasterFactor) + "," + MonitoringOverlayGeometry.FontWeight + ",1>")
                    && text.Contains("<P0><M=" + box.X + "," + box.Y + "," + -box.X + "," + -box.Y + "><L0>"),
                    "RTSS scales its native glyphs and uses the same surface position as editor previews");
                TransportAssert(!text.Contains("<B=0,0,1>") && geometry.Separators.Count == 0 && Regex.Matches(text, "<B=").Count == geometry.SurfaceFragments.Count && text.Length < 4096,
                    "Scaled ROG HUD fills only its clipped background without outlines or divider lines");
                MonitoringOverlayGeometry preset = MonitoringOverlayGeometry.Calculate(layout, metrics, width, height, 0, 0, scale);
                int x = preset.Bounds.X, y = preset.Bounds.Y;
                string presetText = RtssOverlayFormat.Format(snapshot, layout, metrics, true, width, height, 0x12ABCD, 62, 0, 0, scale);
                TransportAssert(presetText.Contains("<P0><M=" + x + "," + y + "," + -x + "," + -y + "><L0>"),
                    "Screen-inset presets use identical positions in RTSS and preview at every size");
                Point offsets = MonitoringOverlayGeometry.GetOffsets(layout, metrics, width, height, box.X, box.Y, scale);
                Rectangle moved = MonitoringOverlayGeometry.Calculate(layout, metrics, width, height, offsets.X, offsets.Y, scale).Bounds;
                TransportAssert(Math.Abs(moved.X - box.X) <= Math.Max(1, width / 1000)
                    && Math.Abs(moved.Y - box.Y) <= Math.Max(1, height / 1000),
                    "Scaled position editing round-trips at the current size rather than the 100-percent preset: "
                    + layout + " " + width + "x" + height + " " + metrics + " " + scale + " " + box + " -> " + offsets + " -> " + moved);
                string transparent = RtssOverlayFormat.Format(snapshot, layout, metrics, true, width, height, 0x12ABCD, 0, 150, 230, scale);
                TransportAssert(!ParseRtssPixelLayers(transparent)[0].HasFill && HasRtssReading(transparent, "144", 0x12ABCD),
                    "At zero opacity scaled text remains visible with no background fill or theme-color outline");
            }
            foreach (int scale in new[] { 60, 100, 200 })
            {
                MonitoringOverlayGeometry roomy = MonitoringOverlayGeometry.Calculate(MonitoringLayout.TopLeft, MonitoringMetrics.Fps, 1920, 1080, 0, 0, scale);
                TransportAssert(roomy.FontPixels == (int)Math.Round(24 * scale / 100.0), "Unconstrained HUD uses native size " + scale);
            }
            string small = RtssOverlayFormat.Format(snapshot, MonitoringLayout.TopLeft, MonitoringMetrics.Fps, true, 1920, 1080, 0x12ABCD, 62, 0, 0, MonitoringPreferences.MinimumScalePercent);
            string large = RtssOverlayFormat.Format(snapshot, MonitoringLayout.TopLeft, MonitoringMetrics.Fps, true, 1920, 1080, 0x12ABCD, 62, 0, 0, 200);
            TransportAssert(RtssOverlayFormat.Format(snapshot, MonitoringLayout.TopLeft, MonitoringMetrics.Fps, true, 1920, 1080, 0x12ABCD, 62, 0, 0, -1) == small
                && RtssOverlayFormat.Format(snapshot, MonitoringLayout.TopLeft, MonitoringMetrics.Fps, true, 1920, 1080, 0x12ABCD, 62, 0, 0, 1000) == large,
                "Renderer bounds caller-supplied scale to supported range");
            Settings.UseTransientStoreForCurrentProcess();
            try
            {
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Fps);
                MonitoringPreferences.TrySetPosition(90, 120);
                MonitoringPreferences.TrySetBackgroundOpacity(0);
                TransportAssert(RtssOverlayClient.FormatForViewport(snapshot, MonitoringLayout.TopBar, true, 319, 1080) == ""
                    && RtssOverlayClient.FormatForViewport(snapshot, MonitoringLayout.TopBar, true, 1920, 239) == ""
                    && RtssOverlayClient.FormatForViewport(snapshot, MonitoringLayout.TopBar, true, 1, 1) == "",
                    "Small real client rectangles suspend the HUD instead of creating a fallback full-HD surface");
                foreach (int scale in new[] { 60, 100, 200 })
                {
                    MonitoringPreferences.TrySetScalePercent(scale);
                    MonitoringOverlayGeometry model = MonitoringOverlayGeometry.Calculate(MonitoringLayout.TopRight, MonitoringMetrics.Fps, 1920, 1080, 90, 120, scale);
                    string published = RtssOverlayClient.FormatForViewport(snapshot, MonitoringLayout.TopRight, true, 1920, 1080);
                    TransportAssert(published.StartsWith("<FNT=" + MonitoringOverlayGeometry.FontFamily + ",-" + (model.FontPixels * RtssOverlayFormat.FontRasterFactor) + "," + MonitoringOverlayGeometry.FontWeight + ",1>")
                        && published.Contains("<P0><M=" + model.Bounds.X + "," + model.Bounds.Y + "," + -model.Bounds.X + "," + -model.Bounds.Y + "><L0>")
                        && !published.Contains("<B=") && !published.Contains("GPU") && !published.Contains("CPU"),
                        "Admitted foreground publication applies the atomic scale/opacity/position tuple and selected metrics");
                }
            }
            finally { Settings.UseTransientStoreForCurrentProcess(); }
        }
        private static void RtssSemanticColorsTests()
        {
            foreach (MonitoringLayout layout in Enum.GetValues(typeof(MonitoringLayout)))
            foreach (int accent in new[] { 0xFF0000, 0x00FF00, 0xAE70FF, 0xFFC857 })
            {
                foreach (double value in new[] { 79.9, 80.0, 87.9, 88.0, 100.0 })
                {
                    var snapshot = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", gpuTemperature: value);
                    string expected = value >= 88 ? "FF5B68" : value >= 80 ? "FFC857" : "F3F6FB";
                    string text = RtssOverlayFormat.Format(snapshot, layout, MonitoringMetrics.GpuTemperature, accentRgb: accent);
                    TransportAssert(HasRtssReading(text, value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " \u00B0C",
                        Int32.Parse(expected, System.Globalization.NumberStyles.HexNumber)),
                        "Temperature colors follow thresholds rather than the first cell or current mode");
                }
                foreach (double value in new[] { 84.9, 85.0, 94.9, 95.0, 100.0 })
                {
                    var snapshot = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", cpuTemperature: value);
                    string expected = value >= 95 ? "FF5B68" : value >= 85 ? "FFC857" : "F3F6FB";
                    string text = RtssOverlayFormat.Format(snapshot, layout, MonitoringMetrics.CpuTemperature, true, accentRgb: accent);
                    TransportAssert(text.Contains("<S=" + RtssOverlayFormat.TextScale(MonitoringOverlayGeometry.LabelScale, true) + ">" + MonitoringMetricPresentation.ShortName(MonitoringMetrics.CpuTemperature) + "<S>")
                        && HasRtssReading(text, value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " \u00B0C",
                            Int32.Parse(expected, System.Globalization.NumberStyles.HexNumber)),
                        "CPU temperature uses Celsius and its exact advisory boundaries independently of the mode");
                }
                foreach (double value in new[] { 84.9, 85.0, 94.9, 95.0, 100.0 })
                foreach (MonitoringMetrics metric in new[] { MonitoringMetrics.CpuUsage, MonitoringMetrics.RamUsage, MonitoringMetrics.VramUsage })
                {
                    var snapshot = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", cpuUsage: value, ramUsage: value, vramUsage: value);
                    string expected = value >= 95 ? "FF5B68" : value >= 85 ? "FFC857" : "F3F6FB";
                    TransportAssert(HasRtssReading(RtssOverlayFormat.Format(snapshot, layout, metric, accentRgb: accent),
                        value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%",
                        Int32.Parse(expected, System.Globalization.NumberStyles.HexNumber)),
                        "Resource pressure colors use their exact numeric boundaries");
                }
                var ordinary = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", fps: 144, gpuUsage: 100, gpuPower: 450, cpuPower: 180);
                string normal = RtssOverlayFormat.Format(ordinary, layout, MonitoringMetrics.Fps | MonitoringMetrics.GpuUsage | MonitoringMetrics.GpuPower | MonitoringMetrics.CpuPower, accentRgb: accent);
                TransportAssert(HasRtssReading(normal, "144", accent)
                    && HasRtssReading(normal, "100%", 0xF3F6FB)
                    && HasRtssReading(normal, "450 W", 0xF3F6FB) && HasRtssReading(normal, "180.0 W", 0xF3F6FB),
                    "FPS follows its theme; normal GPU saturation and arbitrary watts are not false alarms");
                foreach (double value in new[] { 0.0, 20.0, 144.0 })
                {
                    string fps = RtssOverlayFormat.Format(new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", fps: value),
                        layout, MonitoringMetrics.Fps, accentRgb: accent);
                    TransportAssert(HasRtssReading(fps, value.ToString("0", System.Globalization.CultureInfo.InvariantCulture), accent),
                        "FPS always uses the supplied theme color, including low and zero readings");
                }
                string missing = RtssOverlayFormat.Format(new MonitoringSnapshot(1234, "", DateTime.UtcNow, ""), layout,
                    MonitoringMetrics.Fps | MonitoringMetrics.GpuTemperature | MonitoringMetrics.CpuTemperature | MonitoringMetrics.RamUsage, accentRgb: accent);
                TransportAssert(CountRtssReadings(missing, "--", 0xB9C3D2) == 4,
                    "Unavailable readings remain dim without fabricating zero or an alarm");
                foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                {
                    string invalid = RtssOverlayFormat.Format(new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", cpuTemperature: value),
                        layout, MonitoringMetrics.CpuTemperature, accentRgb: accent);
                    TransportAssert(CountRtssReadings(invalid, "--", 0xB9C3D2) == 1 && !invalid.Contains(" \u00B0C<C>"),
                        "Non-finite CPU temperatures remain unknown rather than acquiring units or warning colors");
                }
            }
            var fan = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", gpuFanRpm: 0, gpuFanPercent: 110);
            TransportAssert(MonitoringMetricPresentation.Cells(fan, MonitoringMetrics.GpuFan)[0].Value == "0 RPM",
                "A stopped reported RPM fan remains a valid zero and takes priority over percent");
            fan = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", gpuFanPercent: 110);
            TransportAssert(MonitoringMetricPresentation.Cells(fan, MonitoringMetrics.GpuFan)[0].Value == "110%",
                "Fan speed percentages are not confused with RPM or silently capped at 100");
            var sample = MonitoringMetricPresentation.Example;
            TransportAssert(MonitoringMetricPresentation.Cells(sample, MonitoringMetrics.All).Count == 18,
                "Twenty sources form eighteen display items with one RAM and one VRAM cell");
            foreach (MonitoringLayout layout in Enum.GetValues(typeof(MonitoringLayout)))
            foreach (int width in new[] { 320, 640, 1280, 1920, 3840 })
            {
                string text = RtssOverlayFormat.Format(sample, layout, MonitoringMetrics.All, true, width, width == 320 ? 240 : 1080);
                var geometry = MonitoringOverlayGeometry.Calculate(layout, MonitoringMetrics.All, width, width == 320 ? 240 : 1080);
                AssertRtssPixelBar(text, MonitoringMetricPresentation.Cells(sample, MonitoringMetrics.All), geometry,
                    "expanded semantics / " + layout + " / " + width);
            }
        }

        private static void RtssMergedMemoryTests()
        {
            foreach (bool video in new[] { false, true })
            {
                MonitoringMetrics amount = video ? MonitoringMetrics.Vram : MonitoringMetrics.Ram;
                MonitoringMetrics usage = video ? MonitoringMetrics.VramUsage : MonitoringMetrics.RamUsage;
                string label = video ? "VRAM" : "RAM";
                foreach (MonitoringMetrics selected in new[] { amount, usage, amount | usage })
                foreach (double? capacity in new double?[] { 12698, 0, null, double.NaN })
                foreach (double? percent in new double?[] { 39, 85, 95, 0, null, double.PositiveInfinity })
                {
                    var snapshot = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", ramMiB: capacity,
                        vramMiB: capacity, ramUsage: percent, vramUsage: percent);
                    var cells = MonitoringMetricPresentation.Cells(snapshot, selected);
                    bool hasCapacity = capacity.HasValue && MonitoringFrameWindow.Finite(capacity.Value);
                    bool hasPercent = percent.HasValue && MonitoringFrameWindow.Finite(percent.Value);
                    string expectedAmount = !hasCapacity ? "" : capacity == 0 ? "0.0 GiB" : "12.4 GiB";
                    string expectedUsage = !hasPercent ? "" : percent.Value.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
                    string expectedValue = hasCapacity ? expectedAmount + (hasPercent ? " (" + expectedUsage + ")" : "")
                        : hasPercent ? expectedUsage : "--";
                    int expectedColor = hasPercent ? percent >= 95 ? MonitoringMetricPresentation.CriticalRgb
                        : percent >= 85 ? MonitoringMetricPresentation.WarningRgb : MonitoringMetricPresentation.NeutralRgb
                        : hasCapacity ? MonitoringMetricPresentation.NeutralRgb : MonitoringMetricPresentation.DimRgb;
                    TransportAssert(cells.Count == 1 && cells[0].Name == label && cells[0].Metric == (amount | usage)
                        && cells[0].Value == expectedValue && cells[0].ColorRgb == expectedColor,
                        "Memory pair and either legacy bit render one honest amount/usage cell with pressure colors");
                    TransportAssert(MonitoringOverlayGeometry.CellCount(selected) == 1,
                        "Memory geometry counts a display item rather than its source bits");
                    foreach (MonitoringLayout layout in Enum.GetValues(typeof(MonitoringLayout)))
                    {
                        string output = RtssOverlayFormat.Format(snapshot, layout, selected, true);
                        TransportAssert(Regex.Matches(output, Regex.Escape("<S=" + RtssOverlayFormat.TextScale(MonitoringOverlayGeometry.LabelScale, true) + ">" + MonitoringMetricPresentation.ShortName(amount | usage) + "<S>")).Count == 1
                            && CountRtssReadings(output, expectedValue, expectedColor) == 1,
                            "Every RTSS layout publishes the complete merged value once");
                    }
                }
            }
            var all = MonitoringMetricPresentation.Cells(MonitoringMetricPresentation.Example, MonitoringMetrics.All);
            string[] expectedLabels = { "FPS", "PRESENT", "AVG FPS", "1% LOW", "0.1% LOW", "FT STDDEV", "GAME CPU",
                "CPU POWER", "CPU TEMP", "GPU USAGE", "GPU TEMP", "GPU CLOCK", "VRAM CLOCK", "GPU POWER", "GPU FAN", "RAM", "GAME RAM", "VRAM" };
            TransportAssert(all.Count == expectedLabels.Length && MonitoringOverlayGeometry.CellCount(MonitoringMetrics.All) == all.Count,
                "Presentation and geometry agree on eighteen display items from twenty source values");
            for (int i = 0; i < all.Count; i++)
                TransportAssert(all[i].Name == expectedLabels[i], "HUD labels identify every measurement and the game process precisely");
        }

        private static void RtssActiveModeAccentTests()
        {
            Func<PerformancePreset> previousProvider = RtssOverlayClient.ActiveMode;
            PerformancePreset previousMode = Theme.CurrentMode;
            bool previousLight = Theme.LightMode;
            bool hadCompetitiveOverride = Theme.HasModeColorOverride(PerformancePreset.Competitive);
            bool hadHandheldOverride = Theme.HasModeColorOverride(PerformancePreset.Handheld);
            Color previousCompetitive = Theme.ModeColor(PerformancePreset.Competitive);
            Color previousHandheld = Theme.ModeColor(PerformancePreset.Handheld);
            try
            {
                Theme.SetMode(PerformancePreset.Standard, false);
                Theme.SetLight(false);
                Theme.ClearModeColorOverride(PerformancePreset.Competitive);
                Theme.ClearModeColorOverride(PerformancePreset.Handheld);
                PerformancePreset runtimeMode = PerformancePreset.Competitive;
                RtssOverlayClient.ActiveMode = delegate { return runtimeMode; };
                TransportAssert(Theme.CurrentMode == PerformancePreset.Standard
                    && RtssOverlayClient.AccentRgb() == (Theme.ModeColorBuiltin(PerformancePreset.Competitive).ToArgb() & 0xFFFFFF),
                    "Hidden-panel HUD follows competitive runtime mode while UI remains standard");
                runtimeMode = PerformancePreset.Handheld;
                TransportAssert(Theme.CurrentMode == PerformancePreset.Standard
                    && RtssOverlayClient.AccentRgb() == (Theme.ModeColorBuiltin(PerformancePreset.Handheld).ToArgb() & 0xFFFFFF),
                    "HUD reevaluates runtime mode changes without a UI theme tick");
                Theme.SetLight(true);
                TransportAssert(RtssOverlayClient.AccentRgb() == (Theme.ModeColorBuiltin(PerformancePreset.Handheld).ToArgb() & 0xFFFFFF),
                    "Runtime mode color honors the current light palette");
                Theme.SetModeColorOverride(PerformancePreset.Handheld, Color.FromArgb(0x12, 0xAB, 0xCD));
                TransportAssert(Theme.CurrentMode == PerformancePreset.Standard && RtssOverlayClient.AccentRgb() == 0x12ABCD,
                    "HUD uses the active mode's custom color rather than the standard UI accent");
                RtssOverlayClient.ActiveMode = null;
                TransportAssert(RtssOverlayClient.AccentRgb() == (Theme.ModeColor(PerformancePreset.Standard).ToArgb() & 0xFFFFFF),
                    "Without a runtime provider the HUD retains its UI mode fallback");
            }
            finally
            {
                if (hadCompetitiveOverride) Theme.SetModeColorOverride(PerformancePreset.Competitive, previousCompetitive);
                else Theme.ClearModeColorOverride(PerformancePreset.Competitive);
                if (hadHandheldOverride) Theme.SetModeColorOverride(PerformancePreset.Handheld, previousHandheld);
                else Theme.ClearModeColorOverride(PerformancePreset.Handheld);
                Theme.SetLight(previousLight);
                Theme.SetMode(previousMode, false);
                RtssOverlayClient.ActiveMode = previousProvider;
            }
            TransportAssert(RtssOverlayClient.ActiveMode == previousProvider && Theme.CurrentMode == previousMode
                && Theme.LightMode == previousLight
                && Theme.HasModeColorOverride(PerformancePreset.Competitive) == hadCompetitiveOverride
                && Theme.HasModeColorOverride(PerformancePreset.Handheld) == hadHandheldOverride
                && Theme.ModeColor(PerformancePreset.Competitive) == previousCompetitive
                && Theme.ModeColor(PerformancePreset.Handheld) == previousHandheld,
                "Accent test restores provider, UI mode, palette and exact override state");
        }

        private static void RtssFormatterViewportTests(MonitoringSnapshot snapshot)
        {
            int[,] displays = { { 1280, 720, 20 }, { 1920, 1080, 24 }, { 2560, 1440, 32 }, { 3840, 2160, 36 } };
            for (int index = 0; index < displays.GetLength(0); index++)
            {
                int width = displays[index, 0], height = displays[index, 1], expected = displays[index, 2];
                TransportAssert(RtssOverlayFormat.BaseFontPixels(height) == expected,
                    "Corner font follows actual viewport height: " + width + "x" + height);
                foreach (MonitoringLayout layout in new[] { MonitoringLayout.TopLeft, MonitoringLayout.TopRight })
                {
                    string output = RtssOverlayFormat.Format(snapshot, layout, MonitoringMetrics.LegacyAll,
                        fontOverride: true, viewportWidth: width, viewportHeight: height, accentRgb: 0x12ABCD);
                    string font = "<FNT=" + MonitoringOverlayGeometry.FontFamily + ",-" + (expected * RtssOverlayFormat.FontRasterFactor) + "," + MonitoringOverlayGeometry.FontWeight + ",1>";
                    var model = MonitoringOverlayGeometry.Calculate(layout, MonitoringMetrics.LegacyAll, width, height, snapshot: snapshot);
                    string margins = "<M=" + model.Bounds.X + "," + model.Bounds.Y + "," + -model.Bounds.X + "," + -model.Bounds.Y + ">";
                    TransportAssert(output.StartsWith(font + "<P0>") && output.Contains(margins),
                        "Native font and shared viewport corner position: " + layout + " at " + height + "p");
                    TransportAssert(output.Contains("<C=12ABCD>") && !output.Contains("<B=0,0,1>"),
                        "Mode accent emphasizes FPS while ordinary readings stay neutral and the panel stays borderless");
                    TransportAssert(Regex.IsMatch(output, @"<C=9E0A0C12><B=[1-9]\d*,[1-9]\d*>"),
                        "Panel uses alpha 9E with a graphite surface while game remains visible");
                    TransportAssert(output.Length < 4096 && !output.Contains("<untrusted>"),
                        "Viewport-aware formatting remains bounded and omits untrusted target names");
                }
            }
            TransportAssert(RtssOverlayFormat.BaseFontPixels(4320) == 36,
                "Very high resolution corners retain the maximum 36-pixel font");
            TransportAssert(RtssOverlayFormat.BaseFontPixels(0) == 24,
                "Missing viewport dimensions use readable 1080p defaults");

            foreach (int width in new[] { 320, 640, 800, 960, 1024, 1280, 1920, 2560, 3840 })
            foreach (int scale in new[] { 60, 100, 200 })
            {
                var geometry = MonitoringOverlayGeometry.Calculate(MonitoringLayout.TopBar, MonitoringMetrics.LegacyAll,
                    width, 1080, 0, 0, scale, snapshot);
                string output = RtssOverlayFormat.Format(snapshot, MonitoringLayout.TopBar, MonitoringMetrics.LegacyAll,
                    fontOverride: true, viewportWidth: width, viewportHeight: 1080, accentRgb: 0x24C9B8, scale: scale);
                TransportAssert(output.StartsWith("<FNT=" + MonitoringOverlayGeometry.FontFamily + ",-" + (geometry.FontPixels * RtssOverlayFormat.FontRasterFactor) + "," + MonitoringOverlayGeometry.FontWeight + ",1><P0>"),
                    "Top bar uses the shared font and viewport origin at width " + width);
                AssertRtssPixelBar(output, MonitoringMetricPresentation.Cells(snapshot, MonitoringMetrics.LegacyAll, 0x24C9B8),
                    geometry, "legacy / " + width + " / " + scale, 0x24C9B8);
                TransportAssert(HasRtssReading(output, "144", 0x24C9B8) && !output.Contains("<B=0,0,1>")
                    && Regex.IsMatch(output, @"<C=9E0A0C12><B=[1-9]\d*,[1-9]\d*>") && output.Length < 4096,
                    "Responsive bar preserves accent, opacity and owned RTSS slot capacity");
            }
            string legacy = RtssOverlayFormat.Format(snapshot, MonitoringLayout.TopLeft, MonitoringMetrics.Fps,
                fontOverride: false, viewportWidth: 1280, viewportHeight: 720, accentRgb: unchecked((int)0xFA12ABCD));
            var legacyGeometry = MonitoringOverlayGeometry.Calculate(MonitoringLayout.TopLeft, MonitoringMetrics.Fps, 1280, 720);
            TransportAssert(!legacy.Contains("<FNT=") && legacy.StartsWith("<P0><M=" + legacyGeometry.Bounds.X + ","
                + legacyGeometry.Bounds.Y + "," + -legacyGeometry.Bounds.X + "," + -legacyGeometry.Bounds.Y + ">")
                && legacy.Contains("<C=12ABCD>"),
                "Legacy renderer inherits its font while RGB input ignores any caller-supplied alpha");
            TransportAssert(RtssOverlayFormat.Format(null, MonitoringLayout.TopBar, MonitoringMetrics.All, true, 1280, 720, 0x12ABCD) == "",
                "Viewport parameters never fabricate an overlay for a missing snapshot");
        }

        private sealed class RtssPixelLayer
        {
            internal Rectangle Bounds;
            internal Size FillSize;
            internal bool HasExtent, HasFill;
            internal string Text;
            internal int ColorRgb = -1, FillArgb, Scale = 100;
        }

        private static List<RtssPixelLayer> ParseRtssPixelLayers(string output)
        {
            var layers = new List<RtssPixelLayer>();
            MatchCollection headers = Regex.Matches(output, @"(?:<P(\d+)>)?<M=(-?\d+),(-?\d+),(-?\d+),(-?\d+)><L(\d+)>");
            for (int index = 0; index < headers.Count; index++)
            {
                Match header = headers[index];
                int left = Int32.Parse(header.Groups[2].Value), top = Int32.Parse(header.Groups[3].Value);
                TransportAssert((!header.Groups[1].Success || header.Groups[1].Value == "0")
                    && Int32.Parse(header.Groups[6].Value) == index
                    && left == -Int32.Parse(header.Groups[4].Value) && top == -Int32.Parse(header.Groups[5].Value),
                    "Every pixel layer owns a sequential id and balanced viewport-origin margins");
                int start = header.Index + header.Length, end = index + 1 < headers.Count ? headers[index + 1].Index : output.Length;
                string body = output.Substring(start, end - start);
                MatchCollection extents = Regex.Matches(body, @"<E=(\d+),(\d+)>");
                TransportAssert(extents.Count <= 1, "A pixel layer has at most one explicit extent");
                var layer = new RtssPixelLayer { Bounds = new Rectangle(left, top,
                    extents.Count == 0 ? 0 : Int32.Parse(extents[0].Groups[1].Value),
                    extents.Count == 0 ? 0 : Int32.Parse(extents[0].Groups[2].Value)), HasExtent = extents.Count == 1,
                    Text = Regex.Replace(body, "<[^>]+>", "").Replace("\r", "") };
                Match scale = Regex.Match(body, @"<S=(\d+)>");
                if (scale.Success) layer.Scale = Int32.Parse(scale.Groups[1].Value) * (output.Contains("<FNT=") ? RtssOverlayFormat.FontRasterFactor : 1);
                Match color = Regex.Match(body, @"<C=([0-9A-F]{6})>");
                if (color.Success) layer.ColorRgb = Int32.Parse(color.Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
                Match fill = Regex.Match(body, @"<C=([0-9A-F]{8})><B=(-?\d+),(-?\d+)>");
                layer.HasFill = fill.Success;
                if (fill.Success)
                {
                    layer.FillArgb = unchecked((int)UInt32.Parse(fill.Groups[1].Value, System.Globalization.NumberStyles.HexNumber));
                    layer.FillSize = new Size(Int32.Parse(fill.Groups[2].Value), Int32.Parse(fill.Groups[3].Value));
                    if (!layer.HasExtent) layer.Bounds.Size = layer.FillSize;
                }
                layers.Add(layer);
            }
            TransportAssert(layers.Count > 0 && Regex.Matches(output, @"<L\d+>").Count == layers.Count,
                "Every emitted RTSS layer has a parsed absolute viewport position");
            return layers;
        }

        private static List<RtssPixelLayer> RtssTextLayers(List<RtssPixelLayer> layers)
        {
            var text = new List<RtssPixelLayer>();
            foreach (RtssPixelLayer layer in layers)
                if (layer.Text.Trim(' ', '\b', '\n').Length > 0) text.Add(layer);
            return text;
        }

        private static int ExpectedSurfaceCoverage(int x, int y, int width, int height, int cut)
        {
            if (y < cut)
            {
                int edge = width - cut + y;
                return x < edge ? 255 : x == edge ? 128 : 0;
            }
            if (y >= height - cut)
            {
                int edge = y - (height - cut);
                return x > edge ? 255 : x == edge ? 128 : 0;
            }
            return 255;
        }

        private static void AssertRtssSurface(List<RtssPixelLayer> layers, MonitoringOverlayGeometry geometry,
            int opacity, string context)
        {
            int width = geometry.Bounds.Width, height = geometry.Bounds.Height;
            int cut = Math.Min(3, Math.Max(2, geometry.FontPixels / 12));
            cut = Math.Min(cut, Math.Min((width - 1) / 2, (height - 1) / 2));
            TransportAssert(MonitoringOverlayGeometry.SurfaceRgb == 0x0A0C12
                && geometry.SurfaceCutPixels == cut && cut >= 0 && cut <= 3,
                "Graphite HUD uses opposing cuts capped at two or three physical pixels: " + context);
            TransportAssert(geometry.SurfaceFragments.Count == (cut == 0 ? 1 : 1 + 4 * cut)
                && geometry.SurfaceFragments.Count <= 13,
                "Antialiased clipped panel needs at most thirteen native rectangles: " + context);
            var surface = new Rectangle(0, 0, width, height);
            long coverageUnits = 0;
            foreach (MonitoringOverlayFill fragment in geometry.SurfaceFragments)
            {
                Rectangle box = fragment.Bounds;
                TransportAssert(box.Width > 0 && box.Height > 0 && surface.Contains(box)
                    && (fragment.Coverage == 255 || fragment.Coverage == 128),
                    "Every background fragment is positive, contained and either solid or half-covered: " + context);
                foreach (Point corner in new[] { box.Location, new Point(box.Right - 1, box.Top),
                    new Point(box.Left, box.Bottom - 1), new Point(box.Right - 1, box.Bottom - 1) })
                    TransportAssert(ExpectedSurfaceCoverage(corner.X, corner.Y, width, height, cut) == fragment.Coverage,
                        "Fragment corners match the opposing 45-degree cuts and half-covered diagonal: " + context);
                coverageUnits += (long)box.Width * box.Height * fragment.Coverage;
            }
            for (int i = 0; i < geometry.SurfaceFragments.Count; i++)
                for (int j = 0; j < i; j++)
                    TransportAssert(!geometry.SurfaceFragments[i].Bounds.IntersectsWith(geometry.SurfaceFragments[j].Bounds),
                        "Panel fragments never overlap and compound alpha: " + context);
            TransportAssert(coverageUnits == 255L * width * height - 255L * cut * cut + cut,
                "Clipped mask covers the complete panel with no holes, overlaps or hidden corner masks: " + context);
            TransportAssert(layers[0].HasExtent && !layers[0].HasFill && layers[0].Bounds == geometry.Bounds
                && layers[0].Text.Trim(' ', '\b', '\n').Length == 0,
                "Layer zero remains an empty surface extent, never an automatic fill or placeholder: " + context);
            int alpha = (Math.Max(0, Math.Min(100, opacity)) * 255 + 50) / 100, fills = 0;
            var seen = new HashSet<Rectangle>();
            bool reachedText = false;
            for (int i = 1; i < layers.Count; i++)
            {
                RtssPixelLayer layer = layers[i];
                if (!layer.HasFill) { reachedText = true; continue; }
                fills++;
                TransportAssert(!reachedText && !layer.HasExtent && layer.FillSize == layer.Bounds.Size
                    && layer.Text.Trim(' ', '\b', '\n').Length == 0 && layer.ColorRgb == -1,
                    "Background uses explicit pixel B rectangles before text, with no extra E or glyph draw: " + context);
                Rectangle local = layer.Bounds;
                local.Offset(-geometry.Bounds.X, -geometry.Bounds.Y);
                bool matched = false;
                foreach (MonitoringOverlayFill fragment in geometry.SurfaceFragments)
                    if (fragment.Bounds == local)
                    {
                        int coveredAlpha = (alpha * fragment.Coverage + 127) / 255;
                        TransportAssert(seen.Add(local) && unchecked((uint)layer.FillArgb) == ((uint)coveredAlpha << 24 | 0x0A0C12u),
                            "Native fill bounds, graphite color and per-pixel coverage alpha exactly match preview: " + context);
                        matched = true;
                    }
                TransportAssert(matched, "Native background has no extra rectangles, border or accent layers: " + context);
            }
            TransportAssert(fills == (alpha > 0 ? geometry.SurfaceFragments.Count : 0),
                "Zero opacity omits every fill; other opacities publish each background fragment once: " + context);
        }

        private static bool HasRtssReading(string output, string value, int rgb)
        {
            foreach (RtssPixelLayer layer in ParseRtssPixelLayers(output))
                if (layer.Text == value && layer.ColorRgb == rgb) return true;
            return false;
        }

        private static int CountRtssReadings(string output, string value, int rgb)
        {
            int count = 0;
            foreach (RtssPixelLayer layer in ParseRtssPixelLayers(output))
                if (layer.Text == value && layer.ColorRgb == rgb) count++;
            return count;
        }

        private static RtssPixelLayer FindRtssText(List<RtssPixelLayer> layers, Rectangle local,
            MonitoringOverlayGeometry geometry, string text, string context)
        {
            Point expected = new Point(local.Left + geometry.Bounds.Left, local.Top + geometry.Bounds.Top);
            RtssPixelLayer found = null;
            foreach (RtssPixelLayer layer in layers)
                if (layer.Bounds.Location == expected && layer.Text == text)
                {
                    TransportAssert(found == null, "Each text item is emitted exactly once: " + context);
                    found = layer;
                }
            TransportAssert(found != null, "RTSS contains the full text at its preview position: " + context + " / " + text);
            if (found != null && found.HasExtent)
                TransportAssert(found.Bounds.Size == local.Size, "Explicit text extents match preview geometry: " + context);
            return found;
        }

        private static void AssertRtssPixelBar(string output, List<MonitoringMetricCell> cells, MonitoringOverlayGeometry geometry, string context,
            int accentRgb = 0xAE70FF, int opacity = 62)
        {
            List<RtssPixelLayer> layers = ParseRtssPixelLayers(output);
            TransportAssert(!output.Contains("<A=") && !output.Contains("<P=") && !output.Contains("<P2>")
                && RtssTextEncoding.GetByteCount(output, 65001) < 4096,
                "Grouped HUD owns its viewport coordinates and fits the RTSS slot: " + context);
            bool supported = true;
            for (int i = 0; i < output.Length; i++)
                if (output[i] > 127 && !(output[i] == '\u00B0' && i + 1 < output.Length && output[i + 1] == 'C')) supported = false;
            TransportAssert(supported,
                "Native OSD contains only ASCII and the officially supported Celsius degree symbol: " + context);
            Rectangle background = layers[0].Bounds;
            TransportAssert(layers[0].HasExtent && background == geometry.Bounds
                && layers[0].Text.Trim(' ', '\b').Length == 0,
                "The background has exact shared geometry and contains no user-facing placeholder: " + context);
            if (geometry.IsCentered)
                TransportAssert(Math.Abs(background.Left * 2 + background.Width - geometry.ViewportWidth) <= 1
                    && background.Width < geometry.ViewportWidth,
                    "Top bar uses a centered backing that hugs its content rather than the entire viewport: " + context);
            TransportAssert(Regex.Matches(output, "<FNT=").Count == 1,
                "Native HUD defines one master font per publication without unverified per-layer font switching: " + context);

            var localSurface = new Rectangle(Point.Empty, background.Size);
            var seen = new HashSet<int>();
            var contentBoxes = new List<Rectangle>();
            int textLayers = 0;
            foreach (RtssPixelLayer layer in layers)
                if (layer.Text.Trim(' ', '\b', '\n').Length > 0) textLayers++;
            TransportAssert(textLayers == cells.Count * 2,
                "The inline OSD has exactly one label/value pair per selected metric without heading text: " + context);
            AssertRtssSurface(layers, geometry, opacity, context);
            TransportAssert(geometry.Separators.Count == 0
                && layers.Count == 1 + (opacity > 0 ? geometry.SurfaceFragments.Count : 0) + textLayers,
                "The HUD has only its empty extent, clipped background and measured readings, with no divider or outline layers: " + context);
            foreach (RtssPixelLayer layer in RtssTextLayers(layers))
                TransportAssert(!layer.HasFill,
                    "Metric text never introduces a line, border or extra panel fill: " + context);
            string[] order = { "FRAME", "GPU", "CPU", "MEMORY" };
            int previousGroup = -1;
            TransportAssert(geometry.Groups.Count > 0 && geometry.Groups.Count <= 4,
                "Only nonempty semantic groups are shown: " + context);
            for (int groupIndex = 0; groupIndex < geometry.Groups.Count; groupIndex++)
            {
                MonitoringOverlayGroup group = geometry.Groups[groupIndex];
                int rank = Array.IndexOf(order, group.Name);
                TransportAssert(rank > previousGroup && group.Items.Length > 0 && group.Columns > 0 && group.Rows > 0,
                    "Metric families remain ordered FRAME / GPU / CPU / MEMORY without empty groups: " + context);
                previousGroup = rank;
                TransportAssert(localSurface.Contains(group.Bounds) && group.HeaderBounds.IsEmpty,
                    "Metric families remain within the HUD without taking a visible heading row: " + context + " / " + group.Name);
                for (int other = 0; other < groupIndex; other++)
                    TransportAssert(!group.Bounds.IntersectsWith(geometry.Groups[other].Bounds),
                        "Semantic group surfaces never overlap: " + context);
                foreach (int index in group.Items)
                {
                    TransportAssert(index >= 0 && index < cells.Count && seen.Add(index),
                        "Each selected metric belongs to exactly one group: " + context);
                    MonitoringMetricCell cell = cells[index];
                    TransportAssert(MonitoringMetricPresentation.GroupName(cell.Metric) == group.Name,
                        "Every measurement appears beneath its true family: " + context + " / " + cell.Name);
                    Rectangle label = geometry.LabelBounds(index), value = geometry.ValueBounds(index);
                    TransportAssert(group.Bounds.Contains(label) && group.Bounds.Contains(value)
                        && !label.IntersectsWith(value) && label.Width > 0 && value.Width > 0,
                        "Labels and readings are readable within their group: " + context + " / " + cell.Name);
                    string shortName = MonitoringMetricPresentation.ShortName(cell);
                    RtssPixelLayer labelLayer = FindRtssText(layers, label, geometry, shortName, context + " label");
                    RtssPixelLayer valueLayer = FindRtssText(layers, value, geometry, cell.Value, context + " value");
                    TransportAssert(labelLayer.Scale == MonitoringOverlayGeometry.LabelScale && valueLayer.Scale == geometry.ValueScale(index)
                        && labelLayer.ColorRgb == (cell.Metric == MonitoringMetrics.Fps ? accentRgb & 0xFFFFFF : MonitoringMetricPresentation.LabelRgb)
                        && valueLayer.ColorRgb == cell.ColorRgb,
                        "Live labels, emphasis and semantic colors match the preview: " + context + " / " + cell.Name);
                    TransportAssert(geometry.ValueScale(index) == (cell.Metric == MonitoringMetrics.Fps
                        ? geometry.IsCentered ? MonitoringOverlayGeometry.BarFpsScale : MonitoringOverlayGeometry.FpsScale
                        : MonitoringOverlayGeometry.DefaultValueScale),
                        "FPS uses the preset's primary emphasis while other values retain the shared supporting type scale: " + context);
                    TransportAssert(!geometry.IsStacked(index) || (!geometry.IsCentered && group.Name == "FRAME"
                        && (cells[0].Metric & MonitoringMetrics.Fps) != 0 && label.Bottom <= value.Top),
                        "The corner frame hero puts labels above values without stacking hardware or top-bar readings: " + context);
                    contentBoxes.Add(label); contentBoxes.Add(value);
                }
            }
            TransportAssert(seen.Count == cells.Count, "Grouping preserves every selected metric: " + context);
            for (int item = 0; item < contentBoxes.Count; item++)
                for (int previous = 0; previous < item; previous++)
                    TransportAssert(!contentBoxes[item].IntersectsWith(contentBoxes[previous]),
                        "No metric name or reading overlaps another text item: " + context);
        }

        private static void RtssBalancedBarTests()
        {
            TransportAssert(MonitoringOverlayGeometry.FontFamily == "Bahnschrift SemiCondensed" && MonitoringOverlayGeometry.FontWeight == 700
                && MonitoringOverlayGeometry.HudFontStyle == FontStyle.Bold
                && MonitoringOverlayGeometry.LabelScale == 76 && MonitoringOverlayGeometry.DefaultValueScale == 96
                && MonitoringOverlayGeometry.FpsScale == 160 && MonitoringOverlayGeometry.BarFpsScale == 136
                && MonitoringMetricPresentation.ShortName(MonitoringMetrics.Fps) == "// FPS",
                "ROG HUD uses condensed bold technical typography with a slash caption and distinct corner/bar FPS emphasis");
            MonitoringMetrics[] qualifiedMetrics = { MonitoringMetrics.CpuUsage, MonitoringMetrics.CpuPower,
                MonitoringMetrics.CpuTemperature, MonitoringMetrics.GpuUsage, MonitoringMetrics.GpuTemperature,
                MonitoringMetrics.GpuPower, MonitoringMetrics.GameRam };
            string[] qualifiedLabels = { "GAME CPU", "CPU PWR", "CPU TEMP", "GPU", "GPU TEMP", "GPU PWR", "GAME RAM" };
            for (int i = 0; i < qualifiedMetrics.Length; i++)
                TransportAssert(MonitoringMetricPresentation.ShortName(qualifiedMetrics[i]) == qualifiedLabels[i],
                    "Standalone inline labels identify hardware and game-specific readings without heading rows");
            foreach (MonitoringLayout layout in new[] { MonitoringLayout.TopLeft, MonitoringLayout.TopRight, MonitoringLayout.TopBar })
            {
                var geometry = MonitoringOverlayGeometry.Calculate(layout, MonitoringMetrics.Default, 1920, 1080);
                var defaultCells = MonitoringMetricPresentation.Cells(MonitoringMetricPresentation.Example, MonitoringMetrics.Default);
                for (int i = 0; i < defaultCells.Count; i++)
                {
                    bool hero = layout != MonitoringLayout.TopBar
                        && (defaultCells[i].Metric == MonitoringMetrics.Fps || defaultCells[i].Metric == MonitoringMetrics.FrameTime);
                    TransportAssert(geometry.IsStacked(i) == hero,
                        "Default corner has a paired FPS/frame-time hero; default top bar and hardware remain inline");
                }
                TransportAssert(layout == MonitoringLayout.TopBar
                    ? geometry.Bounds.Width <= 800 && geometry.Bounds.Height <= 72
                    : geometry.Bounds.Width <= 360 && geometry.Bounds.Height <= 180,
                    "Default four-metric HUD stays compact instead of reproducing the previous full-size grouped panel");
                var noFps = MonitoringOverlayGeometry.Calculate(layout, MonitoringMetrics.Default & ~MonitoringMetrics.Fps, 1920, 1080);
                for (int i = 0; i < MonitoringOverlayGeometry.CellCount(MonitoringMetrics.Default & ~MonitoringMetrics.Fps); i++)
                    TransportAssert(!noFps.IsStacked(i), "Removing FPS returns every remaining reading to an inline layout");
            }
            var compact = MonitoringOverlayGeometry.Calculate(MonitoringLayout.TopBar,
                MonitoringMetrics.Fps | MonitoringMetrics.SystemMemory, 1920, 1080);
            TransportAssert(compact.Groups.Count == 2 && compact.Groups[0].Name == "FRAME" && compact.Groups[1].Name == "MEMORY",
                "Sparse selections retain their semantic identities without placeholder hardware groups");

            MonitoringMetrics[] selections = { MonitoringMetrics.Fps, MonitoringMetrics.Default,
                MonitoringMetrics.All, MonitoringMetrics.All & ~MonitoringMetrics.FrameTime,
                MonitoringMetrics.LegacyAll | MonitoringMetrics.GpuFan,
                MonitoringMetrics.GpuPower | MonitoringMetrics.CpuPower | MonitoringMetrics.GameRam };
            int[] counts = { 1, 4, 18, 17, 11, 3 };
            for (int selection = 0; selection < selections.Length; selection++)
            foreach (MonitoringLayout layout in Enum.GetValues(typeof(MonitoringLayout)))
            foreach (int width in new[] { 320, 640, 800, 960, 1024, 1280, 1920, 2560, 3840, 5120, 7680, 16384 })
            foreach (int scale in new[] { 60, 100, 140, 200 })
            foreach (int height in width == 320 ? new[] { 240, 1080 } : new[] { 1080 })
            {
                MonitoringMetrics metrics = selections[selection];
                var cells = MonitoringMetricPresentation.Cells(MonitoringMetricPresentation.Example, metrics, 0x24C9B8);
                var geometry = MonitoringOverlayGeometry.Calculate(layout, metrics, width, height, 0, 0, scale);
                string output = RtssOverlayFormat.Format(MonitoringMetricPresentation.Example, layout, metrics,
                    true, width, height, 0x24C9B8, 62, 0, 0, scale);
                string context = layout + " / " + counts[selection] + " items / " + width + "x" + height + " / " + scale + "%";
                TransportAssert(cells.Count == counts[selection], "Grouped layout exercises the intended selection: " + context);
                TransportAssert(geometry.Bounds.Left >= 0 && geometry.Bounds.Right <= width
                    && geometry.Bounds.Top >= 0 && geometry.Bounds.Bottom <= height, "Grouped HUD stays inside its viewport: " + context);
                if (layout == MonitoringLayout.TopBar)
                    TransportAssert(output == RtssOverlayFormat.Format(MonitoringMetricPresentation.Example, layout, metrics,
                        true, width, height, 0x24C9B8, 62, 1000, 0, scale),
                        "Centered top bar ignores horizontal offsets without moving text or resizing its content-sized backing: " + context);
                AssertRtssPixelBar(output, cells, geometry, context, 0x24C9B8);
            }
        }

        private static void RtssFanGeometryStabilityTests()
        {
            MonitoringSnapshot[] samples = {
                new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", fps: 144),
                new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", fps: 144, gpuFanPercent: 0),
                new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", fps: 144, gpuFanPercent: 110),
                new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", fps: 144, gpuFanRpm: 0),
                new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", fps: 144, gpuFanRpm: 1460),
                new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", fps: 144, gpuFanRpm: 99999)
            };
            string[] values = { "--", "0%", "110%", "0 RPM", "1460 RPM", "99999 RPM" };
            foreach (MonitoringLayout layout in Enum.GetValues(typeof(MonitoringLayout)))
            foreach (MonitoringMetrics metrics in new[] { MonitoringMetrics.Fps | MonitoringMetrics.GpuFan, MonitoringMetrics.All })
            foreach (int width in new[] { 320, 640, 1280, 1920 })
            foreach (int scale in new[] { 60, 100, 200 })
            {
                var baseline = MonitoringOverlayGeometry.Calculate(layout, metrics, width, 1080, 0, 0, scale, samples[0]);
                for (int sample = 0; sample < samples.Length; sample++)
                {
                    var geometry = MonitoringOverlayGeometry.Calculate(layout, metrics, width, 1080, 0, 0, scale, samples[sample]);
                    MonitoringMetricCell fan = MonitoringMetricPresentation.Cells(samples[sample], MonitoringMetrics.GpuFan)[0];
                    TransportAssert(fan.Value == values[sample] && fan.ValueCharacters == 10,
                        "Missing, percent and RPM fan readings reserve the widest supported unit");
                    TransportAssert(geometry.Bounds == baseline.Bounds && geometry.FontPixels == baseline.FontPixels
                        && geometry.Groups.Count == baseline.Groups.Count,
                        "Fan availability and unit changes do not resize or reflow the HUD: " + layout + " / " + width + " / " + scale);
                    for (int group = 0; group < geometry.Groups.Count; group++)
                        TransportAssert(geometry.Groups[group].Bounds == baseline.Groups[group].Bounds
                            && geometry.Groups[group].HeaderBounds == baseline.Groups[group].HeaderBounds
                            && geometry.Groups[group].Columns == baseline.Groups[group].Columns
                            && geometry.Groups[group].Rows == baseline.Groups[group].Rows,
                            "Sensor unit changes leave group surfaces and heading origins stable");
                    for (int item = 0; item < MonitoringOverlayGeometry.CellCount(metrics); item++)
                        TransportAssert(geometry.LabelBounds(item) == baseline.LabelBounds(item)
                            && geometry.ValueBounds(item) == baseline.ValueBounds(item),
                            "Fan updates keep all metric text in exactly the same place");
                }
            }
        }

        private static MonitoringSnapshot RtssRetainedPresentationSample(MonitoringMetrics retained)
        {
            return new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", fps: 144, frameTimeMs: 6.9, low1: 112,
                cpuUsage: 28, gpuUsage: 100, gpuTemperature: 62, gpuClock: 2460, gpuPower: 185,
                ramMiB: 2048, vramMiB: 1024, averageFps: 138, low01: 86, frameTimeDeviation: 1.2,
                gameRamMiB: 512, ramUsage: 50, vramUsage: 25, gpuMemoryClock: 10501, gpuFanRpm: 0,
                cpuPower: 54.8, cpuTemperature: 65, retainedHardwareMetrics: retained);
        }
        private static void RtssRetainedHardwarePresentationTests()
        {
            const MonitoringMetrics hardware = MonitoringMetrics.CpuPower | MonitoringMetrics.CpuTemperature
                | MonitoringMetrics.GpuUsage | MonitoringMetrics.GpuTemperature | MonitoringMetrics.GpuClock
                | MonitoringMetrics.GpuMemoryClock | MonitoringMetrics.GpuPower | MonitoringMetrics.GpuFan | MonitoringMetrics.VideoMemory;
            var fresh = RtssRetainedPresentationSample(MonitoringMetrics.None);
            var retained = RtssRetainedPresentationSample((MonitoringMetrics)(-1));
            var invalidMask = RtssRetainedPresentationSample((MonitoringMetrics.All & ~hardware) | (MonitoringMetrics)(1 << 24));
            var missing = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", retainedHardwareMetrics: (MonitoringMetrics)(-1));
            var freshCells = MonitoringMetricPresentation.Cells(fresh, MonitoringMetrics.All);
            var retainedCells = MonitoringMetricPresentation.Cells(retained, MonitoringMetrics.All);
            var invalidCells = MonitoringMetricPresentation.Cells(invalidMask, MonitoringMetrics.All);
            var missingCells = MonitoringMetricPresentation.Cells(missing, MonitoringMetrics.All);
            TransportAssert(retainedCells.Count == freshCells.Count && freshCells.Count == 18,
                "Retaining values does not add metric cells or alter selected metrics");
            for (int i = 0; i < freshCells.Count; i++)
            {
                bool expected = (freshCells[i].Metric & hardware) != 0;
                TransportAssert(retainedCells[i].Retained == expected
                    && retainedCells[i].Value == (expected ? "~" : "") + freshCells[i].Value
                    && retainedCells[i].Value.IndexOf('~') == (expected ? 0 : -1)
                    && retainedCells[i].Value.LastIndexOf('~') == (expected ? 0 : -1),
                    "Only retained hardware values have exactly one leading marker: " + freshCells[i].Metric);
                TransportAssert(retainedCells[i].Reading == freshCells[i].Reading && retainedCells[i].ColorRgb == freshCells[i].ColorRgb,
                    "Retained markers preserve numeric readings and semantic colors");
                TransportAssert(freshCells[i].ValueCharacters == retainedCells[i].ValueCharacters
                    && retainedCells[i].ValueCharacters >= retainedCells[i].Value.Length,
                    "Fresh and retained values reserve identical complete widths");
                TransportAssert(!invalidCells[i].Retained && invalidCells[i].Value == freshCells[i].Value,
                    "Unrelated and unknown mask bits cannot mark a current value");
                TransportAssert(!missingCells[i].Retained && missingCells[i].Value == "--",
                    "Missing hardware is never formatted as a retained numeric value");
            }
            var onlyPercent = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", vramUsage: 25,
                retainedHardwareMetrics: MonitoringMetrics.Vram);
            var onlyCapacity = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", vramMiB: 1024,
                retainedHardwareMetrics: MonitoringMetrics.VramUsage);
            TransportAssert(MonitoringMetricPresentation.Cells(onlyPercent, MonitoringMetrics.VideoMemory)[0].Value == "25%"
                && MonitoringMetricPresentation.Cells(onlyCapacity, MonitoringMetrics.VideoMemory)[0].Value == "1.0 GiB",
                "A missing retained VRAM component cannot mark the other fresh component");
            foreach (double bad in new[] { Double.NaN, Double.PositiveInfinity })
            {
                var invalid = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", gpuTemperature: bad,
                    retainedHardwareMetrics: MonitoringMetrics.GpuTemperature);
                var cell = MonitoringMetricPresentation.Cells(invalid, MonitoringMetrics.GpuTemperature)[0];
                TransportAssert(cell.Value == "--" && !cell.Retained, "Nonfinite cached values remain unavailable");
            }
            foreach (MonitoringLayout layout in Enum.GetValues(typeof(MonitoringLayout)))
            foreach (int width in new[] { 320, 1920 })
            foreach (int scale in new[] { 60, 100, 200 })
            {
                var baseline = MonitoringOverlayGeometry.Calculate(layout, MonitoringMetrics.All, width, 1080, scale: scale, snapshot: fresh);
                foreach (MonitoringSnapshot sample in new[] { retained, fresh, missing })
                {
                    var geometry = MonitoringOverlayGeometry.Calculate(layout, MonitoringMetrics.All, width, 1080, scale: scale, snapshot: sample);
                    TransportAssert(geometry.Bounds == baseline.Bounds && geometry.FontPixels == baseline.FontPixels,
                        "Retained, recovered and absent values preserve the HUD surface: " + layout + "/" + width + "/" + scale);
                    for (int i = 0; i < freshCells.Count; i++)
                        TransportAssert(geometry.LabelBounds(i) == baseline.LabelBounds(i) && geometry.ValueBounds(i) == baseline.ValueBounds(i),
                            "Retained availability changes cannot move metric labels or readings");
                }
                string output = RtssOverlayFormat.Format(retained, layout, MonitoringMetrics.All, true, width, 1080, scale: scale);
                string recovered = RtssOverlayFormat.Format(fresh, layout, MonitoringMetrics.All, true, width, 1080, scale: scale);
                TransportAssert(HasRtssReading(output, "~62 \u00B0C", MonitoringMetricPresentation.NeutralRgb)
                    && HasRtssReading(output, "~54.8 W", MonitoringMetricPresentation.NeutralRgb)
                    && HasRtssReading(output, "~0 RPM", MonitoringMetricPresentation.NeutralRgb)
                    && HasRtssReading(output, "~1.0 GiB (25%)", MonitoringMetricPresentation.NeutralRgb),
                    "Native RTSS preserves one marker and exact hardware units");
                TransportAssert(HasRtssReading(output, "144", MonitoringMetricPresentation.DefaultAccentRgb)
                    && HasRtssReading(output, "28%", MonitoringMetricPresentation.NeutralRgb)
                    && HasRtssReading(output, "2.0 GiB (50%)", MonitoringMetricPresentation.NeutralRgb),
                    "FPS, process CPU and system memory never inherit retained hardware markers");
                TransportAssert(recovered.IndexOf('~') < 0 && HasRtssReading(recovered, "62 \u00B0C", MonitoringMetricPresentation.NeutralRgb),
                    "A recovered fresh hardware value immediately loses its cached marker");
                TransportAssert(RtssTextEncoding.GetByteCount(output, 65001) < 4096,
                    "Retained markers stay within the native owner slot");
            }
        }

        private static void RtssExtremeViewportPayloadTests()
        {
            var widest = new MonitoringSnapshot(1234, "", DateTime.MinValue, "", fps: 99999,
                frameTimeMs: 9999.9, low1: 99999, cpuUsage: 100, gpuUsage: 100,
                gpuTemperature: 999, gpuClock: 99999, gpuPower: 99999,
                ramMiB: 9999.9 * 1024, vramMiB: 9999.9 * 1024, averageFps: 99999,
                low01: 99999, frameTimeDeviation: 9999.9, gameRamMiB: 9999.9 * 1024,
                ramUsage: 100, vramUsage: 100, gpuMemoryClock: 99999, gpuFanRpm: 99999,
                cpuPower: 9999.9, cpuTemperature: 999, frameTimeFromGpu: true);
            foreach (MonitoringSnapshot sample in new[] { MonitoringMetricPresentation.Example, widest })
            foreach (MonitoringLayout layout in Enum.GetValues(typeof(MonitoringLayout)))
            foreach (Point position in new[] { new Point(1000, 1000), new Point(-1000, -1000), Point.Empty })
            {
                var geometry = MonitoringOverlayGeometry.Calculate(layout, MonitoringMetrics.All,
                    16384, 16384, position.X, position.Y, 200, sample);
                string output = RtssOverlayFormat.Format(sample, layout, MonitoringMetrics.All, true,
                    16384, 16384, 0x12ABCD, 100, position.X, position.Y, 200);
                AssertRtssPixelBar(output, MonitoringMetricPresentation.Cells(sample, MonitoringMetrics.All, 0x12ABCD),
                    geometry, "maximum viewport / maximum size / " + layout + " / " + position, 0x12ABCD, 100);
                TransportAssert(ParseRtssPixelLayers(output).Count == 50 && output.Length < 4096,
                    "Even longest reserved readings, maximum geometry and all thirteen background quads fit the owner slot");
            }
        }

        private static void RtssSensorAvailabilityGeometryTests()
        {
            var unknown = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "");
            var zero = new MonitoringSnapshot(1234, "", DateTime.UtcNow, "", fps: 0, frameTimeMs: 0,
                cpuUsage: 0, gpuUsage: 0, gpuTemperature: 0, gpuClock: 0, gpuPower: 0,
                ramMiB: 0, vramMiB: 0, ramUsage: 0, vramUsage: 0, gpuFanRpm: 0,
                cpuTemperature: 0, cpuPower: 0, gpuMemoryClock: 0, gameRamMiB: 0);
            foreach (MonitoringLayout layout in Enum.GetValues(typeof(MonitoringLayout)))
            foreach (MonitoringMetrics metrics in new[] { MonitoringMetrics.Default, MonitoringMetrics.All })
            foreach (int width in new[] { 320, 1280, 1920, 3840 })
            {
                var baseline = MonitoringOverlayGeometry.Calculate(layout, metrics, width, 1080, snapshot: unknown);
                foreach (MonitoringSnapshot snapshot in new[] { zero, MonitoringMetricPresentation.Example })
                {
                    var live = MonitoringOverlayGeometry.Calculate(layout, metrics, width, 1080, snapshot: snapshot);
                    TransportAssert(live.Bounds == baseline.Bounds && live.FontPixels == baseline.FontPixels
                        && live.Groups.Count == baseline.Groups.Count,
                        "Missing, zero and ordinary readings keep the same HUD surface and group count");
                    for (int item = 0; item < MonitoringOverlayGeometry.CellCount(metrics); item++)
                        TransportAssert(live.LabelBounds(item) == baseline.LabelBounds(item)
                            && live.ValueBounds(item) == baseline.ValueBounds(item),
                            "Sensor availability never makes labels or numeric columns jump during play");
                }
            }
        }
    }
}

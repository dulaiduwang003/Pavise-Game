#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunRtssComponentTests()
        {
            RunRtssDialogFixtureIfRequested();
            string root = Path.Combine(Path.GetTempPath(), "PaviseRtssComponentChecks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string expected = Path.Combine(root, "RTSS.exe");
                RtssAssert(RtssComponent.NormalizeCandidate(root) == expected, "Install directory normalization");
                RtssAssert(RtssComponent.NormalizeCandidate("\"" + expected + "\"") == expected, "Quoted executable normalization");
                RtssAssert(RtssComponent.NormalizeCandidate("RTSS.exe") == "", "Relative path rejected");
                RtssAssert(RtssComponent.NormalizeCandidate(Path.Combine(root, "other.exe")) == "", "Unrelated executable rejected");
                RtssAssert(RtssComponent.RegisteredVersion(expected, root, null, "7.3.7") == "7.3.7",
                    "Matching installation supplies official release version despite stale executable metadata");
                RtssAssert(RtssComponent.RegisteredVersion(expected, null, "\"" + expected + "\",0", "7.3.7") == "7.3.7",
                    "Quoted display icon matches the exact executable");
                RtssAssert(RtssComponent.RegisteredVersion(expected, root.ToUpperInvariant(), null, "7.3.7") == "7.3.7",
                    "Windows installation paths compare without case sensitivity");
                RtssAssert(RtssComponent.RegisteredVersion(expected, Path.Combine(root, "other"), null, "7.3.7") == ""
                    && RtssComponent.RegisteredVersion(expected, null, Path.Combine(root, "other.exe") + ",0", "7.3.7") == "",
                    "Another installation or executable cannot supply a release version");
                RtssAssert(RtssComponent.RegisteredVersion(expected, null, root, "7.3.7") == "",
                    "A display icon must name the exact executable, not a directory");
                string siblingUninstaller = "\"" + Path.Combine(root, "uninstall.exe") + "\"";
                RtssAssert(RtssComponent.RegisteredUninstallerVersion(expected, "RTSS", "RivaTuner Statistics Server 7.3.7",
                    siblingUninstaller + ",0", null, "7.3.7") == "7.3.7",
                    "Official NSIS display icon resolves only the verified executable's sibling installation");
                RtssAssert(RtssComponent.RegisteredUninstallerVersion(expected, "RTSS", "RTSS", null,
                    siblingUninstaller + " /S", "7.3.7") == "7.3.7",
                    "Quoted exact sibling uninstall command can supply release version");
                RtssAssert(RtssComponent.RegisteredUninstallerVersion(expected, "Other", "RTSS",
                    siblingUninstaller, null, "7.3.7") == ""
                    && RtssComponent.RegisteredUninstallerVersion(expected, "RTSS", "Another product",
                    siblingUninstaller, null, "7.3.7") == ""
                    && RtssComponent.RegisteredUninstallerVersion(expected, "RTSS", "RivaTuner Statistics Server Other",
                    siblingUninstaller, null, "7.3.7") == "",
                    "Sibling fallback requires the exact RTSS registry key and product name");
                RtssAssert(RtssComponent.RegisteredUninstallerVersion(expected, "RTSS", "RTSS",
                    Path.Combine(root, "other", "uninstall.exe"), null, "7.3.7") == ""
                    && RtssComponent.RegisteredUninstallerVersion(expected, "RTSS", "RTSS",
                    Path.Combine(root, "other.exe"), null, "7.3.7") == ""
                    && RtssComponent.RegisteredUninstallerVersion(expected, "RTSS", "RTSS",
                    "uninstall.exe", null, "7.3.7") == "",
                    "Sibling fallback rejects other folders, filenames and relative commands");
                foreach (string version in new[] { "", "7.3.7 beta", "0.0.0", "garbage" })
                    RtssAssert(RtssComponent.RegisteredVersion(expected, root, null, version) == "",
                        "Invalid release registration rejected: " + version);
                RtssAssert(new RtssComponentInfo { Version = "7.3.7" }.SupportsFontOverride
                    && !new RtssComponentInfo { Version = "7.3.5.28314" }.SupportsFontOverride,
                    "Font override requires the 7.3.7 release");
                RtssAssert(new RtssComponentInfo { Version = "7.3.2" }.SupportsOverlay
                    && !new RtssComponentInfo { Version = "7.3.1" }.SupportsOverlay,
                    "Existing overlay minimum version remains 7.3.2");

                RtssComponentBackend backend = RtssMockBackend(expected);
                backend.Version = delegate { return ""; };
                RtssAssert(!RtssComponent.Detect(backend).Installed, "Invalid metadata cannot prove installation");
                backend.Version = delegate { return "7.3.7.28314"; };
                RtssComponentInfo detected = RtssComponent.Detect(backend);
                RtssAssert(detected.Installed && !detected.Running, "Installed and running are distinct");
                backend.Running = delegate { return true; };
                RtssAssert(RtssComponent.Detect(backend).Running, "Running detection");
                backend.Elevated = delegate { throw new InvalidOperationException("Must preserve existing install"); };
                backend.Download = delegate { throw new InvalidOperationException("Must not overwrite existing install"); };
                RtssAssert(RtssComponent.Install(backend, null, CancellationToken.None).Installed,
                    "Existing install bypasses elevation and download");

                backend = RtssMockBackend(expected);
                backend.Candidates = delegate { return new string[0]; };
                backend.Elevated = delegate { return false; };
                backend.Download = delegate { throw new InvalidOperationException("Download must not start"); };
                RtssAssert(RtssComponent.Install(backend, null, CancellationToken.None).Error.Contains("管理员"),
                    "Silent install refuses an elevation dialog");

                backend.InstallerRunning = delegate { return true; };
                RtssAssert(RtssComponent.Install(backend, null, CancellationToken.None).Error.Contains("正在进行"),
                    "A pending background installer prevents duplicate installation");

                backend = RtssMockBackend(expected);
                backend.Candidates = delegate { throw new InvalidOperationException("Cancelled before detect"); };
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    RtssAssert(RtssComponent.Install(backend, null, cancelled.Token).Error.Contains("取消"), "Pre-download cancellation");
                }

                backend = RtssMockBackend(expected);
                backend.Candidates = delegate { return new string[0]; };
                bool ran = false, extracted = false;
                backend.Verify = delegate { throw new InvalidDataException("checksum rejected"); };
                backend.Extract = delegate { extracted = true; };
                backend.RunInstaller = delegate { ran = true; return 0; };
                RtssAssert(RtssComponent.Install(backend, null, CancellationToken.None).Error == "checksum rejected"
                    && !extracted && !ran, "Bad checksum cannot reach extraction or execution");

                backend = RtssMockBackend(expected);
                int detections = 0;
                backend.Candidates = delegate { return ++detections == 1 ? new string[0] : new[] { expected }; };
                backend.RunInstaller = delegate { throw new InvalidOperationException("Race must preserve external install"); };
                RtssAssert(RtssComponent.Install(backend, null, CancellationToken.None).Installed,
                    "Concurrent external install preserved after download");

                backend = RtssMockBackend(expected);
                bool installed = false;
                var phases = new List<string>();
                backend.Candidates = delegate { return installed ? new[] { expected } : new string[0]; };
                backend.RunInstaller = delegate { installed = true; return 3010; };
                RtssComponentInfo result = RtssComponent.Install(backend,
                    delegate(RtssInstallProgress p) { phases.Add(p.Phase); }, CancellationToken.None);
                RtssAssert(result.Installed && result.RestartRequired && !result.Running,
                    "Success verifies install, reports restart, does not launch RTSS");
                RtssAssert(phases.SequenceEqual(new[] { "Checking", "Verifying", "Installing", "Checking", "Ready" }),
                    "Install progress sequence");

                backend = RtssMockBackend(expected);
                backend.Candidates = delegate { return new string[0]; };
                backend.RunInstaller = delegate { return 2; };
                result = RtssComponent.Install(backend, null, CancellationToken.None);
                RtssAssert(!result.Installed && result.Error.Contains("退出码 2"), "Non-success installer exit rejected");
                backend.RunInstaller = delegate { return 0; };
                RtssAssert(!RtssComponent.Install(backend, null, CancellationToken.None).Installed,
                    "Exit zero alone cannot prove installation");

                ProcessStartInfo start = RtssComponent.InstallerStartInfo(Path.Combine(root, RtssComponent.InstallerName));
                RtssAssert(start.Arguments == "/S" && !start.UseShellExecute && start.CreateNoWindow
                    && start.WindowStyle == ProcessWindowStyle.Hidden && !start.ErrorDialog
                    && String.IsNullOrEmpty(start.Verb), "Installer has no visible window or runas route");

                byte[] body = Encoding.UTF8.GetBytes("isolated RTSS ZIP fixture; never executable");
                foreach (bool compressed in new[] { false, true })
                {
                    string archive = Path.Combine(root, "valid-" + compressed + ".zip");
                    File.WriteAllBytes(archive, RtssZip(new[] { RtssComponent.InstallerName }, body, compressed));
                    string output = Path.Combine(root, "unpacked-" + compressed);
                    RtssComponent.ExtractInstaller(archive, output, CancellationToken.None);
                    RtssAssert(File.ReadAllBytes(output).SequenceEqual(body), "Exact stored/deflated extraction " + compressed);
                }
                string invalid = Path.Combine(root, "invalid.zip");
                File.WriteAllBytes(invalid, RtssZip(new[] { "../" + RtssComponent.InstallerName }, body, false));
                RtssRejectZip(invalid, Path.Combine(root, "unsafe"), "Path traversal is never extracted");
                File.WriteAllBytes(invalid, RtssZip(new[] { RtssComponent.InstallerName, RtssComponent.InstallerName }, body, false));
                RtssRejectZip(invalid, Path.Combine(root, "duplicate"), "Duplicate installer rejected");
                byte[] truncated = RtssZip(new[] { RtssComponent.InstallerName }, body, false);
                File.WriteAllBytes(invalid, truncated.Take(truncated.Length - 5).ToArray());
                RtssRejectZip(invalid, Path.Combine(root, "truncated"), "Truncated directory rejected");
                byte[] encrypted = RtssZip(new[] { RtssComponent.InstallerName }, body, false);
                int directory = -1;
                for (int i = 0; i <= encrypted.Length - 4; i++)
                    if (BitConverter.ToUInt32(encrypted, i) == 0x02014b50) { directory = i; break; }
                encrypted[directory + 8] = 1;
                File.WriteAllBytes(invalid, encrypted);
                RtssRejectZip(invalid, Path.Combine(root, "encrypted"), "Encrypted installer rejected");
                File.WriteAllBytes(invalid, body);
                bool badHash = false;
                try { RtssComponent.VerifyArchive(invalid); } catch (InvalidDataException) { badHash = true; }
                RtssAssert(badHash, "Pinned hash rejects untrusted downloads");
                RunRtssInstallationOwnershipTests(root, expected);
                RunRtssDownloadSourceTests(root, expected);
                RunRtssUninstallTests(expected);
                RunRtssMaintenanceTests(expected);
                RunRtssHiddenSetupTests(root);
                RunRtssNativeDialogFixtures();
                Console.WriteLine("PASS RTSS component detection, install/uninstall lifecycle and bounded ZIP extraction");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        private static RtssComponentBackend RtssMockBackend(string expected)
        {
            return new RtssComponentBackend
            {
                Candidates = delegate { return new[] { expected }; },
                Exists = delegate { return true; },
                Version = delegate { return "7.3.7.28314"; },
                Running = delegate { return false; },
                Elevated = delegate { return true; },
                Download = delegate(string path, Action<RtssInstallProgress> progress, CancellationToken token) { File.WriteAllText(path, "mock"); },
                Verify = delegate { },
                Extract = delegate(string archive, string output, CancellationToken token) { File.WriteAllText(output, "mock"); },
                RunInstaller = delegate { return 0; }
            };
        }

        private static void RunRtssInstallationOwnershipTests(string root, string expected)
        {
            Settings.UseTransientStoreForCurrentProcess();
            string directory = Path.Combine(root, "ownership"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "RTSS.exe"); File.WriteAllText(path, "owned RTSS fixture");
            RtssAssert(!RtssComponent.IsInstallationOwned(path), "Legacy/unrecorded installation is user-owned");
            RtssAssert(RtssComponent.RecordInstallation(path) && RtssComponent.IsInstallationOwned(path),
                "Verified installation receipt persists exact executable identity");
            RtssAssert(RtssComponent.IsInstallationOwned(path.ToUpperInvariant()), "Receipt paths are case insensitive");
            string other = Path.Combine(root, "other-install"); Directory.CreateDirectory(other);
            string otherPath = Path.Combine(other, "RTSS.exe"); File.Copy(path, otherPath);
            RtssAssert(!RtssComponent.IsInstallationOwned(otherPath), "Identical RTSS at a different path stays user-owned");
            DateTime creation = File.GetCreationTimeUtc(path);
            File.SetCreationTimeUtc(path, creation.AddSeconds(2));
            RtssAssert(!RtssComponent.IsInstallationOwned(path), "Same-content reinstall cannot inherit old ownership");
            File.SetCreationTimeUtc(path, creation);
            File.WriteAllText(path, "user replaced RTSS");
            RtssAssert(!RtssComponent.IsInstallationOwned(path), "Replaced executable invalidates installation receipt");
            RtssAssert(!RtssComponent.RecordInstallation("RTSS.exe") && !RtssComponent.RecordInstallation(otherPath + ".missing"),
                "Invalid or absent executable cannot create ownership");
            Settings.SaveStr(RtssComponent.InstallationOwnerKey, path + "\ncorrupt");
            RtssAssert(!RtssComponent.IsInstallationOwned(path), "Malformed receipt fails toward preserving user settings");
            RtssAssert(!AppDataReset.IsUserPreference(RtssComponent.InstallationOwnerKey), "Preference reset preserves installation source");

            foreach (string scenario in new[] { "existing", "race", "success", "failed", "unverified", "record-failed" })
            {
                bool installed = scenario == "existing", recorded = false;
                int checks = 0, records = 0;
                var backend = RtssMockBackend(expected);
                backend.Candidates = delegate {
                    checks++; if (scenario == "race" && checks == 2) installed = true;
                    return installed ? new[] { expected } : new string[0];
                };
                backend.InstallationOwned = delegate { return recorded; };
                backend.RecordInstallation = delegate(string executable) {
                    records++; RtssAssert(executable == expected, "Only verified installation path is recorded");
                    if (scenario == "record-failed") throw new IOException("fixture receipt failure");
                    return recorded = true;
                };
                backend.RunInstaller = delegate {
                    if (scenario == "existing" || scenario == "race") throw new Exception("Existing RTSS must never be reinstalled");
                    installed = scenario != "unverified";
                    return scenario == "failed" ? 2 : 0;
                };
                var result = RtssComponent.Install(backend, null, CancellationToken.None);
                RtssAssert(result.InstalledByPavise == (scenario == "success"), "Installation source result: " + scenario);
                RtssAssert(records == (scenario == "success" || scenario == "record-failed" ? 1 : 0),
                    "Reuse/race/failure cannot claim user installation: " + scenario);
                if (scenario == "success") RtssAssert(RtssComponent.Detect(backend).InstalledByPavise, "Later detection retains installation source");
                if (scenario == "record-failed") RtssAssert(result.Installed && result.Error.Length == 0,
                    "Receipt failure preserves installed component but never authorizes hiding");
            }
            foreach (int exit in new[] { 0, 3010, 1641, 2, -1 })
            foreach (bool promptFailure in new[] { false, true })
            {
                Action pending = null; int records = 0;
                var setup = new RtssHiddenSetupBackend {
                    CreateDesktop = delegate { return new IntPtr(41); }, Start = delegate { return new IntPtr(42); },
                    Wait = delegate(IntPtr process, int milliseconds) { return milliseconds < 0; },
                    ExitCode = delegate { if (exit < 0) throw new IOException("unknown exit"); return exit; },
                    CloseProcess = delegate { }, CloseDesktop = delegate { }, CompleteLater = delegate(Action work) { pending = work; },
                    PromptError = promptFailure ? "fixture prompt failure" : "", State = new RtssSetupState()
                };
                try { RtssComponent.RunHiddenSetup(RtssComponent.InstallerStartInfo(path), setup, false, null, delegate { records++; }); }
                catch (InvalidOperationException) { }
                RtssAssert(records == 0 && pending != null, "Pending install never claims ownership before completion");
                pending();
                RtssAssert(records == (!promptFailure && (exit == 0 || exit == 3010 || exit == 1641) ? 1 : 0),
                    "Late ownership requires verified successful exit and no prompt error");
            }
            Settings.UseTransientStoreForCurrentProcess();
        }

        private static void RunRtssDownloadSourceTests(string root, string expected)
        {
            string token = "exp=1791815055~acl=/*~hmac=" + new string('a', 64);
            RtssAssert(RtssComponent.ParseMsiDownloadToken("[\"" + token + "\"]") == token
                && RtssComponent.ParseMsiDownloadToken(" \r\n[\"" + token.Replace("/", "\\/") + "\"] ") == token,
                "Official plain/JSON-escaped tokens are parsed without retaining an expiring link");
            foreach (string bad in new[] { "", "<html>denied</html>", "[\"https://other.example/payload\"]",
                "[\"" + token + "&other=1\"]", "[\"" + token + "\",\"extra\"]", new string(' ', 1025) })
            {
                bool rejected = false;
                try { RtssComponent.ParseMsiDownloadToken(bad); } catch (InvalidDataException) { rejected = true; }
                RtssAssert(rejected, "Malformed tokens/URLs/extra parameters cannot become download destinations");
            }

            string path = Path.Combine(root, "source-download.zip");
            int fallbackCalls = 0;
            Action<string, Action<RtssInstallProgress>, CancellationToken> fallback = delegate(string p, Action<RtssInstallProgress> progress, CancellationToken c) {
                fallbackCalls++;
                RtssAssert(!File.Exists(p), "Partial primary download removed before fallback");
                File.WriteAllText(p, "trusted");
            };
            Action<string> verify = delegate(string p) {
                if (File.ReadAllText(p) != "trusted") throw new InvalidDataException("untrusted fixture");
            };
            var notices = new List<string>();
            Action<RtssInstallProgress> report = delegate(RtssInstallProgress p) { notices.Add(p.Detail); };
            RtssComponent.DownloadWithFallback(path, report, CancellationToken.None,
                delegate(string p, Action<RtssInstallProgress> progress, CancellationToken c) {
                    File.WriteAllText(p, "partial"); throw new System.Net.WebException("offline fixture");
                }, fallback, verify);
            RtssAssert(fallbackCalls == 1 && File.ReadAllText(path) == "trusted" && notices.Count >= 2,
                "Network failure switches once to a validated official fallback with progress");
            File.Delete(path);
            fallbackCalls = 0;
            RtssComponent.DownloadWithFallback(path, null, CancellationToken.None,
                delegate(string p, Action<RtssInstallProgress> progress, CancellationToken c) { File.WriteAllText(p, "wrong version"); }, fallback, verify);
            RtssAssert(fallbackCalls == 1 && File.ReadAllText(path) == "trusted", "Changed MSI bundle falls back before extraction");
            File.Delete(path);
            fallbackCalls = 0;
            RtssComponent.DownloadWithFallback(path, null, CancellationToken.None,
                delegate(string p, Action<RtssInstallProgress> progress, CancellationToken c) { File.WriteAllText(p, "trusted"); }, fallback, verify);
            RtssAssert(fallbackCalls == 0, "Successful MSI source never downloads the second package");
            bool existingRejected = false;
            try { RtssComponent.DownloadWithFallback(path, null, CancellationToken.None, fallback, fallback, verify); }
            catch (IOException) { existingRejected = true; }
            RtssAssert(existingRejected && File.ReadAllText(path) == "trusted", "Existing input is not overwritten or deleted");
            File.Delete(path);

            using (var cancelled = new CancellationTokenSource())
            {
                fallbackCalls = 0;
                bool stopped = false;
                try {
                    RtssComponent.DownloadWithFallback(path, null, cancelled.Token,
                        delegate(string p, Action<RtssInstallProgress> progress, CancellationToken c) {
                            File.WriteAllText(p, "partial"); cancelled.Cancel(); throw new System.Net.WebException("aborted");
                        }, fallback, verify);
                } catch (OperationCanceledException) { stopped = true; }
                RtssAssert(stopped && fallbackCalls == 0 && !File.Exists(path), "Cancellation cleans partial data and never triggers another source");
            }
            bool failed = false;
            Action<string, Action<RtssInstallProgress>, CancellationToken> unavailable = delegate(string p, Action<RtssInstallProgress> progress, CancellationToken c) {
                File.WriteAllText(p, "bad"); throw new IOException("offline");
            };
            try { RtssComponent.DownloadWithFallback(path, null, CancellationToken.None, unavailable, unavailable, verify); }
            catch (IOException) { failed = true; }
            RtssAssert(failed && !File.Exists(path), "Both sources failing leaves no partial download");

            var msiUrl = new Uri(RtssComponent.MsiDownloadUrl);
            string cdnUrl = "https://download-2.msi.com/uti_exe/vga/MSIAfterburnerSetup.zip?__token__=fixture";
            RtssAssert(RtssComponent.ResolveDownloadRedirect(msiUrl, cdnUrl).Host == "download-2.msi.com",
                "Official MSI CDN redirects are accepted");
            foreach (string invalid in new[] { "", "http://download-2.msi.com/uti_exe/vga/MSIAfterburnerSetup.zip",
                "https://untrusted.invalid/uti_exe/vga/MSIAfterburnerSetup.zip", "https://download-2.msi.com/other.exe",
                "https://download-2.msi.com:8443/uti_exe/vga/MSIAfterburnerSetup.zip",
                "https://name@download-2.msi.com/uti_exe/vga/MSIAfterburnerSetup.zip" })
            {
                bool rejected = false;
                try { RtssComponent.ResolveDownloadRedirect(msiUrl, invalid); }
                catch (InvalidDataException) { rejected = true; }
                RtssAssert(rejected, "Untrusted or changed download redirects are rejected");
            }

            string range = Path.Combine(root, "range-input");
            File.WriteAllBytes(range, Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
            RtssComponent.CopyComponentRange(range, path, 32, 128, CancellationToken.None);
            RtssAssert(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(range).Skip(32).Take(128)), "Bounded extraction copies only the audited payload");
            File.Delete(path);
            foreach (long[] bounds in new[] { new long[] { -1, 5 }, new long[] { 0, 0 }, new long[] { 250, 7 }, new long[] { long.MaxValue, 1 } })
            {
                bool rejected = false;
                try { RtssComponent.CopyComponentRange(range, path, bounds[0], bounds[1], CancellationToken.None); }
                catch (InvalidDataException) { rejected = true; }
                RtssAssert(rejected && !File.Exists(path), "Out-of-range payload cannot create an output");
            }

            foreach (string failure in new[] { "success", "download", "verify", "extract", "installer", "cancel" })
            {
                using (var cancellation = new CancellationTokenSource())
                {
                    string downloaded = null;
                    bool installed = false, ran = false;
                    RtssComponentBackend backend = RtssMockBackend(expected);
                    backend.Candidates = delegate { return installed ? new[] { expected } : new string[0]; };
                    backend.Download = delegate(string p, Action<RtssInstallProgress> progress, CancellationToken c) {
                        downloaded = p; File.WriteAllText(p, "download");
                        if (failure == "download") throw new IOException("fixture download failure");
                        if (failure == "cancel") { cancellation.Cancel(); c.ThrowIfCancellationRequested(); }
                    };
                    backend.Verify = delegate { if (failure == "verify") throw new InvalidDataException("fixture hash failure"); };
                    backend.Extract = delegate(string archive, string output, CancellationToken c) {
                        File.WriteAllText(output, "RTSS only");
                        if (failure == "extract") throw new InvalidDataException("fixture extraction failure");
                    };
                    backend.RunInstaller = delegate(string installer) {
                        ran = true;
                        RtssAssert(!File.Exists(downloaded) && Directory.GetFiles(Path.GetDirectoryName(installer)).Length == 1,
                            "Large archive removed before RTSS setup starts");
                        if (failure == "installer") return 2;
                        installed = true; return 0;
                    };
                    RtssComponentInfo result = RtssComponent.Install(backend, null, cancellation.Token);
                    RtssAssert(result.Installed == (failure == "success") && (failure == "success" ? result.Error.Length == 0 : result.Error.Length > 0),
                        "Preparation/install outcome " + failure);
                    RtssAssert(downloaded != null && !Directory.Exists(Path.GetDirectoryName(downloaded)),
                        "All owned temporary files removed after " + failure);
                    RtssAssert(ran == (failure == "success" || failure == "installer"), "No setup after preparation failure " + failure);
                }
            }
        }

        private static void RunRtssUninstallTests(string expected)
        {
            string uninstaller = Path.Combine(Path.GetDirectoryName(expected), "uninstall.exe");
            var registration = new RtssUninstallRegistration {
                KeyName = "RTSS", DisplayName = "RivaTuner Statistics Server 7.3.7", Publisher = "Unwinder",
                UninstallString = "\"" + uninstaller + "\" /S --ignored-untrusted-arguments", DisplayVersion = "7.3.7"
            };
            RtssAssert(RtssComponent.RegisteredUninstaller(expected, registration) == uninstaller,
                "Only the registered exact sibling is selected; registry arguments are never executed");
            foreach (string wrong in new[] { Path.Combine(Path.GetDirectoryName(expected), "other", "uninstall.exe"),
                Path.Combine(Path.GetDirectoryName(expected), "cmd.exe"), "uninstall.exe", @"\\host\share\uninstall.exe" })
            {
                string saved = registration.UninstallString;
                registration.UninstallString = "\"" + wrong + "\"";
                RtssAssert(RtssComponent.RegisteredUninstaller(expected, registration) == "", "Wrong/relative/network uninstall path rejected");
                registration.UninstallString = saved;
            }
            registration.Publisher = "Other";
            RtssAssert(RtssComponent.RegisteredUninstaller(expected, registration) == "", "Other publisher rejected");
            registration.Publisher = "Unwinder";
            registration.KeyName = "Other";
            RtssAssert(RtssComponent.RegisteredUninstaller(expected, registration) == "", "Other registered product key rejected");
            registration.KeyName = "RTSS";
            registration.InstallLocation = Path.Combine(Path.GetDirectoryName(expected), "other");
            RtssAssert(RtssComponent.RegisteredUninstaller(expected, registration) == "", "Mismatched registered install directory rejected");
            registration.InstallLocation = null;
            registration.DisplayName = "Another product";
            RtssAssert(RtssComponent.RegisteredUninstaller(expected, registration) == "", "Different registered product rejected");
            registration.DisplayName = "RivaTuner Statistics Server 7.3.7";
            ProcessStartInfo start = RtssComponent.UninstallerStartInfo(Path.Combine(Path.GetDirectoryName(expected), "mock-copy.exe"), Path.GetDirectoryName(expected));
            RtssAssert(start.Arguments == "/S _?=" + Path.GetDirectoryName(expected) && !start.UseShellExecute
                && start.CreateNoWindow && start.WindowStyle == ProcessWindowStyle.Hidden && !start.ErrorDialog
                && String.IsNullOrEmpty(start.Verb),
                "Uninstall is silent and hidden, keeps _?= last, and never uses registry arguments or runas");
            foreach (string invalidDirectory in new[] { "relative", @"C:\", @"\\host\share\RTSS", "C:\\unsafe\rpath" })
            {
                bool rejected = false;
                try { RtssComponent.UninstallerStartInfo(start.FileName, invalidDirectory); }
                catch { rejected = true; }
                RtssAssert(rejected, "Uninstaller rejects root, network, relative and multiline installation paths");
            }

            Func<RtssComponentBackend> mock = delegate {
                RtssComponentBackend b = RtssMockBackend(expected);
                b.UninstallRegistrations = delegate { return new[] { registration }; };
                b.ValidateUninstaller = delegate { return true; };
                return b;
            };
            RtssComponentBackend backend = mock();
            bool called = false;
            backend.Elevated = delegate { return false; };
            backend.RunUninstaller = delegate { called = true; return 0; };
            RtssComponentInfo result = RtssComponent.Uninstall(backend, null, CancellationToken.None);
            RtssAssert(result.Installed && result.Error.Contains("管理员") && !called, "Non-elevated removal preserves install and never opens UAC");
            backend = mock();
            backend.InstallerRunning = delegate { return true; };
            backend.RunUninstaller = delegate { called = true; return 0; };
            result = RtssComponent.Uninstall(backend, null, CancellationToken.None);
            RtssAssert(result.Installed && result.InstallationInProgress && result.Error.Length > 0 && !called,
                "Pending installation blocks removal");
            backend = mock();
            backend.UninstallerRunning = delegate { return true; };
            result = RtssComponent.Uninstall(backend, null, CancellationToken.None);
            RtssAssert(result.UninstallationInProgress && result.Installed && result.Error.Length > 0, "Pending hidden uninstaller blocks duplicate removal");
            backend = mock();
            backend.ValidateUninstaller = delegate { return false; };
            backend.RunUninstaller = delegate { called = true; return 0; };
            result = RtssComponent.Uninstall(backend, null, CancellationToken.None);
            RtssAssert(result.Installed && result.Error.Contains("注册卸载") && !called, "Invalid uninstall file never executes");
            backend = mock();
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                backend.RunUninstaller = delegate { called = true; return 0; };
                result = RtssComponent.Uninstall(backend, null, cancellation.Token);
                RtssAssert(result.Installed && result.Error.Contains("取消") && !called, "Prelaunch cancellation preserves installation");
            }
            backend = mock();
            bool removed = false;
            backend.Candidates = delegate { return removed ? new string[0] : new[] { expected }; };
            bool running = true;
            backend.Running = delegate { return running; };
            backend.RequestExit = delegate { running = false; return true; };
            var phases = new List<string>();
            backend.RunUninstaller = delegate(string file, string directory, CancellationToken cancellation) {
                RtssAssert(file == uninstaller && directory == Path.GetDirectoryName(expected), "Only exact validated executable/directory reaches runner");
                called = true; removed = true; return 0;
            };
            result = RtssComponent.Uninstall(backend, delegate(RtssInstallProgress p) { phases.Add(p.Phase); }, CancellationToken.None);
            RtssAssert(!result.Installed && result.Error == "" && called && phases.SequenceEqual(new[] { "Checking", "Stopping", "Uninstalling", "Checking", "Ready" }),
                "Normal exit is requested before silent removal; only verified absence confirms removal");
            backend = mock();
            called = false;
            backend.Running = delegate { return true; };
            backend.RequestExit = delegate { return false; };
            backend.RunUninstaller = delegate { called = true; return 0; };
            result = RtssComponent.Uninstall(backend, null, CancellationToken.None);
            RtssAssert(result.Installed && result.Running && result.Error.Contains("正常退出") && !called,
                "Unresponsive RTSS stays running; no forced kill or uninstaller launch");
            RtssAssert(!result.Error.Contains("托盘") && !result.Error.Contains("向导"),
                "A blocked hidden operation never asks the user to expose another interface");
            backend.RequestExit = delegate { return true; };
            result = RtssComponent.Uninstall(backend, null, CancellationToken.None);
            RtssAssert(result.Installed && result.Running && !called && result.Error.Contains("仍在运行"),
                "Exit acknowledgement alone cannot bypass the post-shutdown running-process check");
            backend = mock();
            backend.RunUninstaller = delegate { return 0; };
            result = RtssComponent.Uninstall(backend, null, CancellationToken.None);
            RtssAssert(result.Installed && result.Error.Contains("仍检测"), "Exit zero alone cannot confirm removal");
            backend.RunUninstaller = delegate { return 2; };
            result = RtssComponent.Uninstall(backend, null, CancellationToken.None);
            RtssAssert(result.Installed && result.Error.Contains("退出码 2"), "Script abort preserves detected installation");
            backend.RunUninstaller = delegate { return 1; };
            RtssAssert(RtssComponent.Uninstall(backend, null, CancellationToken.None).Error.Contains("取消"), "Silent uninstaller cancellation exit is reported");
            backend.RunUninstaller = delegate { throw new RtssComponent.UninstallerStillRunningException(); };
            result = RtssComponent.Uninstall(backend, null, CancellationToken.None);
            RtssAssert(result.Installed && result.UninstallationInProgress && result.Error.Contains("仍在进行")
                && !result.Error.Contains("向导"), "Timeout preserves the hidden operation without pretending completion or exposing a wizard");
            backend = mock();
            removed = false;
            backend.Candidates = delegate { return removed ? new string[0] : new[] { expected }; };
            using (var cancellation = new CancellationTokenSource())
            {
                backend.RunUninstaller = delegate { cancellation.Cancel(); removed = true; return 0; };
                result = RtssComponent.Uninstall(backend, null, cancellation.Token);
                RtssAssert(!result.Installed && result.Error == "", "Cancellation after launch never interrupts removal or hides a verified result");
            }
            backend = mock();
            int reads = 0;
            backend.Candidates = delegate { return new[] { ++reads == 1 ? expected : Path.Combine(Path.GetDirectoryName(expected), "new", "RTSS.exe") }; };
            called = false;
            backend.RunUninstaller = delegate { called = true; return 0; };
            result = RtssComponent.Uninstall(backend, null, CancellationToken.None);
            RtssAssert(result.Installed && result.Error.Contains("路径已改变") && !called, "Changed installation identity cannot reach execution");
            backend = mock();
            string blocked = "";
            backend.RunUninstaller = delegate { return 1; };
            RtssComponent.Uninstall(backend, delegate(RtssInstallProgress p) {
                if (p.Phase == "Checking" && p.Percent == 0) blocked = RtssComponent.Install(backend, null, CancellationToken.None).Error;
            }, CancellationToken.None);
            RtssAssert(blocked.Contains("操作正在进行"), "Install and uninstall share a non-reentrant operation gate");
        }

        private static void RunRtssHiddenSetupTests(string root)
        {
            var startup = RtssComponent.HiddenSetupStartupInfo("PaviseRtssSetup-fixture");
            RtssAssert(startup.Desktop == "PaviseRtssSetup-fixture" && startup.Flags == 1 && startup.ShowWindow == 0
                && startup.Size > 0 && RtssComponent.HiddenSetupCreationFlags == 0x08000000,
                "Native launch targets only a named private desktop with SW_HIDE and CREATE_NO_WINDOW");
            foreach (string bad in new[] { "", "Default", @"WinSta0\Default", @"PaviseRtssSetup-fixture\Default" })
            {
                bool rejected = false;
                try { RtssComponent.HiddenSetupStartupInfo(bad); } catch { rejected = true; }
                RtssAssert(rejected, "A hidden setup cannot target the user's default desktop");
            }
            foreach (bool uninstall in new[] { false, true })
            {
                ProcessStartInfo start = uninstall
                    ? RtssComponent.UninstallerStartInfo(Path.Combine(root, "uninstall-copy.exe"), root)
                    : RtssComponent.InstallerStartInfo(Path.Combine(root, RtssComponent.InstallerName));
                var events = new List<string>();
                Action completion = null;
                string created = null;
                var backend = new RtssHiddenSetupBackend {
                    CreateDesktop = delegate(string name) { created = name; events.Add("desktop"); return new IntPtr(41); },
                    Start = delegate(ProcessStartInfo info, string desktop) {
                        RtssAssert(info == start && desktop == created && desktop.StartsWith("PaviseRtssSetup-", StringComparison.Ordinal)
                            && info.Arguments.StartsWith("/S", StringComparison.Ordinal) && info.WindowStyle == ProcessWindowStyle.Hidden,
                            "Both setup directions retain silent arguments and the exact private desktop at launch");
                        events.Add("start"); return new IntPtr(42);
                    },
                    Wait = delegate(IntPtr process, int milliseconds) {
                        RtssAssert(process == new IntPtr(42) && milliseconds == 240000, "Only the owned setup process has a bounded foreground wait");
                        events.Add("wait"); return true;
                    },
                    ExitCode = delegate { events.Add("code"); return 3010; },
                    CloseProcess = delegate { events.Add("close-process"); },
                    CloseDesktop = delegate { events.Add("close-desktop"); },
                    CompleteLater = delegate(Action done) { events.Add("pending"); completion = done; },
                    State = new RtssSetupState()
                };
                int code = RtssComponent.RunHiddenSetup(start, backend, uninstall, delegate { events.Add("cleanup"); });
                RtssAssert(code == 3010 && completion == null && events.SequenceEqual(new[] { "desktop", "start", "wait", "code", "close-process", "close-desktop" }),
                    "A completed silent process returns its real exit code and closes its process/desktop handles");
                events.Clear();
                backend.Wait = delegate(IntPtr process, int milliseconds) {
                    RtssAssert(process == new IntPtr(42), "Deferred completion retains only the owned process handle");
                    events.Add(milliseconds < 0 ? "wait-later" : "wait"); return milliseconds < 0;
                };
                bool timedOut = false;
                try { RtssComponent.RunHiddenSetup(start, backend, uninstall, delegate { events.Add("cleanup"); }); }
                catch (InvalidOperationException error) { timedOut = error.Message.Contains("重新检测"); }
                RtssAssert(timedOut && completion != null && events.SequenceEqual(new[] { "desktop", "start", "wait", "pending" }),
                    "A slow installer stays hidden and keeps process/desktop resources alive without forcing termination");
                completion();
                RtssAssert(events.SequenceEqual(new[] { "desktop", "start", "wait", "pending", "wait-later", "code", "close-process", "close-desktop", "cleanup" }),
                    "Temporary executables are cleaned only after the retained process has exited");
                var lateResult = new RtssComponentInfo();
                backend.State.Apply(lateResult);
                RtssAssert(lateResult.RestartRequired && !lateResult.InstallationInProgress && !lateResult.UninstallationInProgress,
                    "A late 3010 result survives later component detection");
                events.Clear(); completion = null;
                backend.Wait = delegate(IntPtr process, int milliseconds) {
                    events.Add(milliseconds < 0 ? "wait-later" : "wait-failed");
                    if (milliseconds >= 0) throw new InvalidOperationException("mock indeterminate wait");
                    return true;
                };
                timedOut = false;
                try { RtssComponent.RunHiddenSetup(start, backend, uninstall, delegate { events.Add("cleanup"); }); }
                catch (InvalidOperationException error) { timedOut = error.Message.Contains("重新检测"); }
                RtssAssert(timedOut && completion != null && events.SequenceEqual(new[] { "desktop", "start", "wait-failed", "pending" }),
                    "A failed wait preserves a started process and all of its input resources");
                RtssAssert(backend.State.Pending(uninstall), "Indeterminate wait remains an active operation until verified completion");
                completion();
                RtssAssert(events.Last() == "cleanup" && !backend.State.Pending(uninstall), "Recovery from a failed initial wait releases resources only after actual exit");
                foreach (bool throwOnCompletion in new[] { false, true })
                {
                    events.Clear(); completion = null;
                    backend.Wait = delegate(IntPtr process, int milliseconds) {
                        if (milliseconds < 0 && throwOnCompletion) throw new InvalidOperationException("mock completion wait failure");
                        return false;
                    };
                    try { RtssComponent.RunHiddenSetup(start, backend, uninstall, delegate { events.Add("cleanup"); }); }
                    catch (InvalidOperationException) { }
                    completion();
                    RtssAssert(!events.Any(e => e.StartsWith("close-") || e == "cleanup") && backend.State.Pending(uninstall),
                        "A false/failed deferred wait cannot clean input files or claim the process exited");
                }
                events.Clear(); completion = null;
                backend.CompleteLater = delegate { events.Add("queue-failed"); throw new InvalidOperationException("mock worker creation failed"); };
                bool retained = false;
                try { RtssComponent.RunHiddenSetup(start, backend, uninstall, delegate { events.Add("cleanup"); }); }
                catch (InvalidOperationException error) {
                    retained = uninstall ? error is RtssComponent.UninstallerStillRunningException : error.GetType().Name == "InstallerStillRunningException";
                }
                RtssAssert(retained && backend.State.Pending(uninstall) && events.SequenceEqual(new[] { "desktop", "start", "queue-failed" }),
                    "Worker creation failure preserves pending ownership and cannot trigger ordinary cleanup");
                backend.CompleteLater = delegate(Action done) { completion = done; };
                foreach (bool unknownExit in new[] { false, true })
                {
                    events.Clear(); completion = null;
                    backend.Wait = delegate(IntPtr process, int milliseconds) { return milliseconds < 0; };
                    backend.ExitCode = delegate { if (unknownExit) throw new InvalidOperationException("mock exit read failed"); return 2; };
                    try { RtssComponent.RunHiddenSetup(start, backend, uninstall, null); } catch (InvalidOperationException) { }
                    completion();
                    lateResult = new RtssComponentInfo(); backend.State.Apply(lateResult);
                    RtssAssert(!backend.State.Pending(uninstall) && lateResult.Error.Contains(unknownExit ? "无法确认结果" : "退出码 2"),
                        "A late failed or unreadable exit code is retained instead of becoming installed/ready");
                }
                long oldOperation = backend.State.Started(false), newOperation = backend.State.Started(true);
                backend.State.Finished(oldOperation, 3010, "stale", true);
                lateResult = new RtssComponentInfo(); backend.State.Apply(lateResult);
                RtssAssert(lateResult.UninstallationInProgress && !lateResult.RestartRequired && !lateResult.Error.Contains("stale"),
                    "A stale operation cannot overwrite a newer operation's state");
                backend.State.Finished(newOperation, 0, "known prompt abort", false);
                lateResult = new RtssComponentInfo(); backend.State.Apply(lateResult);
                RtssAssert(lateResult.Error == "known prompt abort", "A prompt failure is retained even when NSIS returns zero");
                events.Clear(); completion = null;
                backend.Start = delegate { events.Add("start-failed"); throw new InvalidOperationException("mock start failed"); };
                bool failed = false;
                try { RtssComponent.RunHiddenSetup(start, backend, uninstall, null); }
                catch (InvalidOperationException error) { failed = error.Message == "mock start failed"; }
                RtssAssert(failed && completion == null && events.SequenceEqual(new[] { "desktop", "start-failed", "close-desktop" }),
                    "Failed hidden launch releases its desktop and never retries on the user's desktop");
                events.Clear();
                backend.CreateDesktop = delegate { events.Add("desktop-failed"); return IntPtr.Zero; };
                failed = false;
                try { RtssComponent.RunHiddenSetup(start, backend, uninstall, null); }
                catch (InvalidOperationException) { failed = true; }
                RtssAssert(failed && events.SequenceEqual(new[] { "desktop-failed" }),
                    "Failure to isolate setup stops execution instead of falling back to a visible process");
            }
        }

        private static void RunRtssMaintenanceTests(string expected)
        {
            RtssComponentBackend backend = RtssMockBackend(expected);
            bool called = false, blocked = false;
            RtssComponent.Install(backend, delegate(RtssInstallProgress p) {
                if (p.Phase == "Checking") blocked = !RtssComponent.TryRunMaintenance(delegate { called = true; return true; });
            }, CancellationToken.None);
            RtssAssert(blocked && !called, "Configuration maintenance cannot overlap installation");
            bool guarded = RtssComponent.TryRunMaintenance(delegate {
                return RtssComponent.Install(backend, null, CancellationToken.None).Error.Contains("操作正在进行")
                    && RtssComponent.Uninstall(backend, null, CancellationToken.None).Error.Contains("操作正在进行")
                    && !RtssComponent.TryRunMaintenance(delegate { return true; });
            });
            RtssAssert(guarded, "Configuration maintenance excludes install, uninstall and recursive maintenance");
            try { RtssComponent.TryRunMaintenance(delegate { throw new InvalidOperationException("mock maintenance failure"); }); }
            catch (InvalidOperationException) { }
            RtssAssert(RtssComponent.TryRunMaintenance(delegate { return true; }) && !RtssComponent.TryRunMaintenance(null),
                "Maintenance exceptions release the gate without changing no-op behavior");
        }

        private const string RtssKeepFixtureText = "Do you want to keep your RivaTuner Statistics Server settings? Answer YES if you're uninstalling the utility before upgrading it to a new version.";
        private const string RtssRebootFixtureText = "Some RivaTuner Statistics Server files are currently in use and cannot be removed right now, reboot is required to finish the uninstallation and remove these files. Do you want to reboot now?";
        internal static void RunRtssDialogFixtureIfRequested()
        {
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                const string prefix = "--pavise-rtss-dialog-fixture=";
                if (!argument.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string kind = argument.Substring(prefix.Length);
                string text = kind == "keep" ? RtssKeepFixtureText : kind == "reboot" ? RtssRebootFixtureText : "Unrecognized isolated RTSS setup fixture";
                using (var deadline = new Timer(delegate { Environment.Exit(32); }, null, 10000, Timeout.Infinite))
                {
                    int answer = RtssFixtureMessageBox(IntPtr.Zero, text, "RivaTuner Statistics Server 7.3.7 Setup", kind.StartsWith("unknown", StringComparison.Ordinal) ? 1u : 4u);
                    int expected = kind == "keep" ? 6 : kind == "reboot" ? 7 : 2;
                    if (kind == "unknown-delayed") Thread.Sleep(3500);
                    Environment.Exit(answer == expected ? 0 : 31);
                }
            }
        }
        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
        private static extern int RtssFixtureMessageBox(IntPtr owner, string text, string caption, uint type);

        private static void RunRtssNativeDialogFixtures()
        {
            RtssAssert(RtssComponent.KnownSetupPromptAction(RtssKeepFixtureText, true) == 6
                && RtssComponent.KnownSetupPromptAction(RtssKeepFixtureText, false) == 0,
                "Only uninstall may answer YES to the exact audited keep-settings question");
            RtssAssert(RtssComponent.KnownSetupPromptAction(RtssRebootFixtureText, true) == 107
                && RtssComponent.KnownSetupPromptAction("Do you want to reboot now?", true) == 0,
                "Only the exact audited reboot question is recognized and it always chooses NO");
            foreach (string kind in new[] { "keep", "reboot", "unknown" })
            {
                var start = new ProcessStartInfo {
                    FileName = Process.GetCurrentProcess().MainModule.FileName,
                    Arguments = "/S --pavise-rtss-dialog-fixture=" + kind,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                };
                int exitCode = -1;
                bool rejected = false;
                try { exitCode = RtssComponent.RunNativeHiddenSetupFixture(start, true); }
                catch (InvalidOperationException error) { rejected = error.Message.Contains("未识别"); }
                RtssAssert(kind == "unknown" ? rejected : !rejected && exitCode == (kind == "reboot" ? 3010 : 0),
                    "Owned MessageBox fixture on a private desktop is answered safely or closed without appearing on the user's desktop: " + kind);
            }
            using (var completed = new ManualResetEvent(false))
            {
                var start = new ProcessStartInfo {
                    FileName = Process.GetCurrentProcess().MainModule.FileName,
                    Arguments = "/S --pavise-rtss-dialog-fixture=unknown-delayed",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                };
                bool pending = false;
                try { RtssComponent.RunNativeHiddenSetupFixture(start, true, delegate { completed.Set(); }); }
                catch (RtssComponent.UninstallerStillRunningException) { pending = true; }
                RtssAssert(pending && completed.WaitOne(6000),
                    "A real private-desktop prompt abort followed by delayed process exit is tracked through final cleanup");
            }
        }

        private static void RtssAssert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("RTSS component regression: " + message);
        }

        private static void RtssRejectZip(string archive, string output, string message)
        {
            bool rejected = false;
            try { RtssComponent.ExtractInstaller(archive, output, CancellationToken.None); }
            catch (InvalidDataException) { rejected = true; }
            RtssAssert(rejected && !File.Exists(output), message);
        }

        private static byte[] RtssZip(string[] names, byte[] body, bool deflated)
        {
            byte[] packed = body;
            if (deflated)
                using (var memory = new MemoryStream())
                {
                    using (var compressor = new DeflateStream(memory, CompressionMode.Compress, true))
                        compressor.Write(body, 0, body.Length);
                    packed = memory.ToArray();
                }
            using (var memory = new MemoryStream())
            using (var writer = new BinaryWriter(memory, Encoding.UTF8))
            {
                var offsets = new List<uint>();
                foreach (string name in names)
                {
                    byte[] encoded = Encoding.UTF8.GetBytes(name);
                    offsets.Add((uint)memory.Position);
                    writer.Write(0x04034b50u); writer.Write((ushort)20); writer.Write((ushort)0);
                    writer.Write((ushort)(deflated ? 8 : 0)); writer.Write(0u); writer.Write(0u);
                    writer.Write((uint)packed.Length); writer.Write((uint)body.Length);
                    writer.Write((ushort)encoded.Length); writer.Write((ushort)0); writer.Write(encoded); writer.Write(packed);
                }
                uint directory = (uint)memory.Position;
                for (int i = 0; i < names.Length; i++)
                {
                    byte[] encoded = Encoding.UTF8.GetBytes(names[i]);
                    writer.Write(0x02014b50u); writer.Write((ushort)20); writer.Write((ushort)20); writer.Write((ushort)0);
                    writer.Write((ushort)(deflated ? 8 : 0)); writer.Write(0u); writer.Write(0u);
                    writer.Write((uint)packed.Length); writer.Write((uint)body.Length);
                    writer.Write((ushort)encoded.Length); writer.Write((ushort)0); writer.Write((ushort)0);
                    writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0u); writer.Write(offsets[i]); writer.Write(encoded);
                }
                uint size = (uint)memory.Position - directory;
                writer.Write(0x06054b50u); writer.Write((ushort)0); writer.Write((ushort)0);
                writer.Write((ushort)names.Length); writer.Write((ushort)names.Length); writer.Write(size); writer.Write(directory);
                writer.Write((ushort)0);
                return memory.ToArray();
            }
        }
    }
}
#endif

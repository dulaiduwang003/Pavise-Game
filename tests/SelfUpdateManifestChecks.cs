#if PAVISE_SELFUPDATE_MANIFEST_CHECKS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

[assembly: AssemblyVersion(PaviseApp.UpdateFixtureVersion.Candidate)]
namespace PaviseApp
{
    internal static class Lang { internal static int Cur = 0; }

    internal static class SelfUpdateManifestChecks
    {
        private static int checks;
        private static RSACryptoServiceProvider key;
        private static string publicKey;
        private static int httpCases, httpNetworkCases;
        private static readonly DateTime Now = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        private const string GoodVersion = UpdateFixtureVersion.Candidate;
        private const string WrongVersion = UpdateFixtureVersion.Mismatch;
        private static string Payload(string change, string replacement)
        {
            string value = "{\"schema\":1,\"version\":\"" + GoodVersion + "\",\"size\":1024,\"sha256\":\"" + new string('a', 64)
                + "\",\"minUpdater\":1,\"expiresUtc\":\"2027-01-01T00:00:00Z\",\"urls\":[\"" + MainExe + "\",\"" + BackupExe + "\",\"" + GithubExe + "\"]}";
            return change == null ? value : value.Replace(change, replacement);
        }
        private static string Sign(string payload) { return Sign(Encoding.UTF8.GetBytes(payload)); }
        private static string Sign(byte[] payload)
        {
            return "{\"payload\":\"" + Convert.ToBase64String(payload) + "\",\"signature\":\""
                + Convert.ToBase64String(key.SignData(payload, CryptoConfig.MapNameToOID("SHA256"))) + "\"}";
        }
        private static SelfUpdatePackage Parse(string value) { return SelfUpdateManifest.ParseAndVerify(value, publicKey, Now); }
        private static void Assert(bool value, string message)
        {
            checks++;
            if (!value) throw new Exception(message);
        }
        private static void Reject(Action action, string message)
        {
            checks++;
            try { action(); } catch (InvalidDataException) { return; }
            throw new Exception("Expected rejection: " + message);
        }
        private static void RejectPayload(string from, string to, string message)
        {
            Reject(delegate { Parse(Sign(Payload(from, to))); }, message);
        }
        public static int Main()
        {
            try
            {
                using (key = new RSACryptoServiceProvider(3072))
                {
                    key.PersistKeyInCsp = false;
                    publicKey = key.ToXmlString(false);
                    ManifestChecks();
                    UrlChecks();
                    StreamChecks();
                    ExecutableChecks();
                    HttpChecks();
                }
                Console.WriteLine("PASS SelfUpdateManifestChecks: " + checks + " checks, including " + httpCases
                    + " HTTP pipeline scenarios (" + httpNetworkCases + " use real loopback HTTP; " + (httpCases - httpNetworkCases) + " reject before transport)");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
        private static void ManifestChecks()
        {
            Assert(new Version(GoodVersion) > new Version(App.Version), "candidate version stays newer than the production client");
            string payload = Payload(null, null), manifest = Sign(payload);
            SelfUpdatePackage parsed = Parse(manifest);
            Assert(parsed.Version == GoodVersion && parsed.Size == 1024 && parsed.DownloadUrls.Length == 3 && parsed.ManifestText == manifest, "signed package round trip");
            using (RSACryptoServiceProvider trusted = new RSACryptoServiceProvider())
            {
                trusted.PersistKeyInCsp = false;
                trusted.FromXmlString(SelfUpdateManifest.TrustedPublicKeyXml);
                Assert(trusted.KeySize == 3072, "production RSA3072 trust anchor");
            }
            Reject(delegate { SelfUpdateManifest.ParseAndVerify(manifest); }, "test key cannot bypass production anchor");
            Reject(delegate { Parse(null); }, "null");
            Reject(delegate { Parse(new string(' ', SelfUpdateManifest.MaxManifestBytes + 1)); }, "oversize");
            Reject(delegate { Parse("{\"version\":\"3.2.0.2\"}"); }, "unsigned legacy manifest");
            Reject(delegate { Parse(manifest + "x"); }, "trailing envelope data");
            Reject(delegate { Parse(manifest.Replace("{", "{\"signature\":\"AA==\",")); }, "duplicate envelope signature");
            Reject(delegate { Parse(manifest.Replace("{", "{\"unknown\":1,")); }, "unknown envelope property");
            Reject(delegate { Parse(manifest.Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)), Convert.ToBase64String(Encoding.UTF8.GetBytes(payload + " ")))); }, "tampered payload");
            Reject(delegate { Parse(Sign(new byte[] { 0xc0, 0xaf })); }, "signed invalid UTF8");
            Reject(delegate { Parse(Sign("\ufeff" + payload)); }, "payload BOM");
            Reject(delegate { Parse(Sign(payload + "{}")); }, "trailing payload");
            RejectPayload("\"schema\":1", "\"schema\":1,\"schema\":1", "duplicate payload field");
            RejectPayload("\"schema\":1", "\"schema\":1,\"schem\\u0061\":1", "escaped duplicate payload field");
            RejectPayload("\"schema\":1", "\"schema\":2", "schema");
            RejectPayload("\"schema\":1", "\"schema\":01", "noncanonical integer");
            RejectPayload("\"schema\":1", "\"schema\":1.0", "floating point field");
            RejectPayload("\"schema\":1", "\"schema\":\"1\"", "string schema");
            RejectPayload("\"size\":1024", "\"size\":0", "empty EXE");
            RejectPayload("\"size\":1024", "\"size\":-1", "negative size");
            RejectPayload("\"size\":1024", "\"size\":134217729", "EXE size bound");
            RejectPayload("\"size\":1024", "\"size\":9223372036854775808", "integer overflow");
            RejectPayload(new string('a', 64), new string('a', 63), "short hash");
            RejectPayload(new string('a', 64), new string('A', 64), "noncanonical uppercase hash");
            RejectPayload(GoodVersion, "0" + GoodVersion, "noncanonical version");
            RejectPayload(GoodVersion, "3.2.2", "three part version");
            RejectPayload("\"minUpdater\":1", "\"minUpdater\":2", "future updater protocol");
            RejectPayload("\"minUpdater\":1", "\"minUpdater\":0", "invalid updater protocol");
            RejectPayload("2027-01-01T00:00:00Z", "2026-11-02T00:00:00Z", "expiry boundary");
            RejectPayload("2027-01-01T00:00:00Z", "2026-11-01T23:59:59Z", "expired");
            RejectPayload("2027-01-01T00:00:00Z", "2027-01-01T00:00:00+00:00", "noncanonical UTC");
            RejectPayload("\"schema\":1", "\"schema\":1,\"extra\":1", "unknown payload field");
            RejectPayload(MainExe, MainExe.Replace("pavise.club", "attacker.example"), "untrusted signed URL");
            RejectPayload(BackupExe, MainExe, "duplicate mirror");
        }
        private static void UrlChecks()
        {
            const string website = MainExe;
            const string github = GithubExe;
            Assert(SelfUpdateManifest.IsAllowedPackageUrl(website, GoodVersion), "official URL");
            Assert(SelfUpdateManifest.IsAllowedPackageUrl(github, GoodVersion), "canonical release URL");
            string[] invalid = new string[] { website.Replace("https:", "http:"), website + "#fragment", website + "?download=1",
                website.Replace("pavise.club", "pavise.club.attacker.example"), website.Replace("pavise.club", "pavise.club."),
                website.Replace("pavise.club", "user@pavise.club"), website.Replace("pavise.club", "pavise.club:444"),
                website.Replace(GoodVersion, "3.1.0.8"), " " + website, website + "\r\n", website.Replace("Pavise.exe", "Pavise.exe/"),
                github.Replace("Pavise-Game", "Pavise2x"), github.Replace("/download/v" + GoodVersion + "/", "/latest/download/") };
            foreach (string value in invalid) Assert(!SelfUpdateManifest.IsAllowedPackageUrl(value, GoodVersion), "URL rejected: " + value);
            Uri initial = new Uri(github);
            foreach (string host in new string[] { "release-assets.githubusercontent.com", "objects.githubusercontent.com", "github-releases.githubusercontent.com" })
                Assert(SelfUpdateManifest.IsAllowedRedirect(initial, new Uri("https://" + host + "/asset?token=opaque")), "GitHub release CDN");
            Assert(!SelfUpdateManifest.IsAllowedRedirect(initial, new Uri("https://release-assets.githubusercontent.com.attacker.example/asset")), "CDN suffix attack");
            Assert(!SelfUpdateManifest.IsAllowedRedirect(initial, new Uri("http://release-assets.githubusercontent.com/asset")), "CDN HTTPS downgrade");
            Assert(!SelfUpdateManifest.IsAllowedRedirect(initial, new Uri("https://raw.githubusercontent.com/asset")), "unlisted GitHub host");
            Assert(!SelfUpdateManifest.IsAllowedRedirect(new Uri(website), new Uri("https://release-assets.githubusercontent.com/asset")), "official source cannot redirect arbitrarily");
            Assert(SelfUpdateManifest.IsAllowedRedirect(new Uri(website), new Uri(website.Replace("pavise.club", SelfUpdateManifest.FallbackHost))), "official fallback redirect");
        }
        private static void StreamChecks()
        {
            byte[] bytes = new byte[100]; long received = 0;
            using (MemoryStream input = new MemoryStream(bytes))
            using (MemoryStream output = new MemoryStream())
            {
                SelfUpdateDownload.CopyBounded(input, output, 100, 100, delegate(long value) { received = value; }, CancellationToken.None);
                Assert(output.Length == 100 && received == 100, "bounded copy reports actual bytes");
            }
            Reject(delegate { using (MemoryStream input = new MemoryStream(bytes)) using (MemoryStream output = new MemoryStream()) SelfUpdateDownload.CopyBounded(input, output, 99, 100, null, CancellationToken.None); }, "oversize stream");
            Reject(delegate { using (MemoryStream input = new MemoryStream(bytes)) using (MemoryStream output = new MemoryStream()) SelfUpdateDownload.CopyBounded(input, output, 200, 101, null, CancellationToken.None); }, "truncated stream");
            Reject(delegate { using (MemoryStream input = new MemoryStream()) using (MemoryStream output = new MemoryStream()) SelfUpdateDownload.CopyBounded(input, output, 100, -1, null, CancellationToken.None); }, "empty chunked stream");
            using (CancellationTokenSource source = new CancellationTokenSource())
            using (MemoryStream input = new MemoryStream(new byte[131072]))
            using (MemoryStream output = new MemoryStream())
            {
                bool canceled = false;
                try { SelfUpdateDownload.CopyBounded(input, output, 131072, 131072, delegate(long value) { source.Cancel(); }, source.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && output.Length == 65536, "mid-download cancellation stops before next read");
            }
        }
        private static void ExecutableChecks()
        {
            string path = Assembly.GetExecutingAssembly().Location;
            string hash;
            using (FileStream stream = File.OpenRead(path)) using (SHA256 sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            SelfUpdatePackage package = new SelfUpdatePackage { Version = GoodVersion, Size = new FileInfo(path).Length, Sha256 = hash };
            SelfUpdateDownload.VerifyDownloadedFile(package, path, CancellationToken.None);
            Assert(true, "EXE hash and assembly version checked without execution");
            package.Version = WrongVersion;
            Reject(delegate { SelfUpdateDownload.VerifyDownloadedFile(package, path, CancellationToken.None); }, "assembly version mismatch");
            package.Version = GoodVersion; package.Size++;
            Reject(delegate { SelfUpdateDownload.VerifyDownloadedFile(package, path, CancellationToken.None); }, "EXE length mismatch");
            package.Size--; package.Sha256 = new string('0', 64);
            Reject(delegate { SelfUpdateDownload.VerifyDownloadedFile(package, path, CancellationToken.None); }, "EXE hash mismatch");
        }

        private const string MainFeed = "https://pavise.club/updates/stable.json";
        private const string BackupFeed = "https://pavise-website.2074055628.workers.dev/updates/stable.json";
        private const string MainExe = "https://pavise.club/updates/" + GoodVersion + "/Pavise.exe";
        private const string BackupExe = "https://pavise-website.2074055628.workers.dev/updates/" + GoodVersion + "/Pavise.exe";
        private const string GithubExe = "https://github.com/dulaiduwang003/Pavise-Game/releases/download/v" + GoodVersion + "/Pavise.exe";
        private static byte[] httpExe;
        private static string HttpManifest(string[] urls, string version)
        {
            string hash;
            using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(httpExe)).Replace("-", "").ToLowerInvariant();
            return Sign("{\"schema\":1,\"version\":\"" + version + "\",\"size\":" + httpExe.Length.ToString(CultureInfo.InvariantCulture)
                + ",\"sha256\":\"" + hash + "\",\"minUpdater\":1,\"expiresUtc\":\""
                + DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                + "\",\"urls\":[\"" + string.Join("\",\"", urls) + "\"]}");
        }
        private static SelfUpdatePackage HttpPackage(params string[] urls)
        { return SelfUpdateManifest.ParseAndVerify(HttpManifest(urls, GoodVersion)); }
        private static void HttpCase(string name, Action<HttpFixture, string> test)
        {
            string directory = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "http-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using (HttpFixture server = new HttpFixture())
            {
                SelfUpdateManifest.FixturePublicKeyXml = publicKey;
                SelfUpdateDownload.FixtureTransportEndpoint = server.Endpoint;
                SelfUpdateDownload.FixtureFeedTimeoutMilliseconds = 3000;
                SelfUpdateDownload.FixtureMirrorTimeoutMilliseconds = 3000;
                SelfUpdateDownload.FixtureDownloadTimeoutMilliseconds = 10000;
                SelfUpdateDownload.FixtureReadTimeoutMilliseconds = 3000;
                try
                {
                    test(server, directory);
                    Assert(Directory.GetFiles(directory).Length == 0, name + ": no staged files remain");
                    httpCases++;
                    if (server.RequestCount > 0) httpNetworkCases++;
                    Console.WriteLine("PASS HTTP " + name + " (" + server.RequestCount.ToString(CultureInfo.InvariantCulture) + " requests)");
                }
                finally
                {
                    SelfUpdateManifest.FixturePublicKeyXml = null;
                    SelfUpdateDownload.FixtureTransportEndpoint = null;
                    SelfUpdateDownload.FixtureFeedTimeoutMilliseconds = 0;
                    SelfUpdateDownload.FixtureMirrorTimeoutMilliseconds = 0;
                    SelfUpdateDownload.FixtureDownloadTimeoutMilliseconds = 0;
                    SelfUpdateDownload.FixtureReadTimeoutMilliseconds = 0;
                }
            }
            Directory.Delete(directory, false);
        }
        private static void DownloadSuccess(SelfUpdatePackage package, string directory, Action<SelfUpdateProgress> progress)
        {
            string candidate = SelfUpdateDownload.Download(package, directory, progress, CancellationToken.None);
            Assert(File.Exists(candidate) && new FileInfo(candidate).Length == httpExe.Length, "HTTP candidate has the expected size");
            SelfUpdateDownload.VerifyExecutable(package, candidate, CancellationToken.None);
            Assert(Directory.GetFiles(directory).Length == 1, "only the verified candidate survives mirror retries");
            File.Delete(candidate);
        }
        private static void DownloadFailure(SelfUpdatePackage package, string directory)
        {
            bool rejected = false;
            try { SelfUpdateDownload.Download(package, directory, null, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            catch (TimeoutException) { rejected = true; }
            Assert(rejected, "all invalid HTTP mirrors fail closed");
        }
        private static void HttpChecks()
        {
            httpExe = File.ReadAllBytes(Assembly.GetExecutingAssembly().Location);
            foreach (string version in new[] { App.Version, "0.0.0.0" })
                HttpCase("signed-equal-or-older-version-rejected-" + version, delegate(HttpFixture server, string directory)
                {
                    string url = MainExe.Replace(GoodVersion, version);
                    server.Set(url, new Reply { Body = httpExe });
                    SelfUpdatePackage package = SelfUpdateManifest.ParseAndVerify(HttpManifest(new[] { url }, version));
                    Reject(delegate { SelfUpdateDownload.Download(package, directory, null, CancellationToken.None); }, "equal or older version rejected");
                    Assert(server.RequestCount == 0, "version rejection must happen before download transport");
                });
            HttpCase("feed-503-switches-to-backup", delegate(HttpFixture server, string directory)
            {
                server.Set(MainFeed, new Reply { Status = 503 });
                server.Set(BackupFeed, Reply.Text(HttpManifest(new[] { MainExe, BackupExe }, GoodVersion)));
                Assert(SelfUpdateDownload.Check(CancellationToken.None).Version == GoodVersion, "backup signed feed accepted");
                Assert(server.Hits(MainFeed) == 1 && server.Hits(BackupFeed) == 1, "both feed transports executed");
            });
            HttpCase("unsigned-main-feed-switches-to-signed-backup", delegate(HttpFixture server, string directory)
            {
                server.Set(MainFeed, Reply.Text("{\"version\":\"3.2.0.2\"}"));
                server.Set(BackupFeed, Reply.Text(HttpManifest(new[] { MainExe }, GoodVersion)));
                Assert(SelfUpdateDownload.Check(CancellationToken.None).Version == GoodVersion, "invalid primary manifest is retryable");
                Assert(server.Hits(MainFeed) == 1 && server.Hits(BackupFeed) == 1, "bad feed did not suppress mirror fallback");
            });
            HttpCase("all-feed-signatures-rejected", delegate(HttpFixture server, string directory)
            {
                string invalid = HttpManifest(new[] { MainExe }, GoodVersion).Replace("\"signature\":\"", "\"signature\":\"A");
                server.Set(MainFeed, Reply.Text(invalid)); server.Set(BackupFeed, Reply.Text(invalid));
                bool rejected = false;
                try { SelfUpdateDownload.Check(CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
                Assert(rejected && server.Hits(MainFeed) == 1 && server.Hits(BackupFeed) == 1, "invalid signatures never produce a package");
            });
            HttpCase("oversize-feed-header-retries-backup", delegate(HttpFixture server, string directory)
            {
                server.Set(MainFeed, new Reply { Body = new byte[SelfUpdateManifest.MaxManifestBytes + 1] });
                server.Set(BackupFeed, Reply.Text(HttpManifest(new[] { MainExe }, GoodVersion)));
                Assert(SelfUpdateDownload.Check(CancellationToken.None).Version == GoodVersion && server.Hits(BackupFeed) == 1, "feed Content-Length limit enforced");
            });
            HttpCase("oversize-chunked-feed-retries-backup", delegate(HttpFixture server, string directory)
            {
                server.Set(MainFeed, new Reply { Body = new byte[SelfUpdateManifest.MaxManifestBytes + 1], Chunked = true });
                server.Set(BackupFeed, Reply.Text(HttpManifest(new[] { MainExe }, GoodVersion)));
                Assert(SelfUpdateDownload.Check(CancellationToken.None).Version == GoodVersion && server.Hits(BackupFeed) == 1, "feed decoded-byte size limit enforced");
            });
            HttpCase("feed-header-timeout-retries-backup", delegate(HttpFixture server, string directory)
            {
                SelfUpdateDownload.FixtureFeedTimeoutMilliseconds = 400;
                server.Set(MainFeed, new Reply { HeaderDelayMilliseconds = 1500 });
                server.Set(BackupFeed, Reply.Text(HttpManifest(new[] { MainExe }, GoodVersion)));
                Stopwatch elapsed = Stopwatch.StartNew();
                Assert(SelfUpdateDownload.Check(CancellationToken.None).Version == GoodVersion && server.Hits(BackupFeed) == 1, "feed timeout switches source");
                Assert(elapsed.ElapsedMilliseconds < 2500, "feed Abort timeout is bounded");
            });
            HttpCase("package-500-switches-to-backup", delegate(HttpFixture server, string directory)
            {
                server.Set(MainExe, new Reply { Status = 500 }); server.Set(BackupExe, new Reply { Body = httpExe });
                DownloadSuccess(HttpPackage(MainExe, BackupExe), directory, null);
                Assert(server.Hits(MainExe) == 1 && server.Hits(BackupExe) == 1, "package mirror switch used real HTTP");
            });
            HttpCase("wrong-hash-restarts-next-mirror-at-zero", delegate(HttpFixture server, string directory)
            {
                byte[] bad = (byte[])httpExe.Clone(); bad[bad.Length - 1] ^= 1;
                server.Set(MainExe, new Reply { Body = bad }); server.Set(BackupExe, new Reply { Body = httpExe });
                int starts = 0, verified = 0;
                DownloadSuccess(HttpPackage(MainExe, BackupExe), directory, delegate(SelfUpdateProgress p)
                {
                    if (p.Stage == "Downloading" && p.Received == 0) starts++;
                    if (p.Stage == "Verifying") verified++;
                });
                Assert(starts == 2 && verified == 2 && server.Hits(BackupExe) == 1, "bad hash is rejected then next source starts at zero");
            });
            HttpCase("all-wrong-hash-packages-rejected", delegate(HttpFixture server, string directory)
            {
                byte[] bad = (byte[])httpExe.Clone(); bad[bad.Length - 1] ^= 1;
                server.Set(MainExe, new Reply { Body = bad }); server.Set(BackupExe, new Reply { Body = bad });
                DownloadFailure(HttpPackage(MainExe, BackupExe), directory);
                Assert(server.Hits(MainExe) == 1 && server.Hits(BackupExe) == 1, "no corrupted candidate survives all mirror failures");
            });
            HttpCase("wrong-content-length-header-retries", delegate(HttpFixture server, string directory)
            {
                server.Set(MainExe, new Reply { Body = httpExe, DeclaredLength = httpExe.Length + 1 });
                server.Set(BackupExe, new Reply { Body = httpExe });
                DownloadSuccess(HttpPackage(MainExe, BackupExe), directory, null);
                Assert(server.Hits(BackupExe) == 1, "incorrect Content-Length rejected before accepting payload");
            });
            HttpCase("truncated-http-body-retries", delegate(HttpFixture server, string directory)
            {
                byte[] shortBody = new byte[httpExe.Length - 1]; Buffer.BlockCopy(httpExe, 0, shortBody, 0, shortBody.Length);
                server.Set(MainExe, new Reply { Body = shortBody, DeclaredLength = httpExe.Length });
                server.Set(BackupExe, new Reply { Body = httpExe });
                DownloadSuccess(HttpPackage(MainExe, BackupExe), directory, null);
                Assert(server.Hits(BackupExe) == 1, "truncated response cannot be installed");
            });
            HttpCase("chunked-overflow-rejected-before-mirror-success", delegate(HttpFixture server, string directory)
            {
                byte[] longBody = new byte[httpExe.Length + 1]; Buffer.BlockCopy(httpExe, 0, longBody, 0, httpExe.Length);
                server.Set(MainExe, new Reply { Body = longBody, Chunked = true }); server.Set(BackupExe, new Reply { Body = httpExe });
                DownloadSuccess(HttpPackage(MainExe, BackupExe), directory, null);
                Assert(server.Hits(BackupExe) == 1, "chunked decoded-byte limit enforced");
            });
            HttpCase("chunked-exact-size-downloads-successfully", delegate(HttpFixture server, string directory)
            {
                server.Set(MainExe, new Reply { Body = httpExe, Chunked = true });
                DownloadSuccess(HttpPackage(MainExe), directory, null);
            });
            HttpCase("signed-package-wrong-assembly-version-rejected", delegate(HttpFixture server, string directory)
            {
                string a = MainExe.Replace(GoodVersion, WrongVersion), b = BackupExe.Replace(GoodVersion, WrongVersion);
                server.Set(a, new Reply { Body = httpExe }); server.Set(b, new Reply { Body = httpExe });
                SelfUpdatePackage package = SelfUpdateManifest.ParseAndVerify(HttpManifest(new[] { a, b }, WrongVersion));
                DownloadFailure(package, directory);
                Assert(server.Hits(a) == 1 && server.Hits(b) == 1, "correct hash with wrong assembly version rejected on every mirror");
            });
            HttpCase("official-redirect-to-workers-same-path", delegate(HttpFixture server, string directory)
            {
                server.Set(MainExe, Reply.Redirect(BackupExe)); server.Set(BackupExe, new Reply { Body = httpExe });
                DownloadSuccess(HttpPackage(MainExe), directory, null);
                Assert(server.Hits(MainExe) == 1 && server.Hits(BackupExe) == 1, "allowed official redirect followed");
            });
            HttpCase("github-release-cdn-redirect-chain", delegate(HttpFixture server, string directory)
            {
                const string asset = "https://release-assets.githubusercontent.com/fixture-asset?token=opaque";
                const string objectUrl = "https://objects.githubusercontent.com/fixture-asset?token=opaque";
                server.Set(GithubExe, Reply.Redirect(asset)); server.Set(asset, Reply.Redirect(objectUrl)); server.Set(objectUrl, new Reply { Body = httpExe });
                DownloadSuccess(HttpPackage(GithubExe), directory, null);
                Assert(server.Hits(GithubExe) == 1 && server.Hits(asset) == 1 && server.Hits(objectUrl) == 1, "explicit GitHub release CDN redirects followed");
            });
            HttpCase("foreign-redirect-never-requested", delegate(HttpFixture server, string directory)
            {
                const string foreign = "https://attacker.example/payload";
                server.Set(MainExe, Reply.Redirect(foreign)); server.Set(foreign, new Reply { Body = httpExe }); server.Set(BackupExe, new Reply { Body = httpExe });
                DownloadSuccess(HttpPackage(MainExe, BackupExe), directory, null);
                Assert(server.Hits(foreign) == 0 && server.Hits(BackupExe) == 1, "untrusted redirect rejected before issuing HTTP request");
            });
            HttpCase("https-downgrade-never-requested", delegate(HttpFixture server, string directory)
            {
                string insecure = BackupExe.Replace("https:", "http:");
                server.Set(MainExe, Reply.Redirect(insecure)); server.Set(BackupExe, new Reply { Body = httpExe }); server.Set(GithubExe, new Reply { Body = httpExe });
                DownloadSuccess(HttpPackage(MainExe, GithubExe), directory, null);
                Assert(server.Hits(insecure) == 0 && server.Hits(GithubExe) == 1, "HTTPS downgrade rejected before issuing HTTP request");
            });
            HttpCase("redirect-loop-stops-after-five-hops", delegate(HttpFixture server, string directory)
            {
                server.Set(MainExe, Reply.Redirect(BackupExe)); server.Set(BackupExe, Reply.Redirect(MainExe)); server.Set(GithubExe, new Reply { Body = httpExe });
                DownloadSuccess(HttpPackage(MainExe, GithubExe), directory, null);
                Assert(server.Hits(MainExe) + server.Hits(BackupExe) == 6 && server.Hits(GithubExe) == 1, "redirect loop bounded to initial request plus five hops");
            });
            HttpTimeoutCase("headers-timeout-retries-mirror", false, false);
            HttpTimeoutCase("body-total-timeout-retries-mirror", true, false);
            HttpTimeoutCase("body-idle-timeout-retries-mirror", true, true);
            HttpCase("total-download-budget-spans-all-mirrors", delegate(HttpFixture server, string directory)
            {
                SelfUpdateDownload.FixtureMirrorTimeoutMilliseconds = 700;
                SelfUpdateDownload.FixtureDownloadTimeoutMilliseconds = 1000;
                server.Set(MainExe, new Reply { HeaderDelayMilliseconds = 3000 });
                server.Set(BackupExe, new Reply { HeaderDelayMilliseconds = 3000 });
                server.Set(GithubExe, new Reply { HeaderDelayMilliseconds = 3000 });
                Stopwatch elapsed = Stopwatch.StartNew();
                DownloadFailure(HttpPackage(MainExe, BackupExe, GithubExe), directory);
                Assert(elapsed.ElapsedMilliseconds < 1800, "mirror retries share a single total time budget");
                Assert(server.Hits(MainExe) == 1 && server.Hits(BackupExe) == 1 && server.Hits(GithubExe) <= 1, "budget aborts active request and does not loop mirrors");
            });
            HttpCancelCase("cancel-while-waiting-for-headers", false);
            HttpCancelCase("cancel-while-reading-response-body", true);
            HttpCase("cancel-feed-before-headers-does-not-switch-source", delegate(HttpFixture server, string directory)
            {
                Reply blocked = new Reply { HeaderDelayMilliseconds = 1500 };
                server.Set(MainFeed, blocked); server.Set(BackupFeed, Reply.Text(HttpManifest(new[] { MainExe }, GoodVersion)));
                using (CancellationTokenSource source = new CancellationTokenSource())
                {
                    Exception failure = null;
                    Thread worker = new Thread(delegate()
                    {
                        try { SelfUpdateDownload.Check(source.Token); } catch (Exception error) { failure = error; }
                    });
                    worker.IsBackground = true; worker.Start();
                    Assert(blocked.Blocked.WaitOne(2500), "feed request reached blocked header phase");
                    source.Cancel();
                    Assert(worker.Join(2500) && failure is OperationCanceledException, "feed HTTP Abort cancellation completes");
                    Assert(server.Hits(BackupFeed) == 0, "cancelled feed does not attempt backup source");
                }
            });
            HttpCase("mutated-package-rejected-before-network", delegate(HttpFixture server, string directory)
            {
                SelfUpdatePackage package = HttpPackage(MainExe); package.Size++;
                Reject(delegate { SelfUpdateDownload.Download(package, directory, null, CancellationToken.None); }, "package object must match signed manifest");
                Assert(server.RequestCount == 0, "mutated signed package never fetched");
            });
            HttpCase("fixture-transport-cannot-access-public-http", delegate(HttpFixture server, string directory)
            {
                SelfUpdateDownload.FixtureTransportEndpoint = delegate(Uri ignored) { return new Uri("http://example.com/fixture"); };
                DownloadFailure(HttpPackage(MainExe), directory);
                Assert(server.RequestCount == 0, "test transport restricted to exact loopback endpoint");
            });
        }
        private static void HttpTimeoutCase(string name, bool bodyDelay, bool idleTimeout)
        {
            HttpCase(name, delegate(HttpFixture server, string directory)
            {
                SelfUpdateDownload.FixtureMirrorTimeoutMilliseconds = idleTimeout ? 3000 : 400;
                SelfUpdateDownload.FixtureReadTimeoutMilliseconds = idleTimeout ? 250 : 3000;
                Reply blocked = new Reply { Body = httpExe };
                if (bodyDelay) blocked.BodyDelayMilliseconds = 1500; else blocked.HeaderDelayMilliseconds = 1500;
                server.Set(MainExe, blocked); server.Set(BackupExe, new Reply { Body = httpExe });
                Stopwatch elapsed = Stopwatch.StartNew();
                DownloadSuccess(HttpPackage(MainExe, BackupExe), directory, null);
                Assert(server.Hits(BackupExe) == 1 && elapsed.ElapsedMilliseconds < 2500, "blocked HTTP operation times out and falls back promptly");
            });
        }
        private static void HttpCancelCase(string name, bool bodyDelay)
        {
            HttpCase(name, delegate(HttpFixture server, string directory)
            {
                Reply blocked = new Reply { Body = httpExe };
                if (bodyDelay) blocked.BodyDelayMilliseconds = 1500; else blocked.HeaderDelayMilliseconds = 1500;
                server.Set(MainExe, blocked); server.Set(BackupExe, new Reply { Body = httpExe });
                SelfUpdatePackage package = HttpPackage(MainExe, BackupExe);
                using (CancellationTokenSource source = new CancellationTokenSource())
                {
                    Exception failure = null;
                    Thread worker = new Thread(delegate()
                    {
                        try { SelfUpdateDownload.Download(package, directory, null, source.Token); }
                        catch (Exception error) { failure = error; }
                    });
                    worker.IsBackground = true; worker.Start();
                    Assert(blocked.Blocked.WaitOne(2500), "request reached blocked " + (bodyDelay ? "body" : "header") + " phase");
                    Stopwatch elapsed = Stopwatch.StartNew(); source.Cancel();
                    Assert(worker.Join(2500), "HTTP Abort cancellation finishes promptly");
                    Assert(failure is OperationCanceledException && elapsed.ElapsedMilliseconds < 2500, "cancellation remains cancellation");
                    Assert(server.Hits(BackupExe) == 0, "cancelled download never tries a mirror");
                }
            });
        }

        private sealed class Reply
        {
            internal int Status = 200;
            internal byte[] Body = new byte[0];
            internal long DeclaredLength = -1;
            internal string Location;
            internal bool Chunked;
            internal int HeaderDelayMilliseconds, BodyDelayMilliseconds;
            internal readonly ManualResetEvent Blocked = new ManualResetEvent(false);
            internal static Reply Text(string text) { return new Reply { Body = Encoding.UTF8.GetBytes(text) }; }
            internal static Reply Redirect(string location) { return new Reply { Status = 302, Location = location }; }
            internal void Send(NetworkStream stream)
            {
                if (HeaderDelayMilliseconds > 0) { Blocked.Set(); Thread.Sleep(HeaderDelayMilliseconds); }
                string headers = "HTTP/1.1 " + Status.ToString(CultureInfo.InvariantCulture) + " Fixture\r\nConnection: close\r\n";
                if (Location != null) headers += "Location: " + Location + "\r\n";
                headers += Chunked ? "Transfer-Encoding: chunked\r\n" : "Content-Length: " + (DeclaredLength < 0 ? Body.Length : DeclaredLength).ToString(CultureInfo.InvariantCulture) + "\r\n";
                Write(stream, headers + "\r\n");
                if (BodyDelayMilliseconds > 0)
                {
                    int prefix = Math.Min(32, Body.Length);
                    if (prefix > 0) stream.Write(Body, 0, prefix);
                    stream.Flush(); Blocked.Set(); Thread.Sleep(BodyDelayMilliseconds);
                    stream.Write(Body, prefix, Body.Length - prefix);
                }
                else if (Chunked)
                {
                    for (int offset = 0; offset < Body.Length; offset += 1024)
                    {
                        int length = Math.Min(1024, Body.Length - offset);
                        Write(stream, length.ToString("x", CultureInfo.InvariantCulture) + "\r\n");
                        stream.Write(Body, offset, length); Write(stream, "\r\n");
                    }
                    Write(stream, "0\r\n\r\n");
                }
                else stream.Write(Body, 0, Body.Length);
                stream.Flush();
            }
            private static void Write(Stream stream, string value) { byte[] bytes = Encoding.ASCII.GetBytes(value); stream.Write(bytes, 0, bytes.Length); }
        }
        // Raw loopback TCP avoids URL ACL changes and exercises HttpWebRequest's real
        // HTTP framing, redirects, Abort and timeout behavior without an external service.
        private sealed class HttpFixture : IDisposable
        {
            private readonly TcpListener listener;
            private readonly Thread worker;
            private readonly object gate = new object();
            private readonly Dictionary<string, Reply> replies = new Dictionary<string, Reply>(StringComparer.Ordinal);
            private readonly List<string> requests = new List<string>();
            private readonly List<TcpClient> clients = new List<TcpClient>();
            private readonly int port;
            private volatile bool stopping;
            internal HttpFixture()
            {
                listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
                worker = new Thread(Accept); worker.IsBackground = true; worker.Start();
            }
            internal Uri Endpoint(Uri logical) { return new Uri("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/" + logical.Host + logical.PathAndQuery); }
            private static string Route(string url) { Uri uri = new Uri(url); return uri.Host + uri.AbsolutePath; }
            internal void Set(string url, Reply reply) { lock (gate) replies[Route(url)] = reply; }
            internal int Hits(string url) { string route = Route(url); lock (gate) { int count = 0; foreach (string request in requests) if (request == route) count++; return count; } }
            internal int RequestCount { get { lock (gate) return requests.Count; } }
            private void Accept()
            {
                while (!stopping)
                {
                    TcpClient client;
                    try { client = listener.AcceptTcpClient(); } catch { return; }
                    lock (gate) clients.Add(client);
                    ThreadPool.QueueUserWorkItem(delegate { Handle(client); });
                }
            }
            private void Handle(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        client.ReceiveTimeout = 2500; client.SendTimeout = 2500;
                        using (NetworkStream stream = client.GetStream())
                        {
                            StringBuilder header = new StringBuilder();
                            int previous = -1, end = 0;
                            while (header.Length < 16384)
                            {
                                int value = stream.ReadByte(); if (value < 0) return;
                                header.Append((char)value);
                                if (previous == '\r' && value == '\n') end++; else if (value != '\r') end = 0;
                                previous = value;
                                if (end == 2) break;
                            }
                            string requestLine = header.ToString().Split('\n')[0].Trim();
                            string[] parts = requestLine.Split(' '); if (parts.Length < 2) return;
                            string route = parts[1].TrimStart('/'); int query = route.IndexOf('?'); if (query >= 0) route = route.Substring(0, query);
                            Reply reply;
                            lock (gate) { requests.Add(route); if (!replies.TryGetValue(route, out reply)) reply = new Reply { Status = 404 }; }
                            reply.Send(stream);
                        }
                    }
                    catch (IOException) { }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                }
            }
            public void Dispose()
            {
                stopping = true; listener.Stop();
                lock (gate) foreach (TcpClient client in clients) { try { client.Close(); } catch { } }
                worker.Join(2500);
            }
        }
    }
}
#endif

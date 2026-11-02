#if PAVISE_SELFUPDATE_WEBSITE_CHECKS
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;

namespace PaviseApp
{
    internal static class Lang { internal const int Cur = 0; }

    // Uses the real static-asset server and downloader. Only the socket address
    // and the explicitly test-only signing key are replaced; no EXE is executed.
    internal static class SelfUpdateWebsiteChecks
    {
        private static int checks;
        private static void Check(bool value, string message)
        { checks++; if (!value) throw new InvalidOperationException(message); }

        private static void Headers(Uri endpoint, string path, string mediaType, bool attachment)
        {
            var request = (HttpWebRequest)WebRequest.Create(new Uri(endpoint, path));
            request.Method = "HEAD"; request.Timeout = 5000; request.AllowAutoRedirect = false;
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                Check(response.StatusCode == HttpStatusCode.OK, "Static update asset is unavailable: " + path);
                Check(response.ContentType.StartsWith(mediaType, StringComparison.OrdinalIgnoreCase), "Wrong update content type: " + path);
                Check((response.Headers["Cache-Control"] ?? "").Contains("no-store"), "Update assets may be served stale: " + path);
                Check((response.Headers["X-Content-Type-Options"] ?? "") == "nosniff", "Update asset lost nosniff: " + path);
                if (attachment) Check((response.Headers["Content-Disposition"] ?? "").Contains("attachment"), "EXE lacks download disposition");
            }
        }

        public static int Main(string[] args)
        {
            try
            {
                if (args.Length != 3) return 2;
                var endpoint = new Uri(args[0]);
                Check(endpoint.IsLoopback && endpoint.Scheme == "http", "Website test must use local transport");
                SelfUpdateManifest.FixturePublicKeyXml = File.ReadAllText(args[2]);
                var requests = new List<string>();
                SelfUpdateDownload.FixtureTransportEndpoint = delegate(Uri logical)
                {
                    requests.Add(logical.AbsoluteUri);
                    return new Uri(endpoint, logical.PathAndQuery);
                };
                SelfUpdateDownload.FixtureFeedTimeoutMilliseconds = 5000;
                SelfUpdateDownload.FixtureMirrorTimeoutMilliseconds = 10000;
                SelfUpdateDownload.FixtureDownloadTimeoutMilliseconds = 20000;
                SelfUpdateDownload.FixtureReadTimeoutMilliseconds = 5000;
                SelfUpdatePackage package = SelfUpdateDownload.Check(CancellationToken.None);
                Check(new Version(package.Version) > new Version(App.Version), "Website fixture is not a newer version");
                Headers(endpoint, "/updates/stable.json", "application/json", false);
                Headers(endpoint, "/updates/" + package.Version + "/Pavise.exe", "application/octet-stream", true);
                var stages = new HashSet<string>();
                bool progressValid = true;
                string downloaded = SelfUpdateDownload.Download(package, args[1], delegate(SelfUpdateProgress progress)
                {
                    stages.Add(progress.Stage);
                    progressValid &= progress.Received >= 0 && progress.Received <= progress.Total;
                }, CancellationToken.None);
                SelfUpdateDownload.VerifyExecutable(package, downloaded, CancellationToken.None);
                Check(progressValid, "Invalid progress from actual static download");
                Check(new FileInfo(downloaded).Length == package.Size, "Downloaded static EXE size mismatch");
                Check(stages.Contains("Downloading") && stages.Contains("Verifying"), "Website download lost progress stages");
                Check(requests.Count >= 2 && requests[0].EndsWith("/updates/stable.json")
                    && requests[requests.Count - 1].EndsWith("/updates/" + package.Version + "/Pavise.exe"), "Client did not use the production feed and versioned EXE paths");
                File.Delete(downloaded);
                Console.WriteLine("PASS website updater checks=" + checks + " current=" + App.Version + " candidate=" + package.Version
                    + " server=wrangler-local signature=fixture-only sha256=verified assembly_version=verified exe_executed=false production_untouched=true");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
#endif

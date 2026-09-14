// @author bdth 2074055628@qq.com
// File purpose Checks the project version and returns the update URL, manifest is hosted with the official website; downloads always open its release log
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    // The donate QR code also hangs off the manifest, the client re-fetches the image only when the id changes, image URLs allow trusted hosts only
    internal sealed class DonateInfo
    {
        public string Id;
        public string Url;
    }

    internal class UpdateResult
    {
        public bool Ok;
        public bool Newer;
        public string Latest;
        public string Url;
        public string Error;
        public string Source;
        public DonateInfo Donate;
    }

    internal static class UpdateChecker
    {
        private const int RequestTimeoutMs = 8000;
        private const int TotalTimeoutMs = 13000;
        private const int GraceAfterFirstHitMs = 2500;
        private const int MaxBodyBytes = 256 * 1024;
        private const int MaxDonateId = 64;

        private sealed class Source
        {
            public string Name;
            public string Url;
        }

        public static void CheckAsync(Action<UpdateResult> done)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                UpdateResult r = Check();
                try { done(r); } catch { }
            });
        }

        // Two routes to the same manifest, official domain and Cloudflare fallback, whichever returns first wins, add more backups to the array as needed
        //   The query string differs every time, otherwise a stale version number held by caches along the way cannot be bypassed, the no-cache set at upload is the other end
        private static Source[] BuildSources()
        {
            string bust = "?t=" + DateTime.UtcNow.Ticks.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            return new[]
            {
                new Source { Name = Lang.T("site.entry"), Url = App.VersionFeedUrl + bust },
                new Source { Name = "Cloudflare", Url = App.VersionFeedFallbackUrl + bust }
            };
        }

        private static UpdateResult Check()
        {
            try
            {
                var sp = ServicePointManager.SecurityProtocol;
                if (sp != (SecurityProtocolType)0)
                    ServicePointManager.SecurityProtocol = sp | (SecurityProtocolType)3072;
            }
            catch { }

            Source[] sources = BuildSources();
            var gate = new object();
            var signal = new AutoResetEvent(false);
            var hits = new List<UpdateResult>();
            int pending = sources.Length;
            long firstHitTicks = 0;

            foreach (Source s in sources)
            {
                Source src = s;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    UpdateResult hit = null;
                    try { hit = Probe(src); }
                    catch { }
                    lock (gate)
                    {
                        if (hit != null)
                        {
                            hits.Add(hit);
                            if (firstHitTicks == 0) firstHitTicks = DateTime.UtcNow.Ticks;
                        }
                        pending--;
                    }
                    signal.Set();
                });
            }

            long deadline = DateTime.UtcNow.Ticks + TotalTimeoutMs * TimeSpan.TicksPerMillisecond;
            while (true)
            {
                int left; long fh;
                lock (gate) { left = pending; fh = firstHitTicks; }
                long now = DateTime.UtcNow.Ticks;
                if (left == 0 || now >= deadline) break;
                long until = deadline;
                if (fh != 0)
                {
                    long grace = fh + GraceAfterFirstHitMs * TimeSpan.TicksPerMillisecond;
                    if (now >= grace) break;
                    if (grace < until) until = grace;
                }
                int waitMs = (int)((until - now) / TimeSpan.TicksPerMillisecond) + 1;
                signal.WaitOne(waitMs < 50 ? 50 : waitMs);
            }

            UpdateResult r = null;
            lock (gate)
                foreach (UpdateResult hit in hits)
                    if (r == null || IsNewer(hit.Latest, r.Latest)) r = hit;

            if (r == null)
            {
                r = new UpdateResult();
                r.Error = Lang.T("t.updatechecker.4");
                Logger.Warn(Lang.T("log.updatechecker.5") + sources.Length + Lang.T("log.updatechecker.6"));
                return r;
            }
            r.Newer = IsNewer(r.Latest, App.Version);
            Logger.Log(Lang.T("log.updatechecker.7") + r.Source + Lang.T("log.updatechecker.8") + r.Latest);
            return r;
        }

        private static UpdateResult Probe(Source src)
        {
            string body = FetchText(src.Url);
            if (body == null) return null;

            return ParseManifest(body, src.Name);
        }

        internal static UpdateResult ParseManifest(string body, string source)
        {
            if (string.IsNullOrEmpty(body)) return null;
            if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes) return null;
            string tag = JsonValue(body, "version");
            if (string.IsNullOrEmpty(tag)) return null;
            if (!IsCanonicalManifestVersion(tag)) return null;

            var r = new UpdateResult();
            r.Ok = true;
            r.Latest = tag;
            r.Source = source;
            r.Url = App.ChangelogUrl;
            r.Donate = ParseDonate(body);
            return r;
        }

        // Both donate fields are at the manifest top level, missing id or untrusted URL means none
        internal static DonateInfo ParseDonate(string body)
        {
            string id = Clean(JsonValue(body, "donateId"), MaxDonateId);
            if (string.IsNullOrEmpty(id) || !IsSaneDonateId(id)) return null;
            string url = JsonValue(body, "donateUrl");
            if (!IsTrustedWebsiteUrl(url)) return null;
            return new DonateInfo { Id = id, Url = url };
        }

        private static bool IsSaneDonateId(string id)
        {
            foreach (char c in id)
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.') return false;
            return true;
        }

        // Normalize the donate cache identifier, every other control char becomes a space to keep cursor control sequences out
        private static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return null;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '\n') { sb.Append('\n'); continue; }
                if (c == '\r' || c == '\t') { sb.Append(' '); continue; }
                sb.Append(char.IsControl(c) ? ' ' : c);
            }
            string v = sb.ToString().Trim();
            if (v.Length == 0) return null;
            return v.Length > max ? v.Substring(0, max) : v;
        }

        private static bool IsCanonicalManifestVersion(string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag.Trim() != tag) return false;
            Version parsed;
            if (!Version.TryParse(tag, out parsed) || parsed.Revision < 0) return false;
            try { return parsed.ToString(4) == tag; }
            catch { return false; }
        }

        private static HttpWebRequest NewRequest(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Pavise-Update-Check";
            req.Timeout = RequestTimeoutMs;
            req.ReadWriteTimeout = RequestTimeoutMs;
            req.KeepAlive = false;
            return req;
        }

        private static string FetchText(string url)
        {
            try
            {
                HttpWebRequest req = NewRequest(url);
                req.AllowAutoRedirect = true;
                using (var rsp = (HttpWebResponse)req.GetResponse())
                using (Stream raw = rsp.GetResponseStream())
                {
                    if (raw == null) return null;
                    if (rsp.ContentLength > MaxBodyBytes) return null;
                    var buf = new byte[8192];
                    var mem = new MemoryStream();
                    int n;
                    while ((n = raw.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (mem.Length + n > MaxBodyBytes) { mem.Dispose(); return null; }
                        mem.Write(buf, 0, n);
                    }
                    byte[] body = mem.ToArray();
                    mem.Dispose();
                    return Encoding.UTF8.GetString(body);
                }
            }
            catch { return null; }
        }

        // Small files like the QR code go through here, the caller gives the cap, over the cap is dropped, never written to disk
        public static byte[] FetchBytes(string url, int maxBytes)
        {
            if (!IsTrustedWebsiteUrl(url)) return null;
            byte[] bytes = FetchImageBytes(url, maxBytes);
            string fallback = WebsiteFallbackFor(url);
            return bytes ?? (fallback == null ? null : FetchImageBytes(fallback, maxBytes));
        }

        internal static string WebsiteFallbackFor(string url)
        {
            if (!IsTrustedWebsiteUrl(url)) return null;
            Uri source = new Uri(url);
            if (source.Host != new Uri(App.WebsiteUrl).Host) return null;
            return App.WebsiteFallbackUrl.TrimEnd('/') + source.PathAndQuery;
        }

        private static byte[] FetchImageBytes(string url, int maxBytes)
        {
            try
            {
                HttpWebRequest req = NewRequest(url);
                req.AllowAutoRedirect = false;
                using (var rsp = (HttpWebResponse)req.GetResponse())
                using (Stream raw = rsp.GetResponseStream())
                {
                    if (raw == null) return null;
                    if (rsp.ContentLength > maxBytes) return null;
                    var buf = new byte[8192];
                    var mem = new MemoryStream();
                    int n;
                    while ((n = raw.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (mem.Length + n > maxBytes) { mem.Dispose(); return null; }
                        mem.Write(buf, 0, n);
                    }
                    byte[] body = mem.ToArray();
                    mem.Dispose();
                    return body;
                }
            }
            catch { return null; }
        }

        private static string JsonValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            while (i >= 0)
            {
                int p = i + needle.Length;
                while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
                if (p < json.Length && json[p] == ':')
                {
                    p++;
                    while (p < json.Length && (json[p] == ' ' || json[p] == '\t')) p++;
                    if (p < json.Length && json[p] == '"')
                    {
                        var sb = new StringBuilder();
                        p++;
                        while (p < json.Length && json[p] != '"')
                        {
                            if (json[p] == '\\' && p + 1 < json.Length)
                            {
                                p++;
                                if (json[p] == 'u' && p + 4 < json.Length)
                                {
                                    int cp;
                                    if (int.TryParse(json.Substring(p + 1, 4),
                                        System.Globalization.NumberStyles.HexNumber,
                                        System.Globalization.CultureInfo.InvariantCulture, out cp))
                                        sb.Append((char)cp);
                                    p += 5;
                                    continue;
                                }
                                if (json[p] == 'n') sb.Append('\n');
                                else if (json[p] == 't') sb.Append('\t');
                                else sb.Append(json[p]);
                                p++;
                                continue;
                            }
                            sb.Append(json[p]);
                            p++;
                        }
                        if (p >= json.Length || json[p] != '"') return null;
                        string v = sb.ToString();
                        if (v.Length > 0) return v;
                    }
                }
                i = json.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
            }
            return null;
        }

        public static bool IsTrustedWebsiteUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps) return false;
            string host = u.Host.ToLowerInvariant();
            if (u.IsDefaultPort && string.IsNullOrEmpty(u.UserInfo)
                && (host == new Uri(App.WebsiteUrl).Host || host == new Uri(App.WebsiteFallbackUrl).Host)) return true;
            return false;
        }

        public static bool IsNewer(string remote, string local)
        {
            int[] a = ParseVer(remote), b = ParseVer(local);
            if (a.Length == 0) return false;
            int n = Math.Max(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < a.Length ? a[i] : 0;
                int y = i < b.Length ? b[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }

        private static int[] ParseVer(string v)
        {
            if (v == null) return new int[0];
            v = v.Trim();
            int st = 0;
            while (st < v.Length && !char.IsDigit(v[st])) st++;
            v = v.Substring(st);
            int cut = 0;
            while (cut < v.Length && (char.IsDigit(v[cut]) || v[cut] == '.')) cut++;
            var list = new List<int>();
            foreach (string p in v.Substring(0, cut).Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int x;
                if (int.TryParse(p, out x)) list.Add(x);
            }
            return list.ToArray();
        }
    }
}

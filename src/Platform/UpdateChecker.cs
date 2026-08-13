// @author bdth 2074055628@qq.com
// 文件用途 检查项目版本并返回更新地址 直连与国内镜像多条线路并发赛跑

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal class UpdateResult
    {
        public bool Ok;
        public bool Newer;
        public string Latest;
        public string Url;
        public string Error;
        public string Source;
    }

    internal static class UpdateChecker
    {
        private const int RequestTimeoutMs = 8000;
        private const int TotalTimeoutMs = 13000;
        private const int MaxBodyBytes = 256 * 1024;

        private const string VersionFile = "version.json";
        private const string VersionBranch = "main";

        private static readonly string[] TrustedHosts =
        {
            "github.com", "githubusercontent.com", "githubassets.com",
            "ghproxy.net", "gh-proxy.com", "ghfast.top",
            "jsdelivr.net", "jsdelivr.com", "gitee.com",
            "lanzou.com", "lanzoux.com", "lanzoui.com", "lanzouy.com", "lanzn.com",
            "123pan.com", "123pan.cn", "123684.com",
            "pan.baidu.com", "alipan.com", "aliyundrive.com", "quark.cn"
        };

        private enum Feed
        {
            Redirect,
            GitHubJson,
            Manifest,
            JsDelivrApi
        }

        private sealed class Source
        {
            public string Name;
            public string Url;
            public Feed Kind;
            public string Prefix;
        }

        public static void CheckAsync(Action<UpdateResult> done)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                UpdateResult r = Check();
                try { done(r); } catch { }
            });
        }

        private static string RawPath
        {
            get { return App.RepoName + "/" + VersionBranch + "/" + VersionFile; }
        }

        private static Source[] BuildSources()
        {
            string raw = "https://raw.githubusercontent.com/" + RawPath;
            return new[]
            {
                new Source { Name = "GitHub 直连", Kind = Feed.Redirect,
                    Url = App.ReleasesUrl + "/latest" },
                new Source { Name = "GitHub API", Kind = Feed.GitHubJson,
                    Url = "https://api.github.com/repos/" + App.RepoName + "/releases/latest" },
                new Source { Name = "raw 直连", Kind = Feed.Manifest, Url = raw },
                new Source { Name = "ghproxy", Kind = Feed.Manifest,
                    Url = "https://ghproxy.net/" + raw, Prefix = "https://ghproxy.net/" },
                new Source { Name = "gh-proxy", Kind = Feed.Manifest,
                    Url = "https://gh-proxy.com/" + raw, Prefix = "https://gh-proxy.com/" },
                new Source { Name = "ghfast", Kind = Feed.Manifest,
                    Url = "https://ghfast.top/" + raw, Prefix = "https://ghfast.top/" },
                new Source { Name = "jsDelivr", Kind = Feed.Manifest,
                    Url = "https://fastly.jsdelivr.net/gh/" + App.RepoName + "@" + VersionBranch + "/" + VersionFile },
                new Source { Name = "jsDelivr gcore", Kind = Feed.Manifest,
                    Url = "https://gcore.jsdelivr.net/gh/" + App.RepoName + "@" + VersionBranch + "/" + VersionFile },
                new Source { Name = "jsDelivr 包信息", Kind = Feed.JsDelivrApi,
                    Url = "https://data.jsdelivr.com/v1/packages/gh/" + App.RepoName }
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
            var finished = new ManualResetEvent(false);
            UpdateResult winner = null;
            int pending = sources.Length;

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
                        if (hit != null && winner == null) winner = hit;
                        pending--;
                        if (winner != null || pending == 0) finished.Set();
                    }
                });
            }

            finished.WaitOne(TotalTimeoutMs);
            UpdateResult r;
            lock (gate) r = winner;

            if (r == null)
            {
                r = new UpdateResult();
                r.Error = "全部线路都没通 网络受限或仓库暂无发布";
                Logger.Log("检查更新 " + sources.Length + " 条线路全部失败");
                return r;
            }
            r.Newer = IsNewer(r.Latest, App.Version);
            Logger.Log("检查更新 线路 " + r.Source + " 远端 " + r.Latest);
            return r;
        }

        private static UpdateResult Probe(Source src)
        {
            string tag = null, download = null;

            if (src.Kind == Feed.Redirect)
            {
                tag = TagFromReleaseUrl(FetchLocation(src.Url));
            }
            else
            {
                string body = FetchText(src.Url, src.Kind == Feed.GitHubJson);
                if (body == null) return null;
                if (src.Kind == Feed.GitHubJson)
                {
                    tag = JsonValue(body, "tag_name");
                    download = JsonValue(body, "browser_download_url");
                }
                else if (src.Kind == Feed.Manifest)
                {
                    tag = JsonValue(body, "version");
                    download = JsonValue(body, "mirror");
                    if (string.IsNullOrEmpty(download)) download = JsonValue(body, "url");
                }
                else
                {
                    tag = JsonValue(body, "version");
                }
            }

            if (string.IsNullOrEmpty(tag)) return null;
            if (ParseVer(tag).Length == 0) return null;

            var r = new UpdateResult();
            r.Ok = true;
            r.Latest = tag;
            r.Source = src.Name;
            r.Url = ResolveDownload(src, download);
            if (!IsTrustedDownloadUrl(r.Url)) r.Url = App.ReleasesUrl;
            return r;
        }

        private static string ResolveDownload(Source src, string download)
        {
            if (string.IsNullOrEmpty(download)) download = App.ReleasesUrl + "/latest";
            if (string.IsNullOrEmpty(src.Prefix)) return download;
            bool isFile = download.IndexOf("/releases/download/", StringComparison.OrdinalIgnoreCase) >= 0
                || download.IndexOf("/archive/", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!isFile) return download;
            if (download.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
                || download.StartsWith("https://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase))
                return src.Prefix + download;
            return download;
        }

        private static string TagFromReleaseUrl(string url)
        {
            if (url == null) return null;
            int p = url.IndexOf("/releases/tag/", StringComparison.OrdinalIgnoreCase);
            if (p < 0) return null;
            return Uri.UnescapeDataString(url.Substring(p + "/releases/tag/".Length).TrimEnd('/'));
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

        private static string FetchLocation(string url)
        {
            try
            {
                HttpWebRequest req = NewRequest(url);
                req.AllowAutoRedirect = false;
                using (var rsp = (HttpWebResponse)req.GetResponse())
                {
                    int code = (int)rsp.StatusCode;
                    if (code < 300 || code >= 400) return null;
                    string loc = rsp.Headers["Location"];
                    if (loc != null && loc.StartsWith("/")) loc = "https://github.com" + loc;
                    return loc;
                }
            }
            catch { return null; }
        }

        private static string FetchText(string url, bool githubApi)
        {
            try
            {
                HttpWebRequest req = NewRequest(url);
                req.AllowAutoRedirect = true;
                if (githubApi) req.Accept = "application/vnd.github+json";
                using (var rsp = (HttpWebResponse)req.GetResponse())
                using (Stream raw = rsp.GetResponseStream())
                {
                    if (raw == null) return null;
                    var buf = new byte[8192];
                    var mem = new MemoryStream();
                    int n;
                    while ((n = raw.Read(buf, 0, buf.Length)) > 0)
                    {
                        mem.Write(buf, 0, n);
                        if (mem.Length > MaxBodyBytes) break;
                    }
                    return Encoding.UTF8.GetString(mem.ToArray());
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
                        string v = sb.ToString();
                        if (v.Length > 0) return v;
                    }
                }
                i = json.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
            }
            return null;
        }

        public static bool IsTrustedDownloadUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps) return false;
            string host = u.Host.ToLowerInvariant();
            foreach (string ok in TrustedHosts)
                if (host == ok || host.EndsWith("." + ok, StringComparison.Ordinal)) return true;
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

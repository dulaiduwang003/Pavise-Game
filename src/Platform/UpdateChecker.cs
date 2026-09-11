// @author bdth 2074055628@qq.com
// 文件用途 检查项目版本并返回更新地址 清单在自家对象存储 下载走网盘
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    // 公告正文来自网络 只当纯文本用 不解析富文本 不执行 不当地址拼
    internal sealed class NoticeInfo
    {
        public string Id;
        public string Title;
        public string Body;
        public string Url;
    }

    // 捐赠二维码也挂在清单上 id 变了客户端才重新拉图 图片地址只放行信任域名
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
        public NoticeInfo Notice;
        public DonateInfo Donate;
    }

    internal static class UpdateChecker
    {
        private const int RequestTimeoutMs = 8000;
        private const int TotalTimeoutMs = 13000;
        private const int GraceAfterFirstHitMs = 2500;
        private const int MaxBodyBytes = 256 * 1024;
        private const int MaxNoticeId = 64;
        private const int MaxNoticeTitle = 60;
        private const int MaxNoticeBody = 800;

        // 清单域名和下载域名共用这一份白名单 清单里读到的地址不在里面就退回官方网盘
        private static readonly string[] TrustedHosts =
        {
            "aliyuncs.com",
            "lanzou.com", "lanzoux.com", "lanzoui.com", "lanzouy.com", "lanzn.com",
            "123pan.com", "123pan.cn", "123684.com",
            "pan.baidu.com", "alipan.com", "aliyundrive.com", "quark.cn"
        };

        // 公告里的链接单独一张表 除了下载站再放行文档和表单所在的域
        //   概览页那三条外链就挂在这个域 公告要引到教程或表单时用得上
        private static readonly string[] TrustedNoticeHosts = { "feishu.cn", "feishu.net" };

        public static bool IsTrustedNoticeUrl(string url)
        {
            if (IsTrustedDownloadUrl(url)) return true;
            if (string.IsNullOrEmpty(url)) return false;
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps) return false;
            string host = u.Host.ToLowerInvariant();
            foreach (string ok in TrustedNoticeHosts)
                if (host == ok || host.EndsWith("." + ok, StringComparison.Ordinal)) return true;
            return false;
        }

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

        // 同一个清单的两条路 直连和传输加速 谁先回来用谁 要再加备用往数组里添就是
        //   查询串每次都不一样 沿途缓存留住的旧版本号绕不过去 上传时压的 no-cache 是另一头
        private static Source[] BuildSources()
        {
            string bust = "?t=" + DateTime.UtcNow.Ticks.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            return new[]
            {
                new Source { Name = Lang.T("t.updatechecker.1"), Url = App.VersionFeedUrl + bust },
                new Source { Name = Lang.T("t.updatechecker.2"), Url = App.VersionFeedUrlAccelerate + bust }
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

            // 下载地址写在清单里 网盘换地方只改清单 不必为此发一个新版本
            string mirror = JsonValue(body, "mirror");
            string url = JsonValue(body, "url");
            string download = IsTrustedDownloadUrl(mirror) ? mirror
                : (IsTrustedDownloadUrl(url) ? url : null);
            if (download == null) return null;

            var r = new UpdateResult();
            r.Ok = true;
            r.Latest = tag;
            r.Source = source;
            r.Url = download;
            r.Notice = ParseNotice(body);
            r.Donate = ParseDonate(body);
            return r;
        }

        // 公告三个字段都在清单顶层 缺 id 或缺标题就当没有公告
        //   id 只做已读标记 长度和字符都卡死 免得拿它当路径或注册表名使
        internal static NoticeInfo ParseNotice(string body)
        {
            string id = Clean(JsonValue(body, "noticeId"), MaxNoticeId);
            if (string.IsNullOrEmpty(id) || !IsSaneNoticeId(id)) return null;
            string title = Clean(JsonValue(body, "noticeTitle"), MaxNoticeTitle);
            if (string.IsNullOrEmpty(title)) return null;

            var n = new NoticeInfo();
            n.Id = id;
            n.Title = title;
            n.Body = Clean(JsonValue(body, "noticeBody"), MaxNoticeBody);
            string link = JsonValue(body, "noticeUrl");
            n.Url = IsTrustedNoticeUrl(link) ? link : null;
            return n;
        }

        // 捐赠两个字段都在清单顶层 缺 id 或地址不可信就当没有
        internal static DonateInfo ParseDonate(string body)
        {
            string id = Clean(JsonValue(body, "donateId"), MaxNoticeId);
            if (string.IsNullOrEmpty(id) || !IsSaneNoticeId(id)) return null;
            string url = JsonValue(body, "donateUrl");
            if (!IsTrustedDownloadUrl(url)) return null;
            return new DonateInfo { Id = id, Url = url };
        }

        private static bool IsSaneNoticeId(string id)
        {
            foreach (char c in id)
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.') return false;
            return true;
        }

        // 换行留着 公告要分段 其余控制字符一律换空格 免得跑出光标控制那一套
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

        // 二维码这类小文件走这里 上限由调用方给 超限直接丢 不落盘
        public static byte[] FetchBytes(string url, int maxBytes)
        {
            try
            {
                HttpWebRequest req = NewRequest(url);
                req.AllowAutoRedirect = true;
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

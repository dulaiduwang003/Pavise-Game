// @author bdth 2074055628@qq.com
// 文件用途 检查项目版本并返回更新地址

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AegisApp
{
    internal class UpdateResult
    {
        public bool Ok;
        public bool Newer;
        public string Latest;
        public string Url;
        public string Error;
    }

    internal static class UpdateChecker
    {
        public static void CheckAsync(Action<UpdateResult> done)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                UpdateResult r = Check();
                try { done(r); } catch { }
            });
        }

        private static UpdateResult Check()
        {
            var r = new UpdateResult();
            try
            {
                var sp = ServicePointManager.SecurityProtocol;
                if (sp != (SecurityProtocolType)0)
                    ServicePointManager.SecurityProtocol = sp | (SecurityProtocolType)3072;

                string tag = null, url = null;
                string loc = FetchRedirect(App.ReleasesUrl + "/latest");
                if (loc != null)
                {
                    int p = loc.IndexOf("/releases/tag/", StringComparison.OrdinalIgnoreCase);
                    if (p >= 0)
                    {
                        tag = Uri.UnescapeDataString(loc.Substring(p + "/releases/tag/".Length).TrimEnd('/'));
                        url = loc;
                    }
                }
                if (tag == null) { r.Error = "无法获取远端版本（网络不可用或仓库暂无发布）"; return r; }

                r.Ok = true;
                r.Latest = tag;
                r.Url = string.IsNullOrEmpty(url) ? App.ReleasesUrl : url;
                r.Newer = IsNewer(tag, App.Version);
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        private static string FetchRedirect(string url)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.UserAgent = "Aegis-Update-Check";
                req.AllowAutoRedirect = false;
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
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

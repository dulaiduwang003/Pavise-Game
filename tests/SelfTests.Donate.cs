// @author bdth 2074055628@qq.com
// File purpose Donate QR code, manifest field parsing and cache refresh decision
#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void RunDonateTests()
        {
            const string good = "{\"version\":\"1.0.0.0\",\"url\":\"https://pan.quark.cn/s/x\","
                + "\"donateId\":\"20260911-wechat\",\"donateUrl\":\"https://pavise.club/assets/wechat.png\"}";
            DonateInfo d = UpdateChecker.ParseDonate(good);
            if (d == null || d.Id != "20260911-wechat" || !d.Url.EndsWith("/wechat.png")) throw new Exception("捐赠字段没解析出来");
            UpdateResult r = UpdateChecker.ParseManifest(good, "donate");
            if (r == null || r.Donate == null || r.Donate.Id != d.Id) throw new Exception("清单结果没带上捐赠字段");

            // A URL outside the trusted domains is treated as absent, so a tampered manifest cannot point elsewhere
            const string evil = "{\"version\":\"1.0.0.0\",\"url\":\"https://pan.quark.cn/s/x\","
                + "\"donateId\":\"a\",\"donateUrl\":\"https://evil.example/qr.png\"}";
            if (UpdateChecker.ParseDonate(evil) != null) throw new Exception("非信任域名的二维码地址被接受");
            const string plain = "{\"version\":\"1.0.0.0\",\"donateId\":\"a\",\"donateUrl\":\"http://pavise.club/x.png\"}";
            if (UpdateChecker.ParseDonate(plain) != null) throw new Exception("明文 http 的二维码地址被接受");
            // id is only a cache tag, anything with path characters is rejected
            const string badId = "{\"donateId\":\"..\\\\x\",\"donateUrl\":\"https://pavise.club/x.png\"}";
            if (UpdateChecker.ParseDonate(badId) != null) throw new Exception("带路径字符的 donateId 被接受");
            if (UpdateChecker.ParseDonate("{\"version\":\"1.0.0.0\"}") != null) throw new Exception("没有捐赠字段却解析出了东西");
            // Old manifests lack these two fields, the update check itself must not fail because of that
            if (UpdateChecker.ParseManifest("{\"version\":\"1.0.0.0\",\"url\":\"https://pan.quark.cn/s/x\"}", "legacy") == null)
                throw new Exception("没有捐赠字段的旧清单被拒绝");

            // Without the fields in the manifest fall back to the built-in one; id and URL pass the same check, the manifest wins when present
            if (DonateCache.Default == null || DonateCache.Effective != DonateCache.Default) throw new Exception("没有清单时没退到内置捐赠信息");
            if (UpdateChecker.ParseDonate("{\"donateId\":\"" + DonateCache.Default.Id + "\",\"donateUrl\":\"" + DonateCache.Default.Url + "\"}") == null)
                throw new Exception("内置捐赠信息过不了清单同一道检查");
            DonateCache.Latest = d;
            try { if (DonateCache.Effective != d) throw new Exception("有清单时没以清单为准"); }
            finally { DonateCache.Latest = null; }

            // Refresh decision: no id in manifest means no action, no local image means fetch, id mismatch means fetch, match means no fetch
            Eq(false, DonateCache.NeedsRefresh("", null, false));
            Eq(false, DonateCache.NeedsRefresh("a", "", true));
            Eq(true, DonateCache.NeedsRefresh("", "a", false));
            Eq(true, DonateCache.NeedsRefresh("a", "b", true));
            Eq(true, DonateCache.NeedsRefresh("a", "a", false));
            Eq(false, DonateCache.NeedsRefresh("a", "a", true));

            // Non-image bytes are not written to disk, nor is anything over the limit
            if (DonateCache.Decode(new byte[] { 1, 2, 3 }) != null) throw new Exception("非图片字节被解成了图");
            if (DonateCache.Decode(new byte[DonateCache.MaxImageBytes + 1]) != null) throw new Exception("超限字节没有被拒");
            Console.WriteLine("PASS Donate: manifest fields, trusted host only, refresh table, decode guard");
        }
    }
}
#endif

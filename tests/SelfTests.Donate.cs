// 作者：bdth 2074055628@qq.com
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

            const string evil = "{\"version\":\"1.0.0.0\",\"url\":\"https://pan.quark.cn/s/x\","
                + "\"donateId\":\"a\",\"donateUrl\":\"https://evil.example/qr.png\"}";
            if (UpdateChecker.ParseDonate(evil) != null) throw new Exception("非信任域名的二维码地址被接受");
            const string plain = "{\"version\":\"1.0.0.0\",\"donateId\":\"a\",\"donateUrl\":\"http://pavise.club/x.png\"}";
            if (UpdateChecker.ParseDonate(plain) != null) throw new Exception("明文 http 的二维码地址被接受");
            const string badId = "{\"donateId\":\"..\\\\x\",\"donateUrl\":\"https://pavise.club/x.png\"}";
            if (UpdateChecker.ParseDonate(badId) != null) throw new Exception("带路径字符的 donateId 被接受");
            if (UpdateChecker.ParseDonate("{\"version\":\"1.0.0.0\"}") != null) throw new Exception("没有捐赠字段却解析出了东西");
            if (UpdateChecker.ParseManifest("{\"version\":\"1.0.0.0\",\"url\":\"https://pan.quark.cn/s/x\"}", "legacy") == null)
                throw new Exception("没有捐赠字段的旧清单被拒绝");

            if (DonateCache.Default == null || DonateCache.Effective != DonateCache.Default) throw new Exception("没有清单时没退到内置捐赠信息");
            if (UpdateChecker.ParseDonate("{\"donateId\":\"" + DonateCache.Default.Id + "\",\"donateUrl\":\"" + DonateCache.Default.Url + "\"}") == null)
                throw new Exception("内置捐赠信息过不了清单同一道检查");
            DonateCache.Latest = d;
            try { if (DonateCache.Effective != d) throw new Exception("有清单时没以清单为准"); }
            finally { DonateCache.Latest = null; }

            Eq(false, DonateCache.NeedsRefresh("", null, false));
            Eq(false, DonateCache.NeedsRefresh("a", "", true));
            Eq(true, DonateCache.NeedsRefresh("", "a", false));
            Eq(true, DonateCache.NeedsRefresh("a", "b", true));
            Eq(true, DonateCache.NeedsRefresh("a", "a", false));
            Eq(false, DonateCache.NeedsRefresh("a", "a", true));

            if (DonateCache.Decode(new byte[] { 1, 2, 3 }) != null) throw new Exception("非图片字节被解成了图");
            if (DonateCache.Decode(new byte[DonateCache.MaxImageBytes + 1]) != null) throw new Exception("超限字节没有被拒");
            Console.WriteLine("PASS Donate: manifest fields, trusted host only, refresh table, decode guard");
        }
    }
}
#endif

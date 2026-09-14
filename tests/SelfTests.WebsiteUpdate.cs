#if PAVISE_SELFTEST
using System;
namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void RunWebsiteUpdateTests()
        {
            // The new feed needs only a canonical version. Legacy download or notice fields cannot redirect the client.
            UpdateResult result = UpdateChecker.ParseManifest("{\"version\":\"3.0.0.0\",\"url\":\"https://evil.example/file.exe\",\"noticeTitle\":\"ignored\"}", "website");
            Eq(true, result != null && result.Ok);
            Eq(App.ChangelogUrl, result.Url);
            Eq(true, UpdateChecker.ParseManifest("{\"version\":\"3.0.0.0\"}", "website") != null);
            foreach (string version in new[] { "v3.0.0.0", "3.0", "3.0.0.0 ", "03.0.0.0", "invalid" })
                Eq(true, UpdateChecker.ParseManifest("{\"version\":\"" + version + "\"}", "website") == null);
            Eq(true, UpdateChecker.IsTrustedWebsiteUrl(App.WebsiteUrl + "assets/wechat.png"));
            Eq(App.WebsiteFallbackUrl + "assets/wechat.png?v=2", UpdateChecker.WebsiteFallbackFor(App.WebsiteUrl + "assets/wechat.png?v=2"));
            Eq(true, UpdateChecker.WebsiteFallbackFor(App.WebsiteFallbackUrl) == null);
            Eq(true, UpdateChecker.WebsiteFallbackFor("https://evil.example/assets/wechat.png") == null);
            foreach (string url in new[] { "https://pavise.club.evil.example/a", "http://pavise.club/a", "https://pavise.club:444/a", "https://user@pavise.club/a", "https://paivse.oss-cn-shanghai.aliyuncs.com/a" })
                Eq(false, UpdateChecker.IsTrustedWebsiteUrl(url));
            Eq(true, UpdateChecker.IsNewer("2.2.2.10", "2.2.2.9"));
            Eq(false, UpdateChecker.IsNewer(App.Version, App.Version));
        }
    }
}
#endif

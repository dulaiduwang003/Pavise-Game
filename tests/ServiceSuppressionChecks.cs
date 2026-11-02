#if PAVISE_SERVICE_SUPPRESSION_CHECKS
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // Which services-session processes the competitive sweep may suppress, and which network paths
    // stay protected. Pure policy: no process is opened and nothing is written.
    internal static class ServiceSuppressionChecks
    {
        private static int checks;
        private static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException("FAIL " + reason); }
        private static ProcEntry Entry(int pid, int session, string name, string path)
        { return new ProcEntry { Pid = pid, Session = session, Creation = 133000000000000000L + pid, Name = name, Path = path }; }

        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--negative-control") { Console.WriteLine("FAIL: intentional service suppression negative control"); return 3; }
            try
            {
                const string windows = @"C:\Windows";
                const string viewer = @"C:\App\网易UU远程\GameViewer\GameViewerService.exe";
                Check(BackgroundServicePolicy.ThirdPartyImage(viewer, windows), "a third-party service image was treated as Windows");
                Check(BackgroundServicePolicy.ThirdPartyImage(@"C:\Windows.old\app\svc.exe", windows), "a sibling of the Windows directory was treated as Windows");
                foreach (string inside in new[] { @"C:\Windows\System32\svchost.exe", @"c:\windows\system32\DriverStore\FileRepository\nv\NVDisplay.Container.exe", @"C:\Windows\explorer.exe" })
                    Check(!BackgroundServicePolicy.ThirdPartyImage(inside, windows + "\\"), "a Windows component counted as third-party: " + inside);
                foreach (string unknown in new string[] { null, "", "svc.exe", @"\Device\HarddiskVolume3\App\svc.exe", @"\\server\share\svc.exe" })
                    Check(!BackgroundServicePolicy.ThirdPartyImage(unknown, windows), "an unknown or non-local image counted as third-party: " + unknown);

                var hosts = new List<KeyValuePair<string, int>> { new KeyValuePair<string, int>("Spooler", 700), new KeyValuePair<string, int>("WSearch", 701) };
                ProcEntry service = Entry(4688, 0, "GameViewerService", null);
                Check(BackgroundServicePolicy.Eligible(PerformancePreset.Competitive, service, viewer, null, windows), "a competitive sweep skipped a third-party service");
                Check(BackgroundServicePolicy.Eligible(PerformancePreset.Extreme, service, viewer, hosts, windows), "the extreme scene skipped a third-party service");
                Check(!BackgroundServicePolicy.Eligible(PerformancePreset.Standard, service, viewer, hosts, windows), "the smart scene suppressed a service");
                Check(!BackgroundServicePolicy.Eligible(PerformancePreset.Competitive, service, null, hosts, windows), "a service with an unknown image was suppressed");
                Check(!BackgroundServicePolicy.Eligible(PerformancePreset.Competitive, Entry(4689, 1, "GameViewer", viewer), viewer, hosts, windows), "a user-session process took the service path");
                var unborn = Entry(4690, 0, "GameViewerService", viewer); unborn.Creation = 0;
                Check(!BackgroundServicePolicy.Eligible(PerformancePreset.Competitive, unborn, viewer, hosts, windows), "a process without a creation time was admitted");
                Check(!BackgroundServicePolicy.Eligible(PerformancePreset.Competitive, Entry(4, 0, "System", viewer), viewer, hosts, windows), "the System process was admitted");
                Check(!BackgroundServicePolicy.Eligible(PerformancePreset.Competitive, Entry(900, 0, "svchost", @"C:\Windows\System32\svchost.exe"),
                    @"C:\Windows\System32\svchost.exe", hosts, windows), "a generic service host was admitted");
                Check(BackgroundServicePolicy.Eligible(PerformancePreset.Competitive, Entry(700, 0, "spoolsv", @"C:\Windows\System32\spoolsv.exe"),
                    @"C:\Windows\System32\spoolsv.exe", hosts, windows), "the print spooler lost its existing admission");
                Check(!BackgroundServicePolicy.Eligible(PerformancePreset.Competitive, Entry(702, 0, "SearchIndexer", @"C:\Windows\System32\SearchIndexer.exe"),
                    @"C:\Windows\System32\SearchIndexer.exe", hosts, windows), "a search indexer hosting no WSearch service was admitted");

                foreach (string tunnel in new[] { "clash-verge", "Clash for Windows", "mihomo", "sing-box", "v2rayN", "xray", "wireguard", "openvpnserv",
                    "tailscaled", "zerotier-one_x64", "SSTap", "Netch", "uu", "leigod", "GameAccelerator" })
                    Check(NetAcceleratorCatalog.IsAcceleratorLikeName(tunnel), "a network path process lost its protection: " + tunnel);
                foreach (string plain in new[] { "GameViewerService", "ToDesk_Service", "SunloginService", "explorer", "OfficeClickToRun", "MicrosoftEdgeUpdate" })
                    Check(!NetAcceleratorCatalog.IsAcceleratorLikeName(plain), "an ordinary program was protected as a network path: " + plain);

                // 通用文件名的隧道内核按两级目录识别；更上层的目录名不参与，避免用户名或盘符目录牵连。
                foreach (string tunnelPath in new[] { @"C:\Users\Star\.config\clash\service\service.exe", @"D:\Tools\Clash Verge\resources\helper.exe",
                    @"C:\Program Files\OpenVPN Connect\agent_ovpnconnect.exe", @"C:\Program Files (x86)\Netease\网易UU加速器\svc.exe" })
                    Check(NetAcceleratorCatalog.IsAcceleratorLikePath(tunnelPath), "a tunnel service lost its directory protection: " + tunnelPath);
                foreach (string plainPath in new[] { viewer, @"C:\clash\a\b\svc.exe", @"C:\Program Files\ToDesk\ToDesk_Service.exe", "service.exe", null, "", "C:\\bad|path\\x.exe" })
                    Check(!NetAcceleratorCatalog.IsAcceleratorLikePath(plainPath), "an ordinary service was protected by its directory: " + plainPath);

                foreach (string runtime in new[] { "GamingServices", "gamingservicesnet" })
                    Check(!BackgroundServicePolicy.Eligible(PerformancePreset.Competitive, Entry(5000, 0, runtime, null),
                        @"C:\Program Files\WindowsApps\Microsoft.GamingServices_1.0_x64__8wekyb3d8bbwe\" + runtime + ".exe", hosts, windows),
                        "an Xbox game runtime service was admitted: " + runtime);

                // 安全中心登记的产品：取产品目录，不上溯到厂商目录，不覆盖 Windows 目录或盘符根。
                string[] bases = { @"C:\Program Files", @"C:\Program Files (x86)", @"C:\ProgramData" };
                Check(SecurityProductCatalog.ProductRoot(@"%ProgramFiles%\Windows Defender\MsMpeng.exe", new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }, windows)
                    == Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + @"\Windows Defender", "the Defender reporting path did not expand to its directory");
                Check(SecurityProductCatalog.ProductRoot(@"C:\Program Files (x86)\360\360Safe\safemon\360tray.exe", bases, windows) == @"C:\Program Files (x86)\360\360Safe",
                    "the 360 product root does not cover its engine directory");
                Check(SecurityProductCatalog.ProductRoot(@"C:\Program Files (x86)\Tencent\QQPCMgr\17.5.0\QQPCTray.exe", bases, windows) == @"C:\Program Files (x86)\Tencent\QQPCMgr",
                    "the PC manager root escaped its product directory");
                Check(SecurityProductCatalog.ProductRoot(@"C:\Program Files (x86)\Tencent\QQPCMgr\QQPCTray.exe", bases, windows) == @"C:\Program Files (x86)\Tencent\QQPCMgr",
                    "a product directly under its vendor folder widened to the vendor");
                Check(SecurityProductCatalog.ProductRoot(@"""C:\Program Files\ESET\ESET Security\ecmds.exe""", bases, windows) == @"C:\Program Files\ESET\ESET Security",
                    "a quoted registration lost its product directory");
                Check(SecurityProductCatalog.ProductRoot(@"D:\Soft\Huorong\Sysdiag\bin\HipsTray.exe", bases, windows) == @"D:\Soft\Huorong\Sysdiag\bin",
                    "a custom install widened beyond its own directory");
                foreach (string none in new[] { "windowsdefender://", @"C:\Windows\System32\SecurityHealthService.exe", @"C:\Program Files\av.exe", @"D:\av.exe", "", null, "av.exe" })
                    Check(SecurityProductCatalog.ProductRoot(none, bases, windows) == null, "an unusable registration produced a protected root: " + none);
                Check(FamilyBoundary.UnderRoot(@"C:\Program Files (x86)\360\360Safe\deepscan\zhudongfangyu.exe", @"C:\Program Files (x86)\360\360Safe")
                    && !FamilyBoundary.UnderRoot(@"C:\Program Files (x86)\Tencent\QQ\Bin\QQ.exe", @"C:\Program Files (x86)\Tencent\QQPCMgr"), "the product root boundary is wrong");

                // 服务也受用户白名单约束；预设名称只在可信路径生效。
                var rules = new List<WhitelistRule>();
                WhitelistRule rule;
                Check(WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, "GameViewerService.exe", out rule), "the name rule was not created"); rules.Add(rule);
                Check(WhitelistRule.TryCreate(WhitelistRuleKind.ExactPath, @"C:\App\ToDesk\ToDesk_Service.exe", out rule), "the path rule was not created"); rules.Add(rule);
                Check(WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, "wslservice", out rule), "the preset rule was not created"); rules.Add(rule);
                Check(GameMode.ServiceRuleMatches(rules, "GameViewerService", viewer), "a whitelisted service name was ignored");
                Check(GameMode.ServiceRuleMatches(rules, "ToDesk_Service", @"c:\app\todesk\ToDesk_Service.exe"), "a whitelisted service path was ignored");
                Check(!GameMode.ServiceRuleMatches(rules, "ToDesk_Service", @"D:\Other\ToDesk_Service.exe"), "a path rule matched a different image");
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                Check(GameMode.ServiceRuleMatches(rules, "wslservice", programFiles + @"\WSL\wslservice.exe"), "the WSL preset no longer protects its service");
                Check(!GameMode.ServiceRuleMatches(rules, "wslservice", @"C:\Temp\wslservice.exe"), "a preset name protected an untrusted image");
                Check(!GameMode.ServiceRuleMatches(rules, "SunloginService", @"C:\Program Files\Oray\SunLogin\SunloginService.exe"), "an unlisted service was whitelisted");
                Check(!GameMode.ServiceRuleMatches(null, "GameViewerService", viewer), "a missing rule list protected a service");

                Console.WriteLine("PASS service suppression checks=" + checks);
                return 0;
            }
            catch (Exception error) { Console.WriteLine(error.Message); return 1; }
        }
    }
}
#endif

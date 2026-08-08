// @author bdth 2074055628@qq.com
// 文件用途 验证按程序名的 QoS 限速策略能否真实压住本进程的上行速率 并确认生效条件

using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private const string QosPolicyRoot = @"SOFTWARE\Policies\Microsoft\Windows\QoS";
        private const string QosLabPolicy = "PaviseQosLab";

        private static IPAddress FindGateway()
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (GatewayIPAddressInformation gw in nic.GetIPProperties().GatewayAddresses)
                    if (gw.Address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(gw.Address))
                        return gw.Address;
            }
            return null;
        }

        private static double UdpBlastMbps(IPAddress target, int seconds, long capBytes)
        {
            using (var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                var ep = new IPEndPoint(target, 9);
                var payload = new byte[1400];
                new Random(7).NextBytes(payload);
                long sent = 0;
                var sw = Stopwatch.StartNew();
                long until = (long)seconds * Stopwatch.Frequency;
                while (sw.ElapsedTicks < until && sent < capBytes)
                {
                    try { sent += sock.SendTo(payload, ep); }
                    catch (SocketException) { Thread.Sleep(1); }
                }
                sw.Stop();
                double sec = sw.Elapsed.TotalSeconds;
                return sec > 0 ? sent * 8.0 / 1000000.0 / sec : 0;
            }
        }

        private static void WriteQosPolicy(string exeName, string throttleKbps)
        {
            using (RegistryKey root = Registry.LocalMachine.CreateSubKey(QosPolicyRoot + "\\" + QosLabPolicy))
            {
                root.SetValue("Version", "1.0", RegistryValueKind.String);
                root.SetValue("Application Name", exeName, RegistryValueKind.String);
                root.SetValue("Protocol", "*", RegistryValueKind.String);
                root.SetValue("Local Port", "*", RegistryValueKind.String);
                root.SetValue("Local IP", "*", RegistryValueKind.String);
                root.SetValue("Local IP Prefix Length", "*", RegistryValueKind.String);
                root.SetValue("Remote Port", "*", RegistryValueKind.String);
                root.SetValue("Remote IP", "*", RegistryValueKind.String);
                root.SetValue("Remote IP Prefix Length", "*", RegistryValueKind.String);
                root.SetValue("DSCP Value", "-1", RegistryValueKind.String);
                root.SetValue("Throttle Rate", throttleKbps, RegistryValueKind.String);
            }
        }

        private static void DeleteQosPolicy()
        {
            try { Registry.LocalMachine.DeleteSubKeyTree(QosPolicyRoot + "\\" + QosLabPolicy, false); }
            catch { }
        }

        private static void GpUpdate()
        {
            try
            {
                using (Process p = Process.Start(new ProcessStartInfo("gpupdate.exe", "/target:computer /force")
                { UseShellExecute = false, CreateNoWindow = true }))
                    p.WaitForExit(60000);
            }
            catch { }
        }

        private static void RunQosLab(string output, string kbpsArg)
        {
            string kbps = "1000";
            int parsed;
            if (int.TryParse(kbpsArg ?? "", out parsed) && parsed >= 100) kbps = parsed.ToString();

            var sb = new StringBuilder();
            sb.AppendLine("=== QoS 上行限速台架 ===");
            sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                + "  Throttle Rate=" + kbps);
            try
            {
                IPAddress gw = FindGateway();
                if (gw == null)
                {
                    sb.AppendLine("结论：找不到默认网关，无法测试");
                    Environment.ExitCode = 1;
                    return;
                }
                sb.AppendLine("目标：网关 " + gw + " UDP:9（discard）");

                double baseline = UdpBlastMbps(gw, 2, 100L * 1024 * 1024);
                sb.AppendLine("基线上行：" + baseline.ToString("F1") + " Mbps");

                string exeName = System.IO.Path.GetFileName(
                    Process.GetCurrentProcess().MainModule.FileName);
                WriteQosPolicy(exeName, kbps);
                Thread.Sleep(1500);
                double immediate = UdpBlastMbps(gw, 2, 100L * 1024 * 1024);
                sb.AppendLine("写入策略后立即：" + immediate.ToString("F1") + " Mbps");

                double afterGp = -1;
                if (immediate > baseline * 0.5)
                {
                    GpUpdate();
                    Thread.Sleep(1500);
                    afterGp = UdpBlastMbps(gw, 2, 100L * 1024 * 1024);
                    sb.AppendLine("gpupdate 后：" + afterGp.ToString("F1") + " Mbps");
                }

                DeleteQosPolicy();
                GpUpdate();
                Thread.Sleep(1000);
                double restored = UdpBlastMbps(gw, 2, 100L * 1024 * 1024);
                sb.AppendLine("删除策略后：" + restored.ToString("F1") + " Mbps");

                sb.AppendLine();
                double limited = afterGp >= 0 ? afterGp : immediate;
                bool works = baseline > 5 && limited < baseline * 0.5;
                bool recovers = restored > baseline * 0.5;
                if (works)
                {
                    sb.AppendLine("限速生效：" + baseline.ToString("F0") + " → " + limited.ToString("F1")
                        + " Mbps（" + (afterGp >= 0 ? "需要 gpupdate 刷新" : "写注册表即生效") + "）"
                        + "，删除后" + (recovers ? "恢复正常" : "未恢复！"));
                    sb.AppendLine("结论：机制有效，值得实现（游戏时限制后台进程上行，退出删除策略）");
                }
                else
                {
                    sb.AppendLine("结论：策略未能压住上行速率（" + baseline.ToString("F0") + " → "
                        + limited.ToString("F1") + " Mbps），机制不可靠，不做");
                }
                Environment.ExitCode = works && recovers ? 0 : 1;
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR=" + ex.Message);
                DeleteQosPolicy();
                Environment.ExitCode = 1;
            }
            finally
            {
                System.IO.File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
            }
        }
    }
}

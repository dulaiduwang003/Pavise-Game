// @author bdth 2074055628@qq.com
// 文件用途 逐网卡禁用 Nagle 与延迟 ACK 只惠及 TCP 联网的游戏 逐值快照可逆

using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    // Nagle 合包 + 延迟 ACK 叠加最坏可到 200ms 级（老 MMO 时代有大量实测），
    // 但只作用于 TCP——UDP 系竞技游戏（MOBA / FPS）严格零收益，文案必须写明。
    // 每块网卡的两个原值都走 ReversibleReg 独立快照，关闭时逐值还原；
    // 开启后新插的网卡不会被覆盖，重新开关一次即可。
    internal static class NagleTweak
    {
        private const string IfRoot = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
        private const string ListKey = "NagleIfList";

        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load("NagleOffByPavise", false); } }

        private static ReversibleReg RegOf(string guid, string valName)
        {
            return new ReversibleReg(Registry.LocalMachine, IfRoot + "\\" + guid, valName,
                RegistryValueKind.DWord, "Nagle_" + valName + "_" + guid);
        }

        public static bool Enable()
        {
            lock (lk)
            {
                try
                {
                    string[] guids;
                    using (var root = Registry.LocalMachine.OpenSubKey(IfRoot))
                    {
                        if (root == null) return false;
                        guids = root.GetSubKeyNames();
                    }
                    var touched = new List<string>();
                    foreach (string guid in guids)
                    {
                        bool ok = RegOf(guid, "TcpAckFrequency").Apply(1);
                        ok &= RegOf(guid, "TCPNoDelay").Apply(1);
                        if (ok) touched.Add(guid);
                        else Logger.Log("TCP 低延迟：网卡 " + guid + " 写入失败，已跳过");
                    }
                    if (touched.Count == 0) return false;
                    if (!Settings.SaveStr(ListKey, string.Join(";", touched.ToArray())))
                    {
                        foreach (string guid in touched)
                        {
                            RegOf(guid, "TcpAckFrequency").Restore();
                            RegOf(guid, "TCPNoDelay").Restore();
                        }
                        Logger.Log("TCP 低延迟：网卡清单无法持久化，已全部还原");
                        return false;
                    }
                    Settings.Save("NagleOffByPavise", true);
                    Logger.Log("TCP 低延迟已启用：" + touched.Count + " 块网卡禁用 Nagle 与延迟 ACK，新建连接生效");
                    return true;
                }
                catch { return false; }
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                try
                {
                    bool all = true;
                    foreach (string guid in ParseList(Settings.LoadStr(ListKey, "")))
                    {
                        all &= RegOf(guid, "TcpAckFrequency").Restore();
                        all &= RegOf(guid, "TCPNoDelay").Restore();
                    }
                    if (all)
                    {
                        Settings.SaveStr(ListKey, "");
                        Settings.Save("NagleOffByPavise", false);
                        Logger.Log("TCP 低延迟已关闭，各网卡原值已还原");
                    }
                    else Logger.Log("TCP 低延迟：部分网卡还原失败，快照保留待下次重试");
                    return all;
                }
                catch { return false; }
            }
        }

        internal static string[] ParseList(string raw)
        {
            return (raw ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}

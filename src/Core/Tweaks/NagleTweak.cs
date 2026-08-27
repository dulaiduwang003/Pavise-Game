// @author bdth 2074055628@qq.com
// 文件用途 对以太与无线网卡关闭 Nagle 与延迟 ACK 降低小包延迟 记原值可精确还原 还原时全接口扫描不漏孤儿快照
using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class NagleTweak
    {
        private const string IfRoot = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
        private const string ListKey = "NagleIfList";

        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load("NagleOffByPavise", false); } }

        // 供 UI 判断该不该置灰 Enable 就是照这份接口列表逐个写的 列表空则无处可写
        public static bool HasWritableInterfaces() { return WritableInterfaceGuids().Length > 0; }

        // 只对以太和无线这两类真的走链路的网卡下手
        //   回环 隧道 PPP 这些接口上没有物理排队 写这两个值不会改变任何时序
        //   只是往注册表多铺一份要还原的足迹 顺带把日志里的接口数报虚
        //   拿不到接口清单时退回全量 老口径至少不会漏写
        private static string[] WritableInterfaceGuids()
        {
            string[] all = AllInterfaceGuids();
            var byId = new Dictionary<string, NetworkInterfaceType>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                    if (!string.IsNullOrEmpty(nic.Id)) byId[nic.Id] = nic.NetworkInterfaceType;
            }
            catch { return all; }
            if (byId.Count == 0) return all;

            var kept = new List<string>();
            foreach (string guid in all)
            {
                NetworkInterfaceType type;
                if (!byId.TryGetValue(guid, out type)) continue;
                if (type == NetworkInterfaceType.Ethernet
                    || type == NetworkInterfaceType.GigabitEthernet
                    || type == NetworkInterfaceType.FastEthernetT
                    || type == NetworkInterfaceType.FastEthernetFx
                    || type == NetworkInterfaceType.Wireless80211)
                    kept.Add(guid);
            }
            // 筛完是空的说明这台机器真没有以太或无线网卡 那就是无处可写
            //   这时候再退回全量等于专挑回环和隧道写 正好是最不该碰的那几个
            return kept.ToArray();
        }

        // 对以太与无线网卡写 TcpAckFrequency=1 / TCPNoDelay=1
        //   ReversibleReg.Apply 负责单值记原值与回读；ApplyPair 负责两个值的事务回滚
        //   已应用的接口记进 ListKey 供 Restore 复用（Restore 再叠加全接口扫描兜孤儿）
        //   需管理员（HKLM）新建连接立即生效 不必重启
        public static bool Enable()
        {
            lock (lk)
            {
                try
                {
                    var applied = new List<string>();
                    var owned = new List<string>();
                    int failed = 0;
                    int rollbackFailed = 0;
                    foreach (string guid in WritableInterfaceGuids())
                    {
                        var ack = RegOf(guid, "TcpAckFrequency");
                        var noDelay = RegOf(guid, "TCPNoDelay");
                        bool rollbackFailedForInterface;
                        if (ApplyPair(ack, noDelay, out rollbackFailedForInterface))
                        {
                            applied.Add(guid);
                            owned.Add(guid);
                        }
                        else
                        {
                            failed++;
                            // 回滚失败意味着仍可能持有部分改动。把它记进台账并保持开关为开，
                            // 让用户有明确的“关闭/还原”入口，而不是留下一个看不见的半启用状态。
                            if (rollbackFailedForInterface)
                            {
                                rollbackFailed++;
                                owned.Add(guid);
                            }
                        }
                    }
                    if (owned.Count == 0)
                    {
                        Logger.Log(Lang.T("log.nagletweak.4"));
                        return false;
                    }
                    Settings.SaveStr(ListKey, string.Join(";", owned.ToArray()));
                    Settings.Save("NagleOffByPavise", true);
                    Logger.Log(Lang.F("log.nagletweak.3", applied.Count)
                        + (failed > 0 ? Lang.F("log.nagletweak.5", failed) : "")
                        + (rollbackFailed > 0 ? " 回滚失败 " + rollbackFailed + " 个接口，已保留还原入口。" : ""));
                    if (applied.Count == 0) return false;
                    // 写成一块就算成功 状态已经落盘了 这里回 false 会让界面弹"失败"却把开关显示成开
                    //   个别接口写不进多半是别的工具把这两个值存成了非 DWORD 类型 跳过它不影响其余接口
                    return true;
                }
                catch { return false; }
            }
        }

        // 单块网卡的两个值是一笔事务：任意一个失败就把两个都恢复。
        // ReversibleReg.Apply 自身只负责单值写入与核验，不会替调用方回滚已经成功的兄弟值。
        internal static bool ApplyPair(ReversibleReg ack, ReversibleReg noDelay,
            out bool rollbackFailed)
        {
            rollbackFailed = false;
            bool ackOk = ack != null && ack.Apply(1);
            if (!ackOk)
            {
                bool restoredAck = ack == null || ack.Restore();
                rollbackFailed = !restoredAck;
                return false;
            }
            bool noDelayOk = noDelay != null && noDelay.Apply(1);
            if (noDelayOk) return true;

            bool restored = true;
            if (ack != null) restored &= ack.Restore();
            if (noDelay != null) restored &= noDelay.Restore();
            rollbackFailed = !restored;
            return false;
        }

        public static bool Disable() { return Restore(); }

        public static bool HasResidue()
        {
            if (Settings.Load("NagleOffByPavise", false)) return true;
            if (ParseList(Settings.LoadStr(ListKey, "")).Length > 0) return true;
            foreach (string guid in AllInterfaceGuids())
                if (RegOf(guid, "TcpAckFrequency").HasBackup || RegOf(guid, "TCPNoDelay").HasBackup)
                    return true;
            return false;
        }

        private static string[] AllInterfaceGuids()
        {
            try
            {
                using (var root = Registry.LocalMachine.OpenSubKey(IfRoot))
                    return root == null ? new string[0] : root.GetSubKeyNames();
            }
            catch { return new string[0]; }
        }

        private static ReversibleReg RegOf(string guid, string valName)
        {
            return new ReversibleReg(Registry.LocalMachine, IfRoot + "\\" + guid, valName,
                RegistryValueKind.DWord, "Nagle_" + valName + "_" + guid);
        }

        public static bool Restore()
        {
            lock (lk)
            {
                try
                {
                    var guids = new List<string>(ParseList(Settings.LoadStr(ListKey, "")));
                    foreach (string guid in AllInterfaceGuids())
                    {
                        bool known = false;
                        foreach (string g in guids)
                            if (string.Equals(g, guid, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
                        if (!known) guids.Add(guid);
                    }
                    bool all = true;
                    foreach (string guid in guids)
                    {
                        all &= RegOf(guid, "TcpAckFrequency").Restore();
                        all &= RegOf(guid, "TCPNoDelay").Restore();
                    }
                    if (all)
                    {
                        Settings.SaveStr(ListKey, "");
                        Settings.Save("NagleOffByPavise", false);
                        Logger.Log(Lang.T("log.nagletweak.1"));
                    }
                    else Logger.Log(Lang.T("log.nagletweak.2"));
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

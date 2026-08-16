// @author bdth 2074055628@qq.com
// 文件用途 清理旧版本逐网卡写入的 Nagle 与延迟 ACK 改动 只保留还原能力 还原时全接口扫描不漏孤儿快照

using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class NagleTweak
    {
        private const string IfRoot = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
        private const string ListKey = "NagleIfList";

        private static readonly object lk = new object();

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

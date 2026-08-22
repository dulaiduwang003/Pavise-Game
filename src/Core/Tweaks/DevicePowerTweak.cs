// @author bdth 2074055628@qq.com
// 文件用途 阻止系统为省电关闭网卡 免除唤醒延迟造成的对局卡顿
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class DevicePowerTweak
    {
        private const string NetClass =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
        private const string ListKey = "DevPowerList";
        internal const int NoPowerDownBit = 0x08;

        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load("DevPowerByPavise", false); } }

        internal struct Adapter
        {
            public string Index;
            public string Description;
            public int? PnPCapabilities;
            public bool CanPowerDown;
        }

        public static List<Adapter> Scan()
        {
            var list = new List<Adapter>();
            try
            {
                using (var cls = Registry.LocalMachine.OpenSubKey(NetClass))
                {
                    if (cls == null) return list;
                    foreach (string idx in cls.GetSubKeyNames())
                    {
                        if (idx.Length != 4) continue;
                        using (var node = cls.OpenSubKey(idx))
                        {
                            if (node == null) continue;
                            string desc = node.GetValue("DriverDesc") as string;
                            if (string.IsNullOrEmpty(desc) || node.GetValue("NetCfgInstanceId") == null) continue;
                            object v = node.GetValue("PnPCapabilities");
                            int? cur = v is int ? (int?)(int)v : null;
                            list.Add(new Adapter
                            {
                                Index = idx,
                                Description = desc,
                                PnPCapabilities = cur,
                                CanPowerDown = !cur.HasValue || (cur.Value & NoPowerDownBit) == 0
                            });
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        public static bool Enable()
        {
            lock (lk)
            {
                var done = new List<string>();
                int failed = 0;
                foreach (Adapter a in Scan())
                {
                    if (!a.CanPowerDown) continue;
                    int target = (a.PnPCapabilities.HasValue ? a.PnPCapabilities.Value : 0) | NoPowerDownBit;
                    if (Reg(a.Index).Apply(target) || Reg(a.Index).HasBackup) done.Add(a.Index);
                    else { failed++; Logger.Log(Lang.T("log.devicepowertweak.1") + a.Description); }
                }
                if (done.Count == 0)
                {
                    Logger.Log(Lang.T("log.devicepowertweak.2"));
                    return failed == 0;
                }
                if (!Settings.SaveStr(ListKey, string.Join(";", done.ToArray())))
                {
                    foreach (string idx in done) Reg(idx).Restore();
                    Logger.Log(Lang.T("log.devicepowertweak.3"));
                    return false;
                }
                Settings.Save("DevPowerByPavise", true);
                Logger.Log(Lang.T("log.devicepowertweak.4") + done.Count + Lang.T("log.devicepowertweak.5"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool all = true;
                foreach (string idx in ParseList(Settings.LoadStr(ListKey, "")))
                    all &= Reg(idx).Restore();
                if (all)
                {
                    Settings.SaveStr(ListKey, "");
                    Settings.Save("DevPowerByPavise", false);
                    Logger.Log(Lang.T("log.devicepowertweak.6"));
                }
                else Logger.Log(Lang.T("log.devicepowertweak.7"));
                return all;
            }
        }

        private static ReversibleReg Reg(string index)
        {
            return new ReversibleReg(Registry.LocalMachine, NetClass + @"\" + index,
                "PnPCapabilities", RegistryValueKind.DWord, "DevPower_" + index);
        }

        internal static string[] ParseList(string raw)
        {
            return (raw ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        internal static int Merge(int? current, bool block)
        {
            int baseVal = current.HasValue ? current.Value : 0;
            return block ? (baseVal | NoPowerDownBit) : (baseVal & ~NoPowerDownBit);
        }
    }
}

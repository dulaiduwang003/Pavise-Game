// @author bdth 2074055628@qq.com
// 文件用途 为支持但未启用消息信号中断的设备开启 MSI 还原时删键回到系统默认

using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class MsiModeTweak
    {
        private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum";
        private const string MsiLeaf = @"Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
        private const string ListKey = "MsiList";

        private static readonly object lk = new object();

        private static readonly string[] AllowedClasses = { "Display", "Net" };

        public static bool EnabledByPavise { get { return Settings.Load("MsiOnByPavise", false); } }

        internal struct Candidate
        {
            public string InstanceId;
            public string Description;
            public bool HasKey;
            public int? Value;
        }

        public static List<Candidate> Scan()
        {
            var found = new List<Candidate>();
            try
            {
                using (var pci = Registry.LocalMachine.OpenSubKey(EnumRoot + @"\PCI"))
                {
                    if (pci == null) return found;
                    foreach (string devClass in pci.GetSubKeyNames())
                        using (var dev = pci.OpenSubKey(devClass))
                        {
                            if (dev == null) continue;
                            foreach (string inst in dev.GetSubKeyNames())
                                using (var node = dev.OpenSubKey(inst))
                                {
                                    if (node == null) continue;
                                    string cls = node.GetValue("Class") as string;
                                    if (cls == null || Array.IndexOf(AllowedClasses, cls) < 0) continue;
                                    string id = @"PCI\" + devClass + @"\" + inst;
                                    var c = new Candidate
                                    {
                                        InstanceId = id,
                                        Description = (node.GetValue("DeviceDesc") as string) ?? id
                                    };
                                    int cut = c.Description.LastIndexOf(';');
                                    if (cut >= 0) c.Description = c.Description.Substring(cut + 1);
                                    using (var msi = node.OpenSubKey(MsiLeaf))
                                    {
                                        c.HasKey = msi != null;
                                        object v = msi == null ? null : msi.GetValue("MSISupported");
                                        c.Value = v is int ? (int?)(int)v : null;
                                    }
                                    found.Add(c);
                                }
                        }
                }
            }
            catch { }
            return found;
        }

        public static List<Candidate> Disabled()
        {
            var need = new List<Candidate>();
            foreach (Candidate c in Scan())
                if (c.HasKey && c.Value.HasValue && c.Value.Value == 0) need.Add(c);
            return need;
        }

        public static bool Enable()
        {
            lock (lk)
            {
                List<Candidate> targets = Disabled();
                if (targets.Count == 0)
                {
                    Logger.Log("MSI 模式：显卡与网卡均已启用消息信号中断，无需改动");
                    return true;
                }
                var done = new List<string>();
                foreach (Candidate c in targets)
                {
                    if (Reg(c.InstanceId).Apply(1)) done.Add(c.InstanceId);
                    else Logger.Log("MSI 模式：写入失败 " + c.Description);
                }
                if (done.Count == 0) return false;
                if (!Settings.SaveStr(ListKey, string.Join(";", done.ToArray())))
                {
                    foreach (string id in done) Reg(id).Restore();
                    Logger.Log("MSI 模式：清单无法持久化，已全部还原");
                    return false;
                }
                Settings.Save("MsiOnByPavise", true);
                Logger.Log("MSI 模式：已为 " + done.Count + " 个设备启用，重启后生效");
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool all = true;
                foreach (string id in ParseList(Settings.LoadStr(ListKey, "")))
                    all &= Reg(id).Restore();
                if (all)
                {
                    Settings.SaveStr(ListKey, "");
                    Settings.Save("MsiOnByPavise", false);
                    Logger.Log("MSI 模式：已还原各设备原值，重启后生效");
                }
                else Logger.Log("MSI 模式：部分设备还原失败，快照保留待下次重试");
                return all;
            }
        }

        private static ReversibleReg Reg(string instanceId)
        {
            return new ReversibleReg(Registry.LocalMachine,
                EnumRoot + @"\" + instanceId + @"\" + MsiLeaf,
                "MSISupported", RegistryValueKind.DWord,
                "Msi_" + instanceId.Replace('\\', '_'));
        }

        internal static string[] ParseList(string raw)
        {
            return (raw ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}

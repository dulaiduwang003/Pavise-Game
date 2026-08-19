// @author bdth 2074055628@qq.com
// 文件用途 修复被显式关闭的显卡消息信号中断 MSISupported=0 时一键写回 还原时回到原值
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
        private const string FlagKey = "MsiOnByPavise";

        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load(FlagKey, false); } }

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
            HashSet<string> present;
            try
            {
                present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string presentId in PresentDevices.ByClass(
                    new Guid("4d36e968-e325-11ce-bfc1-08002be10318")))
                    if (!string.IsNullOrEmpty(presentId)) present.Add(presentId);
            }
            catch { return found; }
            if (present.Count == 0) return found;
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
                                    if (!string.Equals(cls, "Display", StringComparison.OrdinalIgnoreCase)) continue;
                                    string id = @"PCI\" + devClass + @"\" + inst;
                                    if (!present.Contains(id)) continue;
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
                    Logger.Log(Lang.T("log.msimodetweak.1"));
                    return true;
                }
                var done = new List<string>();
                foreach (Candidate c in targets)
                {
                    if (Reg(c.InstanceId).Apply(1)) done.Add(c.InstanceId);
                    else Logger.Log(Lang.T("log.msimodetweak.2") + c.Description);
                }
                if (done.Count == 0) return false;
                if (!Settings.SaveStr(ListKey, string.Join(";", done.ToArray())))
                {
                    foreach (string id in done) Reg(id).Restore();
                    Logger.Log(Lang.T("log.msimodetweak.3"));
                    return false;
                }
                Settings.Save(FlagKey, true);
                Logger.Log(Lang.T("log.msimodetweak.4") + done.Count + Lang.T("log.msimodetweak.5"));
                return true;
            }
        }

        public static bool HasResidue()
        {
            return Settings.Load(FlagKey, false)
                || ParseList(Settings.LoadStr(ListKey, "")).Length > 0;
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
                    Settings.Save(FlagKey, false);
                }
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

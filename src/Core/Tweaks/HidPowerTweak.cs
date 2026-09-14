// @author bdth 2074055628@qq.com
// File purpose Stop the system from suspending the USB devices and hubs hosting the keyboard and mouse to save power; reversible
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class HidPowerTweak
    {
        private const string DeviceParams = "Device Parameters";
        private const string ListKey = "HidPowerList";
        private const string FlagKey = "HidPowerByPavise";

        private const string EpmValue = "EnhancedPowerManagementEnabled";
        private const string SsValue = "SelectiveSuspendEnabled";

        private static readonly byte[] SsOff = new byte[] { 0 };
        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load(FlagKey, false); } }

        // A machine with no enumerable USB keyboard or mouse (PS/2 or Bluetooth-only) has nothing for this item to act on
        //   Must not count as failure, or every re-apply on such a machine reports it failed with nothing the user can do about it
        public static bool HasTargets()
        {
            try { return Scan().Count > 0; }
            catch { return false; }
        }

        internal sealed class Target
        {
            public string InstanceId;
            public string Label;
            public bool IsHub;
            public bool EpmOn;
            public bool SsOn;
        }

        public static List<Target> Scan()
        {
            var targets = new List<Target>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (InputDevice d in InputChainProbe.Devices())
                {
                    if (d.Transport != InputTransport.Usb) continue;
                    string prefix = DevicePrefix(d.InstanceId);
                    var chain = new List<string>();
                    if (d.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
                        chain.Add(d.InstanceId);
                    chain.AddRange(PresentDevices.ParentChain(d.InstanceId));
                    foreach (string usbId in chain)
                    {
                        string hardware, instance;
                        if (!SplitInstance(usbId, out hardware, out instance)) continue;
                        if (!seen.Add(usbId)) continue;
                        bool root = hardware.StartsWith("ROOT_HUB", StringComparison.OrdinalIgnoreCase);
                        bool own = prefix != null && hardware.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                        string label = root ? Lang.T("t.hidpowertweak.1") : own ? d.Name : Lang.T("t.hidpowertweak.2");
                        Collect(usbId, label, !own, targets);
                    }
                }
            }
            catch (Exception ex) { Logger.Warn(Lang.T("log.hidpowertweak.3") + ex.Message); }
            return targets;
        }

        internal static string DevicePrefix(string instanceId)
        {
            string key = InputChainProbe.UsbHardwareKeyOf(instanceId);
            if (key == null)
            {
                string hardware, instance;
                if (!SplitInstance(instanceId, out hardware, out instance)) return null;
                key = hardware;
            }
            string[] parts = key.Split('&');
            if (parts.Length < 2) return key;
            return parts[0] + "&" + parts[1];
        }

        internal static bool SplitInstance(string instanceId, out string hardware, out string instance)
        {
            hardware = null; instance = null;
            if (string.IsNullOrEmpty(instanceId)) return false;
            if (!instanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)) return false;
            int start = "USB\\".Length;
            int cut = instanceId.IndexOf('\\', start);
            if (cut < 0 || cut + 1 >= instanceId.Length) return false;
            hardware = instanceId.Substring(start, cut - start);
            instance = instanceId.Substring(cut + 1);
            return hardware.Length > 0 && instance.Length > 0;
        }

        private static void Collect(string instanceId, string label, bool hub, List<Target> targets)
        {
            try
            {
                using (RegistryKey node = Registry.LocalMachine.OpenSubKey(
                    InputChainProbe.EnumRoot + "\\" + instanceId + "\\" + DeviceParams))
                {
                    if (node == null) return;
                    object epm = node.GetValue(EpmValue);
                    object ss = node.GetValue(SsValue);
                    if (epm == null && ss == null) return;

                    targets.Add(new Target
                    {
                        InstanceId = instanceId,
                        Label = label,
                        IsHub = hub,
                        EpmOn = epm != null && ToInt(epm) != 0,
                        SsOn = ss != null && !IsZeroBinary(ss)
                    });
                }
            }
            catch { }
        }

        internal static bool IsZeroBinary(object raw)
        {
            var bytes = raw as byte[];
            if (bytes == null)
            {
                try { return ToInt(raw) == 0; }
                catch { return false; }
            }
            foreach (byte b in bytes) if (b != 0) return false;
            return true;
        }

        private static int ToInt(object raw)
        {
            try { return Convert.ToInt32(raw); }
            catch { return 0; }
        }

        public static bool PowerSaveActive()
        {
            foreach (Target t in Scan()) if (t.EpmOn || t.SsOn) return true;
            return false;
        }

        public static string Describe()
        {
            List<Target> targets = Scan();
            if (targets.Count == 0) return Lang.T("t.hidpowertweak.4");
            int on = 0;
            foreach (Target t in targets) if (t.EpmOn || t.SsOn) on++;
            if (EnabledByPavise)
            {
                int changed = ParseList(Settings.LoadStr(ListKey, "")).Length;
                if (on > 0) return Lang.T("t.hidpowertweak.5") + on + Lang.T("t.hidpowertweak.6");
                if (changed == 0) return Lang.T("t.hidpowertweak.7");
                return Lang.T("t.hidpowertweak.8") + changed + Lang.T("t.hidpowertweak.9");
            }
            if (on == 0) return Lang.T("t.hidpowertweak.10") + targets.Count + Lang.T("t.hidpowertweak.11");
            return targets.Count + Lang.T("t.hidpowertweak.12") + on + Lang.T("t.hidpowertweak.13");
        }

        public static bool Enable()
        {
            lock (lk)
            {
                var done = new List<string>();
                bool anyFail = false;
                int scanned = 0;
                foreach (Target t in Scan())
                {
                    scanned++;
                    if (!t.EpmOn && !t.SsOn) continue;
                    bool epmOk = !t.EpmOn || Epm(t.InstanceId).Apply(0);
                    bool ssOk = !t.SsOn || DisableSs(t.InstanceId);
                    bool wrote = (t.EpmOn && epmOk) || (t.SsOn && ssOk)
                        || Epm(t.InstanceId).HasBackup || Ss(t.InstanceId, RegistryValueKind.Binary).HasBackup;
                    if (wrote) done.Add(t.InstanceId);
                    if (!epmOk || !ssOk) { anyFail = true; Logger.Log(Lang.T("log.hidpowertweak.14") + t.Label); }
                }

                if (done.Count == 0)
                {
                    // Three cases must not be lumped into one line: a write failure is WARN, already disabled is a normal result
                    //   Saying 'all devices blocked from suspending' when no USB keyboard or mouse was enumerated is a lie
                    //   PS/2 or Bluetooth-only machines are exactly that; don't record it as applied, or the UI shows we took over something we never did
                    if (anyFail) { Logger.Warn(Lang.T("log.hidpowertweak.15")); return false; }
                    if (scanned == 0) { Logger.Log(Lang.T("log.hidpowertweak.22")); return false; }
                    Logger.Log(Lang.T("log.hidpowertweak.16"));
                    Settings.Save(FlagKey, true);
                    return true;
                }

                var merged = new List<string>(ParseList(Settings.LoadStr(ListKey, "")));
                foreach (string id in done)
                {
                    bool dup = false;
                    foreach (string old in merged)
                        if (string.Equals(old, id, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    if (!dup) merged.Add(id);
                }
                if (!Settings.SaveStr(ListKey, string.Join(";", merged.ToArray())))
                {
                    foreach (string id in done) { Epm(id).Restore(); RestoreSs(id); }
                    Logger.Log(Lang.T("log.hidpowertweak.17"));
                    return false;
                }
                Settings.Save(FlagKey, true);
                Logger.Log(Lang.T("log.hidpowertweak.18") + done.Count + Lang.T("log.hidpowertweak.19"));
                return !anyFail;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool all = true;
                foreach (string id in ParseList(Settings.LoadStr(ListKey, "")))
                {
                    all &= Epm(id).Restore();
                    all &= RestoreSs(id);
                }
                if (all)
                {
                    Settings.SaveStr(ListKey, "");
                    Settings.Save(FlagKey, false);
                    Logger.Log(Lang.T("log.hidpowertweak.20"));
                }
                else Logger.Log(Lang.T("log.hidpowertweak.21"));
                return all;
            }
        }

        public static bool HasResidue()
        {
            return Settings.Load(FlagKey, false) || ParseList(Settings.LoadStr(ListKey, "")).Length > 0;
        }

        private static ReversibleReg Epm(string instanceId)
        {
            return new ReversibleReg(Registry.LocalMachine,
                InputChainProbe.EnumRoot + @"\" + instanceId + @"\" + DeviceParams,
                EpmValue, RegistryValueKind.DWord, "HidEpm_" + Slot(instanceId));
        }

        private static ReversibleReg Ss(string instanceId, RegistryValueKind kind)
        {
            return new ReversibleReg(Registry.LocalMachine,
                InputChainProbe.EnumRoot + @"\" + instanceId + @"\" + DeviceParams,
                SsValue, kind, "HidSs_" + Slot(instanceId));
        }

        internal static RegistryValueKind SsKind(string instanceId)
        {
            string snap = Settings.LoadStr("HidSs_" + Slot(instanceId), "");
            if (snap.Length > 0 && snap[0] == 'b') return RegistryValueKind.Binary;
            if (snap.Length > 0 && snap[0] == '=') return RegistryValueKind.DWord;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    InputChainProbe.EnumRoot + "\\" + instanceId + "\\" + DeviceParams))
                {
                    if (k != null && k.GetValue(SsValue) != null
                        && k.GetValueKind(SsValue) == RegistryValueKind.DWord)
                        return RegistryValueKind.DWord;
                }
            }
            catch { }
            return RegistryValueKind.Binary;
        }

        private static bool DisableSs(string instanceId)
        {
            RegistryValueKind kind = SsKind(instanceId);
            return Ss(instanceId, kind).Apply(
                kind == RegistryValueKind.DWord ? (object)0 : (object)SsOff);
        }

        private static bool RestoreSs(string instanceId)
        {
            return Ss(instanceId, SsKind(instanceId)).Restore();
        }

        internal static string Slot(string instanceId)
        {
            return (instanceId ?? "").Replace('\\', '_').Replace('&', '-');
        }

        internal static string[] ParseList(string raw)
        {
            return (raw ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}

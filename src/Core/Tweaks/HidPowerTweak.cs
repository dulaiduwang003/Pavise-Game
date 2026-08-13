// @author bdth 2074055628@qq.com
// 文件用途 禁止系统为省电挂起键鼠所在的 USB 设备与根集线器 可逆
// 依据 微软自己的 USB 选择性暂停文档承认 USB 2.0 鼠标从空闲转活动时的退出延迟会表现为屏幕上的顿挫
// 治的是偶发抖动不是稳态延迟 稳态那 1 到 5 毫秒动不了 这一项只把唤醒那一下的窟窿堵上
// 只碰键鼠的 USB 父设备和根集线器 不碰 U 盘 摄像头 声卡这些同样挂在 USB 上的设备

using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class HidPowerTweak
    {
        private const string UsbEnumRoot = InputChainProbe.EnumRoot + @"\USB";
        private const string DeviceParams = "Device Parameters";
        private const string ListKey = "HidPowerList";
        private const string FlagKey = "HidPowerByPavise";

        private const string EpmValue = "EnhancedPowerManagementEnabled";
        private const string SsValue = "SelectiveSuspendEnabled";

        private static readonly byte[] SsOff = new byte[] { 0 };
        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load(FlagKey, false); } }

        internal sealed class Target
        {
            public string InstanceId;
            public string Label;
            public bool IsHub;
            public bool EpmOn;
            public bool SsOn;
            public bool Controllable;
        }

        public static List<Target> Scan()
        {
            var targets = new List<Target>();
            var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (InputDevice d in InputChainProbe.Devices())
                {
                    if (d.Transport != InputTransport.Usb) continue;
                    if (string.IsNullOrEmpty(d.UsbHardwareKey)) continue;
                    wanted[d.UsbHardwareKey] = d.Name;
                }
            }
            catch { }

            try
            {
                using (RegistryKey usb = Registry.LocalMachine.OpenSubKey(UsbEnumRoot))
                {
                    if (usb == null) return targets;
                    foreach (string instanceId in PresentDevices.ByEnumerator("USB"))
                    {
                        string hardware, instance;
                        if (!SplitInstance(instanceId, out hardware, out instance)) continue;

                        bool hub = hardware.StartsWith("ROOT_HUB", StringComparison.OrdinalIgnoreCase);
                        string label = "USB 根集线器";
                        if (!hub && !wanted.TryGetValue(hardware, out label)) continue;

                        using (RegistryKey hardwareKey = usb.OpenSubKey(hardware))
                        {
                            if (hardwareKey == null) continue;
                            Collect(hardwareKey, instanceId, instance, label, hub, targets);
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Log("键鼠设备省电 枚举失败 " + ex.Message); }
            return targets;
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

        private static void Collect(RegistryKey hardwareKey, string instanceId, string instance,
            string label, bool hub, List<Target> targets)
        {
            try
            {
                using (RegistryKey node = hardwareKey.OpenSubKey(instance + "\\" + DeviceParams))
                {
                    if (node == null) return;
                    object epm = node.GetValue(EpmValue);
                    object ss = node.GetValue(SsValue);
                    if (epm == null && ss == null) return;

                    var t = new Target
                    {
                        InstanceId = instanceId,
                        Label = label,
                        IsHub = hub,
                        EpmOn = epm != null && ToInt(epm) != 0,
                        SsOn = ss != null && !IsZeroBinary(ss),
                        Controllable = true
                    };
                    targets.Add(t);
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
            if (targets.Count == 0) return "本机没找到可控的键鼠 USB 设备 这一项跳过";
            if (EnabledByPavise) return "已禁止系统挂起 " + targets.Count + " 个键鼠 USB 设备与根集线器 拨回开关即还原";
            int on = 0;
            foreach (Target t in targets) if (t.EpmOn || t.SsOn) on++;
            if (on == 0) return "本机 " + targets.Count + " 个键鼠 USB 设备都已经不允许省电挂起 不用处理";
            return targets.Count + " 个键鼠 USB 设备里有 " + on + " 个允许系统省电挂起 空闲后第一下操作会有唤醒顿挫";
        }

        public static bool Enable()
        {
            lock (lk)
            {
                var done = new List<string>();
                bool anyFail = false;
                foreach (Target t in Scan())
                {
                    if (!t.EpmOn && !t.SsOn) continue;
                    bool ok = true;
                    if (t.EpmOn) ok &= Epm(t.InstanceId).Apply(0);
                    if (t.SsOn) ok &= Ss(t.InstanceId).Apply(SsOff);
                    if (ok) done.Add(t.InstanceId);
                    else { anyFail = true; Logger.Log("键鼠设备省电 写入失败 " + t.Label); }
                }

                if (done.Count == 0)
                {
                    Logger.Log(anyFail
                        ? "键鼠设备省电 全部写入失败 多半是权限不足"
                        : "键鼠设备省电 所有键鼠 USB 设备均已禁止挂起 无需改动");
                    if (!anyFail) Settings.Save(FlagKey, true);
                    return !anyFail;
                }

                if (!Settings.SaveStr(ListKey, string.Join(";", done.ToArray())))
                {
                    foreach (string id in done) { Epm(id).Restore(); Ss(id).Restore(); }
                    Logger.Log("键鼠设备省电 清单无法持久化 已全部还原");
                    return false;
                }
                Settings.Save(FlagKey, true);
                Logger.Log("键鼠设备省电 已禁止系统挂起 " + done.Count + " 个键鼠 USB 设备");
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
                    all &= Ss(id).Restore();
                }
                if (all)
                {
                    Settings.SaveStr(ListKey, "");
                    Settings.Save(FlagKey, false);
                    Logger.Log("键鼠设备省电 已还原原值");
                }
                else Logger.Log("键鼠设备省电 部分设备还原失败 快照保留待下次重试");
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

        private static ReversibleReg Ss(string instanceId)
        {
            return new ReversibleReg(Registry.LocalMachine,
                InputChainProbe.EnumRoot + @"\" + instanceId + @"\" + DeviceParams,
                SsValue, RegistryValueKind.Binary, "HidSs_" + Slot(instanceId));
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

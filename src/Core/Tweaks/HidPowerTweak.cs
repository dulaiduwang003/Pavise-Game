// @author bdth 2074055628@qq.com
// 文件用途 禁止系统为省电挂起键鼠所在的 USB 设备与集线器 可逆
// 依据 微软自己的 USB 选择性暂停文档承认 USB 2.0 鼠标从空闲转活动时的退出延迟会表现为屏幕上的顿挫
// 治的是偶发抖动不是稳态延迟 稳态那 1 到 5 毫秒动不了 这一项只把唤醒那一下的窟窿堵上
// 沿设备树父链只碰键鼠所在 USB 链路上的接口节点 复合父设备与集线器 不碰 U 盘 摄像头 声卡
// SelectiveSuspendEnabled 在不同设备上有 REG_BINARY 和 REG_DWORD 两种形态 写入前按原值类型自适应
// 这些值由驱动在设备启动时读取 写入后要重启或重插设备才生效

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
                        string label = root ? "USB 根集线器" : own ? d.Name : "USB 集线器";
                        Collect(usbId, label, !own, targets);
                    }
                }
            }
            catch (Exception ex) { Logger.Log("键鼠设备省电 枚举失败 " + ex.Message); }
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
            if (targets.Count == 0) return "本机没找到可控的键鼠 USB 设备 这一项跳过";
            int on = 0;
            foreach (Target t in targets) if (t.EpmOn || t.SsOn) on++;
            if (EnabledByPavise)
            {
                int changed = ParseList(Settings.LoadStr(ListKey, "")).Length;
                if (on > 0) return "有 " + on + " 个键鼠 USB 设备仍允许挂起 拨回开关再打开一次可一并禁止";
                if (changed == 0) return "本机键鼠 USB 设备本就都不允许省电挂起 开关没有改动任何值";
                return "已禁止系统挂起 " + changed + " 个键鼠 USB 设备与集线器 重启或重插设备后生效 拨回开关即还原";
            }
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
                    bool epmOk = !t.EpmOn || Epm(t.InstanceId).Apply(0);
                    bool ssOk = !t.SsOn || DisableSs(t.InstanceId);
                    bool wrote = (t.EpmOn && epmOk) || (t.SsOn && ssOk)
                        || Epm(t.InstanceId).HasBackup || Ss(t.InstanceId, RegistryValueKind.Binary).HasBackup;
                    if (wrote) done.Add(t.InstanceId);
                    if (!epmOk || !ssOk) { anyFail = true; Logger.Log("键鼠设备省电 写入失败 " + t.Label); }
                }

                if (done.Count == 0)
                {
                    Logger.Log(anyFail
                        ? "键鼠设备省电 全部写入失败 多半是权限不足"
                        : "键鼠设备省电 所有键鼠 USB 设备均已禁止挂起 无需改动");
                    if (!anyFail) Settings.Save(FlagKey, true);
                    return !anyFail;
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
                    Logger.Log("键鼠设备省电 清单无法持久化 已全部还原");
                    return false;
                }
                Settings.Save(FlagKey, true);
                Logger.Log("键鼠设备省电 已禁止系统挂起 " + done.Count + " 个键鼠 USB 设备 重启或重插设备后生效");
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
                    Logger.Log("键鼠设备省电 已还原原值 重启或重插设备后生效");
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

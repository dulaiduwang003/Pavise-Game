// @author bdth 2074055628@qq.com
// 文件用途 只读探测输入链路 键鼠传输方式 设备省电状态 辅助功能拦截 与已知无效改动的残留
// 依据 端到端延迟里外设段只占 1 到 5 毫秒 真正能动的软件项只有辅助功能拦截和设备省电两处
// 其余结论一律只报不改 交由用户在硬件或游戏内设置上处理

using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal enum InputTransport
    {
        Unknown = 0,
        Ps2 = 1,
        Usb = 2,
        BluetoothClassic = 3,
        BluetoothLe = 4,
        Virtual = 5
    }

    internal sealed class InputDevice
    {
        public string InstanceId;
        public string Name;
        public bool IsMouse;
        public InputTransport Transport;
        public string UsbHardwareKey;
    }

    internal static class AccessibilityFlags
    {
        public const int On = 0x01;
        public const int Available = 0x02;
        public const int HotkeyActive = 0x04;

        public static bool TryParse(string raw, out int flags)
        {
            flags = 0;
            if (string.IsNullOrEmpty(raw)) return false;
            return int.TryParse(raw.Trim(), out flags);
        }

        public static bool NeedsFix(int flags)
        {
            return (flags & On) != 0 || (flags & HotkeyActive) != 0;
        }

        public static int Sanitize(int flags)
        {
            return flags & ~(On | HotkeyActive);
        }

        public static bool IsOn(int flags) { return (flags & On) != 0; }
        public static bool HotkeyOn(int flags) { return (flags & HotkeyActive) != 0; }
    }

    internal sealed class AccessibilityState
    {
        public bool Read;
        public int FilterKeys;
        public int StickyKeys;
        public int ToggleKeys;
        public int DelayBeforeAcceptanceMs;

        public bool AnyNeedsFix
        {
            get
            {
                return Read && (AccessibilityFlags.NeedsFix(FilterKeys)
                    || AccessibilityFlags.NeedsFix(StickyKeys)
                    || AccessibilityFlags.NeedsFix(ToggleKeys));
            }
        }

        public bool FilterKeysSwallowing
        {
            get { return Read && AccessibilityFlags.IsOn(FilterKeys); }
        }
    }

    internal static class InputChainProbe
    {
        internal const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum";
        internal const string AccessRoot = @"Control Panel\Accessibility";
        internal const string FilterKeysPath = AccessRoot + @"\Keyboard Response";
        internal const string StickyKeysPath = AccessRoot + @"\StickyKeys";
        internal const string ToggleKeysPath = AccessRoot + @"\ToggleKeys";
        internal const string MousePath = @"Control Panel\Mouse";

        private const string BtClassicUuid = "{00001124-0000-1000-8000-00805f9b34fb}";
        private const string BtLeUuid = "{00001812-0000-1000-8000-00805f9b34fb}";

        internal const string MouseClassGuid = "{4d36e96f-e325-11ce-bfc1-08002be10318}";
        internal const string KeyboardClassGuid = "{4d36e96b-e325-11ce-bfc1-08002be10318}";

        internal static int ClassifyDeviceClass(string classGuid, string className)
        {
            if (!string.IsNullOrEmpty(classGuid))
            {
                if (string.Equals(classGuid, MouseClassGuid, StringComparison.OrdinalIgnoreCase)) return 1;
                if (string.Equals(classGuid, KeyboardClassGuid, StringComparison.OrdinalIgnoreCase)) return 0;
                return -1;
            }
            if (string.Equals(className, "Mouse", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(className, "Keyboard", StringComparison.OrdinalIgnoreCase)) return 0;
            return -1;
        }

        internal static string CleanDesc(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            if (raw[0] != '@') return raw;
            int cut = raw.LastIndexOf(';');
            if (cut < 0 || cut + 1 >= raw.Length) return null;
            string text = raw.Substring(cut + 1).Trim();
            return text.Length == 0 ? null : text;
        }

        internal static InputTransport ClassifyTransport(string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return InputTransport.Unknown;
            string id = instanceId.ToUpperInvariant();

            if (id.StartsWith("ROOT\\", StringComparison.Ordinal)
                || id.StartsWith("SWD\\", StringComparison.Ordinal)) return InputTransport.Virtual;
            if (id.StartsWith("BTHLEDEVICE", StringComparison.Ordinal)) return InputTransport.BluetoothLe;
            if (id.StartsWith("BTHENUM", StringComparison.Ordinal)) return InputTransport.BluetoothClassic;
            if (id.IndexOf(BtLeUuid.ToUpperInvariant(), StringComparison.Ordinal) >= 0)
                return InputTransport.BluetoothLe;
            if (id.IndexOf(BtClassicUuid.ToUpperInvariant(), StringComparison.Ordinal) >= 0)
                return InputTransport.BluetoothClassic;
            if (id.StartsWith("ACPI\\", StringComparison.Ordinal))
            {
                if (id.IndexOf("PNP0303", StringComparison.Ordinal) >= 0
                    || id.IndexOf("PNP0F03", StringComparison.Ordinal) >= 0
                    || id.IndexOf("PNP0F13", StringComparison.Ordinal) >= 0) return InputTransport.Ps2;
                return InputTransport.Unknown;
            }
            if (id.StartsWith("USB\\", StringComparison.Ordinal)) return InputTransport.Usb;
            if (id.StartsWith("HID\\VID_", StringComparison.Ordinal)) return InputTransport.Usb;
            if (id.StartsWith("HID\\", StringComparison.Ordinal)) return InputTransport.Virtual;
            return InputTransport.Unknown;
        }

        internal static string UsbHardwareKeyOf(string hidInstanceId)
        {
            if (string.IsNullOrEmpty(hidInstanceId)) return null;
            if (!hidInstanceId.StartsWith("HID\\VID_", StringComparison.OrdinalIgnoreCase)) return null;

            int start = "HID\\".Length;
            int stop = hidInstanceId.IndexOf('\\', start);
            string hw = stop < 0 ? hidInstanceId.Substring(start) : hidInstanceId.Substring(start, stop - start);

            string[] parts = hw.Split('&');
            var keep = new List<string>();
            foreach (string part in parts)
            {
                if (part.StartsWith("COL", StringComparison.OrdinalIgnoreCase)) continue;
                keep.Add(part);
            }
            if (keep.Count == 0) return null;
            return string.Join("&", keep.ToArray());
        }

        public static AccessibilityState ReadAccessibility()
        {
            var state = new AccessibilityState();
            try
            {
                int filter, sticky, toggle;
                bool okFilter = ReadFlags(FilterKeysPath, out filter);
                bool okSticky = ReadFlags(StickyKeysPath, out sticky);
                bool okToggle = ReadFlags(ToggleKeysPath, out toggle);
                state.FilterKeys = filter;
                state.StickyKeys = sticky;
                state.ToggleKeys = toggle;
                state.Read = okFilter || okSticky || okToggle;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(FilterKeysPath))
                {
                    int delay;
                    if (key != null && AccessibilityFlags.TryParse(
                        key.GetValue("DelayBeforeAcceptance") as string, out delay))
                        state.DelayBeforeAcceptanceMs = delay;
                }
            }
            catch { }
            return state;
        }

        private static bool ReadFlags(string path, out int flags)
        {
            flags = 0;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path))
                {
                    if (key == null) return false;
                    return AccessibilityFlags.TryParse(key.GetValue("Flags") as string, out flags);
                }
            }
            catch { return false; }
        }

        public static bool PointerPrecisionOn()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(MousePath))
                {
                    if (key == null) return false;
                    int speed;
                    if (!AccessibilityFlags.TryParse(key.GetValue("MouseSpeed") as string, out speed)) return false;
                    return speed != 0;
                }
            }
            catch { return false; }
        }

        public static List<InputDevice> Devices()
        {
            var list = new List<InputDevice>();
            try
            {
                Collect(PresentDevices.ByClass(new Guid(MouseClassGuid)), true, list);
                Collect(PresentDevices.ByClass(new Guid(KeyboardClassGuid)), false, list);
            }
            catch { }
            return list;
        }

        private static void Collect(List<string> ids, bool mouse, List<InputDevice> list)
        {
            if (ids == null) return;
            foreach (string id in ids)
            {
                if (string.IsNullOrEmpty(id)) continue;
                list.Add(new InputDevice
                {
                    InstanceId = id,
                    Name = NameOf(id) ?? id,
                    IsMouse = mouse,
                    Transport = ClassifyTransport(id),
                    UsbHardwareKey = UsbHardwareKeyOf(id)
                });
            }
        }

        private static string NameOf(string instanceId)
        {
            try
            {
                using (RegistryKey node = Registry.LocalMachine.OpenSubKey(EnumRoot + "\\" + instanceId))
                {
                    if (node == null) return null;
                    return CleanDesc(node.GetValue("FriendlyName") as string)
                        ?? CleanDesc(node.GetValue("DeviceDesc") as string);
                }
            }
            catch { return null; }
        }

        public static bool AnyBluetooth(List<InputDevice> devices)
        {
            if (devices == null) return false;
            foreach (InputDevice d in devices)
                if (d.Transport == InputTransport.BluetoothClassic || d.Transport == InputTransport.BluetoothLe)
                    return true;
            return false;
        }

        public static string TransportText(InputTransport transport)
        {
            switch (transport)
            {
                case InputTransport.Ps2: return "PS/2";
                case InputTransport.Usb: return "USB 或 2.4G 接收器";
                case InputTransport.BluetoothClassic: return "蓝牙";
                case InputTransport.BluetoothLe: return "低功耗蓝牙";
                case InputTransport.Virtual: return "虚拟设备";
                default: return "未知";
            }
        }
    }
}

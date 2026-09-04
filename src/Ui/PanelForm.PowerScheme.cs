// @author bdth 2074055628@qq.com
// 文件用途 监听活动电源方案变更并唤醒 Pavise 所有权审计
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal partial class PanelForm : Form
    {
        private const int WmPowerBroadcast = 0x0218;
        private const int PbtPowerSettingChange = 0x8013;
        private const uint DeviceNotifyWindowHandle = 0;
        private static readonly Guid ActivePowerSchemeGuid =
            new Guid("31F9F286-5084-42FE-B720-2B0264993763");

        private IntPtr powerSchemeNotification;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PowerBroadcastSetting
        {
            internal Guid PowerSetting;
            internal uint DataLength;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(
            IntPtr recipient, ref Guid powerSettingGuid, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        private void RegisterPowerSchemeNotifications()
        {
            UnregisterPowerSchemeNotifications();
            if (!IsHandleCreated) return;
            Guid setting = ActivePowerSchemeGuid;
            try
            {
                powerSchemeNotification = RegisterPowerSettingNotification(
                    Handle, ref setting, DeviceNotifyWindowHandle);
            }
            catch
            {
                powerSchemeNotification = IntPtr.Zero;
            }
        }

        private void UnregisterPowerSchemeNotifications()
        {
            IntPtr registration = powerSchemeNotification;
            powerSchemeNotification = IntPtr.Zero;
            if (registration == IntPtr.Zero) return;
            try { UnregisterPowerSettingNotification(registration); } catch { }
        }

        private void HandlePowerSchemeNotification(Message message)
        {
            if (message.Msg != WmPowerBroadcast
                || message.WParam.ToInt64() != PbtPowerSettingChange
                || message.LParam == IntPtr.Zero)
                return;

            try
            {
                var setting = (PowerBroadcastSetting)Marshal.PtrToStructure(
                    message.LParam, typeof(PowerBroadcastSetting));
                if (setting.PowerSetting == ActivePowerSchemeGuid)
                    gameMode.NotifyPowerSchemeChanged();
            }
            catch { }
        }
    }
}

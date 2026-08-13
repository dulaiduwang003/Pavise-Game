// @author bdth 2074055628@qq.com
// 文件用途 还原 v1.7.0.6 移除的视觉效果降级在本机留下的残留 只保留还原能力
// 全屏游戏时桌面本就不合成 关透明与动画对帧率无可测收益 却改动了用户的系统设置

using System;
using Microsoft.Win32;

namespace PaviseApp
{

    internal static class VisualFx
    {
        private static readonly ReversibleReg Transparency = new ReversibleReg(
            Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "EnableTransparency", RegistryValueKind.DWord, "PrevTransparency");

        private const string UiEffectsSlot = "PrevUiEffects";
        private static readonly object lk = new object();

        private static int SavedUiEffects
        {
            get
            {
                string s = Settings.LoadStr(UiEffectsSlot, "");
                int v;
                return s.Length > 0 && int.TryParse(s, out v) ? v : -1;
            }
            set { Settings.SaveStr(UiEffectsSlot, value < 0 ? "" : value.ToString()); }
        }

        private static bool SetUiEffects(bool on)
        {
            try
            {
                return Native.SystemParametersInfoSet(Native.SPI_SETUIEFFECTS, 0,
                    on ? (IntPtr)1 : IntPtr.Zero, Native.SPIF_SENDCHANGE);
            }
            catch { return false; }
        }

        public static bool HasResidue()
        {
            return Transparency.HasBackup || SavedUiEffects >= 0;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool ok = true;
                int saved = SavedUiEffects;
                if (saved >= 0)
                {
                    if (SetUiEffects(saved != 0)) SavedUiEffects = -1;
                    else ok = false;
                }
                if (Transparency.HasBackup)
                {
                    if (Transparency.Restore()) Logger.Log("视觉效果已还原");
                    else ok = false;
                }
                return ok && !Transparency.HasBackup && SavedUiEffects < 0;
            }
        }

        public static void HealFromCrash()
        {
            if (HasResidue()) Restore();
        }
    }
}

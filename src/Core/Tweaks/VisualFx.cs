// @author bdth 2074055628@qq.com
// 文件用途 游戏期间降级桌面视觉效果并在退出后还原

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
        private static bool active;

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

        private static bool TryGetUiEffects(out int value)
        {
            value = 0;
            try { return Native.SystemParametersInfoGet(Native.SPI_GETUIEFFECTS, 0, ref value, 0); }
            catch { return false; }
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

        public static bool Activate()
        {
            lock (lk)
            {
                if (active) return true;

                bool transparencyOff = Transparency.Apply(0);

                bool animationsOff = false;
                int current;
                if (TryGetUiEffects(out current))
                {
                    if (current == 0) animationsOff = true;
                    else
                    {

                        SavedUiEffects = current;
                        if (SavedUiEffects == current && SetUiEffects(false)) animationsOff = true;
                        else SavedUiEffects = -1;
                    }
                }

                active = transparencyOff || animationsOff;
                if (active)
                    Logger.Log("视觉效果降级：" + (transparencyOff ? "已关闭桌面透明" : "透明度未能关闭")
                        + "，" + (animationsOff ? "已关闭窗口动画" : "窗口动画未能关闭"));
                else
                    Logger.Log("视觉效果降级写入失败，本轮未启用");
                return active;
            }
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
                active = false;
                return ok && !Transparency.HasBackup && SavedUiEffects < 0;
            }
        }

        public static void HealFromCrash()
        {
            if (Transparency.HasBackup || SavedUiEffects >= 0) Restore();
        }
    }
}

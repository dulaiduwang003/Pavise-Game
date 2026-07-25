// @author bdth 2074055628@qq.com
// 文件用途 游戏期间降级桌面视觉效果并在退出后还原

using System;
using Microsoft.Win32;

namespace AegisApp
{
    // 桌面的透明毛玻璃和窗口动画都由 DWM 合成，游戏以无边框窗口运行时这些开销真实存在。
    //
    // 窗口动画走 SystemParametersInfo 且不带 SPIF_UPDATEINIFILE，只改运行时状态不落盘。
    // 但"不落盘"不等于"能自愈"：用户不会为了恢复动画专门注销一次。Aegis 被强杀后重启，
    // 内存里的原值没了，Activate() 读到当前是 0 就当成"本来就关着"，于是整个登录会话内
    // 动画再也开不回来。所以原值必须和透明度一样落盘，启动时按快照还原。
    internal static class VisualFx
    {
        private static readonly ReversibleReg Transparency = new ReversibleReg(
            Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "EnableTransparency", RegistryValueKind.DWord, "PrevTransparency");

        private const string UiEffectsSlot = "PrevUiEffects";
        private static readonly object lk = new object();
        private static bool active;

        // -1 表示没有待还原的快照
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
                        // 先落盘快照再动手：写不进去就不改，免得留下还原不回来的状态
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

        // 两半都有落盘快照，崩溃后启动时一并还原
        public static void HealFromCrash()
        {
            if (Transparency.HasBackup || SavedUiEffects >= 0) Restore();
        }
    }
}

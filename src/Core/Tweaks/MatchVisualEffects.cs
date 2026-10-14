using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    // 只临时修改桌面效果，保留字体平滑和 ClearType。
    internal static class MatchVisualEffects
    {
        internal sealed class Effect
        {
            internal readonly string Name;
            internal readonly uint Get, Set;
            internal readonly bool UiParam, Animation;
            internal Effect(string name, uint get, uint set, bool uiParam = false, bool animation = false)
            { Name = name; Get = get; Set = set; UiParam = uiParam; Animation = animation; }
        }
        internal static readonly Effect[] Effects = {
            new Effect("WindowAnimation", 0x0048, 0x0049, false, true),
            new Effect("ClientAnimation", 0x1042, 0x1043),
            new Effect("MenuAnimation", 0x1002, 0x1003),
            new Effect("ComboAnimation", 0x1004, 0x1005),
            new Effect("ListSmoothScroll", 0x1006, 0x1007),
            new Effect("SelectionFade", 0x1014, 0x1015),
            new Effect("TooltipAnimation", 0x1016, 0x1017),
            new Effect("CursorShadow", 0x101A, 0x101B),
            new Effect("DropShadow", 0x1024, 0x1025),
            new Effect("DragFullWindows", 0x0026, 0x0025, true)
        };
        [StructLayout(LayoutKind.Sequential)]
        private struct AnimationInfo { internal uint Size; internal int MinAnimate; }
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        private static extern bool AnimationParameters(uint action, uint param, ref AnimationInfo value, uint flags);
        internal static bool Enabled { get { return Settings.LoadCached(PerformanceActionCatalog.VisualEffects, false); } }
        private static Effect Find(string target)
        {
            foreach (Effect effect in Effects) if (effect.Name == target) return effect;
            throw new FormatException("Unknown desktop visual effect");
        }
        internal static string Read(string target)
        {
            Effect effect = Find(target);
            int value = 0; bool ok;
            if (effect.Animation)
            {
                var info = new AnimationInfo { Size = (uint)Marshal.SizeOf(typeof(AnimationInfo)) };
                ok = AnimationParameters(effect.Get, info.Size, ref info, 0); value = info.MinAnimate;
            }
            else ok = Native.SystemParametersInfoGet(effect.Get, 0, ref value, 0);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read desktop effect: " + target);
            return value == 0 ? "0" : "1";
        }
        internal static bool Write(string target, string value)
        {
            Effect effect = Find(target);
            if (value != "0" && value != "1") throw new FormatException("Invalid desktop effect value");
            int enabled = value == "1" ? 1 : 0;
            const uint sendChange = 2;
            if (effect.Animation)
            {
                var info = new AnimationInfo { Size = (uint)Marshal.SizeOf(typeof(AnimationInfo)), MinAnimate = enabled };
                return AnimationParameters(effect.Set, info.Size, ref info, sendChange);
            }
            return Native.SystemParametersInfoSet(effect.Set, effect.UiParam ? (uint)enabled : 0,
                effect.UiParam ? IntPtr.Zero : new IntPtr(enabled), sendChange);
        }
    }
}

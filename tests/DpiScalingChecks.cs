using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;

// 只替换显示器、DPI 与设置读取边界；缩放计算和状态切换均使用完整产品源码。
namespace System.Windows.Forms
{
    internal sealed class Screen
    {
        internal static Screen PrimaryScreen;
        internal static readonly Dictionary<IntPtr, Screen> Windows = new Dictionary<IntPtr, Screen>();
        internal Rectangle WorkingArea;
        internal static Screen FromHandle(IntPtr handle) { return Windows[handle]; }
    }
}

namespace PaviseApp
{
    internal static class Settings
    {
        internal static string Stored;
        internal static string LoadStr(string key, string fallback) { return Stored ?? fallback; }
    }

    internal static class Native
    {
        internal static int SystemDpi = 96;
        internal static bool SystemReadFails;
        internal static readonly Dictionary<IntPtr, int> WindowDpis = new Dictionary<IntPtr, int>();
        internal static bool SetProcessDpiAwarenessContext(IntPtr context) { return true; }
        internal static uint GetDpiForSystem()
        {
            if (SystemReadFails) throw new InvalidOperationException("fixture DPI unavailable");
            return (uint)SystemDpi;
        }
        internal static uint GetDpiForWindow(IntPtr handle) { return (uint)WindowDpis[handle]; }
    }

    internal static class DpiScalingChecks
    {
        private static readonly IntPtr Primary = new IntPtr(1), Secondary = new IntPtr(2);
        private static int assertions;

        private static void Check(bool condition, string reason)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException("ASSERT: " + reason);
        }

        private static void Near(float actual, float expected, string reason)
        {
            Check(Math.Abs(actual - expected) < 0.0001f, reason + ": actual=" + actual.ToString("F6", CultureInfo.InvariantCulture)
                + ", expected=" + expected.ToString("F6", CultureInfo.InvariantCulture));
        }

        private static void Configure(string preference, Rectangle primary, Rectangle secondary, int systemDpi, int secondaryDpi)
        {
            System.Windows.Forms.Screen.Windows.Clear();
            System.Windows.Forms.Screen.PrimaryScreen = new System.Windows.Forms.Screen { WorkingArea = primary };
            System.Windows.Forms.Screen.Windows[Primary] = System.Windows.Forms.Screen.PrimaryScreen;
            System.Windows.Forms.Screen.Windows[Secondary] = new System.Windows.Forms.Screen { WorkingArea = secondary };
            Native.WindowDpis.Clear();
            Native.WindowDpis[Primary] = systemDpi;
            Native.WindowDpis[Secondary] = secondaryDpi;
            Native.SystemDpi = systemDpi;
            Native.SystemReadFails = false;
            Settings.Stored = preference;
            Dpi.NoFit = false;
            Dpi.SetDesignSize(0, 0);
            Dpi.Init();
            Dpi.SetDesignSize(1220, 760);
            Dpi.UpdateWindow(Primary);
        }

        private static void StoredPreferences()
        {
            Rectangle roomy = new Rectangle(0, 0, 7680, 4320);
            for (int percent = 100; percent <= 300; percent += 25)
            {
                Configure(percent.ToString(CultureInfo.InvariantCulture), roomy, roomy, 144, 192);
                Check(Dpi.ScalePreferencePercent == percent, "合法保存值未加载");
                Near(Dpi.Scale, percent / 100f, "加载手动比例");
                Check(Dpi.IsScalePreferencePercent(percent), "合法比例被拒绝");
            }
            Configure("0", roomy, roomy, 144, 192);
            Check(Dpi.ScalePreferencePercent == 0, "自动设置未加载");
            Near(Dpi.Scale, 1.5f, "自动设置未使用系统 DPI");
            foreach (string value in new[] { "-1", "50", "99", "101", "301", "400", "125.0", "abc", "2147483648", "" })
            {
                Configure(value, roomy, roomy, 144, 192);
                Check(Dpi.ScalePreferencePercent == 0, "非法保存值没有回到自动: " + value);
                Near(Dpi.Scale, 1.5f, "非法保存值改变有效缩放");
            }
            Dpi.SetScalePreference(175);
            foreach (int invalid in new[] { -1, 25, 99, 101, 301, int.MaxValue })
            {
                Check(!Dpi.SetScalePreference(invalid), "非法手动比例被接受");
                Check(Dpi.ScalePreferencePercent == 175, "非法手动比例改变偏好");
                Near(Dpi.Scale, 1.75f, "非法手动比例改变当前缩放");
            }
            Native.SystemReadFails = true;
            Settings.Stored = "0";
            Dpi.Init();
            Near(Dpi.Scale, 1f, "系统 DPI 读取失败没有使用安全默认值");
        }

        private static void LargeSecondary()
        {
            Configure("200", new Rectangle(0, 0, 1920, 1040), new Rectangle(1920, 0, 3840, 2080), 96, 192);
            Near(Dpi.Scale, 1040f / 760f, "主屏空间上限");
            Near(Dpi.FitScale(3840, 2080), 2f, "明确客户区不能再套用缓存屏幕上限");
            Dpi.UpdateWindow(Secondary);
            Near(Dpi.Scale, 2f, "大副屏不能被小主屏封顶");
            Near(Dpi.FitScale(3840, 2080), 2f, "实际副屏客户区应允许 200%");
            Check(Dpi.S(1220) == 2440 && Dpi.S(760) == 1520, "200% 实际界面尺寸不正确");
            Check(Dpi.ScalePreferencePercent == 200, "跨屏覆盖保存偏好");
            Check(!Dpi.UpdateWindow(Secondary), "同一窗口环境反复要求重建");
            Near(Dpi.FitScale(1220, 760), 1f, "手动缩放没有适配较小客户区");
            Check(!Dpi.SetScalePreference(200), "同值设置反复要求重建");
        }

        private static void SmallSecondary()
        {
            Configure("300", new Rectangle(0, 0, 3840, 2080), new Rectangle(-1920, 120, 1920, 1040), 192, 96);
            Near(Dpi.Scale, 2080f / 760f, "大主屏初始适配");
            Dpi.UpdateWindow(Secondary);
            Near(Dpi.Scale, 1040f / 760f, "小副屏未按目标工作区收缩");
            Check(Dpi.S(1220) <= 1920 && Dpi.S(760) <= 1040, "小副屏界面超出可用范围");
            Check(Dpi.ScalePreferencePercent == 300, "适配时丢失 300% 偏好");
            Dpi.UpdateWindow(Primary);
            Near(Dpi.Scale, 2080f / 760f, "返回大屏没有重新使用保存比例");
        }

        private static void SwitchingModes()
        {
            Rectangle roomy = new Rectangle(0, 0, 3840, 2080);
            Configure("0", roomy, roomy, 96, 192);
            Near(Dpi.Scale, 1f, "自动初始比例");
            Dpi.UpdateWindow(Secondary);
            Near(Dpi.Scale, 2f, "自动没有跟随目标窗口 DPI");
            Dpi.SetScalePreference(150);
            Near(Dpi.Scale, 1.5f, "自动转手动");
            Dpi.UpdateWindow(Primary);
            Near(Dpi.Scale, 1.5f, "手动模式被窗口 DPI 覆盖");
            Dpi.SetScalePreference(0);
            Near(Dpi.Scale, 1f, "手动无尺寸变化时没有刷新系统 DPI");
            Dpi.SetScalePreference(200);
            Native.WindowDpis[Secondary] = 144;
            Check(!Dpi.UpdateWindow(Secondary), "手动比例不变时错误要求重建");
            Dpi.SetScalePreference(0);
            Near(Dpi.Scale, 1.5f, "切回自动仍使用过时 DPI");
            Dpi.SetScalePreference(100);
            Check(Dpi.UpdateWindowEnvironment(Primary), "仅刷新窗口环境未报告变化");
            Near(Dpi.Scale, 1f, "环境刷新不应直接修改缩放");
            Dpi.SetScalePreference(0);
            Near(Dpi.Scale, 1f, "仅刷新环境后切回自动仍使用过时 DPI");
            Near(Dpi.FitScale(3840, 2080), 2080f / 760f, "自动模式原有客户区适配被手动上限污染");
            Near(Dpi.FitScale(12200, 7600), Dpi.MaxScale, "客户区适配突破全局上限");
            Dpi.NoFit = true;
            Dpi.SetScalePreference(300);
            Near(Dpi.Scale, 3f, "预览模式错误地套用了显示器适配");
        }

        private static void WindowEnvironment()
        {
            Configure("200", new Rectangle(0, 0, 3840, 2080), new Rectangle(-1920, 0, 1920, 1040), 144, 144);
            // 模拟现有客户区适配：只调用真实 FitScale，环境轮询不得把已适配比例改回去。
            Dpi.Scale = Dpi.FitScale(1220, 760);
            for (int i = 0; i < 100; i++)
            {
                Check(!Dpi.UpdateWindowEnvironment(Primary), "相同环境被误报变化");
                Near(Dpi.Scale, 1f, "相同环境刷新覆盖客户区适配");
            }
            Check(Dpi.UpdateWindowEnvironment(Secondary), "同 DPI 不同工作区未被识别");
            Near(Dpi.Scale, 1f, "仅刷新工作区直接改变当前缩放");
            Dpi.UpdateWindow(Secondary);
            Near(Dpi.Scale, 1040f / 760f, "同 DPI 跨屏后没有适配小工作区");
            Check(!Dpi.UpdateWindowEnvironment(IntPtr.Zero), "无窗口句柄报告环境变化");
            Near(Dpi.Scale, 1040f / 760f, "无窗口句柄丢失有效环境");
            System.Windows.Forms.Screen.Windows.Remove(Primary);
            Native.WindowDpis.Remove(Primary);
            Check(!Dpi.UpdateWindowEnvironment(Primary), "两个读取均失败却修改环境");
            Near(Dpi.ScaleFor(192), 1040f / 760f, "读取失败丢失已知工作区");
            Native.WindowDpis[Secondary] = 0;
            Dpi.UpdateWindowEnvironment(Secondary);
            Dpi.SetScalePreference(0);
            Near(Dpi.Scale, 1040f / 760f, "零 DPI 覆盖了有效 DPI");
            System.Windows.Forms.Screen.Windows[Secondary].WorkingArea = new Rectangle(-3840, 0, 3840, 2080);
            Dpi.UpdateWindow(Secondary);
            Near(Dpi.Scale, 1.5f, "DPI 读取失败后没有保留原 144 DPI");
        }

        private static int Main()
        {
            Console.OutputEncoding = new System.Text.UTF8Encoding(false);
            try
            {
                StoredPreferences();
                LargeSecondary();
                SmallSecondary();
                SwitchingModes();
                WindowEnvironment();
                Console.WriteLine("PASS DPI checks=" + assertions + "; real Dpi.cs; boundary substitutes only; product_started=false; visible_windows=0; registry_writes=0");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }
    }
}

using System;
using System.Globalization;

namespace PaviseApp
{
    [Flags]
    internal enum MonitoringMetrics
    {
        None = 0, Fps = 1, FrameTime = 2, Low1 = 4, CpuUsage = 8,
        GpuUsage = 16, GpuTemperature = 32, GpuClock = 64, GpuPower = 128,
        Ram = 256, Vram = 512, AverageFps = 1024, Low01 = 2048,
        FrameTimeDeviation = 4096, GameRam = 8192, RamUsage = 16384, VramUsage = 32768,
        GpuMemoryClock = 65536, GpuFan = 131072, CpuPower = 262144, CpuTemperature = 524288,
        SystemMemory = Ram | RamUsage, VideoMemory = Vram | VramUsage,
        LegacyAll = 1023, All = 1048575, Default = Fps | FrameTime | GpuUsage | GpuTemperature,
        FrameStatistics = Low1 | AverageFps | Low01 | FrameTimeDeviation
    }
    internal enum MonitoringLayout { TopLeft, TopRight, TopBar }

    internal static class MonitoringPreferences
    {
        internal const string EnabledKey = "PerfMonitorEnabledV1", LayoutKey = "PerfMonitorLayoutV1",
            MetricsKey = "PerfMonitorMetricsV1", AppearanceKey = "PerfMonitorAppearanceV1";
        internal const int DefaultBackgroundOpacity = 62;
        internal const int DefaultScalePercent = 100, MinimumScalePercent = 40, MaximumScalePercent = 200;
        internal const int DefaultSpacingPercent = 100, MinimumSpacingPercent = 50, MaximumSpacingPercent = 180;
        internal const int IntervalMs = 1000;
        internal const string RetiredIntervalKey = "PerfMonitorIntervalMsV1";
        private static readonly object gate = new object();
        private static int cachedGeneration = -1;
        private static MonitoringLayout layout;
        private static MonitoringMetrics metrics;
        private static int backgroundOpacity, offsetX, offsetY, scalePercent, spacingPercent;
        internal static event EventHandler Changed;
        internal static event EventHandler AppearanceChanged;
        internal static event EventHandler VisibilityChanged;
        private static int overlayVisible = 1;
        internal static bool OverlayVisible { get { return System.Threading.Volatile.Read(ref overlayVisible) != 0; } }
        internal static void SetOverlayVisible(bool value)
        {
            if (System.Threading.Interlocked.Exchange(ref overlayVisible, value ? 1 : 0) == (value ? 1 : 0)) return;
            EventHandler changed = VisibilityChanged;
            if (changed != null) changed(null, EventArgs.Empty);
        }
        internal static void ToggleOverlayVisibility() { SetOverlayVisible(!OverlayVisible); }
        internal static bool Enabled { get { return Settings.LoadCached(EnabledKey, false); } }
        internal static MonitoringLayout Layout { get { lock (gate) { Read(); return layout; } } }
        internal static MonitoringMetrics Metrics { get { lock (gate) { Read(); return metrics; } } }
        internal static int BackgroundOpacity { get { lock (gate) { Read(); return backgroundOpacity; } } }
        internal static int OffsetX { get { lock (gate) { Read(); return offsetX; } } }
        internal static int OffsetY { get { lock (gate) { Read(); return offsetY; } } }
        internal static int ScalePercent { get { lock (gate) { Read(); return scalePercent; } } }
        internal static int SpacingPercent { get { lock (gate) { Read(); return spacingPercent; } } }
        internal static void GetAppearance(out int opacity, out int x, out int y)
        { int scale; GetAppearance(out opacity, out x, out y, out scale); }
        internal static void GetAppearance(out int opacity, out int x, out int y, out int scale)
        { int spacing; GetAppearance(out opacity, out x, out y, out scale, out spacing); }
        internal static void GetAppearance(out int opacity, out int x, out int y, out int scale, out int spacing)
        { lock (gate) { Read(); opacity = backgroundOpacity; x = offsetX; y = offsetY; scale = scalePercent; spacing = spacingPercent; } }
        private static void Read()
        {
            int generation = Settings.MutationGeneration;
            if (cachedGeneration == generation) return;
            int value;
            layout = int.TryParse(Settings.LoadStr(LayoutKey, "0"), out value) && value >= 0 && value <= 2
                ? (MonitoringLayout)value : MonitoringLayout.TopLeft;
            metrics = int.TryParse(Settings.LoadStr(MetricsKey, ((int)MonitoringMetrics.Default).ToString(CultureInfo.InvariantCulture)), out value)
                && ValidMetrics((MonitoringMetrics)value) ? NormalizeMetrics((MonitoringMetrics)value) : MonitoringMetrics.Default;
            backgroundOpacity = DefaultBackgroundOpacity; offsetX = offsetY = 0; scalePercent = DefaultScalePercent; spacingPercent = DefaultSpacingPercent;
            // 位置与透明度整体保存，错误数据整体拒绝。
            string[] appearance = Settings.LoadStr(AppearanceKey, "62;0,0;100").Split(';');
            int opacity, x, y, scale = DefaultScalePercent, spacing = DefaultSpacingPercent;
            if (appearance.Length >= 2 && appearance.Length <= 4)
            {
                string[] position = appearance[1].Split(',');
                if (position.Length == 2
                    && int.TryParse(appearance[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out opacity)
                    && int.TryParse(position[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
                    && int.TryParse(position[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y)
                    && (appearance.Length == 2 || int.TryParse(appearance[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out scale))
                    && (appearance.Length < 4 || int.TryParse(appearance[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out spacing))
                    && opacity >= 0 && opacity <= 100 && ValidOffset(x) && ValidOffset(y) && ValidScale(scale) && ValidSpacing(spacing))
                { backgroundOpacity = opacity; offsetX = x; offsetY = y; scalePercent = scale; spacingPercent = spacing; }
            }
            cachedGeneration = generation;
        }
        internal static bool ValidMetrics(MonitoringMetrics value)
        { return value != MonitoringMetrics.None && (value & ~MonitoringMetrics.All) == 0; }
        internal static MonitoringMetrics NormalizeMetrics(MonitoringMetrics value)
        {
            if ((value & MonitoringMetrics.SystemMemory) != 0) value |= MonitoringMetrics.SystemMemory;
            if ((value & MonitoringMetrics.VideoMemory) != 0) value |= MonitoringMetrics.VideoMemory;
            return value;
        }
        private static bool Notify(bool saved, bool appearance = false)
        {
            if (!saved) return false;
            EventHandler changed = appearance ? AppearanceChanged : Changed;
            if (changed != null) changed(null, EventArgs.Empty);
            return true;
        }
        internal static bool TrySetEnabled(bool value)
        {
            if (!Settings.Save(EnabledKey, value)) return false;
            if (value) SetOverlayVisible(true);
            return Notify(true);
        }
        internal static bool TrySetLayout(MonitoringLayout value)
        { return (int)value >= 0 && (int)value <= 2 && Notify(Settings.SaveStr(LayoutKey, ((int)value).ToString(CultureInfo.InvariantCulture)), true); }
        internal static bool TrySetMetrics(MonitoringMetrics value)
        { return ValidMetrics(value) && Notify(Settings.SaveStr(MetricsKey, ((int)NormalizeMetrics(value)).ToString(CultureInfo.InvariantCulture))); }
        private static bool ValidOffset(int value) { return value >= -1000 && value <= 1000; }
        private static bool ValidScale(int value) { return value >= MinimumScalePercent && value <= MaximumScalePercent; }
        private static bool ValidSpacing(int value) { return value >= MinimumSpacingPercent && value <= MaximumSpacingPercent; }
        private static bool SaveAppearance(int opacity, int x, int y, int scale, int spacing)
        {
            return Settings.SaveStr(AppearanceKey, opacity.ToString(CultureInfo.InvariantCulture) + ";"
                + x.ToString(CultureInfo.InvariantCulture) + "," + y.ToString(CultureInfo.InvariantCulture)
                + ";" + scale.ToString(CultureInfo.InvariantCulture) + ";" + spacing.ToString(CultureInfo.InvariantCulture));
        }
        internal static bool TrySetBackgroundOpacity(int value)
        {
            if (value < 0 || value > 100) return false;
            bool saved;
            lock (gate) { Read(); saved = SaveAppearance(value, offsetX, offsetY, scalePercent, spacingPercent); }
            return Notify(saved, true);
        }
        internal static bool TrySetPosition(int x, int y)
        {
            if (!ValidOffset(x) || !ValidOffset(y)) return false;
            bool saved;
            lock (gate) { Read(); saved = SaveAppearance(backgroundOpacity, x, y, scalePercent, spacingPercent); }
            return Notify(saved, true);
        }
        internal static bool TrySetScalePercent(int value)
        {
            if (!ValidScale(value)) return false;
            bool saved;
            lock (gate) { Read(); saved = SaveAppearance(backgroundOpacity, offsetX, offsetY, value, spacingPercent); }
            return Notify(saved, true);
        }
        internal static bool TrySetSpacingPercent(int value)
        {
            if (!ValidSpacing(value)) return false;
            bool saved;
            lock (gate) { Read(); saved = SaveAppearance(backgroundOpacity, offsetX, offsetY, scalePercent, value); }
            return Notify(saved, true);
        }
        internal static bool TryApplyResolutionPreset(int height)
        {
            int scale, spacing;
            if (height == 1080) { scale = 60; spacing = 85; }
            else if (height == 1440) { scale = 65; spacing = 90; }
            else if (height == 2160) { scale = 80; spacing = 100; }
            else return false;
            bool saved;
            lock (gate) { Read(); saved = SaveAppearance(backgroundOpacity, offsetX, offsetY, scale, spacing); }
            return Notify(saved, true);
        }
        internal static bool TryResetAppearance()
        {
            bool saved;
            lock (gate) saved = SaveAppearance(DefaultBackgroundOpacity, 0, 0, DefaultScalePercent, DefaultSpacingPercent);
            return Notify(saved, true);
        }
    }
}

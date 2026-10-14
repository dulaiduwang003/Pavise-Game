#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int monitoringAssertions;
        private static void MonitoringEq<T>(T expected, T actual)
        {
            monitoringAssertions++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException("Monitoring: expected " + expected + ", actual " + actual);
        }
        internal static void RunMonitoringTests()
        {
            monitoringAssertions = 0;
            MonitoringPreferenceContract(); MonitoringMemoryPreferenceContract(); MonitoringScalePreferenceContract(); MonitoringSpacingAndPresetContract(); MonitoringAggregation(); MonitoringExpandedAggregation(); MonitoringPercentileRanks(); MonitoringInvalidFrameBatch();
            MonitoringOptionalFrameRing(); MonitoringLifecycle(); MonitoringGuardedSessionExit(); MonitoringSessionLifetime(); MonitoringExpandedLifecycle();
            MonitoringLaunchHints(); MonitoringStartupPreparation(); MonitoringImmediateFrame();
            MonitoringLeanSampling(); MonitoringPendingAndTransportStatus(); MonitoringOverlayVisibility(); MonitoringSensorInitializationFailure();
            MonitoringRtssStatisticsLifecycle(); MonitoringStatisticsSourceIsolation(); MonitoringPollingCadence(); MonitoringFrameTimeSource(); MonitoringExpandedSensorValues(); MonitoringRetainedSensorValues(); MonitoringAppearanceRepaint(); MonitoringLayoutDuringSample(); MonitoringInFlightDisable(); MonitoringBridgeEntryStop();
            Console.WriteLine("PASS monitoring assertions=" + monitoringAssertions
                + "; settings=transient timers=manual sensors=mocked RTSS=mocked; no windows or native telemetry");
        }
        private static void MonitoringPreferenceContract()
        {
            Settings.UseTransientStoreForCurrentProcess();
            MonitoringEq(false, MonitoringPreferences.Enabled);
            MonitoringEq(MonitoringLayout.TopLeft, MonitoringPreferences.Layout);
            MonitoringEq(MonitoringMetrics.Fps | MonitoringMetrics.FrameTime | MonitoringMetrics.GpuUsage
                | MonitoringMetrics.GpuTemperature, MonitoringMetrics.Default);
            MonitoringEq(MonitoringMetrics.Default, MonitoringPreferences.Metrics);
            MonitoringEq("missing", Settings.LoadStr(MonitoringPreferences.MetricsKey, "missing"));
            MonitoringEq(1000, MonitoringPreferences.IntervalMs);
            MonitoringEq(62, MonitoringPreferences.BackgroundOpacity);
            MonitoringEq(0, MonitoringPreferences.OffsetX); MonitoringEq(0, MonitoringPreferences.OffsetY);
            MonitoringEq(100, MonitoringPreferences.ScalePercent);
            int changes = 0, appearances = 0;
            EventHandler changed = delegate { changes++; };
            EventHandler appearanceChanged = delegate { appearances++; };
            MonitoringPreferences.Changed += changed;
            MonitoringPreferences.AppearanceChanged += appearanceChanged;
            try
            {
                Settings.SaveStr(MonitoringPreferences.RetiredIntervalKey, "500");
                MonitoringEq(1000, MonitoringPreferences.IntervalMs);
                MonitoringEq(false, MonitoringPreferences.TrySetLayout((MonitoringLayout)3));
                MonitoringEq(false, MonitoringPreferences.TrySetMetrics(MonitoringMetrics.None));
                MonitoringEq(false, MonitoringPreferences.TrySetMetrics((MonitoringMetrics)1048576));
                MonitoringEq(false, MonitoringPreferences.TrySetBackgroundOpacity(-1));
                MonitoringEq(false, MonitoringPreferences.TrySetBackgroundOpacity(101));
                MonitoringEq(false, MonitoringPreferences.TrySetPosition(-1001, 0));
                MonitoringEq(false, MonitoringPreferences.TrySetPosition(0, 1001));
                MonitoringEq(0, changes);
                MonitoringEq(0, appearances);
                MonitoringEq(true, MonitoringPreferences.TrySetLayout(MonitoringLayout.TopBar));
                MonitoringEq(true, MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Fps | MonitoringMetrics.Ram));
                MonitoringEq(true, MonitoringPreferences.TrySetEnabled(true));
                MonitoringEq(2, changes); MonitoringEq(1, appearances);
                MonitoringEq(MonitoringLayout.TopBar, MonitoringPreferences.Layout);
                MonitoringEq(MonitoringMetrics.Fps | MonitoringMetrics.SystemMemory, MonitoringPreferences.Metrics);
                MonitoringEq(1000, MonitoringPreferences.IntervalMs);
                MonitoringEq(true, MonitoringPreferences.TrySetPosition(-430, 250));
                MonitoringEq("62;-430,250;100;100", Settings.LoadStr(MonitoringPreferences.AppearanceKey, ""));
                MonitoringEq(true, MonitoringPreferences.TrySetBackgroundOpacity(0));
                MonitoringEq("0;-430,250;100;100", Settings.LoadStr(MonitoringPreferences.AppearanceKey, ""));
                MonitoringEq(0, MonitoringPreferences.BackgroundOpacity);
                MonitoringEq(-430, MonitoringPreferences.OffsetX); MonitoringEq(250, MonitoringPreferences.OffsetY);
                MonitoringEq(2, changes); MonitoringEq(3, appearances);
                MonitoringEq(true, MonitoringPreferences.TryResetAppearance());
                MonitoringEq("62;0,0;100;100", Settings.LoadStr(MonitoringPreferences.AppearanceKey, ""));
                MonitoringEq(true, MonitoringPreferences.TrySetBackgroundOpacity(100));
                MonitoringEq(true, MonitoringPreferences.TrySetPosition(1000, -1000));
                MonitoringEq("100;1000,-1000;100;100", Settings.LoadStr(MonitoringPreferences.AppearanceKey, ""));
                MonitoringEq(6, appearances);
                Settings.SuspendWritesForReset();
                MonitoringEq(false, MonitoringPreferences.TrySetEnabled(false));
                MonitoringEq(false, MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Fps | MonitoringMetrics.VideoMemory));
                MonitoringEq(false, MonitoringPreferences.TrySetLayout(MonitoringLayout.TopRight));
                MonitoringEq(true, MonitoringPreferences.Enabled);
                MonitoringEq(MonitoringMetrics.Fps | MonitoringMetrics.SystemMemory, MonitoringPreferences.Metrics);
                MonitoringEq(((int)(MonitoringMetrics.Fps | MonitoringMetrics.SystemMemory)).ToString(CultureInfo.InvariantCulture),
                    Settings.LoadStr(MonitoringPreferences.MetricsKey, ""));
                MonitoringEq(MonitoringLayout.TopBar, MonitoringPreferences.Layout);
                MonitoringEq(false, MonitoringPreferences.TrySetBackgroundOpacity(38));
                MonitoringEq(false, MonitoringPreferences.TrySetPosition(1, 2));
                MonitoringEq(false, MonitoringPreferences.TryResetAppearance());
                MonitoringEq("100;1000,-1000;100;100", Settings.LoadStr(MonitoringPreferences.AppearanceKey, ""));
                MonitoringEq(100, MonitoringPreferences.BackgroundOpacity);
                MonitoringEq(1000, MonitoringPreferences.OffsetX); MonitoringEq(-1000, MonitoringPreferences.OffsetY);
                MonitoringEq(2, changes); MonitoringEq(6, appearances);
            }
            finally { MonitoringPreferences.Changed -= changed; MonitoringPreferences.AppearanceChanged -= appearanceChanged; Settings.UseTransientStoreForCurrentProcess(); }
            Settings.SaveStr(MonitoringPreferences.RetiredIntervalKey, "2000");
            Settings.SaveStr(MonitoringPreferences.MetricsKey, "-1");
            Settings.SaveStr(MonitoringPreferences.LayoutKey, "999");
            MonitoringEq(1000, MonitoringPreferences.IntervalMs);
            MonitoringEq(MonitoringMetrics.Default, MonitoringPreferences.Metrics);
            MonitoringEq(MonitoringLayout.TopLeft, MonitoringPreferences.Layout);
            foreach (string bad in new[] { "", "garbage", "30;7", "100;1,2,3", "-1;100,200", "101;0,0", "30;1001,0", "30;0,-1001", "30;2147483648,0", "30;1,2;39", "30;1,2;201", "30;1,2;bad", "30;1,2;100;181", "30;1,2;100;49", "30;1,2;100;100;0" })
            {
                Settings.SaveStr(MonitoringPreferences.AppearanceKey, bad);
                MonitoringEq(62, MonitoringPreferences.BackgroundOpacity);
                MonitoringEq(0, MonitoringPreferences.OffsetX); MonitoringEq(0, MonitoringPreferences.OffsetY);
                MonitoringEq(100, MonitoringPreferences.ScalePercent);
            }
            foreach (MonitoringMetrics saved in new[] { MonitoringMetrics.LegacyAll, MonitoringMetrics.Fps,
                MonitoringMetrics.Low01, MonitoringMetrics.CpuPower, MonitoringMetrics.CpuTemperature, (MonitoringMetrics)524287, MonitoringMetrics.All })
            {
                MonitoringEq(true, MonitoringPreferences.TrySetMetrics(saved));
                MonitoringEq(saved == MonitoringMetrics.LegacyAll
                    ? saved | MonitoringMetrics.RamUsage | MonitoringMetrics.VramUsage : saved, MonitoringPreferences.Metrics);
            }
            foreach (string bad in new[] { "0", "-1", "1048576", "1048832", "2147483647", "2147483648", "bad", "" })
            {
                Settings.SaveStr(MonitoringPreferences.MetricsKey, bad);
                int generation = Settings.MutationGeneration;
                MonitoringEq(MonitoringMetrics.Default, MonitoringPreferences.Metrics);
                MonitoringEq(false, MonitoringPreferences.Enabled);
                MonitoringEq(bad, Settings.LoadStr(MonitoringPreferences.MetricsKey, "missing"));
                MonitoringEq(generation, Settings.MutationGeneration);
            }
            Settings.UseTransientStoreForCurrentProcess();
        }
        private static void MonitoringMemoryPreferenceContract()
        {
            Settings.UseTransientStoreForCurrentProcess();
            try
            {
                MonitoringEq(MonitoringMetrics.Ram | MonitoringMetrics.RamUsage, MonitoringMetrics.SystemMemory);
                MonitoringEq(MonitoringMetrics.Vram | MonitoringMetrics.VramUsage, MonitoringMetrics.VideoMemory);
                MonitoringMetrics other = MonitoringMetrics.Fps | MonitoringMetrics.GpuClock | MonitoringMetrics.CpuPower;
                MonitoringMetrics[] oldSelections = { MonitoringMetrics.Ram, MonitoringMetrics.RamUsage,
                    MonitoringMetrics.Vram, MonitoringMetrics.VramUsage };
                MonitoringMetrics[] pairedSelections = { MonitoringMetrics.SystemMemory, MonitoringMetrics.SystemMemory,
                    MonitoringMetrics.VideoMemory, MonitoringMetrics.VideoMemory };
                for (int index = 0; index < oldSelections.Length; index++)
                {
                    string singleSaved = ((int)oldSelections[index]).ToString(CultureInfo.InvariantCulture);
                    Settings.SaveStr(MonitoringPreferences.MetricsKey, singleSaved);
                    int singleGeneration = Settings.MutationGeneration;
                    MonitoringEq(pairedSelections[index], MonitoringPreferences.Metrics);
                    MonitoringEq(singleSaved, Settings.LoadStr(MonitoringPreferences.MetricsKey, "missing"));
                    MonitoringEq(singleGeneration, Settings.MutationGeneration);
                    MonitoringEq(false, MonitoringPreferences.Enabled);
                    MonitoringMetrics oldSelection = other | oldSelections[index], expected = other | pairedSelections[index];
                    string saved = ((int)oldSelection).ToString(CultureInfo.InvariantCulture);
                    Settings.SaveStr(MonitoringPreferences.MetricsKey, saved);
                    int generation = Settings.MutationGeneration;
                    MonitoringEq(expected, MonitoringPreferences.Metrics);
                    MonitoringEq(expected, MonitoringPreferences.NormalizeMetrics(oldSelection));
                    MonitoringEq(saved, Settings.LoadStr(MonitoringPreferences.MetricsKey, "missing"));
                    MonitoringEq(generation, Settings.MutationGeneration);
                    MonitoringEq(false, MonitoringPreferences.Enabled);
                    MonitoringEq("missing", Settings.LoadStr(MonitoringPreferences.EnabledKey, "missing"));
                    MonitoringEq(true, MonitoringPreferences.TrySetMetrics(oldSelection));
                    MonitoringEq(expected, MonitoringPreferences.Metrics);
                    MonitoringEq(((int)expected).ToString(CultureInfo.InvariantCulture), Settings.LoadStr(MonitoringPreferences.MetricsKey, ""));
                    MonitoringEq(false, MonitoringPreferences.Enabled);
                }
                Settings.SaveStr(MonitoringPreferences.MetricsKey, ((int)MonitoringMetrics.LegacyAll).ToString(CultureInfo.InvariantCulture));
                int legacyGeneration = Settings.MutationGeneration;
                MonitoringEq(MonitoringMetrics.LegacyAll | MonitoringMetrics.RamUsage | MonitoringMetrics.VramUsage, MonitoringPreferences.Metrics);
                MonitoringEq("1023", Settings.LoadStr(MonitoringPreferences.MetricsKey, ""));
                MonitoringEq(legacyGeneration, Settings.MutationGeneration);

                MonitoringEq(true, MonitoringPreferences.TrySetMetrics(other));
                MonitoringEq(other, MonitoringPreferences.Metrics);
                MonitoringEq(true, MonitoringPreferences.TrySetMetrics(MonitoringPreferences.Metrics | MonitoringMetrics.SystemMemory));
                MonitoringEq(other | MonitoringMetrics.SystemMemory, MonitoringPreferences.Metrics);
                MonitoringEq(true, MonitoringPreferences.TrySetMetrics(MonitoringPreferences.Metrics | MonitoringMetrics.VideoMemory));
                MonitoringEq(other | MonitoringMetrics.SystemMemory | MonitoringMetrics.VideoMemory, MonitoringPreferences.Metrics);
                MonitoringEq(true, MonitoringPreferences.TrySetMetrics(MonitoringPreferences.Metrics & ~MonitoringMetrics.SystemMemory));
                MonitoringEq(other | MonitoringMetrics.VideoMemory, MonitoringPreferences.Metrics);
                MonitoringEq(true, MonitoringPreferences.TrySetMetrics(MonitoringPreferences.Metrics & ~MonitoringMetrics.VideoMemory));
                MonitoringEq(other, MonitoringPreferences.Metrics);
                MonitoringEq(false, MonitoringPreferences.Enabled);
                MonitoringEq(true, MonitoringPreferences.TrySetEnabled(true));
                MonitoringEq(true, MonitoringPreferences.TrySetMetrics(MonitoringMetrics.SystemMemory));
                MonitoringEq(true, MonitoringPreferences.Enabled);
                foreach (MonitoringMetrics invalid in new[] { MonitoringMetrics.None, (MonitoringMetrics)(-1),
                    (MonitoringMetrics)1048576 | MonitoringMetrics.Ram })
                {
                    MonitoringEq(false, MonitoringPreferences.TrySetMetrics(invalid));
                    MonitoringEq(MonitoringMetrics.SystemMemory, MonitoringPreferences.Metrics);
                }
                MonitoringEq(false, MonitoringPreferences.TrySetMetrics(MonitoringPreferences.Metrics & ~MonitoringMetrics.SystemMemory));
                MonitoringEq(MonitoringMetrics.SystemMemory, MonitoringPreferences.Metrics);
                Settings.SuspendWritesForReset();
                MonitoringEq(false, MonitoringPreferences.TrySetMetrics(MonitoringMetrics.VideoMemory));
                MonitoringEq(MonitoringMetrics.SystemMemory, MonitoringPreferences.Metrics);
                MonitoringEq(((int)MonitoringMetrics.SystemMemory).ToString(CultureInfo.InvariantCulture), Settings.LoadStr(MonitoringPreferences.MetricsKey, ""));
                MonitoringEq(true, MonitoringPreferences.Enabled);
            }
            finally { Settings.UseTransientStoreForCurrentProcess(); }
        }
        private static void MonitoringScalePreferenceContract()
        {
            Settings.UseTransientStoreForCurrentProcess();
            int appearances = 0, changes = 0;
            EventHandler appearanceChanged = delegate { appearances++; }, changed = delegate { changes++; };
            MonitoringPreferences.AppearanceChanged += appearanceChanged; MonitoringPreferences.Changed += changed;
            try
            {
                Settings.SaveStr(MonitoringPreferences.AppearanceKey, "35;123,-200");
                MonitoringEq(35, MonitoringPreferences.BackgroundOpacity);
                MonitoringEq(123, MonitoringPreferences.OffsetX); MonitoringEq(-200, MonitoringPreferences.OffsetY);
                MonitoringEq(100, MonitoringPreferences.ScalePercent);
                MonitoringEq(false, MonitoringPreferences.TrySetScalePercent(39));
                MonitoringEq(false, MonitoringPreferences.TrySetScalePercent(201));
                MonitoringEq(0, appearances); MonitoringEq(0, changes);
                MonitoringEq(true, MonitoringPreferences.TrySetScalePercent(150));
                MonitoringEq("35;123,-200;150;100", Settings.LoadStr(MonitoringPreferences.AppearanceKey, ""));
                MonitoringEq(true, MonitoringPreferences.TrySetBackgroundOpacity(12));
                MonitoringEq(true, MonitoringPreferences.TrySetPosition(1000, -1000));
                MonitoringEq("12;1000,-1000;150;100", Settings.LoadStr(MonitoringPreferences.AppearanceKey, ""));
                int opacity, x, y, scale;
                MonitoringPreferences.GetAppearance(out opacity, out x, out y, out scale);
                MonitoringEq(12, opacity); MonitoringEq(1000, x); MonitoringEq(-1000, y); MonitoringEq(150, scale);
                MonitoringPreferences.GetAppearance(out opacity, out x, out y);
                MonitoringEq(12, opacity); MonitoringEq(1000, x); MonitoringEq(-1000, y);
                MonitoringEq(true, MonitoringPreferences.TrySetScalePercent(60)); MonitoringEq(60, MonitoringPreferences.ScalePercent);
                MonitoringEq(true, MonitoringPreferences.TrySetScalePercent(200)); MonitoringEq(200, MonitoringPreferences.ScalePercent);
                MonitoringEq(5, appearances); MonitoringEq(0, changes);
                MonitoringEq(true, MonitoringPreferences.TryResetAppearance());
                MonitoringEq("62;0,0;100;100", Settings.LoadStr(MonitoringPreferences.AppearanceKey, ""));
                MonitoringEq(true, MonitoringPreferences.TrySetScalePercent(180));
                Settings.SuspendWritesForReset();
                MonitoringEq(false, MonitoringPreferences.TrySetScalePercent(60));
                MonitoringEq(false, MonitoringPreferences.TrySetBackgroundOpacity(0));
                MonitoringEq(false, MonitoringPreferences.TrySetPosition(200, 300));
                MonitoringEq(false, MonitoringPreferences.TryResetAppearance());
                MonitoringEq("62;0,0;180;100", Settings.LoadStr(MonitoringPreferences.AppearanceKey, ""));
                MonitoringEq(180, MonitoringPreferences.ScalePercent);
                MonitoringEq(62, MonitoringPreferences.BackgroundOpacity); MonitoringEq(0, MonitoringPreferences.OffsetX); MonitoringEq(0, MonitoringPreferences.OffsetY);
                MonitoringEq(7, appearances); MonitoringEq(0, changes);
            }
            finally
            {
                MonitoringPreferences.AppearanceChanged -= appearanceChanged; MonitoringPreferences.Changed -= changed;
                Settings.UseTransientStoreForCurrentProcess();
            }
        }
        private static void MonitoringSpacingAndPresetContract()
        {
            Settings.UseTransientStoreForCurrentProcess();
            int notifications = 0;
            EventHandler changed = delegate { notifications++; };
            MonitoringPreferences.AppearanceChanged += changed;
            try
            {
                Settings.SaveStr(MonitoringPreferences.AppearanceKey, "35;123,-200;75");
                MonitoringEq(100, MonitoringPreferences.SpacingPercent);
                MonitoringEq(true, MonitoringPreferences.TrySetSpacingPercent(50));
                MonitoringEq(75, MonitoringPreferences.ScalePercent);
                MonitoringEq(true, MonitoringPreferences.TrySetScalePercent(40));
                MonitoringEq(50, MonitoringPreferences.SpacingPercent);
                MonitoringEq(true, MonitoringPreferences.TrySetPosition(77, -90));
                MonitoringEq(true, MonitoringPreferences.TrySetBackgroundOpacity(24));
                foreach (int invalid in new[] { 49, 181, int.MaxValue, int.MinValue }) MonitoringEq(false, MonitoringPreferences.TrySetSpacingPercent(invalid));
                int count = notifications;
                foreach (int invalid in new[] { 720, 1441, 0, int.MaxValue }) MonitoringEq(false, MonitoringPreferences.TryApplyResolutionPreset(invalid));
                MonitoringEq(count, notifications);
                int[] heights = { 1080, 1440, 2160 }, sizes = { 60, 65, 80 }, gaps = { 85, 90, 100 };
                for (int i = 0; i < heights.Length; i++)
                {
                    int before = notifications;
                    MonitoringEq(true, MonitoringPreferences.TryApplyResolutionPreset(heights[i])); MonitoringEq(before + 1, notifications);
                    MonitoringEq(sizes[i], MonitoringPreferences.ScalePercent); MonitoringEq(gaps[i], MonitoringPreferences.SpacingPercent);
                    MonitoringEq(24, MonitoringPreferences.BackgroundOpacity); MonitoringEq(77, MonitoringPreferences.OffsetX); MonitoringEq(-90, MonitoringPreferences.OffsetY);
                    MonitoringEq(false, MonitoringPreferences.Enabled); MonitoringEq(MonitoringMetrics.Default, MonitoringPreferences.Metrics);
                    string record = Settings.LoadStr(MonitoringPreferences.AppearanceKey, ""); Settings.SaveStr(MonitoringPreferences.AppearanceKey, record);
                    MonitoringEq(gaps[i], MonitoringPreferences.SpacingPercent); MonitoringEq(sizes[i], MonitoringPreferences.ScalePercent);
                }
                string retained = Settings.LoadStr(MonitoringPreferences.AppearanceKey, "");
                count = notifications; Settings.SuspendWritesForReset();
                MonitoringEq(false, MonitoringPreferences.TryApplyResolutionPreset(1080)); MonitoringEq(false, MonitoringPreferences.TrySetSpacingPercent(180));
                MonitoringEq(retained, Settings.LoadStr(MonitoringPreferences.AppearanceKey, ""));
                MonitoringEq(80, MonitoringPreferences.ScalePercent); MonitoringEq(100, MonitoringPreferences.SpacingPercent); MonitoringEq(count, notifications);
            }
            finally { MonitoringPreferences.AppearanceChanged -= changed; Settings.UseTransientStoreForCurrentProcess(); }
        }
        private static void MonitoringAggregation()
        {
            MonitoringEq((double?)null, MonitoringNativeSensors.CpuPercent(-1, 10, 1000, 1000, 8));
            MonitoringEq((double?)25, MonitoringNativeSensors.CpuPercent(0, 10000000, 1000, 1000, 4));
            MonitoringEq((double?)null, MonitoringNativeSensors.CpuPercent(10, 9, 1000, 1000, 4));
            MonitoringEq((double?)null, MonitoringNativeSensors.CpuPercent(0, 10, 0, 1000, 4));
            var window = new MonitoringFrameWindow();
            window.Add(new[] { double.NaN, double.PositiveInfinity, 0.0, -2.0, 20000.0 }, 1);
            MonitoringEq(0, window.Count); MonitoringEq((double?)null, window.Compute(MonitoringMetrics.FrameTimeDeviation).FrameTimeDeviation);
            double[] frames = new double[300]; for (int i = 0; i < frames.Length; i++) frames[i] = 10;
            window.Add(frames, 2); MonitoringEq(300, window.Count); MonitoringNear(0, window.Compute(MonitoringMetrics.FrameTimeDeviation).FrameTimeDeviation);
            window.Add(frames, 2); MonitoringEq(300, window.Count);
            window.Add(frames, 1); MonitoringEq(300, window.Count);
            frames = new double[7000]; for (int i = 0; i < frames.Length; i++) frames[i] = i >= 6940 ? 100 : 10;
            window.Add(frames, 3); MonitoringEq(6000, window.Count);
            MonitoringNear(Math.Sqrt(.01 * .99) * 90, window.Compute(MonitoringMetrics.FrameTimeDeviation).FrameTimeDeviation);
            window.Reset(); MonitoringEq(0, window.Count); MonitoringEq((double?)null, window.Compute(MonitoringMetrics.FrameTimeDeviation).FrameTimeDeviation);
        }
        private static double[] MonitoringFrameTimes(int count, double milliseconds)
        {
            var values = new double[count];
            for (int i = 0; i < values.Length; i++) values[i] = milliseconds;
            return values;
        }
        private static void MonitoringNear(double expected, double? actual)
        { MonitoringEq(true, actual.HasValue && Math.Abs(actual.Value - expected) < 0.000001); }
        private static void MonitoringExpandedAggregation()
        {
            var window = new MonitoringFrameWindow();
            window.Add(MonitoringFrameTimes(299, 20), 1);
            MonitoringEq((double?)null, window.Compute(MonitoringMetrics.FrameTimeDeviation).FrameTimeDeviation);
            window.Reset(); window.Add(MonitoringFrameTimes(300, 1), 1);
            MonitoringEq((double?)null, window.Compute(MonitoringMetrics.FrameTimeDeviation).FrameTimeDeviation);

            window.Reset();
            double[] alternating = MonitoringFrameTimes(300, 10);
            for (int i = 1; i < alternating.Length; i += 2) alternating[i] = 20;
            window.Add(alternating, 1);
            MonitoringNear(5, window.Compute(MonitoringMetrics.FrameTimeDeviation).FrameTimeDeviation);
            MonitoringFrameStatistics statistics = window.Compute(MonitoringMetrics.FrameStatistics);
            MonitoringNear(1000.0 / 15, statistics.AverageFps);
            MonitoringNear(50, statistics.Low1);
            MonitoringEq((double?)null, statistics.Low01);
            MonitoringEq((double?)null, window.Compute(MonitoringMetrics.AverageFps | MonitoringMetrics.Low1 | MonitoringMetrics.Low01).FrameTimeDeviation);
            window.Add(alternating, 1); MonitoringEq(300, window.Count);
            MonitoringNear(5, window.Compute(MonitoringMetrics.FrameTimeDeviation).FrameTimeDeviation);

            window.Reset();
            double[] outliers = MonitoringFrameTimes(3000, 10);
            outliers[0] = outliers[1] = outliers[2] = 100;
            window.Add(outliers, 1);
            statistics = window.Compute(MonitoringMetrics.FrameStatistics);
            MonitoringNear(1000.0 / 10.09, statistics.AverageFps);
            MonitoringNear(100, statistics.Low1);
            MonitoringNear(100, statistics.Low01);
            MonitoringNear(100, window.Compute(MonitoringMetrics.Low01).Low01);
            MonitoringNear(Math.Sqrt((2997.0 * 100 + 3.0 * 10000) / 3000 - 10.09 * 10.09),
                window.Compute(MonitoringMetrics.FrameTimeDeviation).FrameTimeDeviation);

            window.Reset(); window.Add(MonitoringFrameTimes(3000, .5), 1);
            MonitoringEq((double?)null, window.Compute(MonitoringMetrics.FrameTimeDeviation).FrameTimeDeviation);
            window.Add(MonitoringFrameTimes(3000, .5), 2);
            MonitoringNear(0, window.Compute(MonitoringMetrics.FrameTimeDeviation).FrameTimeDeviation);
            window.Reset();
            outliers = MonitoringFrameTimes(6000, 10); outliers[0] = 1000;
            window.Add(outliers, 1); window.Add(new[] { 10.0 }, 2);
            MonitoringEq(6000, window.Count);
            MonitoringNear(0, window.Compute(MonitoringMetrics.FrameTimeDeviation).FrameTimeDeviation);
            MonitoringNear(100, window.Compute(MonitoringMetrics.Low1).Low1);
            MonitoringEq((double?)null, window.Compute(MonitoringMetrics.Low1).AverageFps);
            MonitoringEq((double?)null, window.Compute(MonitoringMetrics.Low1).Low01);
            window.Reset(); MonitoringEq((double?)null, window.Compute(MonitoringMetrics.FrameTimeDeviation).FrameTimeDeviation);
            MonitoringEq((double?)null, window.Compute(MonitoringMetrics.FrameStatistics).Low1);
        }
        private static void MonitoringPercentileRanks()
        {
            var window = new MonitoringFrameWindow();
            double[] frames = MonitoringFrameTimes(3000, 10);
            frames[0] = 1000;
            window.Add(frames, 1);
            MonitoringFrameStatistics values = window.Compute(MonitoringMetrics.FrameStatistics);
            MonitoringNear(100, values.Low1); MonitoringNear(100, values.Low01);
            MonitoringNear(1000.0 / (10 + 990.0 / 3000), values.AverageFps);

            window.Reset(); frames = MonitoringFrameTimes(3000, 10);
            for (int index = 0; index < 30; index++) frames[index] = 100;
            window.Add(frames, 1);
            MonitoringNear(100, window.Compute(MonitoringMetrics.Low1).Low1);
            frames[30] = 100;
            window.Reset(); window.Add(frames, 1);
            MonitoringNear(10, window.Compute(MonitoringMetrics.Low1).Low1);

            window.Reset(); frames = MonitoringFrameTimes(3000, 10);
            for (int index = 0; index < 3; index++) frames[index] = 100;
            window.Add(frames, 1);
            MonitoringNear(100, window.Compute(MonitoringMetrics.Low01).Low01);
            frames[3] = 100;
            window.Reset(); window.Add(frames, 1);
            MonitoringNear(10, window.Compute(MonitoringMetrics.Low01).Low01);

            window.Reset(); frames = MonitoringFrameTimes(3001, 10);
            for (int index = 0; index < 30; index++) frames[index] = 100;
            window.Add(frames, 1);
            MonitoringNear(100, window.Compute(MonitoringMetrics.Low1).Low1);
            window.Reset(); frames[30] = 100; window.Add(frames, 1);
            MonitoringNear(10, window.Compute(MonitoringMetrics.Low1).Low1);
            window.Reset(); frames = MonitoringFrameTimes(3001, 10);
            for (int index = 0; index < 3; index++) frames[index] = 100;
            window.Add(frames, 1);
            MonitoringNear(100, window.Compute(MonitoringMetrics.Low01).Low01);
            window.Reset(); frames[3] = 100; window.Add(frames, 1);
            MonitoringNear(10, window.Compute(MonitoringMetrics.Low01).Low01);
        }
        private static void MonitoringEmptyStatistics(MonitoringFrameStatistics values)
        {
            MonitoringEq((double?)null, values.AverageFps); MonitoringEq((double?)null, values.Low1);
            MonitoringEq((double?)null, values.Low01); MonitoringEq((double?)null, values.FrameTimeDeviation);
        }
        private static void MonitoringInvalidFrameBatch()
        {
            foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.0, -0.1, 10000.001 })
            foreach (int position in new[] { 0, 150, 299 })
            {
                var window = new MonitoringFrameWindow();
                window.Add(MonitoringFrameTimes(6000, 10), 10);
                MonitoringNear(100, window.Compute(MonitoringMetrics.FrameStatistics).AverageFps);
                double[] batch = MonitoringFrameTimes(300, 20); batch[position] = bad;
                window.Add(batch, 11);
                MonitoringEq(0, window.Count); MonitoringEmptyStatistics(window.Compute(MonitoringMetrics.FrameStatistics));
                window.Add(MonitoringFrameTimes(6000, 10), 11);
                window.Add(MonitoringFrameTimes(6000, 10), 10);
                MonitoringEq(0, window.Count); MonitoringEmptyStatistics(window.Compute(MonitoringMetrics.FrameStatistics));
                window.Add(MonitoringFrameTimes(299, 20), 12);
                MonitoringEq(299, window.Count); MonitoringEmptyStatistics(window.Compute(MonitoringMetrics.FrameStatistics));
                window.Add(new[] { 10.0 }, 13);
                MonitoringEq(300, window.Count);
                MonitoringFrameStatistics recovered = window.Compute(MonitoringMetrics.FrameStatistics);
                MonitoringNear(1000.0 / ((299.0 * 20 + 10) / 300), recovered.AverageFps);
                MonitoringNear(50, recovered.Low1); MonitoringEq((double?)null, recovered.Low01);
            }
            var oversized = new MonitoringFrameWindow();
            oversized.Add(MonitoringFrameTimes(6000, 10), 1);
            double[] large = MonitoringFrameTimes(7000, 10); large[0] = double.NaN;
            oversized.Add(large, 2);
            MonitoringEq(0, oversized.Count); MonitoringEmptyStatistics(oversized.Compute(MonitoringMetrics.FrameStatistics));
            oversized.Add(MonitoringFrameTimes(6000, 10), 2); MonitoringEq(0, oversized.Count);
            oversized.Add(MonitoringFrameTimes(1000, 10), 3);
            MonitoringNear(100, oversized.Compute(MonitoringMetrics.Low01).Low01);
        }
        private static void MonitoringOptionalFrameRing()
        {
            Settings.UseTransientStoreForCurrentProcess();
            foreach (MonitoringMetrics selected in new[] { MonitoringMetrics.FrameTimeDeviation, MonitoringMetrics.AverageFps,
                MonitoringMetrics.Low1, MonitoringMetrics.Low01 })
            {
                MonitoringPreferences.TrySetMetrics(selected);
                using (var fixture = new RtssMapFixture())
                using (var session = new RtssMemorySession(fixture.Name, "Pavise.Test.OptionalFrames"))
                {
                    RtssFrameSample sample;
                    MonitoringEq(true, session.TryRead(1234, delegate { return 99; }, out sample));
                    MonitoringEq(0, sample.FrameTimes.Length);
                    fixture.Position(45); fixture.Fresh();
                    MonitoringEq(true, session.TryRead(1234, delegate { return 99; }, out sample));
                    MonitoringEq(35, sample.FrameTimes.Length); MonitoringNear(8.333, sample.FrameTimes[0]);
                    MonitoringEq(35L, sample.FrameSequence);
                    MonitoringEq(true, session.TryRead(1234, delegate { return 99; }, out sample));
                    MonitoringEq(0, sample.FrameTimes.Length);
                    fixture.Position(45 + 2048); fixture.Fresh();
                    MonitoringEq(true, session.TryRead(1234, delegate { return 99; }, out sample));
                    MonitoringEq((double[])null, sample.FrameTimes);
                    MonitoringEq(true, session.TryRead(1234, delegate { return 100; }, out sample));
                    MonitoringEq(0, sample.FrameTimes.Length); MonitoringEq(0L, sample.FrameSequence);

                    fixture.View.Write(RtssMapFixture.AppOffset + 272, unchecked((uint)Environment.TickCount - 5000));
                    MonitoringEq(false, session.TryRead(1234, delegate { return 100; }, out sample));
                    fixture.Position(2200); fixture.Fresh();
                    MonitoringEq(true, session.TryRead(1234, delegate { return 100; }, out sample));
                    MonitoringEq(0, sample.FrameTimes.Length);
                    MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Fps | MonitoringMetrics.CpuPower);
                    fixture.Position(2300); fixture.Fresh();
                    MonitoringEq(true, session.TryRead(1234, delegate { return 100; }, out sample));
                    MonitoringEq((double[])null, sample.FrameTimes);
                    MonitoringPreferences.TrySetMetrics(selected);
                    fixture.Position(2400); fixture.Fresh();
                    MonitoringEq(true, session.TryRead(1234, delegate { return 100; }, out sample));
                    MonitoringEq(0, sample.FrameTimes.Length);
                }
            }
            Settings.UseTransientStoreForCurrentProcess();
        }
        private sealed class MonitoringFakeTimer : IMonitoringTimer
        {
            internal Action Callback;
            internal int Due, Schedules;
            internal bool Disposed;
            internal MonitoringFixture Fixture;
            public void Schedule(int value) { MonitoringEq(false, Disposed); Due = value; Schedules++; }
            public void Dispose() { Disposed = true; }
            internal void Fire()
            {
                if (Disposed) return;
                if (Fixture != null) Fixture.Clock += Due;
                Callback();
            }
            internal void FireUntilPublication()
            {
                MonitoringSnapshot previous = PerformanceMonitoring.Current;
                for (int index = 0; index < 10 && !Disposed; index++)
                {
                    Fire();
                    if (!Object.ReferenceEquals(previous, PerformanceMonitoring.Current)) return;
                }
                if (!Disposed) throw new InvalidOperationException("Monitoring timer failed to reach the next publication");
            }
        }
        private sealed class MonitoringFakeBridge : IMonitoringBridge, IMonitoringPreparationBridge, IMonitoringStatusBridge
        {
            public string Status { get; set; }
            public bool GameActive { get; set; }
            public bool GamePreparing { get; set; }
            internal int Preparations;
            public void Prepare() { Preparations++; }
            internal MonitoringFakeBridge() { GameActive = true; }
            internal RtssFrameSample Target;
            internal int Reads, Publishes, Clears, Closes;
            internal MonitoringSnapshot Published;
            internal MonitoringLayout PublishedLayout;
            internal Action BeforeRead;
            public bool TryReadTarget(out RtssFrameSample sample)
            { Reads++; if (BeforeRead != null) BeforeRead(); sample = Target; return sample != null; }
            public void Publish(MonitoringSnapshot value, MonitoringLayout layout) { Publishes++; Published = value; PublishedLayout = layout; }
            public void Clear() { Clears++; }
            public void Close() { Closes++; }
        }
        private sealed class MonitoringFakeSensors : IMonitoringSensors
        {
            internal int Reads, Resets, Disposals;
            internal bool Valid = true;
            internal Action BeforeRead;
            internal MonitoringSensorReading Values = new MonitoringSensorReading { CpuUsage = 25, RamMiB = 8000, GpuUsage = 50, GpuTemperature = 60, VramMiB = 4000 };
            public bool Read(RtssFrameSample target, MonitoringMetrics metrics, out MonitoringSensorReading reading)
            { Reads++; if (BeforeRead != null) BeforeRead(); reading = Values; return Valid; }
            public void ResetTarget() { Resets++; }
            public void Dispose() { Disposals++; }
        }
        private sealed class MonitoringFixture : IDisposable
        {
            internal readonly MonitoringFakeBridge Bridge = new MonitoringFakeBridge();
            internal readonly MonitoringFakeSensors Sensors = new MonitoringFakeSensors();
            internal readonly List<MonitoringFakeTimer> Timers = new List<MonitoringFakeTimer>();
            internal int SensorFactories;
            internal long Clock = 100000;
            internal MonitoringFixture()
            {
                PerformanceMonitoring.ClockForTest = delegate { return Clock; };
                PerformanceMonitoring.BridgeForTest = Bridge; PerformanceMonitoring.Stop();
                Settings.UseTransientStoreForCurrentProcess();
                PerformanceMonitoring.SensorsFactoryForTest = delegate { SensorFactories++; return Sensors; };
                PerformanceMonitoring.TimerFactoryForTest = delegate(Action callback) {
                    var timer = new MonitoringFakeTimer { Callback = callback, Fixture = this }; Timers.Add(timer); return timer;
                };
                Bridge.Target = new RtssFrameSample { Pid = 1234, Creation = 99, Name = "Owned game", Fps = 100, FrameTimeMs = 10, FrameSequence = 1 };
            }
            public void Dispose()
            {
                PerformanceMonitoring.Stop();
                PerformanceMonitoring.BridgeForTest = null;
                PerformanceMonitoring.SensorsFactoryForTest = null; PerformanceMonitoring.TimerFactoryForTest = null;
                PerformanceMonitoring.ClockForTest = null;
                Settings.UseTransientStoreForCurrentProcess();
            }
        }
        private static void MonitoringPendingAndTransportStatus()
        {
            using (var f = new MonitoringFixture())
            {
                string off = PerformanceMonitoring.Status;
                MonitoringPreferences.TrySetEnabled(true);
                MonitoringEq(false, PerformanceMonitoring.Status == off);
                PerformanceMonitoring.Start();
                MonitoringEq(false, PerformanceMonitoring.Status == off);
                MonitoringEq(0, f.Bridge.Reads);
                RtssFrameSample target = f.Bridge.Target;
                f.Bridge.Target = null; f.Bridge.Status = "RTSS renderer not connected";
                f.Timers[0].Fire();
                MonitoringEq(f.Bridge.Status, PerformanceMonitoring.Status);
                MonitoringEq(f.Bridge.Status, PerformanceMonitoring.Current.Status);
                target.ViewportWidth = 1920; target.ViewportHeight = 1080;
                f.Bridge.Target = target; f.Bridge.Status = "";
                f.Timers[0].FireUntilPublication();
                MonitoringEq(1920, PerformanceMonitoring.Current.ViewportWidth);
                MonitoringEq(1080, PerformanceMonitoring.Current.ViewportHeight);
                MonitoringEq((double?)100, PerformanceMonitoring.Current.Fps);
                f.Bridge.Status = "RTSS overlay slots full";
                MonitoringEq(f.Bridge.Status, PerformanceMonitoring.Status);
                f.Bridge.Status = "";
                MonitoringEq(PerformanceMonitoring.Current.Status, PerformanceMonitoring.Status);
                MonitoringPreferences.TrySetEnabled(false);
                MonitoringEq(off, PerformanceMonitoring.Status);
            }
        }
        private static void MonitoringOverlayVisibility()
        {
            using (var f = new MonitoringFixture())
            {
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true);
                f.Timers[0].FireUntilPublication();
                int publishes = f.Bridge.Publishes, closes = f.Bridge.Closes, sensors = f.Sensors.Reads;
                int schedules = f.Timers[0].Schedules;
                MonitoringPreferences.SetOverlayVisible(false);
                MonitoringEq(true, PerformanceMonitoring.HasTimerForTest);
                MonitoringEq(schedules, f.Timers[0].Schedules);
                MonitoringEq(closes, f.Bridge.Closes);
                f.Timers[0].FireUntilPublication();
                MonitoringEq(sensors + 1, f.Sensors.Reads);
                MonitoringEq(publishes, f.Bridge.Publishes);
                MonitoringEq((double?)100, PerformanceMonitoring.Current.Fps);
                MonitoringPreferences.TrySetBackgroundOpacity(50);
                MonitoringEq(publishes, f.Bridge.Publishes);
                MonitoringPreferences.SetOverlayVisible(true);
                MonitoringEq(publishes + 1, f.Bridge.Publishes);
                MonitoringEq(closes, f.Bridge.Closes);
                MonitoringEq(1, f.Timers.Count);
                MonitoringEq(1000, f.Timers[0].Due);
            }
        }
        private static void MonitoringSensorInitializationFailure()
        {
            using (var f = new MonitoringFixture())
            {
                int attempts = 0;
                PerformanceMonitoring.SensorsFactoryForTest = delegate {
                    attempts++;
                    if (attempts == 1) throw new InvalidOperationException("mock sensor initialization failure");
                    return f.Sensors;
                };
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true);
                f.Timers[0].FireUntilPublication();
                MonitoringEq(1, attempts); MonitoringEq((double?)100, PerformanceMonitoring.Current.Fps);
                MonitoringEq((double?)null, PerformanceMonitoring.Current.GpuUsage);
                MonitoringEq(1234, f.Bridge.Published.Pid);
                for (int i = 0; i < 5; i++) f.Timers[0].FireUntilPublication();
                MonitoringEq(1, attempts); MonitoringEq(0, f.Sensors.Reads);
                MonitoringEq((double?)100, PerformanceMonitoring.Current.Fps);
                f.Clock += PerformanceMonitoring.SensorInitializationRetryMs;
                f.Timers[0].FireUntilPublication();
                MonitoringEq(2, attempts); MonitoringEq(1, f.Sensors.Reads);
                MonitoringEq((double?)100, PerformanceMonitoring.Current.Fps);
                MonitoringEq((double?)50, PerformanceMonitoring.Current.GpuUsage);
            }
        }
        private static void MonitoringLaunchHints()
        {
            long now = 1000; bool alive = true; int validations = 0;
            var profile = new GameProfile { Id = "fixture", Root = @"C:\Games\Example", ExecutablePath = @"C:\Games\Example\Game.exe" };
            var entry = new ProcEntry { Pid = 55, Creation = 900, Session = 2, Name = "Game", Path = profile.ExecutablePath };
            var hint = new MonitoringGamePreparation(delegate { return now; }, delegate(int pid, long birth, string path) {
                validations++; return alive && pid == entry.Pid && birth == entry.Creation && path == entry.Path;
            });
            Func<string, GameProfile> lookup = delegate(string id) { return id == profile.Id ? profile : null; };
            MonitoringEq(false, hint.Observe(profile, entry, 1));
            MonitoringEq(false, MonitoringGamePreparation.Eligible(profile, @"C:\Games\Example\Updater.exe"));
            MonitoringEq(0, validations);
            MonitoringEq(true, hint.Observe(profile, entry, 2));
            MonitoringEq(true, hint.IsPending(lookup));
            now += 30000;
            MonitoringEq(false, hint.Observe(profile, entry, 2));
            MonitoringEq(true, hint.IsPending(lookup));
            now += 30000;
            MonitoringEq(false, hint.IsPending(lookup));
            MonitoringEq(false, hint.Observe(profile, entry, 2));
            entry.Creation++;
            MonitoringEq(true, hint.Observe(profile, entry, 2));
            hint.Exited(entry.Pid, entry.Creation - 1);
            MonitoringEq(true, hint.IsPending(lookup));
            hint.Exited(entry.Pid, entry.Creation);
            MonitoringEq(false, hint.IsPending(lookup));
            entry.Creation++;
            MonitoringEq(true, hint.Observe(profile, entry, 2));
            alive = false; MonitoringEq(false, hint.IsPending(lookup));
            alive = true; MonitoringEq(false, hint.IsPending(lookup));
            entry.Creation++;
            MonitoringEq(true, hint.Observe(profile, entry, 2));
            MonitoringEq(false, hint.IsPending(delegate { return null; }));
            entry.Creation++;
            MonitoringEq(true, hint.Observe(profile, entry, 2));
            profile.ExecutablePath = @"C:\Other\Game.exe";
            MonitoringEq(false, hint.IsPending(lookup));
            MonitoringEq(false, MonitoringGamePreparation.Eligible(profile, entry.Path));
            foreach (string name in new[] { "steam", "steamwebhelper", "WeGame", "WeGameApp", "LeagueClient", "LeagueClientUx",
                "RiotClientServices", "EpicGamesLauncher", "javaw", "RTSS", "UnityCrashHandler64", "EasyAntiCheat" })
            {
                profile.ExecutablePath = @"C:\Games\" + name + ".exe";
                MonitoringEq(false, MonitoringGamePreparation.Eligible(profile, profile.ExecutablePath));
            }
            profile.ExecutablePath = @"C:\Games\League of Legends.exe";
            MonitoringEq(true, MonitoringGamePreparation.Eligible(profile, profile.ExecutablePath));
            profile.ExecutablePath = @"C:\Games\Client-Win64-Shipping.exe";
            MonitoringEq(true, MonitoringGamePreparation.Eligible(profile, profile.ExecutablePath));
        }
        private static void MonitoringStartupPreparation()
        {
            using (var f = new MonitoringFixture())
            {
                f.Bridge.GameActive = false; f.Bridge.GamePreparing = true;
                PerformanceMonitoring.Start(); PerformanceMonitoring.WakeForGameStart();
                MonitoringEq(0, f.Timers.Count);
                MonitoringPreferences.TrySetEnabled(true);
                int closes = f.Bridge.Closes;
                f.Timers[0].Fire();
                MonitoringEq(1, f.Bridge.Preparations); MonitoringEq(0, f.Bridge.Reads);
                MonitoringEq(0, f.SensorFactories); MonitoringEq(0, f.Bridge.Publishes);
                MonitoringEq(closes, f.Bridge.Closes); MonitoringEq(200, f.Timers[0].Due);
                for (int index = 0; index < 40; index++) f.Timers[0].Fire();
                MonitoringEq(1000, f.Timers[0].Due);
                MonitoringEq(0, f.SensorFactories); MonitoringEq(0, f.Bridge.Reads);
                PerformanceMonitoring.WakeForGameStart(); f.Timers[0].Fire();
                MonitoringEq(1000, f.Timers[0].Due);
                f.Bridge.GamePreparing = false; f.Timers[0].Fire();
                MonitoringEq(closes + 1, f.Bridge.Closes);
                f.Bridge.GamePreparing = true; PerformanceMonitoring.WakeForGameStart();
                MonitoringEq(0, f.Timers[0].Due); f.Timers[0].Fire();
                MonitoringEq(200, f.Timers[0].Due);
                closes = f.Bridge.Closes;
                f.Bridge.GameActive = true; f.Bridge.GamePreparing = false;
                PerformanceMonitoring.WakeForGameStart(); f.Timers[0].Fire();
                MonitoringEq(closes, f.Bridge.Closes); MonitoringEq(1234, PerformanceMonitoring.Current.Pid);
                MonitoringEq(1000, f.Timers[0].Due);
                RtssFrameSample target = f.Bridge.Target;
                f.Bridge.Target = null; f.Timers[0].Fire();
                MonitoringEq(closes, f.Bridge.Closes); MonitoringEq(1000, f.Timers[0].Due);
                f.Bridge.Target = target; f.Timers[0].Fire();
                MonitoringEq(closes, f.Bridge.Closes);
                f.Bridge.GameActive = false; f.Timers[0].Fire();
                MonitoringEq(closes + 1, f.Bridge.Closes); MonitoringEq(0, PerformanceMonitoring.Current.Pid);
                MonitoringPreferences.TrySetEnabled(false);
                int preparations = f.Bridge.Preparations;
                f.Bridge.GamePreparing = true; PerformanceMonitoring.WakeForGameStart(); f.Timers[0].Callback();
                MonitoringEq(preparations, f.Bridge.Preparations);
            }
        }
        private static void MonitoringImmediateFrame()
        {
            using (var f = new MonitoringFixture())
            {
                f.Sensors.BeforeRead = delegate {
                    MonitoringEq(1, f.Bridge.Publishes);
                    MonitoringEq((double?)100, f.Bridge.Published.Fps);
                    MonitoringEq((double?)null, f.Bridge.Published.GpuTemperature);
                    f.Clock += 2500;
                };
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true); f.Timers[0].Fire();
                MonitoringEq(2, f.Bridge.Publishes); MonitoringEq((double?)60, PerformanceMonitoring.Current.GpuTemperature);
                f.Sensors.BeforeRead = null; f.Timers[0].Fire();
                MonitoringEq(3, f.Bridge.Publishes);
                f.Sensors.BeforeRead = delegate { throw new InvalidOperationException("Hardware fixture failure"); };
                f.Timers[0].Fire();
                MonitoringEq((double?)100, PerformanceMonitoring.Current.Fps);
                MonitoringEq((double?)null, PerformanceMonitoring.Current.GpuTemperature);
                MonitoringEq(1234, PerformanceMonitoring.Current.Pid);
                f.Sensors.BeforeRead = null; f.Timers[0].Fire();
                MonitoringEq((double?)60, PerformanceMonitoring.Current.GpuTemperature);
            }
            using (var f = new MonitoringFixture())
            {
                f.Sensors.BeforeRead = delegate {
                    f.Sensors.BeforeRead = null;
                    PerformanceMonitoring.WakeForGameStart();
                    var callback = new Thread(delegate() { f.Timers[0].Callback(); });
                    callback.Start(); MonitoringEq(true, callback.Join(3000));
                };
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true); f.Timers[0].Fire();
                MonitoringEq(1, f.Timers[0].Due); MonitoringEq(1, f.Sensors.Reads);
                f.Timers[0].Fire();
                MonitoringEq(1, f.Sensors.Reads); MonitoringEq(999, f.Timers[0].Due);
            }
        }
        private static void MonitoringSessionLifetime()
        {
            using (var f = new MonitoringFixture())
            {
                f.Bridge.GameActive = false;
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true); f.Timers[0].Fire();
                MonitoringEq(0, f.Bridge.Reads); MonitoringEq(0, f.SensorFactories);
                MonitoringEq(0, PerformanceMonitoring.Current.Pid); MonitoringEq(PerformanceMonitoring.IdlePollingMs, f.Timers[0].Due);
                f.Bridge.GameActive = true; f.Timers[0].FireUntilPublication();
                MonitoringEq(1234, PerformanceMonitoring.Current.Pid); MonitoringEq(1, f.SensorFactories);
                var target = f.Bridge.Target;
                int closes = f.Bridge.Closes, disposed = f.Sensors.Disposals;
                f.Bridge.Target = null; f.Timers[0].FireUntilPublication();
                MonitoringEq(closes, f.Bridge.Closes); MonitoringEq(disposed, f.Sensors.Disposals);
                f.Bridge.Target = target; f.Timers[0].FireUntilPublication();
                MonitoringEq(1234, PerformanceMonitoring.Current.Pid);
                f.Bridge.GameActive = false; f.Timers[0].FireUntilPublication();
                MonitoringEq(closes + 1, f.Bridge.Closes); MonitoringEq(disposed + 1, f.Sensors.Disposals);
                MonitoringEq(0, PerformanceMonitoring.Current.Pid);
                int reads = f.Bridge.Reads;
                f.Timers[0].FireUntilPublication();
                MonitoringEq(reads, f.Bridge.Reads); MonitoringEq(disposed + 1, f.Sensors.Disposals);
                f.Bridge.GameActive = true; f.Timers[0].FireUntilPublication();
                MonitoringEq(1234, PerformanceMonitoring.Current.Pid); MonitoringEq(2, f.SensorFactories);
                closes = f.Bridge.Closes;
                MonitoringPreferences.TrySetEnabled(false);
                MonitoringEq(closes + 1, f.Bridge.Closes); MonitoringEq(false, PerformanceMonitoring.HasTimerForTest);
                MonitoringEq(disposed + 2, f.Sensors.Disposals);
            }
        }

        private static void MonitoringGuardedSessionExit()
        {
            foreach (MonitoringMetrics selected in new[] { MonitoringMetrics.Default, MonitoringMetrics.Default | MonitoringMetrics.FrameStatistics })
            using (var f = new MonitoringFixture())
            {
                MonitoringPreferences.TrySetMetrics(selected);
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true); f.Timers[0].FireUntilPublication();
                MonitoringEq(1234, PerformanceMonitoring.Current.Pid);
                MonitoringEq(99L, PerformanceMonitoring.Current.Creation);
                MonitoringEq((double?)100, PerformanceMonitoring.Current.Fps);
                int reads = f.Sensors.Reads, publishes = f.Bridge.Publishes, clears = f.Bridge.Clears;
                long lastSample = f.Clock;
                int bridgeReads = f.Bridge.Reads;
                f.Bridge.Target = null; f.Timers[0].Fire();
                MonitoringEq(lastSample + ((selected & MonitoringMetrics.FrameStatistics) != 0 ? 250 : 1000), f.Clock);
                MonitoringEq(bridgeReads + 1, f.Bridge.Reads);
                MonitoringEq(1000, f.Timers[0].Due);
                MonitoringEq(0, PerformanceMonitoring.Current.Pid);
                MonitoringEq(0L, PerformanceMonitoring.Current.Creation);
                MonitoringEq((double?)null, PerformanceMonitoring.Current.Fps);
                MonitoringEq(reads, f.Sensors.Reads);
                MonitoringEq(publishes, f.Bridge.Publishes);
                MonitoringEq(clears + 1, f.Bridge.Clears);
                MonitoringEq(true, f.Sensors.Resets > 0);
                MonitoringPreferences.TrySetBackgroundOpacity(75);
                MonitoringEq(publishes, f.Bridge.Publishes);
            }
        }
        private static void MonitoringLifecycle()
        {
            using (var f = new MonitoringFixture())
            {
                PerformanceMonitoring.Start(); PerformanceMonitoring.Start();
                MonitoringEq(0, f.Timers.Count); MonitoringEq(0, f.SensorFactories);
                MonitoringEq(0, f.Bridge.Reads); MonitoringEq(0, f.Sensors.Reads);
                MonitoringEq(false, PerformanceMonitoring.HasTimerForTest);
                MonitoringPreferences.TrySetEnabled(true);
                MonitoringEq(1, f.Timers.Count); MonitoringEq(0, f.SensorFactories);
                f.Timers[0].FireUntilPublication();
                MonitoringEq(1, f.SensorFactories); MonitoringEq(2, f.Bridge.Publishes);
                MonitoringEq((double?)100, PerformanceMonitoring.Current.Fps);
                MonitoringEq((double?)10, PerformanceMonitoring.Current.FrameTimeMs);
                MonitoringEq((double?)50, PerformanceMonitoring.Current.GpuUsage);
                MonitoringEq((double?)60, PerformanceMonitoring.Current.GpuTemperature);
                MonitoringEq((double?)null, PerformanceMonitoring.Current.CpuUsage);
                MonitoringEq((double?)null, PerformanceMonitoring.Current.RamMiB);
                MonitoringEq(1000, f.Timers[0].Due);
                f.Timers[0].FireUntilPublication(); MonitoringEq(1, f.SensorFactories);
                MonitoringEq(true, MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Default | MonitoringMetrics.CpuUsage | MonitoringMetrics.Low1));
                f.Timers[0].FireUntilPublication(); MonitoringEq(2, f.SensorFactories);
                MonitoringEq((double?)25, PerformanceMonitoring.Current.CpuUsage);
                MonitoringEq((double?)null, PerformanceMonitoring.Current.Low1);
                f.Bridge.Target.Fps = double.NaN; f.Sensors.Values.GpuUsage = -1;
                f.Timers[0].FireUntilPublication(); MonitoringEq((double?)null, PerformanceMonitoring.Current.Fps);
                MonitoringEq((double?)null, PerformanceMonitoring.Current.GpuUsage);
                f.Bridge.Target.Creation = 0;
                int reads = f.Sensors.Reads; f.Timers[0].FireUntilPublication();
                MonitoringEq(reads, f.Sensors.Reads); MonitoringEq(0, PerformanceMonitoring.Current.Pid);
                f.Bridge.Target.Creation = 100; f.Sensors.Valid = false; f.Timers[0].FireUntilPublication();
                MonitoringEq(0, PerformanceMonitoring.Current.Pid);
                f.Sensors.Valid = true;
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Fps);
                Settings.SaveStr(MonitoringPreferences.RetiredIntervalKey, "2000");
                MonitoringEq(1, f.Timers.Count); f.Timers[0].FireUntilPublication();
                MonitoringEq((double?)null, PerformanceMonitoring.Current.RamMiB);
                MonitoringEq(1000, f.Timers[0].Due);
                MonitoringPreferences.TrySetEnabled(false);
                MonitoringEq(true, f.Timers[0].Disposed); MonitoringEq(false, PerformanceMonitoring.HasTimerForTest);
                MonitoringEq(0, PerformanceMonitoring.Current.Pid);
                int readsAfter = f.Bridge.Reads; f.Timers[0].Callback(); MonitoringEq(readsAfter, f.Bridge.Reads);
                MonitoringPreferences.TrySetEnabled(true); MonitoringEq(2, f.Timers.Count);
                Settings.Save(MonitoringPreferences.EnabledKey, false);
                f.Timers[1].FireUntilPublication(); MonitoringEq(true, f.Timers[1].Disposed);
                MonitoringEq(false, PerformanceMonitoring.HasTimerForTest);
                PerformanceMonitoring.Stop(); MonitoringEq(true, f.Timers[1].Disposed);
                MonitoringPreferences.TrySetEnabled(true); MonitoringEq(2, f.Timers.Count);
            }
        }
        private static void MonitoringExpandedLifecycle()
        {
            using (var f = new MonitoringFixture())
            {
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.FrameTimeDeviation);
                RtssFrameSample target = f.Bridge.Target;
                target.FrameTimes = MonitoringFrameTimes(3000, 10);
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true); f.Timers[0].FireUntilPublication();
                MonitoringNear(0, PerformanceMonitoring.Current.FrameTimeDeviation);
                MonitoringEq((double?)null, PerformanceMonitoring.Current.AverageFps);
                MonitoringEq((double?)null, PerformanceMonitoring.Current.Low1);
                MonitoringEq((double?)null, PerformanceMonitoring.Current.Low01);
                target.FrameTimes = new double[0]; f.Timers[0].FireUntilPublication();
                MonitoringNear(0, PerformanceMonitoring.Current.FrameTimeDeviation);

                target.Creation++; target.FrameTimes = new[] { 10.0 }; target.FrameSequence++;
                f.Timers[0].FireUntilPublication(); MonitoringEq((double?)null, PerformanceMonitoring.Current.FrameTimeDeviation);
                target.FrameTimes = MonitoringFrameTimes(3000, 10); target.FrameSequence++; f.Timers[0].FireUntilPublication();
                MonitoringNear(0, PerformanceMonitoring.Current.FrameTimeDeviation);
                target.Pid++; target.FrameTimes = new double[0]; f.Timers[0].FireUntilPublication();
                MonitoringEq((double?)null, PerformanceMonitoring.Current.FrameTimeDeviation);
                target.FrameTimes = MonitoringFrameTimes(3000, 10); target.FrameSequence++; f.Timers[0].FireUntilPublication();
                MonitoringNear(0, PerformanceMonitoring.Current.FrameTimeDeviation);
                target.FrameTimes = null; f.Timers[0].FireUntilPublication();
                MonitoringEq((double?)null, PerformanceMonitoring.Current.FrameTimeDeviation);

                target.FrameTimes = MonitoringFrameTimes(3000, 10); target.FrameSequence++; f.Timers[0].FireUntilPublication();
                f.Bridge.Target = null; f.Timers[0].FireUntilPublication(); MonitoringEq(0, PerformanceMonitoring.Current.Pid);
                f.Bridge.Target = target; target.FrameTimes = new double[0]; f.Timers[0].FireUntilPublication();
                MonitoringEq((double?)null, PerformanceMonitoring.Current.FrameTimeDeviation);
                target.FrameTimes = MonitoringFrameTimes(3000, 10); target.FrameSequence++; f.Timers[0].FireUntilPublication();
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.FrameTimeDeviation | MonitoringMetrics.CpuUsage);
                f.Sensors.Valid = false; f.Timers[0].FireUntilPublication(); MonitoringEq(0, PerformanceMonitoring.Current.Pid);
                f.Sensors.Valid = true; target.FrameTimes = new double[0]; f.Timers[0].FireUntilPublication();
                MonitoringEq((double?)null, PerformanceMonitoring.Current.FrameTimeDeviation);

                target.FrameTimes = MonitoringFrameTimes(3000, 10); target.FrameSequence++; f.Timers[0].FireUntilPublication();
                f.Bridge.BeforeRead = delegate { f.Bridge.BeforeRead = null; throw new InvalidOperationException("Interrupted bridge read"); };
                f.Timers[0].FireUntilPublication(); MonitoringEq(0, PerformanceMonitoring.Current.Pid);
                target.FrameTimes = new double[0]; f.Timers[0].FireUntilPublication();
                MonitoringEq((double?)null, PerformanceMonitoring.Current.FrameTimeDeviation);

                target.FrameTimes = MonitoringFrameTimes(3000, 10); target.FrameSequence++; f.Timers[0].FireUntilPublication();
                int publishes = f.Bridge.Publishes;
                f.Sensors.BeforeRead = delegate {
                    f.Sensors.BeforeRead = null;
                    MonitoringPreferences.TrySetMetrics(MonitoringMetrics.FrameTimeDeviation | MonitoringMetrics.Fps);
                };
                int readsBeforeChange = f.Sensors.Reads;
                for (int index = 0; index < 5 && f.Sensors.Reads == readsBeforeChange; index++) f.Timers[0].Fire();
                MonitoringEq(readsBeforeChange + 1, f.Sensors.Reads);
                MonitoringEq((Action)null, f.Sensors.BeforeRead);
                MonitoringEq(publishes, f.Bridge.Publishes);
                target.FrameTimes = new double[0]; f.Timers[0].FireUntilPublication();
                MonitoringEq((double?)null, PerformanceMonitoring.Current.FrameTimeDeviation);
                int reads = f.Bridge.Reads, sensorReads = f.Sensors.Reads;
                MonitoringPreferences.TrySetEnabled(false); f.Timers[0].Callback();
                MonitoringEq(reads, f.Bridge.Reads); MonitoringEq(sensorReads, f.Sensors.Reads);
                MonitoringEq(false, PerformanceMonitoring.HasTimerForTest);
                MonitoringPreferences.TrySetEnabled(true); f.Timers[1].FireUntilPublication();
                MonitoringEq((double?)null, PerformanceMonitoring.Current.FrameTimeDeviation);
            }
        }
        private static void MonitoringRtssStatisticsLifecycle()
        {
            foreach (MonitoringMetrics selected in new[] { MonitoringMetrics.AverageFps, MonitoringMetrics.Low1, MonitoringMetrics.Low01 })
            using (var f = new MonitoringFixture())
            {
                MonitoringPreferences.TrySetMetrics(selected);
                f.Bridge.Target.BenchmarkActive = true; f.Bridge.Target.AverageFps = 133.7; f.Bridge.Target.Low1 = 72.4; f.Bridge.Target.Low01 = 51.8;
                f.Bridge.Target.FrameTimes = MonitoringFrameTimes(6000, 10);
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true); f.Timers[0].FireUntilPublication();
                MonitoringSnapshot value = PerformanceMonitoring.Current;
                MonitoringEq(selected == MonitoringMetrics.AverageFps ? (double?)133.7 : null, value.AverageFps);
                MonitoringEq(selected == MonitoringMetrics.Low1 ? (double?)72.4 : null, value.Low1);
                MonitoringEq(selected == MonitoringMetrics.Low01 ? (double?)51.8 : null, value.Low01);
                MonitoringEq((double?)null, value.FrameTimeDeviation);
                int reads = f.Sensors.Reads, schedules = f.Timers[0].Schedules;
                MonitoringPreferences.TrySetPosition(50, 70);
                MonitoringEq(reads, f.Sensors.Reads); MonitoringEq(schedules, f.Timers[0].Schedules);
                f.Bridge.Target.BenchmarkActive = false; f.Bridge.Target.AverageFps = f.Bridge.Target.Low1 = f.Bridge.Target.Low01 = null;
                f.Timers[0].FireUntilPublication(); value = PerformanceMonitoring.Current;
                MonitoringEq(selected == MonitoringMetrics.AverageFps ? (double?)100 : null, value.AverageFps);
                MonitoringEq(selected == MonitoringMetrics.Low1 ? (double?)100 : null, value.Low1);
                MonitoringEq(selected == MonitoringMetrics.Low01 ? (double?)100 : null, value.Low01);
                f.Bridge.Target.FrameTimes = null;
                f.Timers[0].FireUntilPublication(); value = PerformanceMonitoring.Current;
                MonitoringEq((double?)null, value.AverageFps); MonitoringEq((double?)null, value.Low1);
                MonitoringEq((double?)null, value.Low01);
                f.Bridge.Target.FrameTimes = MonitoringFrameTimes(1000, 10); f.Bridge.Target.FrameSequence++;
                f.Timers[0].FireUntilPublication(); value = PerformanceMonitoring.Current;
                MonitoringEq(selected == MonitoringMetrics.Low1 ? (double?)100 : null, value.Low1);
                MonitoringEq(selected == MonitoringMetrics.Low01 ? (double?)100 : null, value.Low01);
                f.Bridge.Target.Creation++; f.Bridge.Target.FrameTimes = new double[0];
                f.Timers[0].FireUntilPublication(); value = PerformanceMonitoring.Current;
                MonitoringEq((double?)null, value.AverageFps); MonitoringEq((double?)null, value.Low1);
                MonitoringEq((double?)null, value.Low01);
                f.Bridge.Target.AverageFps = double.NaN; f.Bridge.Target.Low1 = -1; f.Bridge.Target.Low01 = 100001;
                f.Timers[0].FireUntilPublication(); value = PerformanceMonitoring.Current;
                MonitoringEq((double?)null, value.AverageFps); MonitoringEq((double?)null, value.Low1);
                MonitoringEq((double?)null, value.Low01);
                int bridgeReads = f.Bridge.Reads, sensorReads = f.Sensors.Reads;
                MonitoringPreferences.TrySetEnabled(false); f.Timers[0].Callback();
                MonitoringEq(bridgeReads, f.Bridge.Reads); MonitoringEq(sensorReads, f.Sensors.Reads);
            }
        }
        private static void MonitoringStatisticsSourceIsolation()
        {
            using (var f = new MonitoringFixture())
            {
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.FrameStatistics);
                RtssFrameSample target = f.Bridge.Target;
                target.FrameTimes = MonitoringFrameTimes(3000, 10);
                target.BenchmarkActive = true;
                target.AverageFps = 133.7; target.Low1 = 72.4; target.Low01 = null;
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true); f.Timers[0].FireUntilPublication();
                MonitoringSnapshot value = PerformanceMonitoring.Current;
                MonitoringNear(133.7, value.AverageFps); MonitoringNear(72.4, value.Low1);
                MonitoringEq((double?)null, value.Low01);
                MonitoringNear(0, value.FrameTimeDeviation);

                target.AverageFps = double.NaN; target.Low1 = null; target.Low01 = -1;
                f.Timers[0].FireUntilPublication(); value = PerformanceMonitoring.Current;
                MonitoringEq((double?)null, value.AverageFps); MonitoringEq((double?)null, value.Low1);
                MonitoringEq((double?)null, value.Low01);

                target.BenchmarkActive = false;
                target.AverageFps = 150; target.Low1 = 70; target.Low01 = 40;
                f.Timers[0].FireUntilPublication(); value = PerformanceMonitoring.Current;
                MonitoringNear(100, value.AverageFps); MonitoringNear(100, value.Low1); MonitoringNear(100, value.Low01);

                target.BenchmarkActive = true;
                target.AverageFps = target.Low1 = null; target.Low01 = 51.8;
                f.Timers[0].FireUntilPublication(); value = PerformanceMonitoring.Current;
                MonitoringEq((double?)null, value.AverageFps); MonitoringEq((double?)null, value.Low1);
                MonitoringNear(51.8, value.Low01);
                target.FrameTimes = null;
                f.Timers[0].FireUntilPublication(); value = PerformanceMonitoring.Current;
                MonitoringNear(51.8, value.Low01); MonitoringEq((double?)null, value.FrameTimeDeviation);
            }
        }
        private static void MonitoringPollingCadence()
        {
            using (var f = new MonitoringFixture())
            {
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.FrameStatistics | MonitoringMetrics.GpuUsage);
                f.Bridge.Target.FrameTimes = MonitoringFrameTimes(3000, 10);
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true);
                MonitoringEq(0, f.Timers[0].Due); f.Timers[0].Fire();
                MonitoringEq(1, f.Bridge.Reads); MonitoringEq(1, f.Sensors.Reads); MonitoringEq(1, f.Bridge.Publishes);
                MonitoringEq(250, f.Timers[0].Due);
                for (int index = 0; index < 3; index++)
                {
                    f.Timers[0].Fire();
                    MonitoringEq(index + 2, f.Bridge.Reads);
                    MonitoringEq(1, f.Sensors.Reads); MonitoringEq(1, f.Bridge.Publishes);
                    MonitoringEq(250, f.Timers[0].Due);
                }
                f.Timers[0].Fire();
                MonitoringEq(5, f.Bridge.Reads); MonitoringEq(2, f.Sensors.Reads); MonitoringEq(2, f.Bridge.Publishes);
                MonitoringEq(101000L, f.Clock);
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Fps | MonitoringMetrics.GpuUsage);
                f.Timers[0].Fire(); MonitoringEq(1000, f.Timers[0].Due);
                int bridgeReads = f.Bridge.Reads, sensorReads = f.Sensors.Reads, publishes = f.Bridge.Publishes;
                f.Timers[0].Fire();
                MonitoringEq(bridgeReads + 1, f.Bridge.Reads); MonitoringEq(sensorReads + 1, f.Sensors.Reads);
                MonitoringEq(publishes + 1, f.Bridge.Publishes); MonitoringEq(1000, f.Timers[0].Due);
            }
            using (var f = new MonitoringFixture())
            {
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.FrameStatistics);
                f.Bridge.Target.FrameTimes = MonitoringFrameTimes(25, 10);
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true);
                for (int index = 0; index <= 12; index++)
                {
                    f.Bridge.Target.FrameSequence++;
                    f.Timers[0].Fire();
                    MonitoringEq(index + 1, f.Bridge.Reads);
                    MonitoringEq(0, f.Sensors.Reads);
                    MonitoringEq(index / 4 + 1, f.Bridge.Publishes);
                    if (index < 12) MonitoringEq((double?)null, PerformanceMonitoring.Current.AverageFps);
                }
                MonitoringNear(100, PerformanceMonitoring.Current.AverageFps);
                MonitoringNear(100, PerformanceMonitoring.Current.Low1);
                MonitoringNear(0, PerformanceMonitoring.Current.FrameTimeDeviation);
                MonitoringEq((double?)null, PerformanceMonitoring.Current.Low01);
                MonitoringEq(103000L, f.Clock);
            }
            using (var f = new MonitoringFixture())
            {
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Low1);
                RtssFrameSample target = f.Bridge.Target;
                f.Bridge.Target = null;
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true); f.Timers[0].Fire();
                MonitoringEq(200, f.Timers[0].Due); MonitoringEq(0, f.Sensors.Reads);
                f.Timers[0].Fire(); MonitoringEq(2, f.Bridge.Reads); MonitoringEq(0, f.Sensors.Reads);
                MonitoringEq(0, f.Bridge.Publishes); MonitoringEq(200, f.Timers[0].Due);
                f.Bridge.Target = target; f.Timers[0].Fire();
                MonitoringEq(250, f.Timers[0].Due); MonitoringEq(0, f.Sensors.Reads);
            }
            using (var f = new MonitoringFixture())
            {
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Low1);
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true);
                f.Bridge.BeforeRead = delegate { f.Bridge.BeforeRead = null; f.Clock += 1100; };
                f.Timers[0].Fire();
                MonitoringEq(1, f.Bridge.Reads); MonitoringEq(0, f.Sensors.Reads);
                MonitoringEq(250, f.Timers[0].Due);
                f.Timers[0].Fire();
                MonitoringEq(101350L, f.Clock); MonitoringEq(2, f.Bridge.Reads);
                MonitoringEq(0, f.Sensors.Reads); MonitoringEq(2, f.Bridge.Publishes);
                MonitoringEq(250, f.Timers[0].Due);
            }
            foreach (MonitoringMetrics metrics in new[] { MonitoringMetrics.Fps, MonitoringMetrics.Low1 | MonitoringMetrics.GpuUsage })
            using (var f = new MonitoringFixture())
            {
                MonitoringPreferences.TrySetMetrics(metrics);
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true);
                f.Bridge.BeforeRead = delegate { f.Clock += 37; };
                f.Sensors.BeforeRead = delegate { f.Clock += 13; };
                f.Timers[0].Fire();
                int period = (metrics & MonitoringMetrics.FrameStatistics) != 0 ? 250 : 1000;
                bool nativeSensors = PerformanceMonitoring.NeedsNativeSensors(metrics, null);
                MonitoringEq(period - 37 - (nativeSensors ? 13 : 0), f.Timers[0].Due);
                long firstSample = f.Clock;
                int sensors = f.Sensors.Reads;
                f.Timers[0].Fire();
                if (period == 250)
                {
                    MonitoringEq(firstSample + 237, f.Clock);
                    MonitoringEq(sensors, f.Sensors.Reads); MonitoringEq(213, f.Timers[0].Due);
                    f.Timers[0].Fire(); f.Timers[0].Fire(); f.Timers[0].Fire();
                }
                MonitoringEq(firstSample + 1000, f.Clock);
                MonitoringEq(sensors + (nativeSensors ? 1 : 0), f.Sensors.Reads);
            }
        }
        private static void MonitoringLeanSampling()
        {
            using (var f = new MonitoringFixture())
            {
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Fps | MonitoringMetrics.FrameTime | MonitoringMetrics.FrameStatistics);
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true);
                for (int i = 0; i < 20; i++) f.Timers[0].Fire();
                MonitoringEq(0, f.SensorFactories); MonitoringEq(0, f.Sensors.Reads);
                MonitoringEq(100.0, PerformanceMonitoring.Current.Fps.Value);
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Fps | MonitoringMetrics.GameRam);
                f.Bridge.Target.GameRamMiB = 512; f.Timers[0].FireUntilPublication();
                MonitoringEq(512.0, PerformanceMonitoring.Current.GameRamMiB.Value);
                MonitoringEq(0, f.SensorFactories);
                f.Bridge.Target.GameRamMiB = null; f.Timers[0].FireUntilPublication();
                MonitoringEq(1, f.SensorFactories); MonitoringEq(1, f.Sensors.Reads);
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Fps | MonitoringMetrics.GpuTemperature);
                f.Timers[0].FireUntilPublication();
                int disposals = f.Sensors.Disposals, reads = f.Sensors.Reads;
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Fps);
                f.Timers[0].FireUntilPublication();
                MonitoringEq(disposals + 1, f.Sensors.Disposals);
                MonitoringEq(reads, f.Sensors.Reads);
                int factories = f.SensorFactories;
                for (int i = 0; i < 10; i++) f.Timers[0].FireUntilPublication();
                MonitoringEq(factories, f.SensorFactories);
                MonitoringEq(reads, f.Sensors.Reads);
            }
            using (var f = new MonitoringFixture())
            {
                f.Bridge.GameActive = false;
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true); f.Timers[0].Fire();
                MonitoringEq(5000, f.Timers[0].Due);
                f.Bridge.GameActive = true; PerformanceMonitoring.WakeForGameStart();
                MonitoringEq(0, f.Timers[0].Due); f.Timers[0].Fire();
                MonitoringEq(1234, PerformanceMonitoring.Current.Pid);
                MonitoringEq(1, f.Sensors.Reads);
            }
        }
        private static void MonitoringFrameTimeSource()
        {
            using (var f = new MonitoringFixture())
            {
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Fps | MonitoringMetrics.FrameTime);
                RtssFrameSample target = f.Bridge.Target;
                target.Fps = 269; target.FrameTimeMs = .4; target.GpuFrameTimeMs = 11.5;
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true); f.Timers[0].FireUntilPublication();
                MonitoringSnapshot value = PerformanceMonitoring.Current;
                MonitoringNear(269, value.Fps); MonitoringNear(11.5, value.FrameTimeMs);
                MonitoringEq(true, value.FrameTimeFromGpu);
                MonitoringEq(true, Object.ReferenceEquals(value, f.Bridge.Published));

                foreach (double? badGpu in new double?[] { null, 0, -.1, .0009, 10000.001,
                    double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                {
                    target.GpuFrameTimeMs = badGpu; f.Timers[0].FireUntilPublication();
                    value = PerformanceMonitoring.Current;
                    MonitoringNear(.4, value.FrameTimeMs); MonitoringEq(false, value.FrameTimeFromGpu);
                    MonitoringNear(269, value.Fps);
                }
                foreach (double gpu in new[] { .001, 11.5, 10000.0 })
                {
                    target.GpuFrameTimeMs = gpu; f.Timers[0].FireUntilPublication();
                    value = PerformanceMonitoring.Current;
                    MonitoringNear(gpu, value.FrameTimeMs); MonitoringEq(true, value.FrameTimeFromGpu);
                }
                foreach (double? badRaw in new double?[] { null, 0, -.1, .0009, 10000.001,
                    double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                {
                    target.FrameTimeMs = badRaw; target.GpuFrameTimeMs = 11.5;
                    f.Timers[0].FireUntilPublication(); value = PerformanceMonitoring.Current;
                    MonitoringNear(11.5, value.FrameTimeMs); MonitoringEq(true, value.FrameTimeFromGpu);
                    target.GpuFrameTimeMs = null;
                    f.Timers[0].FireUntilPublication(); value = PerformanceMonitoring.Current;
                    MonitoringEq((double?)null, value.FrameTimeMs); MonitoringEq(false, value.FrameTimeFromGpu);
                }
                target.FrameTimeMs = .4; target.GpuFrameTimeMs = 11.5;
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Fps); f.Timers[0].FireUntilPublication();
                value = PerformanceMonitoring.Current;
                MonitoringNear(269, value.Fps); MonitoringEq((double?)null, value.FrameTimeMs);
                MonitoringEq(false, value.FrameTimeFromGpu);
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.FrameTime); f.Timers[0].FireUntilPublication();
                value = PerformanceMonitoring.Current;
                MonitoringEq((double?)null, value.Fps); MonitoringNear(11.5, value.FrameTimeMs);
                MonitoringEq(true, value.FrameTimeFromGpu);
                f.Bridge.Target = null; f.Timers[0].FireUntilPublication();
                value = PerformanceMonitoring.Current;
                MonitoringEq((double?)null, value.FrameTimeMs); MonitoringEq(false, value.FrameTimeFromGpu);
            }
        }
        private static void MonitoringExpandedSensorValues()
        {
            using (var f = new MonitoringFixture())
            {
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.All);
                f.Sensors.Values = new MonitoringSensorReading { GameRamMiB = 1024, RamUsage = 75,
                    VramUsage = 50, GpuMemoryClock = 10500, GpuFanRpm = 0, GpuFanPercent = 150,
                    CpuPower = 125, CpuTemperature = 65 };
                PerformanceMonitoring.Start(); MonitoringEq(0, f.Sensors.Reads);
                MonitoringPreferences.TrySetEnabled(true); f.Timers[0].FireUntilPublication();
                MonitoringSnapshot value = PerformanceMonitoring.Current;
                MonitoringNear(1024, value.GameRamMiB); MonitoringNear(75, value.RamUsage);
                MonitoringNear(50, value.VramUsage); MonitoringNear(10500, value.GpuMemoryClock);
                MonitoringNear(0, value.GpuFanRpm); MonitoringNear(150, value.GpuFanPercent);
                MonitoringNear(125, value.CpuPower); MonitoringNear(65, value.CpuTemperature);
                MonitoringEq((double?)null, value.AverageFps); MonitoringEq((double?)null, value.Low01);
                MonitoringEq((double?)null, value.FrameTimeDeviation);

                f.Bridge.Target.GameRamMiB = 2048.5; f.Sensors.Values.GameRamMiB = 9999;
                f.Timers[0].FireUntilPublication(); MonitoringNear(2048.5, PerformanceMonitoring.Current.GameRamMiB);
                f.Bridge.Target.GameRamMiB = null;

                f.Sensors.Values = new MonitoringSensorReading { GameRamMiB = -1, RamUsage = 101,
                    VramUsage = double.NaN, GpuMemoryClock = double.PositiveInfinity,
                    GpuFanRpm = -1, GpuFanPercent = 501, CpuPower = 5001, CpuTemperature = 151 };
                f.Timers[0].FireUntilPublication(); value = PerformanceMonitoring.Current;
                MonitoringEq((double?)null, value.GameRamMiB); MonitoringEq((double?)null, value.RamUsage);
                MonitoringEq((double?)null, value.VramUsage); MonitoringEq((double?)null, value.GpuMemoryClock);
                MonitoringEq((double?)null, value.GpuFanRpm); MonitoringEq((double?)null, value.GpuFanPercent);
                MonitoringEq((double?)null, value.CpuPower); MonitoringEq((double?)null, value.CpuTemperature);

                f.Sensors.Values = new MonitoringSensorReading { GameRamMiB = 1024, RamUsage = 0,
                    VramUsage = 0, GpuMemoryClock = 0, GpuFanRpm = 0, GpuFanPercent = 0, CpuPower = 0, CpuTemperature = 65 };
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.CpuPower); f.Timers[0].FireUntilPublication();
                value = PerformanceMonitoring.Current; MonitoringNear(0, value.CpuPower);
                MonitoringEq((double?)null, value.CpuTemperature);
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.CpuTemperature); f.Timers[0].FireUntilPublication();
                value = PerformanceMonitoring.Current; MonitoringNear(65, value.CpuTemperature);
                MonitoringEq((double?)null, value.CpuPower);
                foreach (double? temperature in new double?[] { null, 0, -1, 150.1, double.NaN, double.PositiveInfinity })
                {
                    f.Sensors.Values.CpuTemperature = temperature; f.Timers[0].FireUntilPublication();
                    MonitoringEq((double?)null, PerformanceMonitoring.Current.CpuTemperature);
                }
                f.Sensors.Values.CpuTemperature = 150; f.Timers[0].FireUntilPublication();
                MonitoringNear(150, PerformanceMonitoring.Current.CpuTemperature);
                MonitoringEq((double?)null, value.GameRamMiB); MonitoringEq((double?)null, value.RamUsage);
                MonitoringEq((double?)null, value.VramUsage); MonitoringEq((double?)null, value.GpuMemoryClock);
                MonitoringEq((double?)null, value.GpuFanRpm); MonitoringEq((double?)null, value.GpuFanPercent);
            }
        }
        private static void MonitoringRetainedSensorValues()
        {
            using (var f = new MonitoringFixture())
            {
                MonitoringPreferences.TrySetMetrics(MonitoringMetrics.Fps | MonitoringMetrics.CpuUsage
                    | MonitoringMetrics.GpuUsage | MonitoringMetrics.GpuTemperature);
                f.Sensors.Values = new MonitoringSensorReading { CpuUsage = 20, GpuUsage = 40, GpuTemperature = 65,
                    RetainedHardwareMetrics = MonitoringMetrics.All };
                PerformanceMonitoring.Start();
                MonitoringPreferences.TrySetEnabled(true); f.Timers[0].FireUntilPublication();
                MonitoringSnapshot value = PerformanceMonitoring.Current;
                MonitoringEq(MonitoringMetrics.GpuUsage | MonitoringMetrics.GpuTemperature, value.RetainedHardwareMetrics);
                MonitoringNear(100, value.Fps); MonitoringNear(20, value.CpuUsage);
                var cells = MonitoringMetricPresentation.Cells(value, MonitoringPreferences.Metrics);
                MonitoringEq("100", cells[0].Value); MonitoringEq("20%", cells[1].Value);
                MonitoringEq("~40%", cells[2].Value); MonitoringEq("~65 °C", cells[3].Value);
                f.Sensors.Values.GpuUsage = 60; f.Sensors.Values.GpuTemperature = 70;
                f.Sensors.Values.RetainedHardwareMetrics = MonitoringMetrics.None;
                f.Timers[0].FireUntilPublication(); value = PerformanceMonitoring.Current;
                MonitoringEq(MonitoringMetrics.None, value.RetainedHardwareMetrics);
                cells = MonitoringMetricPresentation.Cells(value, MonitoringPreferences.Metrics);
                MonitoringEq("60%", cells[2].Value); MonitoringEq("70 °C", cells[3].Value);
                MonitoringPreferences.TrySetEnabled(false);
                MonitoringEq(MonitoringMetrics.None, PerformanceMonitoring.Current.RetainedHardwareMetrics);
            }
        }
        private static void MonitoringAppearanceRepaint()
        {
            using (var f = new MonitoringFixture())
            {
                PerformanceMonitoring.Start();
                MonitoringEq(true, MonitoringPreferences.TrySetPosition(50, 60));
                MonitoringEq(true, MonitoringPreferences.TrySetBackgroundOpacity(38));
                MonitoringEq(true, MonitoringPreferences.TrySetLayout(MonitoringLayout.TopRight));
                MonitoringEq(0, f.Timers.Count); MonitoringEq(0, f.SensorFactories);
                MonitoringEq(0, f.Bridge.Reads); MonitoringEq(0, f.Bridge.Publishes);
                MonitoringPreferences.TrySetEnabled(true); f.Timers[0].FireUntilPublication();
                MonitoringSnapshot original = PerformanceMonitoring.Current;
                int schedules = f.Timers[0].Schedules, reads = f.Bridge.Reads,
                    sensorReads = f.Sensors.Reads, publishes = f.Bridge.Publishes, disposals = f.Sensors.Disposals;
                for (int index = 0; index < 20; index++)
                    MonitoringEq(true, MonitoringPreferences.TrySetPosition(index * 10, index * -8));
                MonitoringEq(true, MonitoringPreferences.TrySetBackgroundOpacity(0));
                MonitoringEq(true, MonitoringPreferences.TrySetBackgroundOpacity(100));
                MonitoringEq(true, MonitoringPreferences.TrySetLayout(MonitoringLayout.TopBar));
                foreach (int scale in new[] { 60, 100, 200 }) MonitoringEq(true, MonitoringPreferences.TrySetScalePercent(scale));
                MonitoringEq(true, MonitoringPreferences.TryResetAppearance());
                MonitoringEq(publishes + 27, f.Bridge.Publishes);
                MonitoringEq(schedules, f.Timers[0].Schedules); MonitoringEq(1000, f.Timers[0].Due);
                MonitoringEq(reads, f.Bridge.Reads); MonitoringEq(sensorReads, f.Sensors.Reads);
                MonitoringEq(disposals, f.Sensors.Disposals); MonitoringEq(1, f.SensorFactories);
                MonitoringEq(true, Object.ReferenceEquals(original, PerformanceMonitoring.Current));
                MonitoringEq(true, Object.ReferenceEquals(original, f.Bridge.Published));
                MonitoringEq(MonitoringLayout.TopBar, f.Bridge.PublishedLayout);
                var currentField = typeof(PerformanceMonitoring).GetField("current",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                currentField.SetValue(null, new MonitoringSnapshot(original.Pid, original.TargetName, DateTime.UtcNow.AddSeconds(-5), "old", 100));
                publishes = f.Bridge.Publishes;
                MonitoringEq(true, MonitoringPreferences.TrySetPosition(200, 300));
                MonitoringEq(publishes, f.Bridge.Publishes); MonitoringEq(schedules, f.Timers[0].Schedules);
                currentField.SetValue(null, original);
                f.Timers[0].FireUntilPublication(); MonitoringEq(1, f.SensorFactories);
                MonitoringPreferences.TrySetEnabled(false);
                publishes = f.Bridge.Publishes; reads = f.Bridge.Reads; sensorReads = f.Sensors.Reads;
                MonitoringPreferences.TrySetPosition(0, 0); MonitoringPreferences.TrySetBackgroundOpacity(62);
                MonitoringPreferences.TrySetLayout(MonitoringLayout.TopLeft);
                MonitoringEq(publishes, f.Bridge.Publishes); MonitoringEq(reads, f.Bridge.Reads);
                MonitoringEq(sensorReads, f.Sensors.Reads); MonitoringEq(false, PerformanceMonitoring.HasTimerForTest);
                MonitoringEq(1, f.Timers.Count);
                PerformanceMonitoring.Stop();
                MonitoringPreferences.TrySetEnabled(true); MonitoringPreferences.TrySetPosition(100, 100);
                MonitoringPreferences.TrySetLayout(MonitoringLayout.TopRight);
                MonitoringEq(1, f.Timers.Count); MonitoringEq(publishes, f.Bridge.Publishes);
                MonitoringEq(reads, f.Bridge.Reads); MonitoringEq(sensorReads, f.Sensors.Reads);
            }
        }
        private static void MonitoringLayoutDuringSample()
        {
            using (var f = new MonitoringFixture())
            {
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true); f.Timers[0].FireUntilPublication();
                int reads = f.Bridge.Reads, sensorReads = f.Sensors.Reads,
                    schedules = f.Timers[0].Schedules, publishes = f.Bridge.Publishes;
                f.Bridge.BeforeRead = delegate {
                    f.Bridge.BeforeRead = null;
                    MonitoringEq(true, MonitoringPreferences.TrySetLayout(MonitoringLayout.TopRight));
                };
                f.Timers[0].FireUntilPublication();
                MonitoringEq(MonitoringLayout.TopRight, f.Bridge.PublishedLayout);
                MonitoringEq(reads + 1, f.Bridge.Reads); MonitoringEq(sensorReads + 1, f.Sensors.Reads);
                MonitoringEq(schedules + 1, f.Timers[0].Schedules); MonitoringEq(1000, f.Timers[0].Due);
                MonitoringEq(publishes + 2, f.Bridge.Publishes);
                MonitoringEq(1, f.SensorFactories); MonitoringEq(0, f.Sensors.Disposals);
                reads = f.Bridge.Reads; sensorReads = f.Sensors.Reads;
                schedules = f.Timers[0].Schedules; publishes = f.Bridge.Publishes;
                f.Sensors.BeforeRead = delegate {
                    f.Sensors.BeforeRead = null;
                    MonitoringEq(true, MonitoringPreferences.TrySetLayout(MonitoringLayout.TopBar));
                };
                f.Timers[0].FireUntilPublication();
                MonitoringEq(MonitoringLayout.TopBar, f.Bridge.PublishedLayout);
                MonitoringEq(reads + 1, f.Bridge.Reads); MonitoringEq(sensorReads + 1, f.Sensors.Reads);
                MonitoringEq(schedules + 1, f.Timers[0].Schedules); MonitoringEq(1000, f.Timers[0].Due);
                MonitoringEq(publishes + 2, f.Bridge.Publishes);
                MonitoringEq(1, f.SensorFactories); MonitoringEq(0, f.Sensors.Disposals);
            }
        }
        private static void MonitoringInFlightDisable()
        {
            using (var f = new MonitoringFixture())
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                f.Sensors.BeforeRead = delegate { entered.Set(); if (!release.WaitOne(3000)) throw new TimeoutException("Monitoring test release"); };
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true);
                var worker = new Thread(f.Timers[0].Fire); worker.Start();
                try
                {
                    MonitoringEq(true, entered.WaitOne(3000));
                    f.Timers[0].Fire(); MonitoringEq(1, f.Sensors.Reads);
                    MonitoringPreferences.TrySetEnabled(false);
                    MonitoringEq(false, PerformanceMonitoring.HasTimerForTest);
                }
                finally { release.Set(); MonitoringEq(true, worker.Join(3000)); }
                MonitoringEq(1, f.Bridge.Publishes); MonitoringEq(0, PerformanceMonitoring.Current.Pid);
                MonitoringEq(1, f.Sensors.Disposals);
            }
        }
        private static void MonitoringBridgeEntryStop()
        {
            using (var f = new MonitoringFixture())
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var stopping = new ManualResetEvent(false))
            using (var stopped = new ManualResetEvent(false))
            {
                f.Bridge.BeforeRead = delegate {
                    entered.Set();
                    if (!release.WaitOne(3000)) throw new TimeoutException("Monitoring bridge test release");
                };
                PerformanceMonitoring.Start(); MonitoringPreferences.TrySetEnabled(true);
                var worker = new Thread(f.Timers[0].Fire);
                var stopper = new Thread(delegate() { stopping.Set(); PerformanceMonitoring.Stop(); stopped.Set(); });
                worker.Start();
                try
                {
                    MonitoringEq(true, entered.WaitOne(3000));
                    int closes = f.Bridge.Closes;
                    stopper.Start(); MonitoringEq(true, stopping.WaitOne(3000));
                    MonitoringEq(false, stopped.WaitOne(100));
                    MonitoringEq(closes, f.Bridge.Closes);
                }
                finally
                {
                    release.Set(); MonitoringEq(true, worker.Join(3000));
                    MonitoringEq(true, stopper.Join(3000));
                }
                MonitoringEq(true, stopped.WaitOne(0));
                MonitoringEq(false, PerformanceMonitoring.HasTimerForTest);
                MonitoringEq(0, PerformanceMonitoring.Current.Pid);
                int reads = f.Bridge.Reads;
                f.Timers[0].Callback(); MonitoringEq(reads, f.Bridge.Reads);
                MonitoringEq(true, f.Bridge.Closes > 0);
            }
        }
    }
}
#endif

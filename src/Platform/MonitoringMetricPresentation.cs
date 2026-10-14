using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal sealed class MonitoringMetricCell
    {
        internal readonly MonitoringMetrics Metric;
        internal readonly string Name, Value;
        internal readonly double? Reading;
        internal readonly int ColorRgb;
        internal readonly int ValueCharacters;
        internal readonly bool Retained;
        internal MonitoringMetricCell(MonitoringMetrics metric, string name, double? reading, string format, string suffix, int accentRgb,
            bool retained = false)
        {
            Metric = metric; Name = name;
            Reading = reading.HasValue && MonitoringFrameWindow.Finite(reading.Value) ? reading : null;
            string value = Reading.HasValue ? Reading.Value.ToString(format, CultureInfo.InvariantCulture) + suffix : "--";
            Retained = retained && MonitoringMetricPresentation.Retainable(metric) && Reading.HasValue;
            Value = Retained ? "~" + value : value;
            ColorRgb = MonitoringMetricPresentation.ValueColor(metric, Reading, accentRgb);
            ValueCharacters = Math.Max(value.Length, format == "0.0" ? 6 + suffix.Length : 5 + suffix.Length);
            if (metric == MonitoringMetrics.CpuUsage || metric == MonitoringMetrics.GpuUsage)
                ValueCharacters = Math.Max(value.Length, 4);
            if (metric == MonitoringMetrics.CpuTemperature || metric == MonitoringMetrics.GpuTemperature)
                ValueCharacters = Math.Max(value.Length, 6); // 预留最长单位宽度，避免风扇单位切换时布局跳动。
            if (metric == MonitoringMetrics.GpuFan) ValueCharacters = Math.Max(ValueCharacters, 9);
            if (MonitoringMetricPresentation.Retainable(metric)) ValueCharacters++;
        }

        internal MonitoringMetricCell(MonitoringMetrics metric, string name, double? capacityMiB, double? percent, int accentRgb,
            bool retained = false)
        {
            Metric = metric; Name = name;
            double? capacity = capacityMiB.HasValue && MonitoringFrameWindow.Finite(capacityMiB.Value) ? capacityMiB / 1024.0 : null;
            Reading = percent.HasValue && MonitoringFrameWindow.Finite(percent.Value) ? percent : null;
            string amount = capacity.HasValue ? capacity.Value.ToString("0.0", CultureInfo.InvariantCulture) + " GiB" : "";
            string usage = Reading.HasValue ? Reading.Value.ToString("0", CultureInfo.InvariantCulture) + "%" : "";
            string value = amount.Length > 0 ? amount + (usage.Length > 0 ? " (" + usage + ")" : "") : usage.Length > 0 ? usage : "--";
            Retained = retained && metric == MonitoringMetrics.VideoMemory && (capacity.HasValue || Reading.HasValue);
            Value = Retained ? "~" + value : value;
            ColorRgb = Reading.HasValue ? MonitoringMetricPresentation.ValueColor(
                metric == (MonitoringMetrics.Ram | MonitoringMetrics.RamUsage) ? MonitoringMetrics.RamUsage : MonitoringMetrics.VramUsage,
                Reading, accentRgb) : capacity.HasValue ? MonitoringMetricPresentation.NeutralRgb : MonitoringMetricPresentation.DimRgb;
            ValueCharacters = Math.Max(value.Length, "9999.9 GiB (100%)".Length);
            if (metric == MonitoringMetrics.VideoMemory) ValueCharacters++;
        }
    }

    // 颜色阈值仅作显示提示，不代表硬件安全上限。
    internal static class MonitoringMetricPresentation
    {
        internal const int DefaultAccentRgb = 0xAE70FF, WarningRgb = 0xFFC857, CriticalRgb = 0xFF5B68;
        internal const int NeutralRgb = 0xF3F6FB, LabelRgb = 0xE1EAF5, DimRgb = 0xB9C3D2;
        private const MonitoringMetrics RetainableHardwareMetrics = MonitoringMetrics.CpuPower | MonitoringMetrics.CpuTemperature
            | MonitoringMetrics.GpuUsage | MonitoringMetrics.GpuTemperature | MonitoringMetrics.GpuClock
            | MonitoringMetrics.GpuMemoryClock | MonitoringMetrics.GpuPower | MonitoringMetrics.GpuFan | MonitoringMetrics.VideoMemory;
        internal static bool Retainable(MonitoringMetrics metric)
        { return metric != MonitoringMetrics.None && (metric & ~RetainableHardwareMetrics) == 0; }

        internal static string GroupName(MonitoringMetrics metric)
        {
            if ((metric & (MonitoringMetrics.Fps | MonitoringMetrics.FrameTime | MonitoringMetrics.FrameStatistics)) != 0) return "FRAME";
            if ((metric & (MonitoringMetrics.CpuUsage | MonitoringMetrics.CpuPower | MonitoringMetrics.CpuTemperature)) != 0) return "CPU";
            if ((metric & (MonitoringMetrics.SystemMemory | MonitoringMetrics.VideoMemory | MonitoringMetrics.GameRam)) != 0) return "MEMORY";
            return "GPU";
        }

        internal static int GroupColor(string group, int accentRgb)
        {
            return group == "FRAME" ? accentRgb & 0xFFFFFF : group == "GPU" ? 0x74DDB7
                : group == "CPU" ? 0x83BFFA : DimRgb;
        }

        // 显示时读取当前语言，指标身份保持不变。
        internal static string Name(MonitoringMetrics metric, bool gpuTime = false)
        {
            string key;
            switch (metric)
            {
                case MonitoringMetrics.Fps: key = "fps"; break;
                case MonitoringMetrics.FrameTime: key = gpuTime ? "gpuTime" : "present"; break;
                case MonitoringMetrics.AverageFps: key = "average"; break;
                case MonitoringMetrics.Low1: key = "low1"; break;
                case MonitoringMetrics.Low01: key = "low01"; break;
                case MonitoringMetrics.FrameTimeDeviation: key = "deviation"; break;
                case MonitoringMetrics.CpuUsage: key = "cpuUsage"; break;
                case MonitoringMetrics.CpuPower: key = "cpuPower"; break;
                case MonitoringMetrics.CpuTemperature: key = "cpuTemp"; break;
                case MonitoringMetrics.GpuUsage: key = "gpuUsage"; break;
                case MonitoringMetrics.GpuTemperature: key = "gpuTemp"; break;
                case MonitoringMetrics.GpuClock: key = "gpuClock"; break;
                case MonitoringMetrics.GpuMemoryClock: key = "vramClock"; break;
                case MonitoringMetrics.GpuPower: key = "gpuPower"; break;
                case MonitoringMetrics.GpuFan: key = "gpuFan"; break;
                case MonitoringMetrics.GameRam: key = "gameRam"; break;
                case MonitoringMetrics.Ram: case MonitoringMetrics.RamUsage: case MonitoringMetrics.SystemMemory: key = "ram"; break;
                case MonitoringMetrics.Vram: case MonitoringMetrics.VramUsage: case MonitoringMetrics.VideoMemory: key = "vram"; break;
                default: return "";
            }
            return Lang.T("monitor.label." + key);
        }
        internal static string ShortName(MonitoringMetricCell cell)
        {
            if (cell == null) return "";
            return cell.Metric == MonitoringMetrics.FrameTime ? cell.Name : ShortName(cell.Metric);
        }

        internal static string ShortName(MonitoringMetrics metric)
        {
            if (Lang.Cur != 1) return Name(metric, metric == MonitoringMetrics.FrameTime);
            return EnglishShortName(metric);
        }

        internal static string EnglishShortName(MonitoringMetricCell cell)
        {
            if (cell == null) return "";
            if (cell.Metric == MonitoringMetrics.FrameTime)
                return cell.Name == Name(MonitoringMetrics.FrameTime) ? "PRESENT" : "GPU TIME";
            return EnglishShortName(cell.Metric);
        }

        internal static string EnglishShortName(MonitoringMetrics metric)
        {
            switch (metric)
            {
                case MonitoringMetrics.Fps: return "// FPS";
                case MonitoringMetrics.FrameTime: return "GPU TIME";
                case MonitoringMetrics.AverageFps: return "AVG";
                case MonitoringMetrics.Low1: return "1% LOW";
                case MonitoringMetrics.Low01: return "0.1% LOW";
                case MonitoringMetrics.FrameTimeDeviation: return "FT SD";
                case MonitoringMetrics.CpuUsage: return "GAME CPU";
                case MonitoringMetrics.CpuPower: return "CPU PWR";
                case MonitoringMetrics.GpuPower: return "GPU PWR";
                case MonitoringMetrics.CpuTemperature: return "CPU TEMP";
                case MonitoringMetrics.GpuTemperature: return "GPU TEMP";
                case MonitoringMetrics.GpuUsage: return "GPU";
                case MonitoringMetrics.GpuClock: return "GPU CLK";
                case MonitoringMetrics.GpuMemoryClock: return "VRAM CLK";
                case MonitoringMetrics.GpuFan: return "GPU FAN";
                case MonitoringMetrics.GameRam: return "GAME RAM";
                case MonitoringMetrics.Ram: case MonitoringMetrics.RamUsage: case MonitoringMetrics.SystemMemory: return "RAM";
                case MonitoringMetrics.Vram: case MonitoringMetrics.VramUsage: case MonitoringMetrics.VideoMemory: return "VRAM";
                default: return "";
            }
        }

        internal static int LabelColor(MonitoringMetricCell cell, int accentRgb = DefaultAccentRgb)
        { return cell != null && cell.Metric == MonitoringMetrics.Fps ? accentRgb & 0xFFFFFF : LabelRgb; }

        internal static int ValueColor(MonitoringMetrics metric, double? value, int accentRgb = DefaultAccentRgb)
        {
            if (!value.HasValue || !MonitoringFrameWindow.Finite(value.Value)) return DimRgb;
            if (metric == MonitoringMetrics.Fps) return accentRgb & 0xFFFFFF;
            if (metric == MonitoringMetrics.CpuTemperature)
                return value.Value >= 95 ? CriticalRgb : value.Value >= 85 ? WarningRgb : NeutralRgb;
            if (metric == MonitoringMetrics.GpuTemperature)
                return value.Value >= 88 ? CriticalRgb : value.Value >= 80 ? WarningRgb : NeutralRgb;
            if (metric == MonitoringMetrics.CpuUsage || metric == MonitoringMetrics.RamUsage || metric == MonitoringMetrics.VramUsage)
                return value.Value >= 95 ? CriticalRgb : value.Value >= 85 ? WarningRgb : NeutralRgb;
            return NeutralRgb;
        }

        private static void Add(List<MonitoringMetricCell> cells, MonitoringMetrics selected, MonitoringMetrics metric,
            string name, double? value, string format, string suffix, int accentRgb,
            MonitoringMetrics retained = MonitoringMetrics.None)
        {
            if ((selected & metric) != 0) cells.Add(new MonitoringMetricCell(metric, name, value, format, suffix, accentRgb, (retained & metric) != 0));
        }

        internal static List<MonitoringMetricCell> Cells(MonitoringSnapshot snapshot, MonitoringMetrics selected,
            int accentRgb = DefaultAccentRgb)
        {
            accentRgb &= 0xFFFFFF;
            var cells = new List<MonitoringMetricCell>(18);
            if (snapshot == null) return cells;
            Add(cells, selected, MonitoringMetrics.Fps, Name(MonitoringMetrics.Fps), snapshot.Fps, "0", "", accentRgb);
            Add(cells, selected, MonitoringMetrics.FrameTime, Name(MonitoringMetrics.FrameTime, snapshot.FrameTimeFromGpu),
                snapshot.FrameTimeMs, "0.0", " ms", accentRgb);
            Add(cells, selected, MonitoringMetrics.AverageFps, Name(MonitoringMetrics.AverageFps), snapshot.AverageFps, "0", "", accentRgb);
            Add(cells, selected, MonitoringMetrics.Low1, Name(MonitoringMetrics.Low1), snapshot.Low1, "0", "", accentRgb);
            Add(cells, selected, MonitoringMetrics.Low01, Name(MonitoringMetrics.Low01), snapshot.Low01, "0", "", accentRgb);
            Add(cells, selected, MonitoringMetrics.FrameTimeDeviation, Name(MonitoringMetrics.FrameTimeDeviation), snapshot.FrameTimeDeviation, "0.0", " ms", accentRgb);
            Add(cells, selected, MonitoringMetrics.CpuUsage, Name(MonitoringMetrics.CpuUsage), snapshot.CpuUsage, "0", "%", accentRgb);
            Add(cells, selected, MonitoringMetrics.CpuPower, Name(MonitoringMetrics.CpuPower), snapshot.CpuPower, "0.0", " W", accentRgb, snapshot.RetainedHardwareMetrics);
            Add(cells, selected, MonitoringMetrics.CpuTemperature, Name(MonitoringMetrics.CpuTemperature), snapshot.CpuTemperature, "0", " °C", accentRgb, snapshot.RetainedHardwareMetrics);
            Add(cells, selected, MonitoringMetrics.GpuUsage, Name(MonitoringMetrics.GpuUsage), snapshot.GpuUsage, "0", "%", accentRgb, snapshot.RetainedHardwareMetrics);
            Add(cells, selected, MonitoringMetrics.GpuTemperature, Name(MonitoringMetrics.GpuTemperature), snapshot.GpuTemperature, "0", " °C", accentRgb, snapshot.RetainedHardwareMetrics);
            Add(cells, selected, MonitoringMetrics.GpuClock, Name(MonitoringMetrics.GpuClock), snapshot.GpuClock, "0", " MHz", accentRgb, snapshot.RetainedHardwareMetrics);
            Add(cells, selected, MonitoringMetrics.GpuMemoryClock, Name(MonitoringMetrics.GpuMemoryClock), snapshot.GpuMemoryClock, "0", " MHz", accentRgb, snapshot.RetainedHardwareMetrics);
            Add(cells, selected, MonitoringMetrics.GpuPower, Name(MonitoringMetrics.GpuPower), snapshot.GpuPower, "0", " W", accentRgb, snapshot.RetainedHardwareMetrics);
            Add(cells, selected, MonitoringMetrics.GpuFan, Name(MonitoringMetrics.GpuFan), snapshot.GpuFanRpm ?? snapshot.GpuFanPercent, "0",
                snapshot.GpuFanRpm.HasValue ? " RPM" : "%", accentRgb, snapshot.RetainedHardwareMetrics);
            if ((selected & (MonitoringMetrics.Ram | MonitoringMetrics.RamUsage)) != 0)
                cells.Add(new MonitoringMetricCell(MonitoringMetrics.Ram | MonitoringMetrics.RamUsage, Name(MonitoringMetrics.SystemMemory), snapshot.RamMiB, snapshot.RamUsage, accentRgb));
            Add(cells, selected, MonitoringMetrics.GameRam, Name(MonitoringMetrics.GameRam), snapshot.GameRamMiB / 1024.0, "0.0", " GiB", accentRgb);
            if ((selected & (MonitoringMetrics.Vram | MonitoringMetrics.VramUsage)) != 0)
                cells.Add(new MonitoringMetricCell(MonitoringMetrics.Vram | MonitoringMetrics.VramUsage, Name(MonitoringMetrics.VideoMemory), snapshot.VramMiB, snapshot.VramUsage, accentRgb,
                    ((snapshot.RetainedHardwareMetrics & MonitoringMetrics.Vram) != 0 && snapshot.VramMiB.HasValue && MonitoringFrameWindow.Finite(snapshot.VramMiB.Value))
                    || ((snapshot.RetainedHardwareMetrics & MonitoringMetrics.VramUsage) != 0 && snapshot.VramUsage.HasValue && MonitoringFrameWindow.Finite(snapshot.VramUsage.Value))));
            return cells;
        }

        private static readonly MonitoringSnapshot example = new MonitoringSnapshot(1234, "Preview", DateTime.MinValue, "",
            144, 6.9, 112, 28, 94, 62, 2460, 185, 12698, 6349,
            averageFps: 138, low01: 86, frameTimeDeviation: 1.2, gameRamMiB: 4301,
            ramUsage: 39, vramUsage: 78, gpuMemoryClock: 10501, gpuFanRpm: 1460, cpuPower: 54.8, cpuTemperature: 65);
        internal static MonitoringSnapshot Example { get { return example; } }
    }
}

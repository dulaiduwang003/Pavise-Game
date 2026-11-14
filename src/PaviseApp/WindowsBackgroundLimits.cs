using System;
using System.Globalization;

namespace PaviseApp
{
    // 原始优先级只在首次接管时记录，切换治理档位不会覆盖恢复基线。
    internal sealed class WindowsBackgroundLimits
    {
        internal const uint MinimumCpuRate = 1;
        internal uint CpuRate, OriginalPriority, DesiredPriority;
        internal int OriginalIo, DesiredIo;

        internal static uint RateFor(int mode)
        { return MinimumCpuRate; }

        // Job 配额以万分之一为单位；使用小数显示，不能把最低非零值截断成 0%。
        internal static string FormatCpuPercent(uint rate)
        { return (rate / 100m).ToString("0.##", CultureInfo.InvariantCulture); }

        internal static uint PriorityFor(uint original, int mode)
        {
            if (original == 64 || mode == BackgroundGovernancePolicy.Aggressive) return 64;
            return 16384;
        }

        internal WindowsBackgroundLimits WithMode(int mode)
        {
            return new WindowsBackgroundLimits { CpuRate = RateFor(mode), OriginalPriority = OriginalPriority,
                DesiredPriority = PriorityFor(OriginalPriority, mode), OriginalIo = OriginalIo,
                DesiredIo = Math.Min(OriginalIo, mode == BackgroundGovernancePolicy.Aggressive ? 0 : 1) };
        }

        internal bool MatchesMode(int mode)
        {
            WindowsBackgroundLimits wanted = WithMode(mode);
            return CpuRate == wanted.CpuRate && DesiredPriority == wanted.DesiredPriority && DesiredIo == wanted.DesiredIo;
        }

        internal bool SameOriginal(WindowsBackgroundLimits other)
        { return other != null && OriginalPriority == other.OriginalPriority && OriginalIo == other.OriginalIo; }

        internal bool Valid
        {
            get
            {
                return CpuRate > 0 && CpuRate <= 10000 && ValidPriority(OriginalPriority)
                    && (DesiredPriority == 64 || DesiredPriority == 16384)
                    && !(OriginalPriority == 64 && DesiredPriority != 64)
                    && OriginalIo >= 0 && OriginalIo <= 2 && DesiredIo >= 0 && DesiredIo <= Math.Min(OriginalIo, 1);
            }
        }

        private static bool ValidPriority(uint value)
        { return value == 64 || value == 16384 || value == 32 || value == 32768 || value == 128 || value == 256; }

        internal string Encode()
        {
            return string.Join(",", new[] { "wb1", CpuRate.ToString(CultureInfo.InvariantCulture),
                OriginalPriority.ToString(CultureInfo.InvariantCulture), DesiredPriority.ToString(CultureInfo.InvariantCulture),
                OriginalIo.ToString(CultureInfo.InvariantCulture), DesiredIo.ToString(CultureInfo.InvariantCulture) });
        }

        internal static WindowsBackgroundLimits Parse(string text)
        {
            string[] fields = (text ?? "").Split(',');
            var value = new WindowsBackgroundLimits();
            if (fields.Length != 6 || fields[0] != "wb1" || !uint.TryParse(fields[1], out value.CpuRate)
                || !uint.TryParse(fields[2], out value.OriginalPriority) || !uint.TryParse(fields[3], out value.DesiredPriority)
                || !int.TryParse(fields[4], out value.OriginalIo) || !int.TryParse(fields[5], out value.DesiredIo) || !value.Valid) return null;
            return value;
        }
    }
}

using System;
using System.Globalization;

namespace PaviseApp
{
    internal static class BackgroundGovernancePolicy
    {
        internal const string SettingKey = "BackgroundGovernanceMode";
        internal const int Normal = 0;
        internal const int Aggressive = 1;
        internal const string LegacyStoredValue = "2";
        private static readonly object modeWriteSync = new object();

        internal static bool Valid(int mode) { return mode == Normal || mode == Aggressive; }

        internal static int Normalize(int mode) { return Valid(mode) ? mode : Normal; }

        // 界面选项依次是 关闭 正常 激进
        internal static int Selection(bool masterEnabled, int mode)
        { return masterEnabled ? Normalize(mode) + 1 : 0; }

        internal static int Load()
        {
            string value = Settings.LoadStr(SettingKey, "0");
            if (value != LegacyStoredValue) return Parse(value);
            lock (modeWriteSync)
            {
                value = Settings.LoadStr(SettingKey, "0");
                if (value != LegacyStoredValue) return Parse(value);
                return Save(Aggressive) ? Aggressive : Normal;
            }
        }

        internal const string ResetMarkerKey = "BackgroundGovernanceResetV2";

        internal static void ResetForThisVersionOnce()
        {
            lock (modeWriteSync)
            {
                if (Settings.LoadStr(ResetMarkerKey, "") == "1") return;
                string value = Settings.LoadStr(SettingKey, "0");
                if (value == Text(Normal) || Save(Normal))
                    Settings.SaveStrDurable(ResetMarkerKey, "1");
            }
        }

        internal static int Parse(string value)
        {
            int mode;
            return int.TryParse(value, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out mode) ? Normalize(mode) : Normal;
        }

        internal static string Text(int mode)
        { return Normalize(mode).ToString(CultureInfo.InvariantCulture); }

        internal static bool Save(int mode)
        {
            if (!Valid(mode)) return false;
            string value = Text(mode);
            lock (modeWriteSync)
                return Settings.SaveStrDurable(SettingKey, value) && Settings.LoadStr(SettingKey, "") == value;
        }

        internal static uint ResourceCpuRate(int mode, int logicalProcessors)
        {
            if (mode != Aggressive || logicalProcessors <= 4) return 0;
            return CpuRate(3, logicalProcessors);
        }

        internal static uint FocusCpuRate(int logicalProcessors)
        {
            return logicalProcessors <= 2 ? 0 : CpuRate(1, logicalProcessors);
        }

        private static uint CpuRate(int cores, int logicalProcessors)
        {
            return (uint)Math.Round(cores * 10000.0 / logicalProcessors, MidpointRounding.AwayFromZero);
        }

        internal const long AggressiveIoBandwidth = 10L * 1024 * 1024;

        internal static long ResourceIoBandwidth(int mode)
        {
            return mode == Aggressive ? AggressiveIoBandwidth : 0;
        }
    }
}

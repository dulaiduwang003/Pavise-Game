// @author bdth 2074055628@qq.com
// 文件用途 管理前台时间片 可选系统默认或前台加权 也可只报告不改 该项依机型建议自测
using System;
using System.Globalization;
using Microsoft.Win32;

namespace PaviseApp
{
    internal enum QuantumMode
    {
        SystemDefault = 0,
        Foreground = 1,
        ReportOnly = 2
    }

    internal static class QuantumTweak
    {
        public const int SystemDefault = 2;
        public const int ForegroundWeighted = 0x26;

        private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
        private const string ValueName = "Win32PrioritySeparation";
        private const string ModeKey = "QuantumMode";

        private static readonly ReversibleReg Separation = new ReversibleReg(
            Registry.LocalMachine, KeyPath, ValueName, RegistryValueKind.DWord, "PrevPrioritySep");
        private static readonly object lk = new object();

        public static bool RepairedByPavise { get { return Settings.Load("PrioritySepRepaired", false); } }

        public static QuantumMode Mode
        {
            get
            {
                int raw;
                return int.TryParse(Settings.LoadStr(ModeKey, "0"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out raw) && raw >= 0 && raw <= 2
                    ? (QuantumMode)raw : QuantumMode.SystemDefault;
            }
        }

        public static int? Target
        {
            get
            {
                switch (Mode)
                {
                    case QuantumMode.Foreground: return ForegroundWeighted;
                    case QuantumMode.ReportOnly: return null;
                    default: return SystemDefault;
                }
            }
        }

        public static bool SetMode(QuantumMode mode)
        {
            lock (lk)
            {
                Settings.SaveStr(ModeKey, ((int)mode).ToString(CultureInfo.InvariantCulture));
                return mode == QuantumMode.ReportOnly ? Restore() : ApplyTarget();
            }
        }

        public static int? Current()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(KeyPath))
                {
                    if (k == null) return null;
                    object v = k.GetValue(ValueName);
                    return v is int ? (int?)(int)v : null;
                }
            }
            catch { return null; }
        }

        public static bool NeedsRepair()
        {
            int? target = Target;
            if (!target.HasValue) return false;
            int? cur = Current();
            return cur.HasValue && cur.Value != target.Value;
        }

        public static string Describe()
        {
            int? cur = Current();
            if (!cur.HasValue) return Lang.T("quantum.absent");
            string hex = "0x" + cur.Value.ToString("X");
            int? target = Target;
            if (!target.HasValue) return Lang.F("quantum.report", hex);
            if (cur.Value == target.Value)
                return Mode == QuantumMode.Foreground ? Lang.T("quantum.fg.ok") : Lang.T("quantum.ok");
            return Lang.F("quantum.broken", hex);
        }

        public static bool Repair()
        {
            lock (lk) return ApplyTarget();
        }

        private static bool ApplyTarget()
        {
            int? target = Target;
            if (!target.HasValue) return Restore();
            if (FgBoost.HasResidue())
            {
                if (!FgBoost.Restore())
                {
                    Logger.Log(Lang.T("log.quantumtweak.1"));
                    return false;
                }
                Logger.Log(Lang.T("log.quantumtweak.2"));
            }
            if (!NeedsRepair())
            {
                Logger.Log(Lang.T("log.quantumtweak.3"));
                return true;
            }
            if (!Separation.Apply(target.Value))
            {
                Logger.Log(Lang.T("log.quantumtweak.4"));
                return false;
            }
            Settings.Save("PrioritySepRepaired", true);
            Logger.Log(Lang.T("log.quantumtweak.5") + "0x" + target.Value.ToString("X") + Lang.T("log.quantumtweak.6"));
            return true;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                bool ok = !Separation.HasBackup || Separation.Restore();
                if (ok)
                {
                    Settings.Save("PrioritySepRepaired", false);
                    Logger.Log(Lang.T("log.quantumtweak.7"));
                }
                return ok;
            }
        }
    }
}

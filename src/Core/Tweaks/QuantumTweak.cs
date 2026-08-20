// @author bdth 2074055628@qq.com
// 文件用途 解码前台时间片的实际调度语义 判据只认微软有文档的低两位取到 Maximum 修复也只改这两位 高四位原样不动
using System;
using System.Globalization;
using Microsoft.Win32;

namespace PaviseApp
{
    internal struct QuantumShape
    {
        public bool ShortInterval;
        public bool VariableLength;
        public int RawPrioSep;
        public int PrioSep;
        public int ForegroundQuantum;
        public int BackgroundQuantum;

        public bool SameAs(QuantumShape other)
        {
            return ShortInterval == other.ShortInterval
                && VariableLength == other.VariableLength
                && ForegroundQuantum == other.ForegroundQuantum
                && BackgroundQuantum == other.BackgroundQuantum;
        }

        public int Ratio
        {
            get { return BackgroundQuantum <= 0 ? 0 : ForegroundQuantum / BackgroundQuantum; }
        }

        public string Describe()
        {
            string interval = Lang.T(ShortInterval ? "quantum.short" : "quantum.long");
            string length = Lang.T(VariableLength ? "quantum.variable" : "quantum.fixed");
            return interval + " " + length + " "
                + ForegroundQuantum.ToString(CultureInfo.InvariantCulture) + ":"
                + BackgroundQuantum.ToString(CultureInfo.InvariantCulture);
        }
    }

    internal static class QuantumDecoder
    {
        private static readonly int[] ShortVariable = { 6, 12, 18 };
        private static readonly int[] ShortFixed = { 18, 18, 18 };
        private static readonly int[] LongVariable = { 12, 24, 36 };
        private static readonly int[] LongFixed = { 36, 36, 36 };

        public static QuantumShape Decode(int raw)
        {
            int v = raw & 0x3F;
            int interval = (v >> 4) & 3;
            int length = (v >> 2) & 3;
            int prioSep = v & 3;

            QuantumShape shape;
            shape.ShortInterval = interval != 1;
            shape.VariableLength = length != 2;
            shape.RawPrioSep = prioSep;
            shape.PrioSep = prioSep > 2 ? 2 : prioSep;

            int[] table = shape.ShortInterval
                ? (shape.VariableLength ? ShortVariable : ShortFixed)
                : (shape.VariableLength ? LongVariable : LongFixed);
            shape.ForegroundQuantum = table[shape.PrioSep];
            shape.BackgroundQuantum = table[0];
            return shape;
        }

        public static bool Equivalent(int a, int b)
        {
            return Decode(a).SameAs(Decode(b));
        }
    }

    internal static class QuantumTweak
    {
        public const int BoostMask = 3;
        public const int BoostMaximum = 2;

        private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
        private const string ValueName = "Win32PrioritySeparation";
        private const string LegacyModeKey = "QuantumMode";

        private static readonly ReversibleReg Separation = new ReversibleReg(
            Registry.LocalMachine, KeyPath, ValueName, RegistryValueKind.DWord, "PrevPrioritySep");
        private static readonly object lk = new object();

        public static bool RepairedByPavise { get { return Settings.Load("PrioritySepRepaired", false); } }

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

        public static int TargetFor(int current)
        {
            return (current & ~BoostMask) | BoostMaximum;
        }

        public static bool NeedsRepair()
        {
            int? cur = Current();
            if (!cur.HasValue) return false;
            return QuantumDecoder.Decode(cur.Value).RawPrioSep != BoostMaximum;
        }

        public static string Describe()
        {
            int? cur = Current();
            if (!cur.HasValue) return Lang.T("quantum.absent");
            QuantumShape shape = QuantumDecoder.Decode(cur.Value);
            string hex = "0x" + cur.Value.ToString("X");
            if (shape.RawPrioSep == BoostMaximum) return Lang.F("quantum.ok", hex, shape.Describe());
            return Lang.F(shape.RawPrioSep > BoostMaximum ? "quantum.undocumented" : "quantum.weak",
                hex, shape.Describe());
        }

        public static bool Repair()
        {
            lock (lk)
            {
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
                int? cur = Current();
                if (!cur.HasValue) return false;
                int target = TargetFor(cur.Value);
                if (!Separation.Apply(target))
                {
                    Logger.Log(Lang.T("log.quantumtweak.4"));
                    return false;
                }
                Settings.Save("PrioritySepRepaired", true);
                Logger.Log(Lang.T("log.quantumtweak.5") + "0x" + target.ToString("X")
                    + Lang.T("log.quantumtweak.6"));
                return true;
            }
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

        public static bool HasRetiredModeResidue()
        {
            return Settings.LoadStr(LegacyModeKey, "").Length > 0 || Separation.HasBackup;
        }

        public static bool PurgeRetiredMode()
        {
            lock (lk)
            {
                bool ok = !Separation.HasBackup || Separation.Restore();
                if (!ok) return false;
                Settings.Remove(LegacyModeKey);
                Settings.Save("PrioritySepRepaired", false);
                return !HasRetiredModeResidue();
            }
        }
    }
}

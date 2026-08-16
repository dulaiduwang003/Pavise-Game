// @author bdth 2074055628@qq.com
// 文件用途 校正被优化教程改坏的前台时间片值 回到系统默认 台架实测偏方值伤尾部帧

using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class QuantumTweak
    {
        public const int SystemDefault = 2;
        private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
        private const string ValueName = "Win32PrioritySeparation";

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

        public static bool NeedsRepair()
        {
            int? cur = Current();
            return cur.HasValue && cur.Value != SystemDefault;
        }

        public static string Describe()
        {
            int? cur = Current();
            if (!cur.HasValue) return Lang.T("quantum.absent");
            if (cur.Value != SystemDefault)
                return Lang.F("quantum.broken", "0x" + cur.Value.ToString("X"));
            return Lang.T("quantum.ok");
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
                if (!Separation.Apply(SystemDefault))
                {
                    Logger.Log(Lang.T("log.quantumtweak.4"));
                    return false;
                }
                Settings.Save("PrioritySepRepaired", true);
                Logger.Log(Lang.T("log.quantumtweak.5") + SystemDefault + Lang.T("log.quantumtweak.6"));
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
    }
}

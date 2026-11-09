using System;
using System.Globalization;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {

        public static bool TryCurrentUnparked(out bool unparked)
        {
            unparked = false;
            Guid? guid = Current();
            if (!guid.HasValue)
            {
                return false;
            }
            IManagedPowerPlans managedPlans = ManagedPlans;
            return ManagedPowerPlanPolicy.ParkingDisabled(managedPlans.Read(guid.Value, SubProcessor, CpMinCores, true), managedPlans.Read(guid.Value, SubProcessor, CpMinCores, false), managedPlans.Read(guid.Value, SubProcessor, CpMinCores1, true), managedPlans.Read(guid.Value, SubProcessor, CpMinCores1, false), Native.OnAcPower(), CpuTopology.Hybrid, out unparked);
        }

        public static bool RestoreParkState()
        {
            lock (lk)
            {
                string message;
                if (!Settings.TryLoadStr("CoreParkSnap", out message) || message == null)
                {
                    return false;
                }
                if (message.Length == 0)
                {
                    return true;
                }
                string[] messageParts = message.Split('|');
                bool valuesRestored = messageParts.Length == 7 && messageParts[0] == "2" && messageParts[6] == "F";
                if (valuesRestored)
                {
                    messageParts = new[] { messageParts[1], messageParts[2], messageParts[3], messageParts[4], messageParts[5] };
                }
                Guid guid;
                if (messageParts.Length != 5 || !Guid.TryParse(messageParts[0], out guid) || guid == Guid.Empty)
                {
                    return ParkRestoreFailed();
                }
                foreach (string original in new[] { messageParts[1], messageParts[2], messageParts[3], messageParts[4] })
                {
                    uint value;
                    if (original != "-" && (!uint.TryParse(original, out value) || value > 100))
                    {
                        return ParkRestoreFailed();
                    }
                }
                if (!valuesRestored)
                {
                    bool primary = WriteBack(guid, CpMinCores, messageParts[1], messageParts[2]);
                    bool secondary = WriteBack(guid, CpMinCores1, messageParts[3], messageParts[4]);
                    if (!primary || !secondary)
                    {
                        return ParkRestoreFailed();
                    }
                    // 参数恢复后，激活失败只重试激活；即使其他所有者后来又选回旧值 100，也不重写参数。

                    string flushReceipt = "2|" + message + "|F";
                    string persisted;
                    if (!Settings.SaveStrDurable("CoreParkSnap", flushReceipt)
                        || !Settings.TryLoadStr("CoreParkSnap", out persisted) || persisted != flushReceipt)
                    {
                        return ParkRestoreFailed();
                    }
                }
                Guid? current = Current();
                if (!current.HasValue || current == Guid.Empty)
                {
                    return ParkRestoreFailed();
                }
                if (current == guid)
                {
                    Guid? confirmed = Current();
                    if (!confirmed.HasValue || confirmed == Guid.Empty
                        || (confirmed == guid && (!Set(guid) || Current() != guid)))
                    {
                        return ParkRestoreFailed();
                    }
                }
                if (!ClearParkSnap())
                {
                    return ParkRestoreFailed();
                }
                Logger.Log(Lang.T("log.powerplancorepark.4", 0));
                return true;
            }
        }

        private static bool ParkRestoreFailed()
        {
            int result;
            int.TryParse(Settings.LoadStr("CoreParkTries", "0"), out result);
            int tries = Math.Min(Math.Max(result, 0), 3);
            if (tries < 3)
            {
                Settings.SaveStr("CoreParkTries", (tries + 1).ToString(CultureInfo.InvariantCulture));
            }
            if (tries == 2) Logger.Warn(Lang.T("log.powerplancorepark.8", 0));
            else Logger.Log(Lang.T("log.powerplancorepark.3", 0));
            return false;
        }

        private static bool ClearParkSnap()
        {
            string remaining;
            if (!Settings.SaveStrDurable("CoreParkSnap", "")
                || !Settings.TryLoadStr("CoreParkSnap", out remaining) || remaining != "")
            {
                return false;
            }
            Settings.Remove("CoreParkTries");
            return true;
        }

        private static bool WriteBack(Guid scheme, Guid setting, string acStr, string dcStr)
        {
            bool ac = RestoreParkIndex(scheme, setting, true, acStr);
            bool dc = RestoreParkIndex(scheme, setting, false, dcStr);
            return ac && dc;
        }

        private static bool RestoreParkIndex(Guid scheme, Guid setting, bool ac, string original)
        {
            try
            {
                if (original == "-") return true;
                uint saved, current;
                if (!uint.TryParse(original, out saved) || saved > 100
                    || !(ac ? ReadAc(scheme, SubProcessor, setting, out current) : ReadDc(scheme, SubProcessor, setting, out current)))
                {
                    return false;
                }
                // 仅持有旧的 Pavise 写入值，保留后续外部改动。
                if (current != 100 || saved == 100) return true;
                if (!(ac ? WriteAc(scheme, SubProcessor, setting, saved) : WriteDc(scheme, SubProcessor, setting, saved)))
                {
                    return false;
                }
                return (ac ? ReadAc(scheme, SubProcessor, setting, out current) : ReadDc(scheme, SubProcessor, setting, out current)) && current == saved;
            }
            catch
            {
                return false;
            }
        }

        public static string MachineSignature()
        {
            if (machineSig != null)
            {
                return machineSig;
            }
            string message = "";
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Cryptography"))
                {
                    if (registryKey != null)
                    {
                        message = (registryKey.GetValue("MachineGuid") as string) ?? "";
                    }
                }
            }
            catch
            {
            }
            if (message.Length == 0)
            {
                try
                {
                    message = Environment.MachineName;
                }
                catch
                {
                    message = "PAVISE";
                }
            }
            uint numericValue = 2166136261u;
            string message2 = message;
            foreach (char c in message2)
            {
                numericValue ^= c;
                numericValue *= 16777619;
            }
            machineSig = (numericValue & 0xFFFFu).ToString("X4");
            return machineSig;
        }

    }
}

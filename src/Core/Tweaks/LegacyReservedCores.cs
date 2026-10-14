// 作者：bdth 2074055628@qq.com
using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class LegacyReservedCores
    {
        private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
        private const string ValueName = "ReservedCpuSets";
        private const string OnKey = "ReservedCoresByPavise";
        private const string SnapKey = "PrevReservedCpuSets";
        internal const string ClearedKey = "LegacyReservedCoresClearedV1";
        internal const string SuspectKey = "LegacyReservedCoresSuspectV1";
        internal const int MinPhysicalCores = 8;

#if PAVISE_SELFTEST
        internal static Func<byte[]> ReadForTest;
        internal static Func<byte[], bool> WriteForTest;
#endif

        internal static ulong LegacyMask(ulong[] physicalCores, ulong perfMask, bool hybrid, ulong allMask, ulong strictGame)
        {
            if (physicalCores == null || physicalCores.Length < MinPhysicalCores || allMask == 0) return 0;
            ulong reserved = 0;
            int picked = 0;
            foreach (ulong core in physicalCores)
            {
                if (core == 0 || (core & 1UL) != 0) continue;
                if (hybrid && (core & perfMask) == 0) continue;
                reserved |= core;
                if (++picked == 2) break;
            }
            if (picked < 2) return 0;
            if (strictGame != 0 && (reserved & ~strictGame) != 0) return 0;
            if (AutoCores.CountBits(allMask & ~reserved) * 2 < AutoCores.CountBits(allMask)) return 0;
            return reserved;
        }

        internal static ulong BytesMask(byte[] bytes)
        {
            if (bytes == null) return 0;
            ulong mask = 0;
            for (int i = 0; i < bytes.Length && i < 8; i++) mask |= (ulong)bytes[i] << (i * 8);
            return mask;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static byte[] ReadRaw()
        {
#if PAVISE_SELFTEST
            if (ReadForTest != null) return ReadForTest();
#endif
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(KeyPath))
                return k == null ? null : k.GetValue(ValueName) as byte[];
        }

        private static bool WriteRaw(byte[] value)
        {
#if PAVISE_SELFTEST
            if (WriteForTest != null) return WriteForTest(value);
#endif
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(KeyPath, true))
            {
                if (k == null) return value == null;
                if (value == null) k.DeleteValue(ValueName, false);
                else k.SetValue(ValueName, value, RegistryValueKind.Binary);
            }
            byte[] after = ReadRaw();
            return SameBytes(after, value);
        }

        internal static bool TryDecodeSnapshot(string snap, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrEmpty(snap)) return false;
            if (snap == ReversibleReg.Absent) return true;
            string[] parts = snap.Split('-');
            if (parts.Length == 0 || parts.Length > 64) return false;
            var decoded = new byte[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Length != 2 || !byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out decoded[i])) return false;
            bytes = decoded;
            return true;
        }

        private static ulong CurrentLegacyMask()
        {
            return LegacyMask(CpuTopology.PhysicalCoreMasks(), CpuTopology.PerfMask, CpuTopology.Hybrid,
                CpuTopology.AllMask, CpuTopology.StrictBoostMask);
        }

        internal static bool Heal()
        {
            if (CpuTopology.MultiGroup) return true;
            string snap = Settings.LoadStr(SnapKey, "");
            bool receipt = Settings.Load(OnKey, false) || snap.Length > 0;
            byte[] raw;
            try { raw = ReadRaw(); }
            catch { return false; }
            ulong current = BytesMask(raw);
            ulong legacy = CurrentLegacyMask();
            if (!receipt)
            {
                if (raw != null && raw.Length == 8 && current != 0 && current == legacy) Settings.SaveStr(SuspectKey, current.ToString("X"));
                else Settings.Remove(SuspectKey);
                return true;
            }
            Settings.Remove(SuspectKey);
            byte[] restore;
            if (!TryDecodeSnapshot(snap, out restore))
            {
                Logger.Warn(Lang.F("log.autocore.legacycorrupt", "0x" + current.ToString("X")));
                return false;
            }
            ulong original = BytesMask(restore);
            if (!SameBytes(raw, restore))
            {
                if (raw == null || raw.Length != 8 || current != legacy)
                {
                    Logger.Log(Lang.F("log.autocore.legacychanged", "0x" + current.ToString("X")));
                    ForgetReceipt();
                    return true;
                }
                bool written;
                try { written = WriteRaw(restore); }
                catch { written = false; }
                if (!written)
                {
                    Logger.Warn(Lang.F("log.autocore.legacyfailed", "0x" + current.ToString("X")));
                    return false;
                }
                RecordCleared(current & ~original);
            }
            ForgetReceipt();
            return true;
        }

        internal static ulong Suspect()
        {
            ulong mask;
            return ParseMask(Settings.LoadStr(SuspectKey, ""), out mask) ? mask : 0;
        }

        internal static bool ClearSuspect()
        {
            ulong suspect = Suspect();
            if (suspect == 0) return false;
            byte[] raw;
            try { raw = ReadRaw(); }
            catch { return false; }
            if (raw == null || raw.Length != 8 || BytesMask(raw) != suspect)
            {
                Settings.Remove(SuspectKey);
                return false;
            }
            bool written;
            try { written = WriteRaw(null); }
            catch { written = false; }
            if (!written)
            {
                Logger.Warn(Lang.F("log.autocore.legacyfailed", "0x" + suspect.ToString("X")));
                return false;
            }
            Settings.Remove(SuspectKey);
            RecordCleared(suspect);
            return true;
        }

        private static void RecordCleared(ulong freed)
        {
            if (freed == 0) return;
            Settings.SaveStr(ClearedKey, freed.ToString("X"));
            Logger.Log(Lang.F("log.autocore.legacycleared", CpuTopology.DescribeMask(freed)));
        }

        private static void ForgetReceipt()
        {
            Settings.Remove(OnKey);
            Settings.Remove(SnapKey);
        }

        private static bool ParseMask(string raw, out ulong mask)
        {
            mask = 0;
            return raw.Length > 0 && ulong.TryParse(raw, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out mask) && mask != 0;
        }

        internal static ulong PendingReboot(ulong liveAllocated)
        {
            ulong freed;
            if (!ParseMask(Settings.LoadStr(ClearedKey, ""), out freed)) return 0;
            if ((freed & liveAllocated) == 0)
            {
                Settings.Remove(ClearedKey);
                return 0;
            }
            return freed & liveAllocated;
        }
    }
}

// @author bdth 2074055628@qq.com
// 文件用途 内核保留核 让系统线程避开两个物理核 分区对局的游戏独享 重启生效
using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class ReservedCoresTweak
    {
        private const string KeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";
        private const string ValueName = "ReservedCpuSets";
        private const string OnKey = "ReservedCoresByPavise";
        private const string SnapKey = "PrevReservedCpuSets";
        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load(OnKey, false); } }
        public static bool OwnsState
        { get { return EnabledByPavise || Settings.LoadStr(SnapKey, "").Length > 0; } }

        // 只保留两个物理核 机器越小副作用越大 八个物理核起步
        //   保留是全天的 系统和所有普通程序一直少这两个核
        //   反作弊拒绝 CPU 集合写入的游戏自己也进不了保留核 这类游戏在库里时极限档不强制
        //   CPU0 是系统和多数驱动的默认落点 含它的物理核绝不保留
        //   混合架构只挑性能核 保留核必须落在分区的游戏核之内
        //   不经 Pavise 分区启动的游戏也会避开保留核 所以只保留两个
        internal const int MinPhysicalCores = 8;

        internal static ulong ComputeMask(ulong[] physicalCores, ulong perfMask, bool hybrid,
            ulong allMask, ulong strictGame)
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
            if (CountBits(allMask & ~reserved) * 2 < CountBits(allMask)) return 0;
            return reserved;
        }

        internal static int CountBits(ulong mask)
        {
            int count = 0;
            while (mask != 0) { count += (int)(mask & 1UL); mask >>= 1; }
            return count;
        }

        public static ulong RecommendedMask()
        {
            if (CpuTopology.MultiGroup) return 0;
            return ComputeMask(CpuTopology.PhysicalCoreMasks(), CpuTopology.PerfMask,
                CpuTopology.Hybrid, CpuTopology.AllMask, CpuTopology.StrictBoostMask);
        }

        internal static byte[] MaskBytes(ulong mask)
        {
            var bytes = new byte[8];
            for (int i = 0; i < 8; i++) bytes[i] = (byte)(mask >> (i * 8));
            return bytes;
        }

        private static byte[] ReadRaw()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(KeyPath))
                    return k == null ? null : k.GetValue(ValueName) as byte[];
            }
            catch { return null; }
        }

        internal static ulong BytesMask(byte[] bytes)
        {
            if (bytes == null) return 0;
            ulong mask = 0;
            for (int i = 0; i < bytes.Length && i < 8; i++) mask |= (ulong)bytes[i] << (i * 8);
            return mask;
        }

        // 值存在且不是我们写的 说明有人自己配过保留核 整个功能不接管
        public static bool ExternalValuePresent()
        {
            return !EnabledByPavise && BytesMask(ReadRaw()) != 0;
        }

        public static bool Enable()
        {
            lock (lk)
            {
                ulong mask = RecommendedMask();
                if (mask == 0) { Logger.Log(Lang.T("log.rescores.1")); return false; }
                byte[] before = ReadRaw();
                if (BytesMask(before) != 0 && !EnabledByPavise)
                { Logger.Log(Lang.T("log.rescores.2")); return false; }
                if (Settings.LoadStr(SnapKey, "").Length == 0)
                {
                    string snap = before == null ? ReversibleReg.Absent : BitConverter.ToString(before);
                    Settings.SaveStr(SnapKey, snap);
                    if (Settings.LoadStr(SnapKey, "") != snap)
                    { Logger.Log(Lang.T("log.rescores.3")); return false; }
                }
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.CreateSubKey(KeyPath))
                    {
                        if (k == null) { Logger.Log(Lang.T("log.rescores.4")); return false; }
                        k.SetValue(ValueName, MaskBytes(mask), RegistryValueKind.Binary);
                    }
                }
                catch { Logger.Log(Lang.T("log.rescores.4")); return false; }
                if (BytesMask(ReadRaw()) != mask)
                { Logger.Log(Lang.T("log.rescores.4")); return false; }
                Settings.Save(OnKey, true);
                Logger.Log(Lang.T("log.rescores.5") + "0x" + mask.ToString("X"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string snap = Settings.LoadStr(SnapKey, "");
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.CreateSubKey(KeyPath))
                    {
                        if (k == null) { Logger.Log(Lang.T("log.rescores.6")); return false; }
                        if (snap.Length == 0 || snap == ReversibleReg.Absent)
                            k.DeleteValue(ValueName, false);
                        else
                        {
                            string[] parts = snap.Split('-');
                            var bytes = new byte[parts.Length];
                            for (int i = 0; i < parts.Length; i++)
                                bytes[i] = Convert.ToByte(parts[i], 16);
                            k.SetValue(ValueName, bytes, RegistryValueKind.Binary);
                        }
                    }
                }
                catch { Logger.Log(Lang.T("log.rescores.6")); return false; }
                Settings.Save(OnKey, false);
                Settings.SaveStr(SnapKey, "");
                Logger.Log(Lang.T("log.rescores.7"));
                return true;
            }
        }
    }
}

// @author bdth 2074055628@qq.com
// 文件用途 对局时解除当前活动电源方案的核心停泊 不管用不用托管方案 退出还原原值 崩溃可自愈
// 托管方案本就把停泊写成不停放 这里覆盖的是用户选用自己电源计划的场景 保证对局中所有核心保持唤醒

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {
        private const string ParkSnapKey = "CoreParkSnap";
        private const string ParkAbsent = "-";

        public static bool HasParkResidue()
        {
            return Settings.LoadStr(ParkSnapKey, "").Length > 0;
        }

        public static bool TryCurrentUnparked(out bool unparked)
        {
            unparked = false;
            Guid? cur = Current();
            if (!cur.HasValue) return false;
            Guid scheme = cur.Value, sub = SubProcessor, set = CpMinCores;
            uint ac;
            if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out ac) != 0) return false;
            unparked = ac >= 100;
            return true;
        }

        public static bool UnparkForSession()
        {
            Guid? cur = Current();
            if (!cur.HasValue) return false;
            Guid scheme = cur.Value;

            var snap = new List<string> { scheme.ToString() };
            bool changed = false;
            changed |= EnsureCoreUnpark(scheme, CpMinCores, snap);
            // CpMinCores1 是 E 核停放 非混合架构机器上该设置也可能存在且值为 0 会被误判成需要解除
            if (CpuTopology.Hybrid && SettingPresent(scheme, SubProcessor, CpMinCores1))
                changed |= EnsureCoreUnpark(scheme, CpMinCores1, snap);
            else { snap.Add(ParkAbsent); snap.Add(ParkAbsent); }

            if (!changed) return true;

            if (Settings.LoadStr(ParkSnapKey, "").Length == 0)
            {
                Settings.SaveStr(ParkSnapKey, string.Join("|", snap.ToArray()));
                if (Settings.LoadStr(ParkSnapKey, "").Length == 0)
                {
                    Logger.Log("核心停泊解除 原值快照无法持久化 取消改动");
                    return false;
                }
            }
            Set(scheme);
            Logger.Log("核心停泊解除 当前电源方案对局中保持全部核心唤醒 退出还原");
            return true;
        }

        private static bool EnsureCoreUnpark(Guid scheme, Guid setting, List<string> snap)
        {
            Guid sub = SubProcessor, set = setting, sc = scheme;
            uint ac, dc;
            bool haveAc = PowerReadACValueIndex(IntPtr.Zero, ref sc, ref sub, ref set, out ac) == 0;
            sc = scheme; sub = SubProcessor; set = setting;
            bool haveDc = PowerReadDCValueIndex(IntPtr.Zero, ref sc, ref sub, ref set, out dc) == 0;
            snap.Add(haveAc ? ac.ToString() : ParkAbsent);
            snap.Add(haveDc ? dc.ToString() : ParkAbsent);
            bool need = (haveAc && ac != 100) || (haveDc && dc != 100);
            if (!need) return false;
            sc = scheme; sub = SubProcessor; set = setting;
            if (haveAc) PowerWriteACValueIndex(IntPtr.Zero, ref sc, ref sub, ref set, 100);
            sc = scheme; sub = SubProcessor; set = setting;
            if (haveDc) PowerWriteDCValueIndex(IntPtr.Zero, ref sc, ref sub, ref set, 100);
            return true;
        }

        public static bool RestoreParkState()
        {
            string s = Settings.LoadStr(ParkSnapKey, "");
            if (s.Length == 0) return true;
            string[] parts = s.Split('|');
            if (parts.Length < 5) { Settings.SaveStr(ParkSnapKey, ""); return true; }
            Guid scheme;
            try { scheme = new Guid(parts[0]); }
            catch { Settings.SaveStr(ParkSnapKey, ""); return true; }

            bool ok = WriteBack(scheme, CpMinCores, parts[1], parts[2]);
            ok &= WriteBack(scheme, CpMinCores1, parts[3], parts[4]);
            if (!ok)
            {
                Logger.Log("核心停泊还原写回失败 快照保留待下次重试");
                return false;
            }
            // 仅当快照方案仍是活动方案时才重设刷新 否则会把已切走的托管方案顶回活动
            Guid? nowActive = Current();
            if (nowActive.HasValue && nowActive.Value == scheme) Set(scheme);
            Settings.SaveStr(ParkSnapKey, "");
            if (Settings.LoadStr(ParkSnapKey, "").Length > 0) return false;
            Logger.Log("核心停泊已还原电源方案原值");
            return true;
        }

        private static bool WriteBack(Guid scheme, Guid setting, string acStr, string dcStr)
        {
            try
            {
                Guid sub = SubProcessor, set = setting, sc = scheme;
                uint v;
                if (acStr != ParkAbsent && uint.TryParse(acStr, out v)
                    && PowerWriteACValueIndex(IntPtr.Zero, ref sc, ref sub, ref set, v) != 0) return false;
                sc = scheme; sub = SubProcessor; set = setting;
                if (dcStr != ParkAbsent && uint.TryParse(dcStr, out v)
                    && PowerWriteDCValueIndex(IntPtr.Zero, ref sc, ref sub, ref set, v) != 0) return false;
                return true;
            }
            catch { return false; }
        }
    }
}

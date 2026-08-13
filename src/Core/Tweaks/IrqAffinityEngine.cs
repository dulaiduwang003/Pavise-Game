// @author bdth 2074055628@qq.com
// 文件用途 中断亲和策略的通用引擎 供显卡和网卡等设备复用

using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class IrqAffinityEngine
    {
        private const int PolicyAllCloseProcessors = 1;
        private const int PolicySpecifiedProcessors = 4;

        private readonly string settingsKey;
        private readonly string slotPrefix;
        private readonly string logPrefix;

        public IrqAffinityEngine(string settingsKey, string slotPrefix, string logPrefix)
        {
            this.settingsKey = settingsKey;
            this.slotPrefix = slotPrefix;
            this.logPrefix = logPrefix;
        }

        public bool EnabledByPavise { get { return Settings.Load(settingsKey, false); } }

        public bool HasResidue
        {
            get { return EnabledByPavise || LoadTouched().Count > 0; }
        }

        private sealed class Target
        {
            public string DeviceId;
            public ReversibleReg Policy;
            public ReversibleReg Mask;
        }

        private static string StableSlot(string deviceId)
        {
            unchecked
            {
                int h = 17;
                foreach (char c in deviceId) h = h * 31 + c;
                return h.ToString("X8");
            }
        }

        internal static byte[] MaskToBytes(ulong mask)
        {
            var b = new byte[8];
            for (int i = 0; i < 8; i++) b[i] = (byte)((mask >> (i * 8)) & 0xFF);
            return b;
        }

        internal static ulong BytesToMask(byte[] b)
        {
            if (b == null || b.Length < 8) return 0;
            ulong m = 0;
            for (int i = 0; i < 8; i++) m |= ((ulong)b[i]) << (i * 8);
            return m;
        }

        private List<Target> BuildTargets(List<string> deviceIds)
        {
            var list = new List<Target>();
            foreach (string id in deviceIds)
            {
                string regPath = @"SYSTEM\CurrentControlSet\Enum\" + id + @"\Device Parameters\Interrupt Management\Affinity Policy";
                string slotBase = slotPrefix + StableSlot(id);
                list.Add(new Target
                {
                    DeviceId = id,
                    Policy = new ReversibleReg(Registry.LocalMachine, regPath, "DevicePolicy", RegistryValueKind.DWord, slotBase + "_Policy"),
                    Mask = new ReversibleReg(Registry.LocalMachine, regPath, "AssignmentSetOverride", RegistryValueKind.Binary, slotBase + "_Mask")
                });
            }
            return list;
        }

        internal static void ReportMsiState(List<string> deviceIds)
        {
            if (deviceIds == null) return;
            foreach (string id in deviceIds)
            {
                try
                {
                    string path = @"SYSTEM\CurrentControlSet\Enum\" + id
                        + @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties";
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
                    {
                        object v = k == null ? null : k.GetValue("MSISupported");
                        if (v == null)
                            Logger.Log("中断体检 " + id + " 未声明 MSI 支持 可能使用传统线中断");
                        else if (Convert.ToInt64(v) == 0)
                            Logger.Log("中断体检 " + id + " 的 MSI 被显式关闭 MSISupported=0");
                    }
                }
                catch { }
            }
        }

        public bool Enable(List<string> deviceIds)
        {
            return Enable(deviceIds, CpuTopology.BoostMask);
        }

        public bool Enable(List<string> deviceIds, ulong preferredMask)
        {
            ReportMsiState(deviceIds);
            if (deviceIds == null || deviceIds.Count == 0)
            {
                Logger.Log(logPrefix + " 未找到可用设备 未作修改");
                return false;
            }
            List<Target> targets = BuildTargets(deviceIds);
            bool useMask = !CpuTopology.MultiGroup && preferredMask != 0 && preferredMask != CpuTopology.AllMask;
            if (CpuTopology.MultiGroup)
                Logger.Log(logPrefix + " 多处理器组系统 掩码无法指明处理器组归属 仅使用 AllCloseProcessors");

            var touched = LoadTouched();
            object policyValue = useMask ? PolicySpecifiedProcessors : PolicyAllCloseProcessors;
            byte[] maskBytes = useMask ? MaskToBytes(preferredMask) : null;

            bool anyOk = false;
            var applied = new List<Target>();
            foreach (Target t in targets)
            {
                if (touched.Contains(t.DeviceId)
                    && t.Policy.Matches(policyValue)
                    && (!useMask || t.Mask.Matches(maskBytes)))
                { anyOk = true; continue; }
                bool ok;
                if (useMask)
                    ok = t.Policy.Apply(policyValue) & t.Mask.Apply(maskBytes);
                else
                    ok = t.Policy.Apply(policyValue);
                if (ok) { anyOk = true; applied.Add(t); }
                else
                {
                    t.Policy.Restore(); t.Mask.Restore();
                    Logger.Log(logPrefix + " 设备 " + t.DeviceId + " 写入或回读失败 已跳过");
                }
            }
            if (!anyOk) return false;

            if (applied.Count > 0)
            {
                foreach (Target t in applied) if (!touched.Contains(t.DeviceId)) touched.Add(t.DeviceId);
                if (!SaveTouched(touched))
                {
                    foreach (Target t in applied) { t.Policy.Restore(); t.Mask.Restore(); }
                    Logger.Log(logPrefix + " 设备名单无法持久化 已撤销本轮注册表修改");
                    return false;
                }
            }

            Settings.Save(settingsKey, true);
            if (!Settings.Load(settingsKey, false))
            {
                foreach (Target t in applied) { t.Policy.Restore(); t.Mask.Restore(); }
                if (applied.Count > 0) SaveTouched(new List<string>());
                Logger.Log(logPrefix + "状态标志无法持久化 已还原注册表修改");
                return false;
            }
            Settings.SaveStr(StampKey, useMask ? MakeStamp(Environment.ProcessorCount, preferredMask) : "");
            if (applied.Count > 0)
                Logger.Log(logPrefix + " 已对 " + applied.Count + " 个设备写入"
                    + (useMask ? "指定处理器策略 掩码 0x" + preferredMask.ToString("X") + " " : "邻近处理器策略")
                    + " 需要重启该设备或重启电脑后生效");
            return true;
        }

        private string TouchedKey { get { return slotPrefix + "Touched"; } }

        private string StampKey { get { return slotPrefix + "MaskStamp"; } }

        internal static string MakeStamp(int processorCount, ulong mask)
        {
            return processorCount.ToString(CultureInfo.InvariantCulture) + ":" + mask.ToString("X");
        }

        internal static bool StampIsStale(string stamp, int processorCount, ulong allMask)
        {
            if (string.IsNullOrEmpty(stamp)) return false;
            int cut = stamp.IndexOf(':');
            if (cut <= 0 || cut + 1 >= stamp.Length) return false;
            int recorded;
            if (!int.TryParse(stamp.Substring(0, cut), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out recorded)) return false;
            ulong mask;
            if (!ulong.TryParse(stamp.Substring(cut + 1), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out mask)) return false;
            if (mask == 0) return true;
            if (recorded != processorCount) return true;
            return allMask != 0 && (mask & ~allMask) != 0;
        }

        internal static bool MaskOutOfRange(ulong mask, ulong allMask)
        {
            if (allMask == 0) return false;
            if (mask == 0) return false;
            return (mask & ~allMask) != 0;
        }

        private bool AppliedMaskStale(out string detail)
        {
            detail = null;
            foreach (string id in LoadTouched())
            {
                try
                {
                    string path = @"SYSTEM\CurrentControlSet\Enum\" + id
                        + @"\Device Parameters\Interrupt Management\Affinity Policy";
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(path))
                    {
                        if (k == null) continue;
                        object raw = k.GetValue("AssignmentSetOverride");
                        ulong mask = BytesToMask(raw as byte[]);
                        if (!MaskOutOfRange(mask, CpuTopology.AllMask)) continue;
                        detail = id + " 掩码 0x" + mask.ToString("X");
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        public bool HealStaleMask()
        {
            if (!EnabledByPavise) return false;
            string stamp = Settings.LoadStr(StampKey, "");
            string reason = null;
            if (StampIsStale(stamp, Environment.ProcessorCount, CpuTopology.AllMask))
                reason = "写入时记录的是 " + stamp;
            else
            {
                string detail;
                if (!AppliedMaskStale(out detail)) return false;
                reason = "注册表里 " + detail;
            }

            Logger.Log(logPrefix + " " + reason
                + " 与本机现状对不上 当前 " + Environment.ProcessorCount + " 个逻辑处理器"
                + " 中断可能被指向已不存在的核心 正在还原");
            bool ok = Disable(null);
            Logger.Log(ok
                ? logPrefix + " 已还原为系统默认中断分配 重启后生效 需要的话可以重新打开开关"
                : logPrefix + " 还原未完成 多半是权限不足 请用管理员身份打开一次再关掉这个开关");
            return ok;
        }

        private List<string> LoadTouched()
        {
            var list = new List<string>();
            foreach (string s in Settings.LoadStr(TouchedKey, "").Split('\n'))
            {
                string id = s.Trim();
                if (id.Length > 0 && !list.Contains(id)) list.Add(id);
            }
            return list;
        }

        private bool SaveTouched(List<string> ids)
        {
            string joined = string.Join("\n", ids.ToArray());
            Settings.SaveStr(TouchedKey, joined);
            return Settings.LoadStr(TouchedKey, "") == joined;
        }

        public bool Disable(List<string> deviceIds)
        {
            var scope = LoadTouched();
            if (deviceIds != null)
                foreach (string id in deviceIds)
                    if (!scope.Contains(id)) scope.Add(id);

            List<Target> targets = BuildTargets(scope);
            bool allOk = true;
            int restored = 0;
            var stillDirty = new List<string>();
            foreach (Target t in targets)
            {
                bool hadBackup = t.Policy.HasBackup || t.Mask.HasBackup;
                bool ok = t.Policy.Restore() & t.Mask.Restore();
                if (hadBackup && ok) restored++;
                if (!ok) { allOk = false; stillDirty.Add(t.DeviceId); }
            }

            SaveTouched(stillDirty);
            if (allOk)
            {
                Settings.Save(settingsKey, false);
                if (Settings.Load(settingsKey, true)) return false;
                Settings.SaveStr(StampKey, "");
                Logger.Log(logPrefix + " 已还原 " + restored + " 个设备 需要重启该设备或重启电脑后生效");
            }
            else
                Logger.Log(logPrefix + " 仍有 " + stillDirty.Count + " 个设备未能还原 已保留记录待下次重试");
            return allOk;
        }
    }
}

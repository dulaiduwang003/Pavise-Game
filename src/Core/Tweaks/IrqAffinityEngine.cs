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

        public List<string> TouchedDevices() { return LoadTouched(); }

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
                            Logger.Log(Lang.T("log.irqaffinityengine.1") + id + Lang.T("log.irqaffinityengine.2"));
                        else if (Convert.ToInt64(v) == 0)
                            Logger.Log(Lang.T("log.irqaffinityengine.1") + id + Lang.T("log.irqaffinityengine.3"));
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
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.4"));
                return false;
            }
            List<Target> targets = BuildTargets(deviceIds);
            bool useMask = !CpuTopology.MultiGroup && preferredMask != 0 && preferredMask != CpuTopology.AllMask;
            if (CpuTopology.MultiGroup)
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.5"));

            var touched = LoadTouched();

            // 先记名单再动手 顺序反了会出人命
            //   原来是先写注册表 循环跑完才存名单 中间被杀掉的话
            //   注册表已经改了 名单里却没有这台设备 Disable 找不到它 用户永远还不回来
            //   反过来先记名单 崩在中间时名单是个超集 还原会多试几台没动过的
            //   而没有备份的设备 Restore 直接返回成功 是安全的空操作 宁可多记不可少记
            var ledger = new List<string>(touched);
            foreach (Target t in targets) if (!ledger.Contains(t.DeviceId)) ledger.Add(t.DeviceId);
            if (!SaveTouched(ledger))
            {
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.8"));
                return false;
            }

            object policyValue = useMask ? PolicySpecifiedProcessors : PolicyAllCloseProcessors;
            byte[] maskBytes = useMask ? MaskToBytes(preferredMask) : null;

            bool anyOk = false;
            var applied = new List<Target>();
            // 写失败之后回滚也失败的设备 注册表已经被改了却不在 applied 里
            //   不单独记下来的话 收敛名单那一步会把它抹掉 从此没人记得它
            var dirty = new List<string>();
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
                    // 回滚的返回值不能丢 回滚不掉说明注册表还是脏的 必须留在名单上
                    if (!(t.Policy.Restore() & t.Mask.Restore())) dirty.Add(t.DeviceId);
                    Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.6") + t.DeviceId + Lang.T("log.irqaffinityengine.7"));
                }
            }
            // 一台都没写成 名单收回原样 但回滚不掉的那几台得留着
            if (!anyOk)
            {
                foreach (string id in dirty) if (!touched.Contains(id)) touched.Add(id);
                SaveTouched(touched);
                return false;
            }

            foreach (Target t in applied) if (!touched.Contains(t.DeviceId)) touched.Add(t.DeviceId);
            foreach (string id in dirty) if (!touched.Contains(id)) touched.Add(id);
            // 收敛成真实名单 存不下就保持刚才那份超集 宁可多记不可少记
            SaveTouched(touched);

            Settings.Save(settingsKey, true);
            if (!Settings.Load(settingsKey, false))
            {
                // 还原的返回值不能丢 还原失败的那台必须留在名单里
                //   原来是不看返回值就把所有 applied 从名单里踢掉
                //   于是还原失败的设备既改了注册表 又没人记得它 永久失联
                var failed = new List<string>();
                foreach (Target t in applied)
                    if (!(t.Policy.Restore() & t.Mask.Restore())) failed.Add(t.DeviceId);
                var remaining = new List<string>();
                foreach (string id in touched)
                {
                    bool wasApplied = false;
                    foreach (Target t in applied)
                        if (string.Equals(t.DeviceId, id, StringComparison.OrdinalIgnoreCase)) { wasApplied = true; break; }
                    if (!wasApplied || failed.Contains(id)) remaining.Add(id);
                }
                SaveTouched(remaining);
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.9"));
                return false;
            }
            Settings.SaveStr(StampKey, useMask ? MakeStamp(Environment.ProcessorCount, preferredMask) : "");
            if (applied.Count > 0)
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.10") + applied.Count + Lang.T("log.irqaffinityengine.11")
                    + (useMask ? Lang.T("log.irqaffinityengine.12") + preferredMask.ToString("X") + " " : Lang.T("log.irqaffinityengine.13"))
                    + Lang.T("log.irqaffinityengine.14"));
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
                        detail = id + Lang.T("t.irqaffinityengine.15") + mask.ToString("X");
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
                reason = Lang.T("t.irqaffinityengine.16") + stamp;
            else
            {
                string detail;
                if (!AppliedMaskStale(out detail)) return false;
                reason = Lang.T("t.irqaffinityengine.17") + detail;
            }

            Logger.Log(logPrefix + " " + reason
                + Lang.T("log.irqaffinityengine.18") + Environment.ProcessorCount + Lang.T("log.irqaffinityengine.19"));
            bool ok = Disable(null);
            Logger.Log(ok
                ? logPrefix + Lang.T("log.irqaffinityengine.20")
                : logPrefix + Lang.T("log.irqaffinityengine.21"));
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

            // 存不下名单也是失败 不然还没还原干净的设备会从名单上消失
            if (!SaveTouched(stillDirty)) allOk = false;
            if (allOk)
            {
                Settings.Save(settingsKey, false);
                if (Settings.Load(settingsKey, true)) return false;
                Settings.SaveStr(StampKey, "");
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.22") + restored + Lang.T("log.irqaffinityengine.23"));
            }
            else
                Logger.Log(logPrefix + Lang.T("log.irqaffinityengine.24") + stillDirty.Count + Lang.T("log.irqaffinityengine.25"));
            return allOk;
        }
    }
}

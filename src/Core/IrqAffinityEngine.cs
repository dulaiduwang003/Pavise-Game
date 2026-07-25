// @author bdth 2074055628@qq.com
// 文件用途 中断亲和策略的通用引擎 供显卡和网卡等设备复用

using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace AegisApp
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

        public bool EnabledByAegis { get { return Settings.Load(settingsKey, false); } }

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

        // AssignmentSetOverride 的格式是有官方定义的：REG_BINARY 时长度不得超过本平台
        // KAFFINITY 的大小、字节序为小端（64 位 Windows 上 KAFFINITY 正好 8 字节）。
        // 真正没有定义的是多处理器组下的「组归属」——KAFFINITY 描述的是某一个组内的处理器，
        // 而这个注册表值没有任何方式指明是哪一组，所以多组系统一律跳过掩码。
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

        // 只读体检：报告这些设备的 MSI（消息信号中断）开关状态。
        // 现代驱动基本默认就开着，社区流传的「一键开 MSI」多数情况下是空操作；
        // 而对确实不支持 MSI 的设备强行写 MSISupported=1 可能导致设备无法启动，
        // 所以这里只报告、不代写，把判断留给用户。
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
                            Logger.Log("中断体检：" + id + " 未声明 MSI 支持（可能使用传统线中断）");
                        else if (Convert.ToInt64(v) == 0)
                            Logger.Log("中断体检：" + id + " 的 MSI 被显式关闭（MSISupported=0）");
                    }
                }
                catch { }
            }
        }

        public bool Enable(List<string> deviceIds)
        {
            ReportMsiState(deviceIds);
            if (deviceIds == null || deviceIds.Count == 0)
            {
                Logger.Log(logPrefix + "：未找到可用设备，未作修改");
                return false;
            }
            List<Target> targets = BuildTargets(deviceIds);
            bool useMask = !CpuTopology.MultiGroup && CpuTopology.BoostMask != 0 && CpuTopology.BoostMask != CpuTopology.AllMask;
            if (CpuTopology.MultiGroup)
                Logger.Log(logPrefix + "：多处理器组系统，掩码无法指明处理器组归属，仅使用 AllCloseProcessors");

            bool anyOk = false;
            var applied = new List<Target>();
            foreach (Target t in targets)
            {
                bool ok;
                if (useMask)
                    ok = t.Policy.Apply(PolicySpecifiedProcessors) & t.Mask.Apply(MaskToBytes(CpuTopology.BoostMask));
                else
                    ok = t.Policy.Apply(PolicyAllCloseProcessors);
                if (ok) { anyOk = true; applied.Add(t); }
                else
                {
                    t.Policy.Restore(); t.Mask.Restore();
                    Logger.Log(logPrefix + "：设备 " + t.DeviceId + " 写入或回读失败，已跳过");
                }
            }
            if (!anyOk) return false;

            // 先落盘"改过哪些设备"再置标志位：这份名单是唯一能在设备枚举不到时找回快照的凭据，
            // 记不上就等于制造了一批还原不了的注册表改动，宁可整轮撤销。
            var touched = LoadTouched();
            foreach (Target t in applied) if (!touched.Contains(t.DeviceId)) touched.Add(t.DeviceId);
            if (!SaveTouched(touched))
            {
                foreach (Target t in applied) { t.Policy.Restore(); t.Mask.Restore(); }
                Logger.Log(logPrefix + "：设备名单无法持久化，已撤销本轮注册表修改");
                return false;
            }

            Settings.Save(settingsKey, true);
            if (!Settings.Load(settingsKey, false))
            {
                foreach (Target t in applied) { t.Policy.Restore(); t.Mask.Restore(); }
                SaveTouched(new List<string>());
                Logger.Log(logPrefix + "状态标志无法持久化，已还原注册表修改");
                return false;
            }
            Logger.Log(logPrefix + "：已对 " + applied.Count + " 个设备写入"
                + (useMask ? "指定处理器策略（掩码 0x" + CpuTopology.BoostMask.ToString("X") + "）" : "邻近处理器策略")
                + "，需要重启该设备或重启电脑后生效");
            return true;
        }

        // 还原范围不能只依赖"当前还能枚举到的设备"：显卡状态变成非 OK、WMI 查询失败返回空表、
        // USB 网卡被拔掉，这些情况下改过的设备都枚举不到，会被静默跳过；而快照槽位是按设备 ID
        // 哈希命名的，一旦标志位被清掉就再也没有任何代码路径能找回它们。
        // 所以开启时把设备 ID 落盘，关闭时用"落盘列表 ∪ 当前枚举"作为还原范围。
        private string TouchedKey { get { return slotPrefix + "Touched"; } }

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
            // 没还原成功的设备必须继续留在落盘列表里，下次才有得重试
            SaveTouched(stillDirty);
            if (allOk)
            {
                Settings.Save(settingsKey, false);
                if (Settings.Load(settingsKey, true)) return false;
                Logger.Log(logPrefix + "：已还原 " + restored + " 个设备，需要重启该设备或重启电脑后生效");
            }
            else
                Logger.Log(logPrefix + "：仍有 " + stillDirty.Count + " 个设备未能还原，已保留记录待下次重试");
            return allOk;
        }
    }
}

// @author bdth 2074055628@qq.com
// 文件用途 有线以太网卡关中断节流并启用 RSS 降低网络延迟 仅对驱动声明支持该 keyword 的物理有线卡下手 记原值可精确还原
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    // 安全底线 只对 *IfType==6 且非 *PhysicalMediaType==9 的物理有线以太卡 且仅当实例键下存在
    //   Ndi\Params\<keyword> 声明该网卡驱动认这个 keyword 时才写值 绝不对未声明的 keyword 强写
    //   改完需 禁用再启用网卡 或重启系统才生效 本程序绝不自动重启或禁用网卡 只写注册表 + 提示
    internal static class NicLatencyTweak
    {
        // 网卡类 GUID 下逐实例键 NNNN 4 位实例号
        private const string ClassRoot =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
        private const string ListKey = "NicLatIfList";
        private const string FlagKey = "NicLatencyByPavise";

        private const string KwInterruptMod = "*InterruptModeration"; // 关中断节流 目标值 0
        private const string KwRss = "*RSS";                          // 启用 RSS 目标值 1

        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load(FlagKey, false); } }

        // 供 UI 判断该不该置灰 只探测不写入 通过判据 1+2 的网卡数
        //   判据 2 是物理有线以太 判据 1 是驱动在 Ndi\Params 里声明认这个 keyword
        //   两条都过才算这台机器有得可写 否则开关点了也只能弹一句"没有合格网卡"
        public static int QualifiedNicCount()
        {
            int n = 0;
            try
            {
                foreach (string nnnn in AllNicInstances())
                    if (IsWiredEthernet(nnnn) && HasAnyKeyword(nnnn)) n++;
            }
            catch { }
            return n;
        }

        private static bool HasAnyKeyword(string nnnn)
        {
            return SupportsKeyword(nnnn, KwInterruptMod) || SupportsKeyword(nnnn, KwRss);
        }

        // 枚举合格网卡逐个 keyword 写值 每个 keyword 写前先探测 Ndi\Params 支持 再由 ReversibleReg 记原值写后回读
        //   qualified 回传通过安全判据 1+2 的合格有线网卡数 供 UI 区分 未找到合格网卡（安全空操作）与 写入失败
        //   返回 true 表示至少写成功一块网卡 需管理员（HKLM）改完重启网卡或系统后生效
        public static bool Enable(out int qualified)
        {
            qualified = 0;
            lock (lk)
            {
                try
                {
                    var applied = new List<string>();
                    bool anyWrite = false;
                    foreach (string nnnn in AllNicInstances())
                    {
                        if (!IsWiredEthernet(nnnn)) continue; // 安全判据 2 只物理有线以太
                        // 判据 1 也要算进 qualified 否则有线但驱动不认这两个 keyword 的机器
                        //   会被当成"写入失败" 实际是压根没有可写的 keyword
                        if (!HasAnyKeyword(nnnn)) continue;
                        qualified++;

                        bool touched = false;
                        // 安全判据 1 仅当 Ndi\Params\<keyword> 存在才写 记原值 写后回读 不通过自动回滚该值
                        if (SupportsKeyword(nnnn, KwInterruptMod) && RegOf(nnnn, KwInterruptMod).Apply("0"))
                            touched = true;
                        if (SupportsKeyword(nnnn, KwRss) && RegOf(nnnn, KwRss).Apply("1"))
                            touched = true;
                        if (touched) { applied.Add(nnnn); anyWrite = true; }
                    }

                    if (qualified == 0)
                    {
                        Logger.Log(Lang.T("log.niclattweak.4")); // 未找到合格有线网卡 预期安全空操作
                        return false;
                    }
                    if (!anyWrite)
                    {
                        Logger.Log(Lang.T("log.niclattweak.5")); // 有合格网卡但无一 keyword 可写或写失败
                        return false;
                    }

                    Settings.SaveStr(ListKey, string.Join(";", applied.ToArray()));
                    Settings.Save(FlagKey, true);
                    Logger.Log(Lang.F("log.niclattweak.3", applied.Count));
                    return true;
                }
                catch { return false; }
            }
        }

        public static bool Disable() { return Restore(); }

        public static bool HasResidue()
        {
            if (Settings.Load(FlagKey, false)) return true;
            if (ParseList(Settings.LoadStr(ListKey, "")).Length > 0) return true;
            foreach (string nnnn in AllNicInstances())
                if (RegOf(nnnn, KwInterruptMod).HasBackup || RegOf(nnnn, KwRss).HasBackup)
                    return true;
            return false;
        }

        // 按台账精确还原 原无则删 原有则写回原值 台账外再叠加全实例扫描兜孤儿快照
        public static bool Restore()
        {
            lock (lk)
            {
                try
                {
                    var insts = new List<string>(ParseList(Settings.LoadStr(ListKey, "")));
                    foreach (string nnnn in AllNicInstances())
                    {
                        bool known = false;
                        foreach (string n in insts)
                            if (string.Equals(n, nnnn, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
                        if (!known) insts.Add(nnnn);
                    }
                    bool all = true;
                    foreach (string nnnn in insts)
                    {
                        all &= RegOf(nnnn, KwInterruptMod).Restore();
                        all &= RegOf(nnnn, KwRss).Restore();
                    }
                    if (all)
                    {
                        Settings.SaveStr(ListKey, "");
                        Settings.Save(FlagKey, false);
                        Logger.Log(Lang.T("log.niclattweak.1"));
                    }
                    else Logger.Log(Lang.T("log.niclattweak.2"));
                    return all;
                }
                catch { return false; }
            }
        }

        // NDIS keyword 在实例键下按 REG_SZ 字符串持久化（"0"/"1"）ReversibleReg 记原值原类型 还原精确
        private static ReversibleReg RegOf(string nnnn, string keyword)
        {
            string tag = keyword == KwRss ? "RSS" : "IM";
            return new ReversibleReg(Registry.LocalMachine, ClassRoot + "\\" + nnnn, keyword,
                RegistryValueKind.String, "NicLat_" + tag + "_" + nnnn);
        }

        // 枚举网卡类下 4 位数字实例键 跳过 Properties 等非实例子键
        private static string[] AllNicInstances()
        {
            try
            {
                using (var root = Registry.LocalMachine.OpenSubKey(ClassRoot))
                {
                    if (root == null) return new string[0];
                    var list = new List<string>();
                    foreach (string name in root.GetSubKeyNames())
                        if (IsInstanceName(name)) list.Add(name);
                    return list.ToArray();
                }
            }
            catch { return new string[0]; }
        }

        private static bool IsInstanceName(string name)
        {
            if (name == null || name.Length != 4) return false;
            foreach (char c in name) if (c < '0' || c > '9') return false;
            return true;
        }

        // 安全判据 2 物理有线以太 *IfType==6(IF_TYPE_ETHERNET_CSMACD) 且非 *PhysicalMediaType==9(Native802_11 无线)
        //   拿不准（缺 *IfType）一律跳过 宁可漏不可误伤无线卡
        private static bool IsWiredEthernet(string nnnn)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(ClassRoot + "\\" + nnnn))
                {
                    if (k == null) return false;
                    if (ReadInt(k, "*IfType", -1) != 6) return false;
                    if (ReadInt(k, "*PhysicalMediaType", -1) == 9) return false;
                    return true;
                }
            }
            catch { return false; }
        }

        // 安全判据 1 只有实例键下存在子键 Ndi\Params\<keyword> 才说明该网卡驱动认这个 keyword 允许写
        private static bool SupportsKeyword(string nnnn, string keyword)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(
                    ClassRoot + "\\" + nnnn + "\\Ndi\\Params\\" + keyword))
                    return k != null;
            }
            catch { return false; }
        }

        // 这些标准属性可能存成 REG_SZ 或 REG_DWORD 统一转 int 读不到或解析失败回退
        private static int ReadInt(RegistryKey key, string name, int fallback)
        {
            try
            {
                object v = key.GetValue(name);
                if (v == null) return fallback;
                if (v is int) return (int)v;
                int n;
                return int.TryParse(v.ToString().Trim(), out n) ? n : fallback;
            }
            catch { return fallback; }
        }

        internal static string[] ParseList(string raw)
        {
            return (raw ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}

// @author bdth 2074055628@qq.com
// 文件用途 量出哪个驱动的中断最长 并把设备的中断钉到指定的几个核上
//
// 为什么不挂在帧归因下面
//   驱动的 DPC 有多长 落在哪几个核上 是可以直接量的
//   它不需要先回答 这一帧为什么慢 那套判决在三台真机上从来没成立过
//   把能量的东西押在量不出来的东西后面 结果就是一次都触发不了
//
// 为什么不常驻
//   按需扫描 十几秒就够 中断会话只订 DPC 与 ISR 比全系统线程切换便宜两个数量级
//   常驻观察在真机上实测 0.31 到 0.67 占用 而且守卫会把自己掐停
//
// 怎么验证动作真的生效
//   不用 1% 最差帧 那个噪声太大 要打一局才有统计意义 还分不清是不是回归均值
//   直接看这个驱动的 DPC 落在哪几个核上 挪之前散在全部核 挪之后应当收敛到目标核
//   这是对动作本身的直接验证 一次扫描就看得见

using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed class IrqCandidate
    {
        public string Driver = "";
        public long Dpc;
        public double TotalUs;
        public double MaxUs;
        public long Over500Us;
        public long Over1Ms;
        public ulong CpuMask;
        public readonly List<string> DeviceIds = new List<string>();
        public string Why = "";
        public bool Actionable { get { return DeviceIds.Count > 0; } }
        public bool Moved;
    }

    internal sealed class IrqScanResult
    {
        public bool Ok;
        public string Error = "";
        public int Seconds;
        public readonly List<IrqCandidate> Candidates = new List<IrqCandidate>();
    }

    internal static class IrqRelocate
    {
        private static readonly IrqAffinityEngine engine =
            new IrqAffinityEngine("IrqRelocateOn", "IrqReloc_", Lang.T("irqmove.logprefix"));

        // 单次 DPC 短于这个就不值得动 挪核换不回一帧 只是白改注册表
        internal const double MinMaxUsToOffer = 200.0;

        public static bool Applied { get { return engine.EnabledByPavise; } }
        public static bool HasResidue { get { return engine.HasResidue; } }

        // 目标核是每台设备自己的 不是全局一个
        //   显卡和 USB 主控凭什么用同一组核 本来就没道理
        //   而且写进注册表的掩码就在设备自己那儿 读回来就是它的目标 不用另外存一份
        //   于是没有全局目标 也就不需要 改了目标要把已钉的都改写一遍 那套东西
        //
        // 自动推荐只是弹窗里的默认值 用户可以不接受
        //   这个算法在不同架构上给的不是同一种东西 大小核给性能核 其余架构给后台核
        //   哪种对没有实测证据 所以它只能是默认值 不能是唯一选择
        public static ulong AutoMask()
        {
            ulong m = CpuTopology.InterruptMask;
            if (m == 0 || m == CpuTopology.AllMask) m = CpuTopology.ThrottleMask;
            return m;
        }

        // 越界的核会让设备拿不到中断资源 全选等于没选 引擎见到全核掩码会跳过不写掩码
        // 这两种都当成非法 返回 0 让调用方拒绝 而不是写一个坏值进注册表
        internal static ulong Sanitize(ulong mask)
        {
            ulong all = CpuTopology.AllMask;
            if (all != 0) mask &= all;
            if (mask == 0 || mask == all) return 0;
            return mask;
        }

        // 把一台设备钉到它自己的那组核上
        public static bool ApplyDevice(string deviceId, ulong mask)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;
            ulong keep = Sanitize(mask);
            if (keep == 0) return false;
            if (!Native.IsElevated()) { Logger.Log(Lang.T("irqmove.needadmin")); return false; }
            foreach (string g in OwnedByOtherTweaks())
                if (string.Equals(g, deviceId, StringComparison.OrdinalIgnoreCase))
                { Logger.Log(Lang.T("irqmove.ownedelsewhere")); return false; }
            return engine.Enable(new List<string>(new string[] { deviceId }), keep);
        }

        // 阻塞若干秒 调用方自己放后台线程
        public static IrqScanResult Scan(int seconds)
        {
            var r = new IrqScanResult();
            if (seconds < 5) seconds = 15;
            if (seconds > 60) seconds = 60;
            r.Seconds = seconds;

            var ia = new InterruptAttribution();
            InterruptAttributionResult raw = null;
            try
            {
                if (!ia.Start()) { r.Error = Lang.T("irqmove.nosession"); return r; }
                Thread.Sleep(seconds * 1000);
                raw = ia.Stop();
            }
            catch (Exception ex) { r.Error = ex.GetType().Name; }
            finally { try { ia.Stop(); } catch { } }

            if (raw == null || raw.Drivers == null || raw.Drivers.Count == 0)
            {
                if (r.Error.Length == 0) r.Error = Lang.T("irqmove.nodata");
                return r;
            }

            List<string> touched = engine.TouchedDevices();
            foreach (DriverInterrupt d in raw.Drivers)
            {
                var c = new IrqCandidate();
                c.Driver = d.Driver ?? "?";
                c.Dpc = d.Dpc;
                c.TotalUs = d.DpcTotalUs;
                c.MaxUs = d.DpcMaxUs;
                c.Over500Us = d.DpcOver500Us;
                c.Over1Ms = d.DpcOver1Ms;
                c.CpuMask = d.CpuMask;

                if (d.DpcMaxUs < MinMaxUsToOffer)
                    c.Why = Lang.F("irqmove.tooshort", d.DpcMaxUs.ToString("F0"), MinMaxUsToOffer.ToString("F0"));
                else
                {
                    DriverDeviceMatch m = DriverDeviceResolver.Resolve(c.Driver);
                    if (!m.Actionable) c.Why = m.Why;
                    else
                    {
                        List<string> owned = OwnedByOtherTweaks();
                        foreach (string id in m.DeviceIds)
                        {
                            bool taken = false;
                            foreach (string g in owned)
                                if (string.Equals(g, id, StringComparison.OrdinalIgnoreCase)) { taken = true; break; }
                            if (!taken) c.DeviceIds.Add(id);
                            else c.Why = Lang.T("irqmove.ownedelsewhere");
                        }
                        foreach (string id in c.DeviceIds)
                            foreach (string t in touched)
                                if (string.Equals(t, id, StringComparison.OrdinalIgnoreCase)) c.Moved = true;
                    }
                }
                r.Candidates.Add(c);
            }
            r.Candidates.Sort(delegate (IrqCandidate a, IrqCandidate b)
            { return b.MaxUs.CompareTo(a.MaxUs); });
            r.Ok = true;
            return r;
        }

        // 台架用的按驱动整批钉 界面走的是 ApplyDevice
        // 这里也要过一遍 Sanitize 推荐值等于全核时引擎会跳过写掩码 退成策略 1
        // 那和调用方想要的 钉到指定核 正好相反 与其写错不如不写
        public static bool Apply(IrqCandidate c)
        {
            if (c == null || !c.Actionable) return false;
            ulong keep = Sanitize(AutoMask());
            if (keep == 0) return false;
            if (!Native.IsElevated()) { Logger.Log(Lang.T("irqmove.needadmin")); return false; }
            // 扫描时过滤过一次 但扫完到动手之间别的开关可能刚被打开 这里再挡一道
            List<string> owned = OwnedByOtherTweaks();
            foreach (string id in c.DeviceIds)
                foreach (string g in owned)
                    if (string.Equals(g, id, StringComparison.OrdinalIgnoreCase))
                    { Logger.Log(Lang.T("irqmove.ownedelsewhere")); return false; }
            return engine.Enable(c.DeviceIds, keep);
        }

        public static bool Revert() { return engine.Disable(null); }

        // 给中断页用 哪些设备已经被别的开关占着 中断页就不能再压同一个注册表值
        public static List<string> OwnedElsewhere() { return OwnedByOtherTweaks(); }

        public static void HealFromCrash()
        {
            try { if (engine.HasResidue && !engine.EnabledByPavise) Revert(); } catch { }
        }

        // 换过 CPU 或核心数变了以后 上次钉进去的掩码可能指向已经不存在的核
        // 那样的设备拿不到中断资源 只能整条还原掉 不能留在那儿
        public static bool HealStaleMask() { return engine.HealStaleMask(); }

        // 已经有别的开关在管的设备必须让开 两套备份压同一个注册表值时
        // 还原结果取决于顺序 那等于把还原做成了随机数
        // 显卡那个开关在 1.8.1.2 退役了 显卡现在就是这一页里普通的一台设备
        // 剩下网卡还在自己管 因为它同时要配 QoS 策略 不只是改中断亲和
        private static List<string> OwnedByOtherTweaks()
        {
            var owned = new List<string>();
            try
            {
                if (NetworkAffinityTweak.EnabledByPavise)
                    foreach (string id in NetworkAffinityTweak.EnumerateNicDeviceIds())
                        if (!Has(owned, id)) owned.Add(id);
            }
            catch { }
            // 三代退役亲和台账里动过的设备同样不能再钉
            // 否则会把旧残留值当原值存进自己的快照 清除时两套备份互相覆盖
            try
            {
                foreach (string id in RetiredAffinityLedgers.TouchedDevices())
                    if (!Has(owned, id)) owned.Add(id);
            }
            catch { }
            return owned;
        }

        private static bool Has(List<string> list, string id)
        {
            if (id == null || id.Length == 0) return true;
            foreach (string had in list)
                if (string.Equals(had, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static string MaskText(ulong mask)
        {
            if (mask == 0) return "?";
            var sb = new System.Text.StringBuilder();
            int run = -1;
            for (int i = 0; i <= 64; i++)
            {
                bool on = i < 64 && (mask & (1UL << i)) != 0;
                if (on && run < 0) run = i;
                else if (!on && run >= 0)
                {
                    if (sb.Length > 0) sb.Append(',');
                    if (i - 1 == run) sb.Append(run);
                    else sb.Append(run).Append('-').Append(i - 1);
                    run = -1;
                }
            }
            return sb.ToString();
        }
    }
}

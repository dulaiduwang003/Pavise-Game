// @author bdth 2074055628@qq.com
// 文件用途 三代已退役中断亲和开关的台账总表 既是它们各自的引擎来源 也是在役 IrqRelocate 的占用判据
//
// 为什么要有这张表
//   退役壳原本各建各的引擎 谁也不知道谁碰过哪台设备
//   IrqRelocate 钉设备前只查了 NetworkAffinityTweak 查不到这三份旧台账
//   于是可能把旧残留值当成"原值"存进自己的快照 清除时两套备份互相覆盖
//   把三份台账收到一处 占用判据才有单一数据源
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class RetiredAffinityLedgers
    {
        public static readonly IrqAffinityEngine Gpu =
            new IrqAffinityEngine("IrqAffinityOnByPavise", "IrqAff_", Lang.T("t.retiredirq.1"));

        public static readonly IrqAffinityEngine Usb =
            new IrqAffinityEngine("UsbAffinityOnByPavise", "UsbAff_", Lang.T("t.usbinterruptaffinitytweak.1"));

        public static readonly IrqAffinityEngine Storage =
            new IrqAffinityEngine("StorAffinityOnByPavise", "StorAff_", Lang.T("t.storageaffinitytweak.1"));

        private static readonly IrqAffinityEngine[] All = { Gpu, Usb, Storage };

        // 任一代还有残留 用于判断是否需要延迟在役功能的清理
        public static bool AnyResidue
        {
            get
            {
                foreach (IrqAffinityEngine e in All) if (e.HasResidue) return true;
                return false;
            }
        }

        // 三份台账里所有被动过的设备 去重后返回 供在役功能避让
        public static List<string> TouchedDevices()
        {
            var ids = new List<string>();
            foreach (IrqAffinityEngine e in All)
            {
                foreach (string id in e.TouchedDevices())
                {
                    if (id == null || id.Length == 0) continue;
                    bool dup = false;
                    foreach (string had in ids)
                        if (string.Equals(had, id, System.StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                    if (!dup) ids.Add(id);
                }
            }
            return ids;
        }
    }
}

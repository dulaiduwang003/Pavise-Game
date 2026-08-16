// @author bdth 2074055628@qq.com
// 文件用途 硬盘控制器中断亲和退役壳 仅保留升级/卸载还原
// 与 USB 中断亲和同投 E 核 对延迟敏感设备是双刃剑 已下架
// Disable() 基于持久 Touched 列表还原设备中断策略到系统默认 不依赖重新枚举设备

namespace PaviseApp
{
    internal static class StorageAffinityTweak
    {
        private static readonly IrqAffinityEngine irqEngine =
            new IrqAffinityEngine("StorAffinityOnByPavise", "StorAff_", Lang.T("t.storageaffinitytweak.1"));

        public static bool HasResidue { get { return irqEngine.HasResidue; } }

        public static bool Disable() { return irqEngine.Disable(null); }
    }
}

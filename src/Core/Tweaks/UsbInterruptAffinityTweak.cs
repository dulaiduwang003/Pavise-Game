// @author bdth 2074055628@qq.com
// 文件用途 USB 控制器中断亲和退役壳 仅保留升级/卸载还原
// 曾在 P 核少的混合架构上把 USB 中断投到低频 E 核 造成高回报率鼠标空闲后首次点击延迟 1.8.1.0 下架
// Disable() 基于持久 Touched 列表还原设备中断策略到系统默认 不依赖重新枚举设备

namespace PaviseApp
{
    internal static class UsbInterruptAffinityTweak
    {
        private static readonly IrqAffinityEngine irqEngine =
            new IrqAffinityEngine("UsbAffinityOnByPavise", "UsbAff_", Lang.T("t.usbinterruptaffinitytweak.1"));

        public static bool HasResidue { get { return irqEngine.HasResidue; } }

        public static bool Disable() { return irqEngine.Disable(null); }
    }
}

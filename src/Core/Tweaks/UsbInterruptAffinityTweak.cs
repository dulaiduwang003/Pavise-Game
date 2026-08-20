// @author bdth 2074055628@qq.com
// 文件用途 USB 控制器中断亲和退役壳 仅保留升级/卸载还原
namespace PaviseApp
{
    internal static class UsbInterruptAffinityTweak
    {
        private static readonly IrqAffinityEngine irqEngine = RetiredAffinityLedgers.Usb;

        public static bool HasResidue { get { return irqEngine.HasResidue; } }

        public static bool Disable() { return irqEngine.Disable(null); }
    }
}

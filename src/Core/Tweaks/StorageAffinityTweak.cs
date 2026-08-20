// @author bdth 2074055628@qq.com
// 文件用途 硬盘控制器中断亲和退役壳 仅保留升级/卸载还原
namespace PaviseApp
{
    internal static class StorageAffinityTweak
    {
        private static readonly IrqAffinityEngine irqEngine = RetiredAffinityLedgers.Storage;

        public static bool HasResidue { get { return irqEngine.HasResidue; } }

        public static bool Disable() { return irqEngine.Disable(null); }
    }
}

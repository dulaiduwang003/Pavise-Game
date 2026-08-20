// @author bdth 2074055628@qq.com
// 文件用途 把 1.8.1.2 之前那个显卡中断亲和开关留下的注册表改动还原掉
//
// 为什么类删了还要留这个
//   旧开关在用户机器上已经写过 DevicePolicy 与 AssignmentSetOverride
//   直接删代码等于把还原能力一起删了 那些值会永远留在注册表里
//   引擎的 Disable(null) 从 IrqAff_Touched 读设备清单 不需要再枚举显卡
//   所以这里只要复原出同样的键前缀 就能把旧残留完整还回去
//
// 这个文件在确认线上不再有 IrqAff_ 残留之后才可以删

namespace PaviseApp
{
    internal static class RetiredIrqAffinity
    {
        private static readonly IrqAffinityEngine engine = RetiredAffinityLedgers.Gpu;

        public static bool HasResidue { get { return engine.HasResidue; } }

        public static bool Disable() { return engine.Disable(null); }
    }
}

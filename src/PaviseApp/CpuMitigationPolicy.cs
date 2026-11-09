using System;
using System.Globalization;

namespace PaviseApp
{
    internal static class CpuMitigationPolicy
    {
        internal const string ExpandedPreferenceKey = "EnvCpuMitigationsExpandedV1";

        // 依据 KB4073119，3/3 选择文档中的组合关闭策略。0x8 显式启用 SSBD，0x40 显式启用 AMD 用户态及内核态 BTI。保留其他覆盖位和掩码位，包括新增缓解项；配置策略本身不能证明全部缓解均已停用。



        internal static string WantedOverride(string original)
        {
            return Encode((Decode(original) & ~0x48u) | 3u);
        }

        internal static string WantedMask(string original)
        {
            return Encode(Decode(original) | 3u);
        }

        internal static bool Supports(int build, string vendor)
        {
            return build >= 19041 && (string.Equals(vendor, "GenuineIntel", StringComparison.OrdinalIgnoreCase)
                || string.Equals(vendor, "AuthenticAMD", StringComparison.OrdinalIgnoreCase));
        }

        private static uint Decode(string value)
        {
            if (value == "absent") return 0;
            if (value == null || !value.StartsWith("d:", StringComparison.Ordinal))
                throw new FormatException("Expected a DWORD setting");
            return unchecked((uint)int.Parse(value.Substring(2), CultureInfo.InvariantCulture));
        }

        private static string Encode(uint value)
        {
            return "d:" + unchecked((int)value).ToString(CultureInfo.InvariantCulture);
        }
    }
}

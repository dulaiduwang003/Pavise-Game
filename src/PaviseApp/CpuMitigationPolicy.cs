using System;
using System.Globalization;

namespace PaviseApp
{
	internal static class CpuMitigationPolicy
	{
		internal const string ExpandedPreferenceKey = "EnvCpuMitigationsExpandedV1";

		// KB4073119: 3/3 selects the documented combined disable policy.
		// 0x8 explicitly enables SSBD; 0x40 explicitly enables AMD user/kernel BTI.
		// Preserve every other override and mask bit, including newer mitigations.
		// This is a configuration policy, not proof that every mitigation is inactive.
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

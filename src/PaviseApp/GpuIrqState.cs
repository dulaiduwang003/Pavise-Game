using System;
using System.Globalization;

namespace PaviseApp
{
	internal sealed class GpuIrqState
	{
		public GpuIrqPhase Phase;

		public string DeviceId = "";

		public string DeviceName = "";

		public string DriverVersion = "";

		public string Reason = "";

		public ulong Target;

		public ulong Region;

		public ulong Seen;

		public double BaselineP99;

		public long CheckedTicks;

		internal string Encode()
		{
			string[] textEntries = new string[20]
			{
				"1|", null, null, null, null, null, null, null, null, null,
				null, null, null, null, null, null, null, null, null, null
			};
			int phase = (int)Phase;
			textEntries[1] = phase.ToString(CultureInfo.InvariantCulture);
			textEntries[2] = "|";
			textEntries[3] = Uri.EscapeDataString(DeviceId ?? "");
			textEntries[4] = "|";
			textEntries[5] = Uri.EscapeDataString(DeviceName ?? "");
			textEntries[6] = "|";
			textEntries[7] = Uri.EscapeDataString(DriverVersion ?? "");
			textEntries[8] = "|";
			textEntries[9] = Uri.EscapeDataString(Reason ?? "");
			textEntries[10] = "|";
			textEntries[11] = Target.ToString("X");
			textEntries[12] = "|";
			textEntries[13] = Region.ToString("X");
			textEntries[14] = "|";
			textEntries[15] = Seen.ToString("X");
			textEntries[16] = "|";
			textEntries[17] = BaselineP99.ToString("R", CultureInfo.InvariantCulture);
			textEntries[18] = "|";
			textEntries[19] = CheckedTicks.ToString(CultureInfo.InvariantCulture);
			return string.Concat(textEntries);
		}

		internal static GpuIrqState Parse(string raw)
		{
			GpuIrqState gpuIrqState = new GpuIrqState();
			if (string.IsNullOrEmpty(raw))
			{
				return gpuIrqState;
			}
			string[] rawParts = raw.Split('|');
			int result;
			ulong result2;
			ulong result3;
			ulong result4;
			double result5;
			long result6;
			if (rawParts.Length != 11 || rawParts[0] != "1" || !int.TryParse(rawParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out result) || result < 0 || result > 6 || !ulong.TryParse(rawParts[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result2) || !ulong.TryParse(rawParts[7], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result3) || !ulong.TryParse(rawParts[8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result4) || !double.TryParse(rawParts[9], NumberStyles.Float, CultureInfo.InvariantCulture, out result5) || !long.TryParse(rawParts[10], NumberStyles.None, CultureInfo.InvariantCulture, out result6))
			{
				return gpuIrqState;
			}
			try
			{
				gpuIrqState.DeviceId = Uri.UnescapeDataString(rawParts[2]);
				gpuIrqState.DeviceName = Uri.UnescapeDataString(rawParts[3]);
				gpuIrqState.DriverVersion = Uri.UnescapeDataString(rawParts[4]);
				gpuIrqState.Reason = Uri.UnescapeDataString(rawParts[5]);
			}
			catch
			{
				return new GpuIrqState();
			}
			gpuIrqState.Phase = (GpuIrqPhase)result;
			gpuIrqState.Target = result2;
			gpuIrqState.Region = result3;
			gpuIrqState.Seen = result4;
			gpuIrqState.BaselineP99 = result5;
			gpuIrqState.CheckedTicks = result6;
			return gpuIrqState;
		}
	}
}

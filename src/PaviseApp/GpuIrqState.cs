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
			string[] array = new string[20]
			{
				"1|", null, null, null, null, null, null, null, null, null,
				null, null, null, null, null, null, null, null, null, null
			};
			int phase = (int)Phase;
			array[1] = phase.ToString(CultureInfo.InvariantCulture);
			array[2] = "|";
			array[3] = Uri.EscapeDataString(DeviceId ?? "");
			array[4] = "|";
			array[5] = Uri.EscapeDataString(DeviceName ?? "");
			array[6] = "|";
			array[7] = Uri.EscapeDataString(DriverVersion ?? "");
			array[8] = "|";
			array[9] = Uri.EscapeDataString(Reason ?? "");
			array[10] = "|";
			array[11] = Target.ToString("X");
			array[12] = "|";
			array[13] = Region.ToString("X");
			array[14] = "|";
			array[15] = Seen.ToString("X");
			array[16] = "|";
			array[17] = BaselineP99.ToString("R", CultureInfo.InvariantCulture);
			array[18] = "|";
			array[19] = CheckedTicks.ToString(CultureInfo.InvariantCulture);
			return string.Concat(array);
		}

		internal static GpuIrqState Parse(string raw)
		{
			GpuIrqState gpuIrqState = new GpuIrqState();
			if (string.IsNullOrEmpty(raw))
			{
				return gpuIrqState;
			}
			string[] array = raw.Split('|');
			int result;
			ulong result2;
			ulong result3;
			ulong result4;
			double result5;
			long result6;
			if (array.Length != 11 || array[0] != "1" || !int.TryParse(array[1], NumberStyles.None, CultureInfo.InvariantCulture, out result) || result < 0 || result > 6 || !ulong.TryParse(array[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result2) || !ulong.TryParse(array[7], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result3) || !ulong.TryParse(array[8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result4) || !double.TryParse(array[9], NumberStyles.Float, CultureInfo.InvariantCulture, out result5) || !long.TryParse(array[10], NumberStyles.None, CultureInfo.InvariantCulture, out result6))
			{
				return gpuIrqState;
			}
			try
			{
				gpuIrqState.DeviceId = Uri.UnescapeDataString(array[2]);
				gpuIrqState.DeviceName = Uri.UnescapeDataString(array[3]);
				gpuIrqState.DriverVersion = Uri.UnescapeDataString(array[4]);
				gpuIrqState.Reason = Uri.UnescapeDataString(array[5]);
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

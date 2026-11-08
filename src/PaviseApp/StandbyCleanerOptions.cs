using System;
using System.Globalization;

namespace PaviseApp
{
	internal sealed class StandbyCleanerOptions
	{
		public const int MaximumMegabytes = 1048576;

		public const int MinimumPollingMilliseconds = 250;

		public const int MaximumPollingMilliseconds = 300000;

		private static readonly StandbyCleanerOptions defaults = new StandbyCleanerOptions(1024, 1024, 4000);

		private static readonly StandbyCleanerOptions conservative = new StandbyCleanerOptions(2048, 512, 8000);

		private static readonly StandbyCleanerOptions aggressive = new StandbyCleanerOptions(512, 1536, 2000);

		private readonly int listMegabytes;

		private readonly int freeMegabytes;

		private readonly int pollingMilliseconds;

		public static StandbyCleanerOptions Default
		{
			get
			{
				return defaults;
			}
		}

		public static StandbyCleanerOptions Conservative
		{
			get
			{
				return conservative;
			}
		}

		public static StandbyCleanerOptions Aggressive
		{
			get
			{
				return aggressive;
			}
		}

		public int ListMegabytes
		{
			get
			{
				return listMegabytes;
			}
		}

		public int FreeMegabytes
		{
			get
			{
				return freeMegabytes;
			}
		}

		public int PollingMilliseconds
		{
			get
			{
				return pollingMilliseconds;
			}
		}

		public bool IsValid
		{
			get
			{
				if (listMegabytes >= 0 && listMegabytes <= 1048576 && freeMegabytes >= 0 && freeMegabytes <= 1048576 && pollingMilliseconds >= 250)
				{
					return pollingMilliseconds <= 300000;
				}
				return false;
			}
		}

		public StandbyCleanerOptions(int listMegabytes, int freeMegabytes, int pollingMilliseconds)
		{
			this.listMegabytes = listMegabytes;
			this.freeMegabytes = freeMegabytes;
			this.pollingMilliseconds = pollingMilliseconds;
		}

		public string Serialize()
		{
			if (!IsValid)
			{
				throw new InvalidOperationException("Invalid standby cleaner options.");
			}
			return "1|" + listMegabytes.ToString(CultureInfo.InvariantCulture) + "|" + freeMegabytes.ToString(CultureInfo.InvariantCulture) + "|" + pollingMilliseconds.ToString(CultureInfo.InvariantCulture);
		}

		public static bool TryParse(string value, out StandbyCleanerOptions options)
		{
			options = null;
			if (string.IsNullOrEmpty(value) || value.Length > 64)
			{
				return false;
			}
			string[] valueParts = value.Split('|');
			int result;
			int result2;
			int result3;
			if (valueParts.Length != 4 || valueParts[0] != "1" || !int.TryParse(valueParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out result) || !int.TryParse(valueParts[2], NumberStyles.None, CultureInfo.InvariantCulture, out result2) || !int.TryParse(valueParts[3], NumberStyles.None, CultureInfo.InvariantCulture, out result3))
			{
				return false;
			}
			StandbyCleanerOptions standbyCleanerOptions = new StandbyCleanerOptions(result, result2, result3);
			if (!standbyCleanerOptions.IsValid)
			{
				return false;
			}
			options = standbyCleanerOptions;
			return true;
		}
	}
}

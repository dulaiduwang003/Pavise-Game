using System;

namespace PaviseApp
{
	internal static class ApplyFailureText
	{
		public static string Of(string detail)
		{
			if (string.IsNullOrEmpty(detail))
			{
				return Lang.T("t.backgroundpressurecontroller.1");
			}
			bool matchesIndexOf = detail.IndexOf("-write", StringComparison.Ordinal) >= 0;
			bool matchesIndexOf2 = detail.IndexOf("-readback", StringComparison.Ordinal) >= 0;
			if (matchesIndexOf && matchesIndexOf2)
			{
				return Lang.T("t.backgroundpressurecontroller.2");
			}
			if (matchesIndexOf2)
			{
				return Lang.T("t.backgroundpressurecontroller.3");
			}
			if (matchesIndexOf)
			{
				return Lang.T("t.backgroundpressurecontroller.4");
			}
			return Lang.T("t.backgroundpressurecontroller.1") + "(" + detail + ")";
		}
	}
}

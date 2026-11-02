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
			bool flag = detail.IndexOf("-write", StringComparison.Ordinal) >= 0;
			bool flag2 = detail.IndexOf("-readback", StringComparison.Ordinal) >= 0;
			if (flag && flag2)
			{
				return Lang.T("t.backgroundpressurecontroller.2");
			}
			if (flag2)
			{
				return Lang.T("t.backgroundpressurecontroller.3");
			}
			if (flag)
			{
				return Lang.T("t.backgroundpressurecontroller.4");
			}
			return Lang.T("t.backgroundpressurecontroller.1") + "(" + detail + ")";
		}
	}
}

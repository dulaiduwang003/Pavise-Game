using System;

namespace PaviseApp
{
	internal static class HardThreadRawHandleClosure
	{
		internal static void Close(ref IntPtr handle, ref bool uncertain, Func<IntPtr, bool> close)
		{
			if (close == null)
			{
				throw new ArgumentNullException("close");
			}
			if (uncertain)
			{
				throw new HardThreadClosureFailure("Original handle closure is unconfirmed");
			}
			if (!(handle == IntPtr.Zero))
			{
				IntPtr arg = handle;
				handle = IntPtr.Zero;
				try
				{
					uncertain = !close(arg);
				}
				catch (Exception inner)
				{
					uncertain = true;
					throw new HardThreadClosureFailure("Original handle closure is unconfirmed", inner);
				}
				if (uncertain)
				{
					throw new HardThreadClosureFailure("Original handle closure is unconfirmed");
				}
			}
		}
	}
}

using System;
using System.Collections.Generic;

namespace PaviseApp
{
	internal static class HardThreadSpawnHandleClosure
	{
		internal static void Close(ref IntPtr thread, ref bool threadUncertain, ref IntPtr process, ref bool processUncertain, bool releaseProcess, Func<IntPtr, bool> closeRaw)
		{
			List<Exception> list = new List<Exception>();
			try
			{
				HardThreadRawHandleClosure.Close(ref thread, ref threadUncertain, closeRaw);
			}
			catch (Exception item)
			{
				list.Add(item);
			}
			if (releaseProcess)
			{
				try
				{
					HardThreadRawHandleClosure.Close(ref process, ref processUncertain, closeRaw);
				}
				catch (Exception item2)
				{
					list.Add(item2);
				}
			}
			if (list.Count != 0)
			{
				throw new AggregateException("Original spawn handles remain unconfirmed", list);
			}
		}
	}
}

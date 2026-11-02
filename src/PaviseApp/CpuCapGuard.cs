using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
	internal static class CpuCapGuard
	{
		internal const string Argument = "--background-cpu-cap-guard";

		internal static bool TryHandleArgs(string[] args)
		{
			if (args.Length == 0 || args[0] != "--background-cpu-cap-guard")
			{
				return false;
			}
			Environment.ExitCode = 1;
			int result;
			long result2;
			if (args.Length != 4 || !int.TryParse(args[1], out result) || result <= 4 || !long.TryParse(args[2], out result2) || result2 <= 0 || !args[3].StartsWith("Local\\Pavise.CpuCap.V1.", StringComparison.Ordinal) || args[3].Length != "Local\\Pavise.CpuCap.V1.".Length + 33)
			{
				return true;
			}
			IntPtr intPtr = Native.OpenProcess(1052672, false, result);
			if (intPtr == IntPtr.Zero)
			{
				return true;
			}
			try
			{
				long creation;
				long cpu;
				ulong io;
				if (!Native.QueryProcessSample(intPtr, out creation, out cpu, out io) || creation != result2)
				{
					return true;
				}
				Environment.ExitCode = ((!Run(intPtr, args[3])) ? 1 : 0);
			}
			catch
			{
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
			return true;
		}

		private static bool Run(IntPtr parent, string prefix)
		{
			BlockingCollection<string> commands = new BlockingCollection<string>(32);
			Thread thread = new Thread((ThreadStart)delegate
			{
				try
				{
					string text;
					while ((text = Console.ReadLine()) != null && text.Length <= 256 && commands.TryAdd(text))
					{
					}
				}
				catch
				{
				}
				finally
				{
					commands.CompleteAdding();
				}
			});
			thread.IsBackground = true;
			thread.Start();
			Dictionary<string, IntPtr> dictionary = new Dictionary<string, IntPtr>();
			Dictionary<string, IntPtr> dictionary2 = new Dictionary<string, IntPtr>();
			bool flag = true;
			try
			{
				Console.WriteLine("READY");
				Stopwatch stopwatch = Stopwatch.StartNew();
				while (CpuCapNative.IsAlive(parent) && stopwatch.ElapsedMilliseconds < 10000)
				{
					List<string> list = new List<string>();
					foreach (KeyValuePair<string, IntPtr> item2 in dictionary2)
					{
						if (!CpuCapNative.IsAlive(item2.Value))
						{
							list.Add(item2.Key);
						}
					}
					foreach (string item3 in list)
					{
						Native.CloseHandle(dictionary2[item3]);
						Native.CloseHandle(dictionary[item3]);
						dictionary2.Remove(item3);
						dictionary.Remove(item3);
					}
					string item;
					if (!commands.TryTake(out item, 250))
					{
						if (commands.IsCompleted)
						{
							break;
						}
						continue;
					}
					stopwatch.Restart();
					if (item == "PING")
					{
						Console.WriteLine("OK");
						continue;
					}
					string[] array = item.Split(' ');
					int result;
					long result2;
					if (array.Length != 4 || array[0] != "WATCH" || !array[1].StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(array[2], out result) || !long.TryParse(array[3], out result2) || array[1] != prefix + result + "." + result2 || dictionary.Count >= 256)
					{
						break;
					}
					if (dictionary.ContainsKey(array[1]))
					{
						Console.WriteLine("OK");
						continue;
					}
					IntPtr intPtr = CpuCapNative.OpenTarget(result, result2);
					IntPtr intPtr2 = CpuCapNative.OpenJobObject(6u, false, array[1]);
					if (intPtr2 == IntPtr.Zero || intPtr == IntPtr.Zero)
					{
						if (intPtr2 != IntPtr.Zero)
						{
							Native.CloseHandle(intPtr2);
						}
						if (intPtr != IntPtr.Zero)
						{
							Native.CloseHandle(intPtr);
						}
						Console.WriteLine("ERR");
					}
					else
					{
						dictionary.Add(array[1], intPtr2);
						dictionary2.Add(array[1], intPtr);
						Console.WriteLine("OK");
					}
				}
			}
			finally
			{
				foreach (IntPtr value in dictionary.Values)
				{
					bool flag2 = false;
					for (int i = 0; i < 3; i++)
					{
						if (flag2)
						{
							break;
						}
						flag2 = CpuCapNative.SetRate(value, 0m);
						if (!flag2)
						{
							Thread.Sleep(100);
						}
					}
					flag = flag && flag2;
					Native.CloseHandle(value);
				}
				foreach (IntPtr value2 in dictionary2.Values)
				{
					Native.CloseHandle(value2);
				}
			}
			return flag;
		}
	}
}

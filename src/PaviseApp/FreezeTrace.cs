using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
	internal sealed class FreezeTrace
	{
		internal const int Capacity = 8192;

		private readonly List<string> lines = new List<string>();

		private int dropped;

		internal static string Identity(FreezeTarget target)
		{
			string text;
			try
			{
				text = Path.GetFileName(target.Path ?? "");
			}
			catch
			{
				text = "?";
			}
			text = text.Replace('\r', '_').Replace('\n', '_').Replace('\t', '_');
			if (text.Length > 160)
			{
				text = text.Substring(0, 160);
			}
			return "pid=" + target.Pid + " born=" + target.Creation + " exe=" + text;
		}

		internal void Add(string message)
		{
			if (lines.Count < 8192)
			{
				lines.Add(message);
			}
			else if (dropped < int.MaxValue)
			{
				dropped++;
			}
		}

		internal string Drain()
		{
			if (dropped > 0)
			{
				lines.Add("trace-truncated dropped=" + dropped);
			}
			string text = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", lines.ToArray())));
			lines.Clear();
			dropped = 0;
			return "TRACE " + text;
		}
	}
}

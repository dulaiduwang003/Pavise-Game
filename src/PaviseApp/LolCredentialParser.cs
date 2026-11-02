using System;
using System.Text.RegularExpressions;

namespace PaviseApp
{
	internal static class LolCredentialParser
	{
		private static readonly Regex PortPattern = new Regex("(?:^|\\s)--app-port(?:=|\\s+)(?:\\\")?(\\d{1,5})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

		private static readonly Regex TokenPattern = new Regex("(?:^|\\s)--remoting-auth-token(?:=|\\s+)(?:\\\"([^\\\"]+)\\\"|([^\\s\\\"]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

		public static bool TryParseCommandLine(string value, out int port, out string token)
		{
			port = 0;
			token = null;
			if (string.IsNullOrEmpty(value))
			{
				return false;
			}
			int num = value.Length;
			while (num > 0)
			{
				int num2 = value.LastIndexOfAny(new char[2] { '\r', '\n' }, num - 1);
				int num3 = num2 + 1;
				if (TryParseSegment(value, num3, num - num3, out port, out token))
				{
					return true;
				}
				num = num2;
				while (num > 0 && (value[num - 1] == '\r' || value[num - 1] == '\n'))
				{
					num--;
				}
			}
			return false;
		}

		private static bool TryParseSegment(string value, int start, int length, out int port, out string token)
		{
			port = 0;
			token = null;
			string input = ((start == 0 && length == value.Length) ? value : value.Substring(start, length));
			MatchCollection matchCollection = PortPattern.Matches(input);
			MatchCollection matchCollection2 = TokenPattern.Matches(input);
			if (matchCollection.Count == 0 || matchCollection2.Count == 0)
			{
				return false;
			}
			Match match = matchCollection[matchCollection.Count - 1];
			Match match2 = matchCollection2[matchCollection2.Count - 1];
			if (!int.TryParse(match.Groups[1].Value, out port) || !ValidPort(port))
			{
				return false;
			}
			token = (match2.Groups[1].Success ? match2.Groups[1].Value : match2.Groups[2].Value);
			if (!ValidToken(token))
			{
				port = 0;
				token = null;
				return false;
			}
			return true;
		}

		public static bool TryParseLockfile(string value, out int port, out string token)
		{
			int pid;
			return TryParseLockfile(value, out pid, out port, out token);
		}

		internal static bool TryParseLockfile(string value, out int pid, out int port, out string token)
		{
			pid = 0;
			port = 0;
			token = null;
			if (string.IsNullOrEmpty(value))
			{
				return false;
			}
			string text = value.Trim();
			int num = text.IndexOfAny(new char[2] { '\r', '\n' });
			if (num >= 0)
			{
				text = text.Substring(0, num);
			}
			string[] array = text.Split(':');
			if (array.Length != 5)
			{
				return false;
			}
			if (!int.TryParse(array[1], out pid) || pid <= 0)
			{
				return false;
			}
			if (!int.TryParse(array[2], out port) || !ValidPort(port))
			{
				return false;
			}
			token = array[3];
			if (!ValidToken(token))
			{
				port = 0;
				pid = 0;
				token = null;
				return false;
			}
			return string.Equals(array[4], "https", StringComparison.OrdinalIgnoreCase);
		}

		private static bool ValidPort(int port)
		{
			if (port > 0)
			{
				return port <= 65535;
			}
			return false;
		}

		private static bool ValidToken(string token)
		{
			if (string.IsNullOrEmpty(token) || token.Length > 1024)
			{
				return false;
			}
			for (int i = 0; i < token.Length; i++)
			{
				if (char.IsWhiteSpace(token[i]) || char.IsControl(token[i]))
				{
					return false;
				}
			}
			return true;
		}
	}
}

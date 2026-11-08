using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
	internal static class SelfBuildGuard
	{
		private const int MaxCached = 512;

		private static readonly object sync = new object();

		private static readonly Dictionary<string, bool> cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

		private static string selfProduct;

		private static string selfPrefix;

		internal static string SelfProduct
		{
			get
			{
				if (selfProduct != null)
				{
					return selfProduct;
				}
				string message = "";
				try
				{
					object[] customAttributes = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyProductAttribute), false);
					if (customAttributes.Length > 0)
					{
						message = ((AssemblyProductAttribute)customAttributes[0]).Product ?? "";
					}
				}
				catch
				{
				}
				selfProduct = message;
				return selfProduct;
			}
		}

		internal static string SelfPrefix
		{
			get
			{
				if (selfPrefix != null)
				{
					return selfPrefix;
				}
				string processName = "";
				try
				{
					using (Process process = Process.GetCurrentProcess())
					{
						processName = process.ProcessName;
					}
				}
				catch
				{
				}
				selfPrefix = PrefixOf(processName);
				return selfPrefix;
			}
		}

		internal static string PrefixOf(string processName)
		{
			if (string.IsNullOrEmpty(processName))
			{
				return "";
			}
			int matchIndex = processName.IndexOf('.');
			if (matchIndex <= 0)
			{
				return processName;
			}
			return processName.Substring(0, matchIndex);
		}

		internal static bool NameCouldBeOurs(string processName, string selfPrefixValue)
		{
			if (string.IsNullOrEmpty(processName) || string.IsNullOrEmpty(selfPrefixValue))
			{
				return false;
			}
			return processName.StartsWith(selfPrefixValue, StringComparison.OrdinalIgnoreCase);
		}

		internal static bool ProductMatches(string product)
		{
			string selfProductSnapshot = SelfProduct;
			if (selfProductSnapshot.Length > 0)
			{
				return string.Equals(selfProductSnapshot, product, StringComparison.Ordinal);
			}
			return false;
		}

		public static bool IsOwnBuild(string processName, string imagePath)
		{
			if (string.IsNullOrEmpty(imagePath))
			{
				return false;
			}
			if (!NameCouldBeOurs(processName, SelfPrefix))
			{
				return false;
			}
			if (SelfProduct.Length == 0)
			{
				return false;
			}
			lock (sync)
			{
				bool value;
				if (cache.TryGetValue(imagePath, out value))
				{
					return value;
				}
				value = false;
				try
				{
					value = ProductMatches(FileVersionInfo.GetVersionInfo(imagePath).ProductName);
				}
				catch (IOException)
				{
				}
				catch (UnauthorizedAccessException)
				{
				}
				catch (ArgumentException)
				{
				}
				if (cache.Count >= 512)
				{
					cache.Clear();
				}
				cache[imagePath] = value;
				return value;
			}
		}
	}
}

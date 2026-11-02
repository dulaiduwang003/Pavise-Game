using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Threading;

namespace PaviseApp
{
	internal static class SecurityProductCatalog
	{
		private const int RefreshMs = 600000;

		private const int MaxRoots = 16;

		private static readonly string[] Classes = new string[3] { "AntiVirusProduct", "FirewallProduct", "AntiSpywareProduct" };

		private static readonly object sync = new object();

		private static string[] roots = new string[0];

		private static int stamp;

		private static bool scanned;

		private static bool refreshing;

		private static volatile int generation;

		internal static int CurrentGeneration()
		{
			Roots();
			return generation;
		}

		internal static bool IsSecurityProduct(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return false;
			}
			string[] array = Roots();
			foreach (string root in array)
			{
				if (FamilyBoundary.UnderRoot(path, root))
				{
					return true;
				}
			}
			return false;
		}

		internal static string[] Roots()
		{
			bool flag;
			lock (sync)
			{
				int tickCount = Environment.TickCount;
				if (scanned && (refreshing || tickCount - stamp < 600000))
				{
					return roots;
				}
				flag = !scanned;
				stamp = tickCount;
				scanned = true;
				refreshing = !flag;
			}
			if (flag)
			{
				return Publish(Scan());
			}
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					Publish(Scan());
				}
				finally
				{
					lock (sync)
					{
						refreshing = false;
					}
				}
			});
			lock (sync)
			{
				return roots;
			}
		}

		private static string[] Publish(string[] fresh)
		{
			lock (sync)
			{
				if (!SameSet(roots, fresh))
				{
					roots = fresh;
					generation++;
					Logger.Log("安全软件目录 " + fresh.Length + " 处不压制：" + string.Join(" | ", fresh));
				}
				return roots;
			}
		}

		private static string[] Scan()
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			string[] bases = StandardBases();
			string[] classes = Classes;
			foreach (string text in classes)
			{
				try
				{
					using (ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("root\\SecurityCenter2", "SELECT pathToSignedProductExe, pathToSignedReportingExe FROM " + text))
					{
						managementObjectSearcher.Options.Timeout = TimeSpan.FromSeconds(3.0);
						using (ManagementObjectCollection managementObjectCollection = managementObjectSearcher.Get())
						{
							foreach (ManagementObject item in managementObjectCollection)
							{
								using (item)
								{
									Add(hashSet, ProductRoot(item["pathToSignedProductExe"] as string, bases, folderPath));
									Add(hashSet, ProductRoot(item["pathToSignedReportingExe"] as string, bases, folderPath));
								}
							}
						}
					}
				}
				catch
				{
				}
			}
			List<string> list = new List<string>(hashSet);
			list.Sort(StringComparer.OrdinalIgnoreCase);
			if (list.Count > 16)
			{
				list.RemoveRange(16, list.Count - 16);
			}
			return list.ToArray();
		}

		private static void Add(HashSet<string> set, string root)
		{
			if (root != null)
			{
				set.Add(root);
			}
		}

		private static string[] StandardBases()
		{
			List<string> list = new List<string>();
			Environment.SpecialFolder[] array = new Environment.SpecialFolder[3]
			{
				Environment.SpecialFolder.ProgramFiles,
				Environment.SpecialFolder.ProgramFilesX86,
				Environment.SpecialFolder.CommonApplicationData
			};
			foreach (Environment.SpecialFolder folder in array)
			{
				string folderPath;
				try
				{
					folderPath = Environment.GetFolderPath(folder);
				}
				catch
				{
					continue;
				}
				if (!string.IsNullOrEmpty(folderPath))
				{
					list.Add(folderPath.TrimEnd('\\'));
				}
			}
			return list.ToArray();
		}

		internal static string ProductRoot(string registered, string[] bases, string windowsRoot)
		{
			if (string.IsNullOrEmpty(registered))
			{
				return null;
			}
			string text = Environment.ExpandEnvironmentVariables(registered.Trim().Trim('"'));
			if (text.Length < 4 || text[1] != ':' || text[2] != '\\')
			{
				return null;
			}
			string directoryName;
			try
			{
				directoryName = Path.GetDirectoryName(text);
			}
			catch
			{
				return null;
			}
			if (string.IsNullOrEmpty(directoryName) || directoryName.TrimEnd('\\').Length <= 2)
			{
				return null;
			}
			directoryName = directoryName.TrimEnd('\\');
			if (!string.IsNullOrEmpty(windowsRoot) && (WhitelistRule.PathEquals(directoryName, windowsRoot.TrimEnd('\\')) || FamilyBoundary.UnderRoot(directoryName, windowsRoot)))
			{
				return null;
			}
			string[] array = bases ?? new string[0];
			foreach (string text2 in array)
			{
				if (!string.IsNullOrEmpty(text2))
				{
					if (WhitelistRule.PathEquals(directoryName, text2))
					{
						return null;
					}
					if (FamilyBoundary.UnderRoot(directoryName, text2))
					{
						int num = directoryName.Substring(text2.Length).Trim('\\').Split('\\')
							.Length;
						return (num >= 3) ? Path.GetDirectoryName(directoryName) : directoryName;
					}
				}
			}
			return directoryName;
		}

		private static bool SameSet(string[] a, string[] b)
		{
			if (a.Length != b.Length)
			{
				return false;
			}
			for (int i = 0; i < a.Length; i++)
			{
				if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
			}
			return true;
		}
	}
}

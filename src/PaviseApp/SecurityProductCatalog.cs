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
        private sealed class ProviderState { internal string[] Roots = new string[0]; internal int Failures; }
        private static readonly Dictionary<string, ProviderState> providers = new Dictionary<string, ProviderState>(StringComparer.Ordinal);
        private static long failureCycle = -1;
        private static int queueFailures;
        private static void SyncCycleLocked()
        {
            long cycle = PerformanceTuning.RecoveryCycleSerial;
            if (cycle <= failureCycle) return;
            failureCycle = cycle; queueFailures = 0; scanned = false;
            foreach (ProviderState provider in providers.Values) provider.Failures = 0;
        }
        private static bool ProviderWorkLocked()
        {
            foreach (string name in Classes)
            { ProviderState state; if (!providers.TryGetValue(name, out state) || state.Failures < 3) return true; }
            return false;
        }

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
                SyncCycleLocked();
				int tickCount = Environment.TickCount;
				if (refreshing || queueFailures >= 3 || (scanned && tickCount - stamp < 600000) || !ProviderWorkLocked())
				{
					return roots;
				}
				flag = !scanned;
				stamp = tickCount;
				scanned = true;
				refreshing = true;
			}
            long cycle;
            lock (sync) cycle = failureCycle;
            if (flag)
            {
                try { return Publish(Scan()); }
                finally { lock (sync) { refreshing = false; stamp = Environment.TickCount; } }
            }
            try
            {
                if (!ThreadPool.QueueUserWorkItem(delegate
                {
                    try { Publish(Scan()); }
                    finally { lock (sync) { refreshing = false; stamp = Environment.TickCount; } }
                })) throw new InvalidOperationException("Security product scan could not be queued");
            }
            catch (Exception error)
            {
                lock (sync)
                {
                    refreshing = false; stamp = Environment.TickCount;
                    if (cycle == failureCycle)
                    {
                        queueFailures++;
                        Logger.Warn("Security product queue failed phase=" + cycle + " attempt=" + queueFailures + "/3: " + error.GetType().Name);
                    }
                }
            }
            lock (sync) return roots;
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
            var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in Classes)
            {
                ProviderState provider; long cycle; bool allowed;
                lock (sync)
                {
                    if (!providers.TryGetValue(name, out provider)) providers[name] = provider = new ProviderState();
                    cycle = failureCycle; allowed = provider.Failures < 3;
                }
                if (allowed)
                {
                    try
                    {
                        string[] fresh = ReadProvider(name);
                        lock (sync) provider.Roots = fresh;
                    }
                    catch (Exception error)
                    {
                        lock (sync)
                        {
                            if (cycle == failureCycle)
                            {
                                provider.Failures++;
                                Logger.Warn("Security product read failed provider=" + name + " phase=" + cycle + " attempt=" + provider.Failures + "/3: " + error.GetType().Name);
                            }
                        }
                    }
                }
                lock (sync) foreach (string root in provider.Roots) merged.Add(root);
            }
            var list = new List<string>(merged); list.Sort(StringComparer.OrdinalIgnoreCase);
            if (list.Count > MaxRoots) list.RemoveRange(MaxRoots, list.Count - MaxRoots);
            return list.ToArray();
        }
        private static string[] ReadProvider(string provider)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string[] bases = StandardBases();
            using (var searcher = new ManagementObjectSearcher("root\\SecurityCenter2", "SELECT pathToSignedProductExe, pathToSignedReportingExe FROM " + provider))
            {
                searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                using (ManagementObjectCollection rows = searcher.Get())
                    foreach (ManagementObject row in rows)
                        using (row)
                        {
                            Add(found, ProductRoot(row["pathToSignedProductExe"] as string, bases, windows));
                            Add(found, ProductRoot(row["pathToSignedReportingExe"] as string, bases, windows));
                        }
            }
            return new List<string>(found).ToArray();
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

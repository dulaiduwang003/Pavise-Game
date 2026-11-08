using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    // Seal the security inventory once at startup; sessions and settings must not reopen it.
    internal static class SecurityProductCatalog
    {
        private static readonly object sync = new object();
        private static SecurityProductSnapshot snapshot = SecurityProductSnapshot.Unknown(false, "");
        private static string[] roots = new string[0];
        private static int startupClaimed;
        private static int generation;
        private static SecurityProbeTerminationException startupBlocked;

        internal static SecurityProductSnapshot StartupSnapshot { get { lock (sync) return snapshot; } }

        internal static void StartupScanOnce()
        {
            if (Interlocked.CompareExchange(ref startupClaimed, 1, 0) != 0)
            { if (startupBlocked != null) throw startupBlocked; return; }
            SecurityProductSnapshot fresh;
            try { fresh = RunStartupProbe(); }
            catch (SecurityProbeTerminationException error)
            {
                lock (sync) { startupBlocked = error; snapshot = SecurityProductSnapshot.Unknown(true, "Helper termination unconfirmed; startup stopped"); }
                throw;
            }
            catch (Exception error) { fresh = SecurityProductSnapshot.Unknown(true, error.GetType().Name); }
            if (fresh == null) fresh = SecurityProductSnapshot.Unknown(true, "Empty startup result");
            lock (sync) { snapshot = fresh; roots = fresh.ProtectedRoots; generation = 1; }
            if (fresh.FailureSummary.Length != 0)
                Logger.Warn("Security startup inspection exhausted attempt=1/1; no runtime retry: " + fresh.FailureSummary);
            int active = 0, inactive = 0, uncertain = 0;
            foreach (SecurityProductInfo product in fresh.Products)
            {
                if (product.State == SecurityProductState.Active) active++;
                else if (product.State == SecurityProductState.Inactive) inactive++;
                else uncertain++;
            }
            Logger.Log("Security startup inspection sealed products=" + fresh.Products.Length + " protectedRoots=" + roots.Length
                + " active=" + active + " inactive=" + inactive + " uncertain=" + uncertain + " unknown=" + fresh.HasUnknownState);
        }

        private static SecurityProductSnapshot RunStartupProbe() { return SecurityStartupProbe.ReadOnce(); }
        internal static int CurrentGeneration() { lock (sync) return generation; }
        internal static string[] Roots() { lock (sync) return (string[])roots.Clone(); }
        internal static bool IsSecurityProduct(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            lock (sync)
            {
                foreach (string root in roots) if (FamilyBoundary.UnderRoot(path, root)) return true;
                return false;
            }
        }

        internal static string ProtectedRoot(string path)
        { return ProductRoot(path, StandardBases(), Environment.GetFolderPath(Environment.SpecialFolder.Windows)); }
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


	}
}
